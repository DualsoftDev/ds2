module Ds2.Adapter.Common.Tests.EdgeBufferTests

open System
open System.IO
open Ds2.Core
open Ds2.Adapter.Common
open Xunit

let private mkTemp () =
    let dir = Path.Combine(Path.GetTempPath(), "ds2-edge-" + Guid.NewGuid().ToString("N"))
    let db = Path.Combine(dir, "outbox.db")
    SqliteEdgeBuffer(db), dir

let private sample gaid sig' v : Envelope =
    Envelope.NewSample(GlobalAssetId gaid, SignalId sig', DateTimeOffset.UtcNow, ValueDouble v, None, "test-adapter")

let private ev gaid sig' payload : Envelope =
    Envelope.NewEvent(GlobalAssetId gaid, SignalId sig', DateTimeOffset.UtcNow,
                      "urn:opcfoundation:autoid:OpticalScanEventType", payload, "test-adapter")

[<Fact>]
let ``Enqueue then PullDue returns the envelope`` () =
    let buf, dir = mkTemp()
    try
        let e = sample "urn:x" "line.a.b" 1.0
        buf.Enqueue e
        Assert.Equal(1, buf.PendingCount())
        let due = buf.PullDue 10
        Assert.Single(due) |> ignore
        Assert.Equal(e.EnvelopeId, due.[0].EnvelopeId)
    finally Directory.Delete(dir, true)

[<Fact>]
let ``Ack removes envelope`` () =
    let buf, dir = mkTemp()
    try
        let e = sample "urn:x" "line.a.b" 1.0
        buf.Enqueue e
        buf.Ack e.EnvelopeId
        Assert.Equal(0, buf.PendingCount())
    finally Directory.Delete(dir, true)

[<Fact>]
let ``Requeue schedules retry with backoff`` () =
    let buf, dir = mkTemp()
    try
        let e = sample "urn:x" "line.a.b" 1.0
        buf.Enqueue e
        buf.Requeue(e.EnvelopeId, TimeSpan.FromMinutes 5.0)
        // Now not due (next retry 5 min in future)
        Assert.Empty(buf.PullDue 10)
        Assert.Equal(1, buf.PendingCount())
    finally Directory.Delete(dir, true)

[<Fact>]
let ``Event priority pulled before Sample`` () =
    let buf, dir = mkTemp()
    try
        let s = sample "urn:x" "line.a.sample" 1.0
        let e = ev "urn:x" "line.a.event" """{"code":"1"}"""
        buf.Enqueue s
        buf.Enqueue e
        let due = buf.PullDue 10
        Assert.Equal(2, List.length due)
        Assert.Equal(e.EnvelopeId, due.[0].EnvelopeId)   // Event first
        Assert.Equal(s.EnvelopeId, due.[1].EnvelopeId)
    finally Directory.Delete(dir, true)

[<Fact>]
let ``Enqueue is idempotent on same EnvelopeId`` () =
    let buf, dir = mkTemp()
    try
        let e = sample "urn:x" "line.a.b" 1.0
        buf.Enqueue e
        buf.Enqueue e
        Assert.Equal(1, buf.PendingCount())
    finally Directory.Delete(dir, true)
