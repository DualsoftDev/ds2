module Ds2.Collector.Tests.RetentionTests

open System
open System.IO
open Xunit
open Ds2.Core
open Ds2.Adapter.Common
open Ds2.Collector.DataApi
open Ds2.Collector.Sinks
open Microsoft.Data.Sqlite

[<Fact>]
let ``ISO retention durations parse including weeks`` () =
    Assert.Equal(Some(TimeSpan.FromDays 90.0), Retention.tryParseDuration "P90D")
    Assert.Equal(Some(TimeSpan.FromDays 14.0), Retention.tryParseDuration "P2W")
    Assert.True((Retention.tryParseDuration "invalid").IsNone)

[<Fact>]
let ``retention pruning removes only expired rows for one signal`` () = task {
    let directory = Path.Combine(Path.GetTempPath(), "ds2-retention-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory directory |> ignore
    let telemetry = Path.Combine(directory, "telemetry.db")
    let events = Path.Combine(directory, "events.db")
    try
        let sink = SqliteSinkWriter(telemetry, events)
        Downsample.ensureSchema telemetry
        let now = DateTimeOffset.UtcNow
        let sampleAt timestamp value : Envelope =
            { Envelope.NewSample(
                GlobalAssetId "urn:asset:test",
                SignalId "demo.temperature",
                timestamp,
                ValueDouble value,
                None,
                "test") with SourceTimestamp = timestamp }
        let! inserted = sink.WriteBatchAsync [ sampleAt (now.AddDays -40.0) 1.0; sampleAt now 2.0 ]
        Assert.Equal(2, inserted)
        let eventAt timestamp payload =
            Envelope.NewEvent(
                GlobalAssetId "urn:asset:test",
                SignalId "demo.temperature",
                timestamp,
                "urn:event:test",
                payload,
                "test")
        let! eventInserted = sink.WriteBatchAsync [ eventAt (now.AddDays -40.0) "{\"old\":true}"; eventAt now "{\"new\":true}" ]
        Assert.Equal(2, eventInserted)
        let fromUs = now.AddDays(-41.0).ToUnixTimeMilliseconds() * 1000L
        let toUs = now.AddDays(1.0).ToUnixTimeMilliseconds() * 1000L
        Assert.True(Downsample.runAggregation telemetry fromUs toUs >= 4)

        let resolution = {
            GlobalAssetId = "urn:asset:test"
            SignalId = "demo.temperature"
            DefaultTable = "signals"
            Retention = Some "P30D"
        }
        // raw + hourly + daily + durable dirty marker
        Assert.Equal(5, Retention.pruneSignalIncludingEvents telemetry events now resolution)
        let rows =
            sink.QuerySignals(
                resolution.GlobalAssetId,
                resolution.SignalId,
                now.AddYears(-1).ToUnixTimeMilliseconds() * 1000L,
                now.AddMinutes(1.0).ToUnixTimeMilliseconds() * 1000L)
        Assert.Single(rows) |> ignore
        use connection = new SqliteConnection($"Data Source={telemetry};Mode=ReadOnly;Pooling=False")
        connection.Open()
        for table in [ "signals_1h"; "signals_1d" ] do
            use command = connection.CreateCommand()
            command.CommandText <- $"SELECT COUNT(*) FROM {table} WHERE global_asset_id=$g AND signal_id=$s"
            command.Parameters.AddWithValue("$g", resolution.GlobalAssetId) |> ignore
            command.Parameters.AddWithValue("$s", resolution.SignalId) |> ignore
            Assert.Equal(1L, command.ExecuteScalar() :?> int64)
        use eventConnection = new SqliteConnection($"Data Source={events};Mode=ReadOnly;Pooling=False")
        eventConnection.Open()
        use eventCommand = eventConnection.CreateCommand()
        eventCommand.CommandText <- "SELECT COUNT(*) FROM events WHERE global_asset_id=$g AND signal_id=$s"
        eventCommand.Parameters.AddWithValue("$g", resolution.GlobalAssetId) |> ignore
        eventCommand.Parameters.AddWithValue("$s", resolution.SignalId) |> ignore
        Assert.Equal(1L, eventCommand.ExecuteScalar() :?> int64)
    finally
        if Directory.Exists directory then Directory.Delete(directory, true)
}
