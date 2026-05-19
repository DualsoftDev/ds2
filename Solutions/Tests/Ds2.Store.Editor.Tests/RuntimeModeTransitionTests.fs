module Ds2.Store.Editor.Tests.RuntimeModeTransitionTests

open Ds2.Core
open Ds2.Runtime.Engine.Core
open Xunit

[<Fact>]
let ``Simulation mode is accepted even without IO configured`` () =
    let d = RuntimeModeTransition.evaluate RuntimeMode.Simulation false false false
    Assert.True(d.Accepted)
    Assert.Equal(None, d.RejectionMessage)
    Assert.True(d.ShouldRestoreTray)  // Simulation 은 Monitoring 이 아니므로 트레이 정리
    Assert.False(d.ShouldDisableContinuousInjection)

[<Fact>]
let ``non-Simulation modes are rejected without IO and include mode name in message`` () =
    let modes = [ RuntimeMode.Control; RuntimeMode.Monitoring; RuntimeMode.VirtualPlant ]
    for mode in modes do
        let d = RuntimeModeTransition.evaluate mode false false false
        Assert.False(d.Accepted)
        let msg = d.RejectionMessage |> Option.defaultValue ""
        Assert.Contains(mode.ToString(), msg)
        Assert.Contains("I/O 매핑", msg)
        // 거부 시 cleanup 플래그는 의미 없으므로 false 로 고정.
        Assert.False(d.ShouldRestoreTray)
        Assert.False(d.ShouldDisableContinuousInjection)

[<Fact>]
let ``Monitoring mode keeps tray (no restore) when accepted`` () =
    let d = RuntimeModeTransition.evaluate RuntimeMode.Monitoring true false false
    Assert.True(d.Accepted)
    Assert.False(d.ShouldRestoreTray)  // Monitoring 모드는 트레이 유지

[<Fact>]
let ``Control + RealPlc disables continuous injection when toggled on`` () =
    let d = RuntimeModeTransition.evaluate RuntimeMode.Control true true true
    Assert.True(d.Accepted)
    Assert.True(d.ShouldDisableContinuousInjection)

[<Fact>]
let ``Control without RealPlc keeps continuous injection`` () =
    let d = RuntimeModeTransition.evaluate RuntimeMode.Control true false true
    Assert.True(d.Accepted)
    Assert.False(d.ShouldDisableContinuousInjection)

[<Fact>]
let ``Monitoring always disables continuous injection when on`` () =
    let d = RuntimeModeTransition.evaluate RuntimeMode.Monitoring true false true
    Assert.True(d.Accepted)
    Assert.True(d.ShouldDisableContinuousInjection)

[<Fact>]
let ``continuous injection flag does nothing when toggle is off`` () =
    let d = RuntimeModeTransition.evaluate RuntimeMode.Monitoring true true false
    Assert.True(d.Accepted)
    Assert.False(d.ShouldDisableContinuousInjection)

[<Fact>]
let ``VirtualPlant accepts and restores tray and keeps injection available`` () =
    let d = RuntimeModeTransition.evaluate RuntimeMode.VirtualPlant true false true
    Assert.True(d.Accepted)
    Assert.True(d.ShouldRestoreTray)
    Assert.False(d.ShouldDisableContinuousInjection)
