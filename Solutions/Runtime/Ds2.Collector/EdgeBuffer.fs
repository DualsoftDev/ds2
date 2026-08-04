namespace Ds2.Adapter.Common

open System
open System.IO
open System.Text.Json
open System.Threading
open Microsoft.Data.Sqlite

/// ADR-006/011 · SQLite 로컬 outbox. Collector 접속 단절 시 축적, 복구 후 flush.
///
/// 스키마: pending(envelope_id BLOB PK, payload BLOB, kind TEXT, priority INTEGER,
///                 attempts INTEGER, next_retry_us INTEGER, created_us INTEGER)

type Priority =
    | EventPriority = 0
    | SamplePriority = 1

type PendingRow = {
    EnvelopeId : Guid
    Envelope   : Envelope
    Priority   : Priority
    Attempts   : int
    NextRetry  : DateTimeOffset
    Created    : DateTimeOffset
}

module private EnvelopeJson =
    let opts = Ds2.Core.JsonOptions.createProjectSerializationOptions()
    do opts.WriteIndented <- false
    let toJson (e: Envelope) : string = JsonSerializer.Serialize(e, opts)
    let fromJson (s: string) : Envelope = JsonSerializer.Deserialize<Envelope>(s, opts)

/// SQLite 기반 outbox. 전체 용량의 20%는 Event를 위해 예약해 sample 폭주가
/// 이벤트 저장 공간까지 잠식하지 않게 한다.
type SqliteEdgeBuffer(dbPath: string, ?maxPendingRows: int64, ?maxPayloadBytes: int64) =
    let connectionString = $"Data Source={dbPath};Pooling=False;Default Timeout=5"
    let writeGate = obj()
    let int64Env name fallback minimum =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> fallback
        | value ->
            match Int64.TryParse value with
            | true, parsed -> max minimum parsed
            | _ -> fallback
    let maximumRows = defaultArg maxPendingRows (int64Env "DS2_OUTBOX_MAX_ROWS" 2_000_000L 1_000L)
    let maximumPayloadBytes =
        defaultArg maxPayloadBytes (int64Env "DS2_OUTBOX_MAX_PAYLOAD_BYTES" 1_073_741_824L 16_777_216L)
    let sampleRowsLimit = max 1L (maximumRows * 8L / 10L)
    let samplePayloadLimit = max 1L (maximumPayloadBytes * 8L / 10L)

    do
        Directory.CreateDirectory(Path.GetDirectoryName dbPath) |> ignore
        use conn = new SqliteConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS pending (
                envelope_id BLOB PRIMARY KEY,
                payload BLOB NOT NULL,
                kind TEXT NOT NULL,
                priority INTEGER NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                next_retry_us INTEGER NOT NULL,
                created_us INTEGER NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_pending_priority_next
                ON pending (priority, next_retry_us);
            CREATE TABLE IF NOT EXISTS buffer_stats (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                row_count INTEGER NOT NULL,
                payload_bytes INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO buffer_stats(id, row_count, payload_bytes) VALUES (1, 0, 0);
            CREATE TRIGGER IF NOT EXISTS trg_pending_insert_stats
            AFTER INSERT ON pending BEGIN
                UPDATE buffer_stats
                SET row_count = row_count + 1,
                    payload_bytes = payload_bytes + length(NEW.payload)
                WHERE id = 1;
            END;
            CREATE TRIGGER IF NOT EXISTS trg_pending_delete_stats
            AFTER DELETE ON pending BEGIN
                UPDATE buffer_stats
                SET row_count = max(0, row_count - 1),
                    payload_bytes = max(0, payload_bytes - length(OLD.payload))
                WHERE id = 1;
            END;
            UPDATE buffer_stats
            SET row_count = (SELECT COUNT(*) FROM pending),
                payload_bytes = COALESCE((SELECT SUM(length(payload)) FROM pending), 0)
            WHERE id = 1;
        """
        cmd.ExecuteNonQuery() |> ignore

    let openConn () =
        let c = new SqliteConnection(connectionString)
        c.Open()
        c

    let rec withBusyRetry attempt action =
        try action ()
        with
        | :? SqliteException as ex when (ex.SqliteErrorCode = 5 || ex.SqliteErrorCode = 6) && attempt < 6 ->
            Thread.Sleep(10 * (1 <<< attempt))
            withBusyRetry (attempt + 1) action

    let toUnixUs (dt: DateTimeOffset) =
        UnixTime.toMicroseconds dt

    let fromUnixUs (us: int64) =
        UnixTime.fromMicroseconds us

    let priorityOf (env: Envelope) =
        match env.Kind with
        | Event -> Priority.EventPriority
        | Sample -> Priority.SamplePriority

    member _.Enqueue (env: Envelope) =
        lock writeGate (fun () ->
            withBusyRetry 0 (fun () ->
                use conn = openConn()
                use tx = conn.BeginTransaction()
                let payload = System.Text.Encoding.UTF8.GetBytes(EnvelopeJson.toJson env)
                use capacity = conn.CreateCommand()
                capacity.Transaction <- tx
                capacity.CommandText <-
                    "SELECT row_count, payload_bytes,
                            EXISTS(SELECT 1 FROM pending WHERE envelope_id = $id)
                     FROM buffer_stats WHERE id = 1"
                capacity.Parameters.AddWithValue("$id", env.EnvelopeId.ToByteArray()) |> ignore
                use reader = capacity.ExecuteReader()
                if not (reader.Read()) then invalidOp "Collector outbox capacity metadata is missing."
                let rows = reader.GetInt64 0
                let bytes = reader.GetInt64 1
                let alreadyPending = reader.GetInt64(2) <> 0L
                reader.Close()
                if not alreadyPending then
                    let rowLimit, byteLimit =
                        match priorityOf env with
                        | Priority.SamplePriority -> sampleRowsLimit, samplePayloadLimit
                        | Priority.EventPriority -> maximumRows, maximumPayloadBytes
                        | _ -> maximumRows, maximumPayloadBytes
                    if rows + 1L > rowLimit || bytes + int64 payload.Length > byteLimit then
                        raise (IOException(
                            $"Collector outbox capacity reached: kind={env.Kind} rows={rows}/{rowLimit} " +
                            $"payloadBytes={bytes}/{byteLimit}."))
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <-
                    "INSERT OR IGNORE INTO pending
                     (envelope_id, payload, kind, priority, attempts, next_retry_us, created_us)
                     VALUES ($id, $payload, $kind, $priority, 0, $now, $now)"
                let now = toUnixUs DateTimeOffset.UtcNow
                cmd.Parameters.AddWithValue("$id", env.EnvelopeId.ToByteArray()) |> ignore
                cmd.Parameters.AddWithValue("$payload", payload) |> ignore
                cmd.Parameters.AddWithValue("$kind", string env.Kind) |> ignore
                cmd.Parameters.AddWithValue("$priority", int (priorityOf env)) |> ignore
                cmd.Parameters.AddWithValue("$now", now) |> ignore
                cmd.ExecuteNonQuery() |> ignore
                tx.Commit()))

    member _.PullDue (maxCount: int) : PendingRow list =
        use conn = openConn()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT envelope_id, payload, priority, attempts, next_retry_us, created_us
             FROM pending
             WHERE next_retry_us <= $now
             ORDER BY priority ASC, created_us ASC
             LIMIT $lim"
        let now = toUnixUs DateTimeOffset.UtcNow
        cmd.Parameters.AddWithValue("$now", now) |> ignore
        cmd.Parameters.AddWithValue("$lim", maxCount) |> ignore
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do
            let idBytes = reader.GetFieldValue<byte[]>(0)
            let payloadBytes = reader.GetFieldValue<byte[]>(1)
            let prio = enum<Priority> (reader.GetInt32(2))
            let attempts = reader.GetInt32(3)
            let nextRetry = fromUnixUs (reader.GetInt64(4))
            let created = fromUnixUs (reader.GetInt64(5))
            let env = EnvelopeJson.fromJson (System.Text.Encoding.UTF8.GetString payloadBytes)
            yield {
                EnvelopeId = Guid(idBytes)
                Envelope = env
                Priority = prio
                Attempts = attempts
                NextRetry = nextRetry
                Created = created
            } ]

    member _.Ack (envelopeId: Guid) =
        lock writeGate (fun () ->
            withBusyRetry 0 (fun () ->
                use conn = openConn()
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "DELETE FROM pending WHERE envelope_id = $id"
                cmd.Parameters.AddWithValue("$id", envelopeId.ToByteArray()) |> ignore
                cmd.ExecuteNonQuery() |> ignore))

    member _.AckMany (envelopeIds: Guid seq) =
        let ids = envelopeIds |> Seq.toArray
        if ids.Length > 0 then
            lock writeGate (fun () ->
                withBusyRetry 0 (fun () ->
                    use conn = openConn()
                    use tx = conn.BeginTransaction()
                    use cmd = conn.CreateCommand()
                    cmd.Transaction <- tx
                    cmd.CommandText <- "DELETE FROM pending WHERE envelope_id = $id"
                    let idParameter = cmd.Parameters.Add("$id", SqliteType.Blob)
                    for envelopeId in ids do
                        idParameter.Value <- envelopeId.ToByteArray()
                        cmd.ExecuteNonQuery() |> ignore
                    tx.Commit()))

    member _.Requeue (envelopeId: Guid, backoff: TimeSpan) =
        lock writeGate (fun () ->
            withBusyRetry 0 (fun () ->
                use conn = openConn()
                use cmd = conn.CreateCommand()
                cmd.CommandText <-
                    "UPDATE pending
                     SET attempts = attempts + 1,
                         next_retry_us = $next
                     WHERE envelope_id = $id"
                cmd.Parameters.AddWithValue("$id", envelopeId.ToByteArray()) |> ignore
                cmd.Parameters.AddWithValue("$next", toUnixUs (DateTimeOffset.UtcNow.Add backoff)) |> ignore
                cmd.ExecuteNonQuery() |> ignore))

    member _.RequeueMany (rows: (Guid * TimeSpan) seq) =
        let pending = rows |> Seq.toArray
        if pending.Length > 0 then
            lock writeGate (fun () ->
                withBusyRetry 0 (fun () ->
                    use conn = openConn()
                    use tx = conn.BeginTransaction()
                    use cmd = conn.CreateCommand()
                    cmd.Transaction <- tx
                    cmd.CommandText <-
                        "UPDATE pending
                         SET attempts = attempts + 1,
                             next_retry_us = $next
                         WHERE envelope_id = $id"
                    let idParameter = cmd.Parameters.Add("$id", SqliteType.Blob)
                    let nextParameter = cmd.Parameters.Add("$next", SqliteType.Integer)
                    let current = DateTimeOffset.UtcNow
                    for envelopeId, backoff in pending do
                        idParameter.Value <- envelopeId.ToByteArray()
                        nextParameter.Value <- toUnixUs (current.Add backoff)
                        cmd.ExecuteNonQuery() |> ignore
                    tx.Commit()))

    member _.PendingCount () =
        use conn = openConn()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM pending"
        Convert.ToInt32(cmd.ExecuteScalar())

    member _.PendingUsage () =
        use conn = openConn()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT row_count, payload_bytes FROM buffer_stats WHERE id = 1"
        use reader = cmd.ExecuteReader()
        if reader.Read() then reader.GetInt64(0), reader.GetInt64(1)
        else 0L, 0L

    member _.MaximumRows = maximumRows
    member _.MaximumPayloadBytes = maximumPayloadBytes
