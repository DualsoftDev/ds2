module Ds2.Collector.Tests.DownsampleTests

open System
open System.IO
open Ds2.Core
open Ds2.Adapter.Common
open Ds2.Collector.Sinks
open Xunit

[<Fact>]
let ``ensureSchema creates signals_1h and signals_1d tables`` () = task {
    let dir = Path.Combine(Path.GetTempPath(), "ds2-ds-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let telemetry = Path.Combine(dir, "telemetry.db")
    let events = Path.Combine(dir, "events.db")
    try
        let sink = SqliteSinkWriter(telemetry, events)
        Downsample.ensureSchema telemetry

        // Insert some samples across an hour.
        let baseNow = DateTimeOffset.UtcNow.AddMinutes(-30.0)
        let mkAt (offsetMin: int) (v: float) : Envelope =
            let ts = baseNow.AddMinutes(float offsetMin)
            { Envelope.NewSample(GlobalAssetId "urn:x", SignalId "line.a.b", ts, ValueDouble v, None, "t") with
                SourceTimestamp = ts }
        let envs = [ mkAt 0 1.0; mkAt 5 2.0; mkAt 15 3.0; mkAt 25 4.0 ]
        let! _ = sink.WriteBatchAsync envs

        let fromUs = baseNow.AddMinutes(-5.0).ToUnixTimeMilliseconds() * 1000L
        let toUs = baseNow.AddMinutes(35.0).ToUnixTimeMilliseconds() * 1000L
        let rows = Downsample.runAggregation telemetry fromUs toUs
        Assert.True(rows > 0, sprintf "expected downsample rows > 0, got %d" rows)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)
}
