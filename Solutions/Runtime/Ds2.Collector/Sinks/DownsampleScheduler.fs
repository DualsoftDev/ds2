namespace Ds2.Collector.Sinks

open System
open Microsoft.Data.Sqlite

/// Phase 6 · 시간 단위 aggregate → signals_1h · signals_1d 테이블 생성.
///
/// 실제 스케줄러는 IHostedService 로 wire-up. 여기서는 SQL 로직만.
module Downsample =

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
                PRIMARY KEY (global_asset_id, signal_id, bucket_ts_us)
            ) WITHOUT ROWID;
        """
        cmd.ExecuteNonQuery() |> ignore

    /// 1시간 bucket 크기 (microseconds).
    let private bucket1H = 3_600_000_000L
    let private bucket1D = 86_400_000_000L

    /// Raw signals → signals_1h · signals_1d 로 aggregate 실행.
    let runAggregation (telemetryDb: string) (fromUs: int64) (toUs: int64) : int =
        use conn = new SqliteConnection($"Data Source={telemetryDb};Pooling=False")
        conn.Open()
        use tx = conn.BeginTransaction()
        let runBucket bucketSize table =
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <-
                sprintf """
                INSERT OR REPLACE INTO %s (global_asset_id, signal_id, bucket_ts_us, count, mean, min_v, max_v, last_v)
                SELECT
                    global_asset_id,
                    signal_id,
                    (source_ts_us / $bucket) * $bucket AS bucket_ts_us,
                    COUNT(*)                            AS count,
                    AVG(value_double)                   AS mean,
                    MIN(value_double)                   AS min_v,
                    MAX(value_double)                   AS max_v,
                    (SELECT value_double FROM signals s2
                     WHERE s2.global_asset_id = s.global_asset_id
                       AND s2.signal_id = s.signal_id
                       AND s2.source_ts_us < ((s.source_ts_us / $bucket) * $bucket + $bucket)
                       AND s2.source_ts_us >= (s.source_ts_us / $bucket) * $bucket
                     ORDER BY s2.source_ts_us DESC LIMIT 1) AS last_v
                FROM signals s
                WHERE source_ts_us BETWEEN $f AND $t AND value_double IS NOT NULL
                GROUP BY global_asset_id, signal_id, bucket_ts_us
                """ table
            cmd.Parameters.AddWithValue("$bucket", bucketSize) |> ignore
            cmd.Parameters.AddWithValue("$f", fromUs) |> ignore
            cmd.Parameters.AddWithValue("$t", toUs) |> ignore
            cmd.ExecuteNonQuery()
        let n1 = runBucket bucket1H "signals_1h"
        let n2 = runBucket bucket1D "signals_1d"
        tx.Commit()
        n1 + n2
