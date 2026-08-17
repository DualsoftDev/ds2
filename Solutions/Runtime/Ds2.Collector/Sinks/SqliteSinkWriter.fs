namespace Ds2.Collector.Sinks

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Ds2.Adapter.Common

/// ADR-011 · SQLite telemetry.db + events.db writer.
///
/// - `signals` 테이블에 Sample 저장 (dedup on PRIMARY KEY)
/// - `events`  테이블에 Event 저장 (dedup on UNIQUE index)
/// - Batch write via 하나의 트랜잭션
type SqliteSinkWriter(telemetryDb: string, eventsDb: string) =

    do
        Directory.CreateDirectory(Path.GetDirectoryName telemetryDb) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName eventsDb) |> ignore

        use telemetryConn = new SqliteConnection($"Data Source={telemetryDb};Pooling=False")
        telemetryConn.Open()
        use tc = telemetryConn.CreateCommand()
        tc.CommandText <- """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS signals (
                global_asset_id TEXT NOT NULL,
                signal_id       TEXT NOT NULL,
                source_ts_us    INTEGER NOT NULL,
                server_ts_us    INTEGER,
                envelope_id     BLOB NOT NULL,
                seq_no          INTEGER,
                origin          TEXT NOT NULL,
                value_type      TEXT NOT NULL,
                value_double    REAL,
                value_long      INTEGER,
                value_string    TEXT,
                value_bool      INTEGER,
                quality         INTEGER NOT NULL,
                unit            TEXT,
                PRIMARY KEY (global_asset_id, signal_id, source_ts_us, envelope_id)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_signals_asset_time
                ON signals (global_asset_id, source_ts_us DESC);
            CREATE TABLE IF NOT EXISTS downsample_dirty (
                envelope_id     BLOB PRIMARY KEY,
                global_asset_id TEXT NOT NULL,
                signal_id       TEXT NOT NULL,
                source_ts_us    INTEGER NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_downsample_dirty_time
                ON downsample_dirty (source_ts_us);
        """
        tc.ExecuteNonQuery() |> ignore

        use eventsConn = new SqliteConnection($"Data Source={eventsDb};Pooling=False")
        eventsConn.Open()
        use ec = eventsConn.CreateCommand()
        ec.CommandText <- """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS events (
                id                     INTEGER PRIMARY KEY AUTOINCREMENT,
                envelope_id            BLOB NOT NULL UNIQUE,
                global_asset_id        TEXT NOT NULL,
                signal_id              TEXT NOT NULL,
                event_type_semantic_id TEXT NOT NULL,
                source_ts_us           INTEGER NOT NULL,
                server_ts_us           INTEGER NOT NULL,
                ingested_ts_us         INTEGER NOT NULL,
                seq_no                 INTEGER,
                payload                TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_asset_time
                ON events (global_asset_id, source_ts_us DESC);
            CREATE INDEX IF NOT EXISTS ix_events_asset_signal_time
                ON events (global_asset_id, signal_id, source_ts_us DESC, id DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_events_dedup
                ON events (global_asset_id, signal_id, source_ts_us, envelope_id);
        """
        ec.ExecuteNonQuery() |> ignore

    let openTelemetry () =
        let c = new SqliteConnection($"Data Source={telemetryDb};Pooling=False")
        c.Open()
        c

    let openEvents () =
        let c = new SqliteConnection($"Data Source={eventsDb};Pooling=False")
        c.Open()
        c

    let toUnixUs (dt: DateTimeOffset) =
        UnixTime.toMicroseconds dt

    let valueTypeTag =
        function
        | ValueDouble _ -> "double"
        | ValueLong _   -> "long"
        | ValueString _ -> "string"
        | ValueBool _   -> "bool"
        | ValueNone     -> "none"

    let insertSample (tx: SqliteTransaction) (e: Envelope) : int =
        use cmd = tx.Connection.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "INSERT OR IGNORE INTO signals
             (global_asset_id, signal_id, source_ts_us, server_ts_us, envelope_id, seq_no, origin,
              value_type, value_double, value_long, value_string, value_bool, quality, unit)
             VALUES ($gaid, $sid, $ts, $sts, $eid, $seq, $origin,
                     $vt, $vd, $vl, $vs, $vb, $q, $unit)"
        cmd.Parameters.AddWithValue("$gaid", e.GlobalAssetId.Value) |> ignore
        cmd.Parameters.AddWithValue("$sid", e.SignalId.Value) |> ignore
        cmd.Parameters.AddWithValue("$ts", toUnixUs e.SourceTimestamp) |> ignore
        cmd.Parameters.AddWithValue("$sts", match e.ServerTimestamp with Some t -> box (toUnixUs t) | None -> box DBNull.Value) |> ignore
        cmd.Parameters.AddWithValue("$eid", e.EnvelopeId.ToByteArray()) |> ignore
        cmd.Parameters.AddWithValue("$seq", match e.SeqNo with Some n -> box (int64 n) | None -> box DBNull.Value) |> ignore
        cmd.Parameters.AddWithValue("$origin", e.Origin) |> ignore
        cmd.Parameters.AddWithValue("$vt", valueTypeTag e.Value) |> ignore
        let vd, vl, vs, vb =
            match e.Value with
            | ValueDouble d -> box d, box DBNull.Value, box DBNull.Value, box DBNull.Value
            | ValueLong n   -> box DBNull.Value, box n, box DBNull.Value, box DBNull.Value
            | ValueString s -> box DBNull.Value, box DBNull.Value, box s, box DBNull.Value
            | ValueBool b   -> box DBNull.Value, box DBNull.Value, box DBNull.Value, box (if b then 1 else 0)
            | ValueNone     -> box DBNull.Value, box DBNull.Value, box DBNull.Value, box DBNull.Value
        cmd.Parameters.AddWithValue("$vd", vd) |> ignore
        cmd.Parameters.AddWithValue("$vl", vl) |> ignore
        cmd.Parameters.AddWithValue("$vs", vs) |> ignore
        cmd.Parameters.AddWithValue("$vb", vb) |> ignore
        cmd.Parameters.AddWithValue("$q", int64 e.StatusCode) |> ignore
        cmd.Parameters.AddWithValue("$unit", match e.Unit with Some u -> box u | None -> box DBNull.Value) |> ignore
        let inserted = cmd.ExecuteNonQuery()
        if inserted > 0 then
            use dirty = tx.Connection.CreateCommand()
            dirty.Transaction <- tx
            dirty.CommandText <-
                "INSERT OR IGNORE INTO downsample_dirty
                 (envelope_id, global_asset_id, signal_id, source_ts_us)
                 VALUES ($eid, $gaid, $sid, $ts)"
            dirty.Parameters.AddWithValue("$eid", e.EnvelopeId.ToByteArray()) |> ignore
            dirty.Parameters.AddWithValue("$gaid", e.GlobalAssetId.Value) |> ignore
            dirty.Parameters.AddWithValue("$sid", e.SignalId.Value) |> ignore
            dirty.Parameters.AddWithValue("$ts", toUnixUs e.SourceTimestamp) |> ignore
            dirty.ExecuteNonQuery() |> ignore
        inserted

    let insertEvent (tx: SqliteTransaction) (e: Envelope) : int =
        use cmd = tx.Connection.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "INSERT OR IGNORE INTO events
             (envelope_id, global_asset_id, signal_id, event_type_semantic_id,
              source_ts_us, server_ts_us, ingested_ts_us, seq_no, payload)
             VALUES ($eid, $gaid, $sid, $et, $sts, $srvts, $ing, $seq, $payload)"
        cmd.Parameters.AddWithValue("$eid", e.EnvelopeId.ToByteArray()) |> ignore
        cmd.Parameters.AddWithValue("$gaid", e.GlobalAssetId.Value) |> ignore
        cmd.Parameters.AddWithValue("$sid", e.SignalId.Value) |> ignore
        cmd.Parameters.AddWithValue("$et", e.EventTypeSemanticId |> Option.defaultValue "") |> ignore
        cmd.Parameters.AddWithValue("$sts", toUnixUs e.SourceTimestamp) |> ignore
        cmd.Parameters.AddWithValue("$srvts", toUnixUs (e.ServerTimestamp |> Option.defaultValue e.SourceTimestamp)) |> ignore
        cmd.Parameters.AddWithValue("$ing", toUnixUs DateTimeOffset.UtcNow) |> ignore
        cmd.Parameters.AddWithValue("$seq", match e.SeqNo with Some n -> box (int64 n) | None -> box DBNull.Value) |> ignore
        cmd.Parameters.AddWithValue("$payload", e.EventPayloadJson |> Option.defaultValue "{}") |> ignore
        cmd.ExecuteNonQuery()

    member _.WriteBatchAsync (envelopes: Envelope seq) = task {
        let list = envelopes |> List.ofSeq
        if list.IsEmpty then return 0
        else
            let samples = list |> List.filter (fun e -> e.Kind = Sample)
            let events = list |> List.filter (fun e -> e.Kind = Event)
            let mutable rows = 0
            if not samples.IsEmpty then
                use conn = openTelemetry()
                use tx = conn.BeginTransaction()
                for e in samples do rows <- rows + insertSample tx e
                tx.Commit()
            if not events.IsEmpty then
                use conn = openEvents()
                use tx = conn.BeginTransaction()
                for e in events do rows <- rows + insertEvent tx e
                tx.Commit()
            return rows
    }

    member _.PendingCount() = 0

    member _.DisposeAsync() = task { return () }

    /// 조회 헬퍼 — DataService (Phase 7) 도 재사용.
    member _.QuerySignals(globalAssetId: string, signalId: string, fromUs: int64, toUs: int64) : (int64 * float option) list =
        use conn = new SqliteConnection($"Data Source={telemetryDb};Mode=ReadOnly;Pooling=False")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT source_ts_us, value_double FROM signals
             WHERE global_asset_id = $g AND signal_id = $s
               AND source_ts_us BETWEEN $f AND $t
             ORDER BY source_ts_us"
        cmd.Parameters.AddWithValue("$g", globalAssetId) |> ignore
        cmd.Parameters.AddWithValue("$s", signalId) |> ignore
        cmd.Parameters.AddWithValue("$f", fromUs) |> ignore
        cmd.Parameters.AddWithValue("$t", toUs) |> ignore
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do
              let ts = reader.GetInt64 0
              let v = if reader.IsDBNull 1 then None else Some (reader.GetDouble 1)
              yield ts, v ]
