namespace Ds2.Collector.Sinks

open System
open System.Globalization
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Xml
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Ds2.Collector.DataApi
open Ds2.Adapter.Common

type RetentionOptions = {
    Enabled: bool
    SweepIntervalMs: int
}

[<RequireQualifiedAccess>]
module RetentionOptions =
    let fromEnvironment () =
        let enabled =
            match Environment.GetEnvironmentVariable "DS2_RETENTION_ENABLED" with
            | null | "" -> true
            | value -> match Boolean.TryParse value with true, parsed -> parsed | _ -> true
        let interval =
            match Environment.GetEnvironmentVariable "DS2_RETENTION_SWEEP_MS" with
            | null | "" -> 3_600_000
            | value -> match Int32.TryParse value with true, parsed -> max 10_000 parsed | _ -> 3_600_000
        { Enabled = enabled; SweepIntervalMs = interval }

[<RequireQualifiedAccess>]
module Retention =
    let private weekPattern = Regex("^P([0-9]+)W$", RegexOptions.CultureInvariant)

    /// Core validator가 허용하는 ISO-8601 기간을 실제 TimeSpan으로 변환한다.
    let tryParseDuration (value: string) : TimeSpan option =
        if String.IsNullOrWhiteSpace value then None
        else
            let week = weekPattern.Match value
            if week.Success then
                match Int32.TryParse(week.Groups.[1].Value, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, count when count > 0 -> Some(TimeSpan.FromDays(float count * 7.0))
                | _ -> None
            else
                try
                    let duration = XmlConvert.ToTimeSpan value
                    if duration > TimeSpan.Zero then Some duration else None
                with _ -> None

    let pruneSignal
        (telemetryDb: string)
        (nowUtc: DateTimeOffset)
        (resolution: SeriesResolution) : int =
        match resolution.Retention |> Option.bind tryParseDuration with
        | None -> 0
        | Some retention ->
            let cutoffUs = UnixTime.toMicroseconds (nowUtc - retention)
            use connection = new SqliteConnection($"Data Source={telemetryDb};Pooling=False")
            connection.Open()
            use transaction = connection.BeginTransaction()
            let delete table timestampColumn bucketWidthUs =
                use command = connection.CreateCommand()
                command.Transaction <- transaction
                command.CommandText <-
                    $"DELETE FROM {table}
                      WHERE global_asset_id = $gaid AND signal_id = $signal
                        AND {timestampColumn} + $bucketWidth <= $cutoff"
                command.Parameters.AddWithValue("$gaid", resolution.GlobalAssetId) |> ignore
                command.Parameters.AddWithValue("$signal", resolution.SignalId) |> ignore
                command.Parameters.AddWithValue("$cutoff", cutoffUs) |> ignore
                command.Parameters.AddWithValue("$bucketWidth", bucketWidthUs) |> ignore
                command.ExecuteNonQuery()
            let deleted =
                delete "signals" "source_ts_us" 1L
                + delete "signals_1h" "bucket_ts_us" 3_600_000_000L
                + delete "signals_1d" "bucket_ts_us" 86_400_000_000L
                + delete "downsample_dirty" "source_ts_us" 1L
            transaction.Commit()
            deleted

    /// CollectionPolicy retention을 telemetry와 event history에 함께 적용한다.
    let pruneSignalIncludingEvents
        (telemetryDb: string)
        (eventsDb: string)
        (nowUtc: DateTimeOffset)
        (resolution: SeriesResolution) : int =
        let telemetryDeleted = pruneSignal telemetryDb nowUtc resolution
        match resolution.Retention |> Option.bind tryParseDuration with
        | None -> telemetryDeleted
        | Some retention ->
            let cutoffUs = UnixTime.toMicroseconds (nowUtc - retention)
            use connection = new SqliteConnection($"Data Source={eventsDb};Pooling=False")
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <-
                "DELETE FROM events
                 WHERE global_asset_id = $gaid AND signal_id = $signal AND source_ts_us < $cutoff"
            command.Parameters.AddWithValue("$gaid", resolution.GlobalAssetId) |> ignore
            command.Parameters.AddWithValue("$signal", resolution.SignalId) |> ignore
            command.Parameters.AddWithValue("$cutoff", cutoffUs) |> ignore
            telemetryDeleted + command.ExecuteNonQuery()

/// UA에서 발견한 신호별 retention을 주기적으로 SQLite raw history에 적용한다.
type RetentionService(
        options: RetentionOptions,
        registry: SeriesIdRegistry,
        paths: DataApiPaths,
        logger: ILogger<RetentionService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        if not options.Enabled then
            logger.LogInformation("CollectionPolicy retention disabled (DS2_RETENTION_ENABLED=false).")
        else
            try
                while not stoppingToken.IsCancellationRequested do
                    let mutable deleted = 0
                    for resolution in registry.ListAll() do
                        try
                            deleted <- deleted + Retention.pruneSignalIncludingEvents
                                paths.TelemetryDb paths.EventsDb DateTimeOffset.UtcNow resolution
                        with ex ->
                            logger.LogError(
                                ex,
                                "CollectionPolicy retention failed for asset={Asset} signalId={SignalId}; next sweep will retry.",
                                resolution.GlobalAssetId,
                                resolution.SignalId)
                    if deleted > 0 then
                        logger.LogInformation("CollectionPolicy retention sweep deleted {DeletedRows} history rows.", deleted)
                    do! Task.Delay(options.SweepIntervalMs, stoppingToken)
            with :? OperationCanceledException -> ()
    }
