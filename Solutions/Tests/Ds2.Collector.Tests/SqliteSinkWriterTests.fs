module Ds2.Collector.Tests.SqliteSinkWriterTests

open System
open System.IO
open Ds2.Core
open Ds2.Adapter.Common
open Ds2.Collector.Sinks
open Xunit

let private mkSink () =
    let dir = Path.Combine(Path.GetTempPath(), "ds2-collector-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let t = Path.Combine(dir, "telemetry.db")
    let e = Path.Combine(dir, "events.db")
    SqliteSinkWriter(t, e), dir

let private sample gaid sig' v : Envelope =
    Envelope.NewSample(GlobalAssetId gaid, SignalId sig', DateTimeOffset.UtcNow, ValueDouble v, Some "rpm", "test-adapter")

let private ev gaid sig' payload : Envelope =
    Envelope.NewEvent(GlobalAssetId gaid, SignalId sig', DateTimeOffset.UtcNow,
                      "urn:opcfoundation:autoid:OpticalScanEventType", payload, "test-adapter")

[<Fact>]
let ``WriteBatch persists Sample envelopes`` () = task {
    let sink, dir = mkSink()
    try
        let iw = sink
        let envs = [ sample "urn:x" "line.a.b" 1.0; sample "urn:x" "line.a.b" 2.0 ]
        let! n = iw.WriteBatchAsync envs
        Assert.Equal(2, n)
    finally Directory.Delete(dir, true)
}

[<Fact>]
let ``Dedup: same EnvelopeId written twice returns 0 on second`` () = task {
    let sink, dir = mkSink()
    try
        let iw = sink
        let e = sample "urn:x" "line.a.b" 1.0
        let! n1 = iw.WriteBatchAsync [ e ]
        let! n2 = iw.WriteBatchAsync [ e ]
        Assert.Equal(1, n1)
        Assert.Equal(0, n2)
    finally Directory.Delete(dir, true)
}

[<Fact>]
let ``Events routed to events.db`` () = task {
    let sink, dir = mkSink()
    try
        let iw = sink
        let! n = iw.WriteBatchAsync [ ev "urn:x" "line.bcr05.code" """{"code":"123"}""" ]
        Assert.Equal(1, n)
    finally Directory.Delete(dir, true)
}

[<Fact>]
let ``QuerySignals returns inserted rows`` () = task {
    let sink, dir = mkSink()
    try
        let iw = sink
        let now = DateTimeOffset.UtcNow
        let mkAt v : Envelope = {
            Envelope.NewSample(GlobalAssetId "urn:x", SignalId "line.a.b", now, ValueDouble v, None, "test-adapter") with
                Value = ValueDouble v
        }
        let! _ = iw.WriteBatchAsync [ mkAt 1.0; mkAt 2.0; mkAt 3.0 ]
        let fromUs = now.AddMinutes(-1.0).ToUnixTimeMilliseconds() * 1000L
        let toUs = now.AddMinutes(1.0).ToUnixTimeMilliseconds() * 1000L
        let rows = sink.QuerySignals("urn:x", "line.a.b", fromUs, toUs)
        Assert.NotEmpty(rows)
    finally Directory.Delete(dir, true)
}
