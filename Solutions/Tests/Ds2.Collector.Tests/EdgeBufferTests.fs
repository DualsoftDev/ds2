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

[<Fact>]
let ``AckMany removes a persisted batch atomically`` () =
    let buf, dir = mkTemp()
    try
        let first = sample "urn:x" "line.a.first" 1.0
        let second = sample "urn:x" "line.a.second" 2.0
        buf.Enqueue first
        buf.Enqueue second
        buf.AckMany [ first.EnvelopeId; second.EnvelopeId ]
        Assert.Equal(0, buf.PendingCount())
    finally Directory.Delete(dir, true)

[<Fact>]
let ``pending envelopes survive buffer recreation`` () =
    let buf, dir = mkTemp()
    try
        let envelope = sample "urn:x" "line.a.durable" 1.0
        buf.Enqueue envelope
        let reopened = SqliteEdgeBuffer(Path.Combine(dir, "outbox.db"))
        let due = reopened.PullDue 10
        Assert.Single(due) |> ignore
        Assert.Equal(envelope.EnvelopeId, due.[0].EnvelopeId)
    finally Directory.Delete(dir, true)

[<Fact>]
let ``sample capacity reserves room for an event`` () =
    let dir = Path.Combine(Path.GetTempPath(), "ds2-edge-capacity-" + Guid.NewGuid().ToString("N"))
    let db = Path.Combine(dir, "outbox.db")
    let buf = SqliteEdgeBuffer(db, maxPendingRows = 2L, maxPayloadBytes = 1_000_000L)
    try
        let first = sample "urn:x" "line.a.first" 1.0
        buf.Enqueue first
        Assert.Throws<IOException>(fun () ->
            buf.Enqueue(sample "urn:x" "line.a.second" 2.0)) |> ignore

        let event = ev "urn:x" "line.a.event" """{"code":"reserved"}"""
        buf.Enqueue event
        let rows, bytes = buf.PendingUsage()
        Assert.Equal(2L, rows)
        Assert.True(bytes > 0L)
        Assert.Equal(2, buf.PendingCount())
    finally Directory.Delete(dir, true)

[<Fact>]
let ``buffer usage stays correct after acknowledge`` () =
    let buf, dir = mkTemp()
    try
        let envelope = sample "urn:x" "line.a.usage" 1.0
        buf.Enqueue envelope
        let rowsBefore, bytesBefore = buf.PendingUsage()
        Assert.Equal(1L, rowsBefore)
        Assert.True(bytesBefore > 0L)
        buf.Ack envelope.EnvelopeId
        let rowsAfter, bytesAfter = buf.PendingUsage()
        Assert.Equal(0L, rowsAfter)
        Assert.Equal(0L, bytesAfter)
    finally Directory.Delete(dir, true)
