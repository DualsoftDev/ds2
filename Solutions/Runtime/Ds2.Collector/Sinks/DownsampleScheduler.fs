namespace Ds2.Collector.Sinks

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Ds2.Collector.DataApi
open Ds2.Adapter.Common

/// Raw typed sample을 시간/일 bucket으로 집계한다.
[<RequireQualifiedAccess>]
module Downsample =

    let private aggregateColumns =
        [ "value_type", "TEXT NOT NULL DEFAULT 'double'"
          "last_double", "REAL"
          "last_long", "INTEGER"
          "last_string", "TEXT"
          "last_bool", "INTEGER"
          "last_quality", "INTEGER"
          "unit", "TEXT" ]

    let private ensureColumns (conn: SqliteConnection) table =
        let existing =
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sprintf "PRAGMA table_info(%s)" table
            use reader = cmd.ExecuteReader()
            [ while reader.Read() do yield reader.GetString 1 ]
            |> Set.ofList
        for name, declaration in aggregateColumns do
            if not (existing.Contains name) then
                use cmd = conn.CreateCommand()
                cmd.CommandText <- sprintf "ALTER TABLE %s ADD COLUMN %s %s" table name declaration
                cmd.ExecuteNonQuery() |> ignore

    let ensureSchema (telemetryDb: string) =
        use conn = new SqliteConnection($"Data Source={telemetryDb};Pooling=False")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            CREATE TABLE IF NOT EXISTS signals_1h (
                global_asset_id TEXT NOT NULL,
                signal_id       TEXT NOT NULL,
                bucket_ts_us    INTEGER NOT NULL,
                count           INTEGER NOT NULL,
                mean            REAL,
                min_v           REAL,
                max_v           REAL,
                last_v          REAL,
                value_type      TEXT NOT NULL DEFAULT 'double',
                last_double     REAL,
                last_long       INTEGER,
                last_string     TEXT,
                last_bool       INTEGER,
                last_quality    INTEGER,
                unit            TEXT,
                PRIMARY KEY (global_asset_id, signal_id, bucket_ts_us)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS signals_1d (
                global_asset_id TEXT NOT NULL,
                signal_id       TEXT NOT NULL,
                bucket_ts_us    INTEGER NOT NULL,
                count           INTEGER NOT NULL,
                mean            REAL,
                min_v           REAL,
                max_v           REAL,
                last_v          REAL,
                value_type      TEXT NOT NULL DEFAULT 'double',
                last_double     REAL,
                last_long       INTEGER,
                last_string     TEXT,
                last_bool       INTEGER,
                last_quality    INTEGER,
                unit            TEXT,
                PRIMARY KEY (global_asset_id, signal_id, bucket_ts_us)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS downsample_dirty (
                envelope_id     BLOB PRIMARY KEY,
                global_asset_id TEXT NOT NULL,
                signal_id       TEXT NOT NULL,
                source_ts_us    INTEGER NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_downsample_dirty_time
                ON downsample_dirty (source_ts_us);
        """
        cmd.ExecuteNonQuery() |> ignore
        // 기존 numeric-only DB를 보존한 채 typed aggregate schema로 전진 마이그레이션한다.
        ensureColumns conn "signals_1h"
        ensureColumns conn "signals_1d"

    let private bucket1H = 3_600_000_000L
    let private bucket1D = 86_400_000_000L

    /// Raw signals → signals_1h · signals_1d 로 aggregate 실행.
    /// 입력 범위가 bucket 중간에서 시작해도 영향을 받는 bucket 전체를 다시 계산한다.
    let runAggregation (telemetryDb: string) (fromUs: int64) (toUs: int64) : int =
        use conn = new SqliteConnection($"Data Source={telemetryDb};Pooling=False")
        conn.Open()
        use tx = conn.BeginTransaction()
        let runBucket bucketSize table =
            let alignedFrom = (fromUs / bucketSize) * bucketSize
            let alignedTo = ((toUs / bucketSize) * bucketSize) + bucketSize - 1L
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <-
                sprintf """
                WITH raw AS (
                    SELECT
                        global_asset_id,
                        signal_id,
                        source_ts_us,
                        envelope_id,
                        (source_ts_us / $bucket) * $bucket AS bucket_ts_us,
                        value_type,
                        value_double,
                        value_long,
                        value_string,
                        value_bool,
                        quality,
                        unit,
                        CASE value_type
                            WHEN 'double' THEN value_double
                            WHEN 'long' THEN CAST(value_long AS REAL)
                            WHEN 'bool' THEN CAST(value_bool AS REAL)
                            ELSE NULL
                        END AS numeric_value
                    FROM signals
                    WHERE source_ts_us BETWEEN $f AND $t
                ), ranked AS (
                    SELECT *, ROW_NUMBER() OVER (
                        PARTITION BY global_asset_id, signal_id, bucket_ts_us
                        ORDER BY source_ts_us DESC, envelope_id DESC
                    ) AS rn
                    FROM raw
                ), buckets AS (
                    SELECT
                        global_asset_id,
                        signal_id,
                        bucket_ts_us,
                        COUNT(*) AS count,
                        AVG(numeric_value) AS mean,
                        MIN(numeric_value) AS min_v,
                        MAX(numeric_value) AS max_v
                    FROM raw
                    GROUP BY global_asset_id, signal_id, bucket_ts_us
                )
                INSERT OR REPLACE INTO %s
                    (global_asset_id, signal_id, bucket_ts_us, count, mean, min_v, max_v, last_v,
                     value_type, last_double, last_long, last_string, last_bool, last_quality, unit)
                SELECT
                    b.global_asset_id,
                    b.signal_id,
                    b.bucket_ts_us,
                    b.count,
                    b.mean,
                    b.min_v,
                    b.max_v,
                    CASE r.value_type
                        WHEN 'double' THEN r.value_double
                        WHEN 'long' THEN CAST(r.value_long AS REAL)
                        WHEN 'bool' THEN CAST(r.value_bool AS REAL)
                        ELSE NULL
                    END,
                    r.value_type,
                    r.value_double,
                    r.value_long,
                    r.value_string,
                    r.value_bool,
                    r.quality,
                    r.unit
                FROM buckets b
                JOIN ranked r
                  ON r.global_asset_id = b.global_asset_id
                 AND r.signal_id = b.signal_id
                 AND r.bucket_ts_us = b.bucket_ts_us
                 AND r.rn = 1
                """ table
            cmd.Parameters.AddWithValue("$bucket", bucketSize) |> ignore
            cmd.Parameters.AddWithValue("$f", alignedFrom) |> ignore
            cmd.Parameters.AddWithValue("$t", alignedTo) |> ignore
            cmd.ExecuteNonQuery()
        let n1 = runBucket bucket1H "signals_1h"
        let n2 = runBucket bucket1D "signals_1d"
        tx.Commit()
        n1 + n2

    /// Recomputes only buckets touched by newly persisted samples, regardless of source age.
    /// This closes the lookback-window gap after long offline/outbox recovery periods.
    let runDirtyAggregation (telemetryDb: string) (maxDirty: int) : int * int =
        use conn = new SqliteConnection($"Data Source={telemetryDb};Pooling=False")
        conn.Open()
        use tx = conn.BeginTransaction()
        use createBatch = conn.CreateCommand()
        createBatch.Transaction <- tx
        createBatch.CommandText <-
            "CREATE TEMP TABLE dirty_batch (envelope_id BLOB PRIMARY KEY) WITHOUT ROWID;
             INSERT INTO dirty_batch(envelope_id)
             SELECT envelope_id FROM downsample_dirty ORDER BY source_ts_us LIMIT $limit;"
        createBatch.Parameters.AddWithValue("$limit", max 1 maxDirty) |> ignore
        createBatch.ExecuteNonQuery() |> ignore
        use countCommand = conn.CreateCommand()
        countCommand.Transaction <- tx
        countCommand.CommandText <- "SELECT COUNT(*) FROM dirty_batch"
        let selected = Convert.ToInt32(countCommand.ExecuteScalar())
        if selected = 0 then
            tx.Commit()
            0, 0
        else
            let runBucket bucketSize table =
                use command = conn.CreateCommand()
                command.Transaction <- tx
                command.CommandText <-
                    sprintf """
                    WITH dirty AS (
                        SELECT DISTINCT d.global_asset_id, d.signal_id,
                               (d.source_ts_us / $bucket) * $bucket AS bucket_ts_us
                        FROM downsample_dirty d
                        JOIN dirty_batch b ON b.envelope_id = d.envelope_id
                    ), raw AS (
                        SELECT s.*,
                               (s.source_ts_us / $bucket) * $bucket AS bucket_ts_us,
                               CASE s.value_type
                                   WHEN 'double' THEN s.value_double
                                   WHEN 'long' THEN CAST(s.value_long AS REAL)
                                   WHEN 'bool' THEN CAST(s.value_bool AS REAL)
                                   ELSE NULL
                               END AS numeric_value
                        FROM signals s
                        JOIN dirty d
                          ON d.global_asset_id = s.global_asset_id
                         AND d.signal_id = s.signal_id
                         AND d.bucket_ts_us = (s.source_ts_us / $bucket) * $bucket
                    ), ranked AS (
                        SELECT *, ROW_NUMBER() OVER (
                            PARTITION BY global_asset_id, signal_id, bucket_ts_us
                            ORDER BY source_ts_us DESC, envelope_id DESC
                        ) AS rn
                        FROM raw
                    ), buckets AS (
                        SELECT global_asset_id, signal_id, bucket_ts_us,
                               COUNT(*) AS count, AVG(numeric_value) AS mean,
                               MIN(numeric_value) AS min_v, MAX(numeric_value) AS max_v
                        FROM raw
                        GROUP BY global_asset_id, signal_id, bucket_ts_us
                    )
                    INSERT OR REPLACE INTO %s
                        (global_asset_id, signal_id, bucket_ts_us, count, mean, min_v, max_v, last_v,
                         value_type, last_double, last_long, last_string, last_bool, last_quality, unit)
                    SELECT b.global_asset_id, b.signal_id, b.bucket_ts_us,
                           b.count, b.mean, b.min_v, b.max_v,
                           CASE r.value_type
                               WHEN 'double' THEN r.value_double
                               WHEN 'long' THEN CAST(r.value_long AS REAL)
                               WHEN 'bool' THEN CAST(r.value_bool AS REAL)
                               ELSE NULL
                           END,
                           r.value_type, r.value_double, r.value_long, r.value_string, r.value_bool,
                           r.quality, r.unit
                    FROM buckets b
                    JOIN ranked r
                      ON r.global_asset_id = b.global_asset_id
                     AND r.signal_id = b.signal_id
                     AND r.bucket_ts_us = b.bucket_ts_us
                     AND r.rn = 1
                    """ table
                command.Parameters.AddWithValue("$bucket", bucketSize) |> ignore
                command.ExecuteNonQuery()
            let refreshed = runBucket bucket1H "signals_1h" + runBucket bucket1D "signals_1d"
            use clear = conn.CreateCommand()
            clear.Transaction <- tx
            clear.CommandText <-
                "DELETE FROM downsample_dirty
                 WHERE envelope_id IN (SELECT envelope_id FROM dirty_batch)"
            clear.ExecuteNonQuery() |> ignore
            tx.Commit()
            refreshed, selected

type DownsampleOptions = {
    Enabled: bool
    SweepIntervalMs: int
    LookbackHours: int
}

[<RequireQualifiedAccess>]
module DownsampleOptions =
    let fromEnvironment () =
        let enabled =
            match Environment.GetEnvironmentVariable "DS2_DOWNSAMPLE_ENABLED" with
            | null | "" -> true
            | value -> match Boolean.TryParse value with true, parsed -> parsed | _ -> true
        let interval =
            match Environment.GetEnvironmentVariable "DS2_DOWNSAMPLE_SWEEP_MS" with
            | null | "" -> 300_000
            | value -> match Int32.TryParse value with true, parsed -> max 10_000 parsed | _ -> 300_000
        let lookback =
            match Environment.GetEnvironmentVariable "DS2_DOWNSAMPLE_LOOKBACK_HOURS" with
            | null | "" -> 48
            | value -> match Int32.TryParse value with true, parsed -> max 1 parsed | _ -> 48
        { Enabled = enabled; SweepIntervalMs = interval; LookbackHours = lookback }

/// 늦게 도착한 샘플도 다음 sweep에 포함되도록 lookback 구간을 idempotent하게 재집계한다.
type DownsampleService(
        options: DownsampleOptions,
        paths: DataApiPaths,
        logger: ILogger<DownsampleService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        if not options.Enabled then
            logger.LogInformation("Downsample disabled (DS2_DOWNSAMPLE_ENABLED=false).")
        else
            try
                while not stoppingToken.IsCancellationRequested do
                    try
                        let now = DateTimeOffset.UtcNow
                        let fromUs = UnixTime.toMicroseconds (now.AddHours(float -options.LookbackHours))
                        let toUs = UnixTime.toMicroseconds now
                        let mutable rows = Downsample.runAggregation paths.TelemetryDb fromUs toUs
                        let mutable processed = 1
                        let mutable batches = 0
                        while processed > 0 && batches < 10 && not stoppingToken.IsCancellationRequested do
                            let refreshed, dirty = Downsample.runDirtyAggregation paths.TelemetryDb 10_000
                            rows <- rows + refreshed
                            processed <- dirty
                            batches <- batches + 1
                        if rows > 0 then
                            logger.LogInformation("Downsample sweep refreshed {Rows} aggregate rows.", rows)
                    with ex ->
                        logger.LogError(ex, "Downsample sweep failed; the next sweep will retry durable dirty buckets.")
                    do! Task.Delay(options.SweepIntervalMs, stoppingToken)
            with :? OperationCanceledException -> ()
    }
