module Ds2.Collector.Tests.RetentionTests

open System
open System.IO
open Xunit
open Ds2.Core
open Ds2.Adapter.Common
open Ds2.Collector.DataApi
open Ds2.Collector.Sinks

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

        let resolution = {
            GlobalAssetId = "urn:asset:test"
            SignalId = "demo.temperature"
            DefaultTable = "signals"
            Retention = Some "P30D"
        }
        Assert.Equal(1, Retention.pruneSignal telemetry now resolution)
        let rows =
            sink.QuerySignals(
                resolution.GlobalAssetId,
                resolution.SignalId,
                now.AddYears(-1).ToUnixTimeMilliseconds() * 1000L,
                now.AddMinutes(1.0).ToUnixTimeMilliseconds() * 1000L)
        Assert.Single(rows) |> ignore
    finally
        if Directory.Exists directory then Directory.Delete(directory, true)
}
