namespace Ds2.Adapter.Common

open System
open System.IO
open System.Text.Json
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

/// SQLite 기반 outbox.
type SqliteEdgeBuffer(dbPath: string) =
    do
        Directory.CreateDirectory(Path.GetDirectoryName dbPath) |> ignore
        use conn = new SqliteConnection($"Data Source={dbPath};Pooling=False")
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
        """
        cmd.ExecuteNonQuery() |> ignore

    let openConn () =
        let c = new SqliteConnection($"Data Source={dbPath};Pooling=False")
        c.Open()
        c

    let toUnixUs (dt: DateTimeOffset) =
        (dt.ToUnixTimeMilliseconds() * 1000L)

    let fromUnixUs (us: int64) =
        DateTimeOffset.FromUnixTimeMilliseconds(us / 1000L)

    let priorityOf (env: Envelope) =
        match env.Kind with
        | Event -> Priority.EventPriority
        | Sample -> Priority.SamplePriority

    member _.Enqueue (env: Envelope) =
        use conn = openConn()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "INSERT OR REPLACE INTO pending
             (envelope_id, payload, kind, priority, attempts, next_retry_us, created_us)
             VALUES ($id, $payload, $kind, $priority, 0, $now, $now)"
        let now = toUnixUs DateTimeOffset.UtcNow
        let payload = System.Text.Encoding.UTF8.GetBytes(EnvelopeJson.toJson env)
        cmd.Parameters.AddWithValue("$id", env.EnvelopeId.ToByteArray()) |> ignore
        cmd.Parameters.AddWithValue("$payload", payload) |> ignore
        cmd.Parameters.AddWithValue("$kind", string env.Kind) |> ignore
        cmd.Parameters.AddWithValue("$priority", int (priorityOf env)) |> ignore
        cmd.Parameters.AddWithValue("$now", now) |> ignore
        cmd.ExecuteNonQuery() |> ignore

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
        use conn = openConn()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "DELETE FROM pending WHERE envelope_id = $id"
        cmd.Parameters.AddWithValue("$id", envelopeId.ToByteArray()) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    member _.Requeue (envelopeId: Guid, backoff: TimeSpan) =
        use conn = openConn()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "UPDATE pending
             SET attempts = attempts + 1,
                 next_retry_us = $next
             WHERE envelope_id = $id"
        cmd.Parameters.AddWithValue("$id", envelopeId.ToByteArray()) |> ignore
        cmd.Parameters.AddWithValue("$next", toUnixUs (DateTimeOffset.UtcNow.Add backoff)) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    member _.PendingCount () =
        use conn = openConn()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM pending"
        Convert.ToInt32(cmd.ExecuteScalar())
