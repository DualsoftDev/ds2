module Ds2.Collector.Tests.CollectorRuntimeStateTests

open Ds2.Collector
open Xunit

[<Fact>]
let ``readiness requires a healthy writer and configured UA connection`` () =
    let state = CollectorRuntimeState()
    state.MarkStarted(true)
    Assert.False(state.Snapshot(0).Ready)

    state.MarkConnected()
    Assert.True(state.Snapshot(0).Ready)

    state.MarkWriteFailure(3, "disk busy")
    let failed = state.Snapshot(3)
    Assert.False(failed.Ready)
    Assert.Equal(1L, failed.WriteFailures)
    Assert.Equal(3L, failed.RetriedEnvelopes)

    state.MarkPersisted(3)
    let recovered = state.Snapshot(0)
    Assert.True(recovered.Ready)
    Assert.True(recovered.WriterHealthy)
    Assert.Equal(3L, recovered.AcknowledgedEnvelopes)

[<Fact>]
let ``disabled subscription remains ready while durable writer is healthy`` () =
    let state = CollectorRuntimeState()
    state.MarkStarted(false)
    let snapshot = state.Snapshot(2)
    Assert.True(snapshot.Ready)
    Assert.False(snapshot.SubscriptionEnabled)
    Assert.Equal(2, snapshot.PendingEnvelopes)
