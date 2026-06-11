module Ds2.Store.Editor.Tests.EngineRealTimeClockTests

open System.Threading
open Xunit
open Ds2.Core
open Ds2.Store.Editor.Tests.TestHelpers
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core

// AdvanceSimulationToRealTime — Hub(SignalR) 스레드가 forced transition 을 drain 하기 전에
// 엔진 시계를 벽시계 타깃으로 당기는 경로. 이게 없으면 Monitoring 에서 전이 ClockMs 가
// 마지막 loop wake 시각(stale)으로 stamp 되어 간트 막대 길이가 0~수초로 왜곡된다.

let private buildEngine () =
    let store = createStore ()
    setupBasicHierarchy store |> ignore
    let index = SimIndex.build store 10
    new EventDrivenEngine(index, RuntimeMode.Monitoring) :> ISimulationEngine

[<Fact>]
let ``AdvanceSimulationToRealTime advances clock to wall pace while running`` () =
    use engine = buildEngine ()
    engine.Start()
    // 예약 이벤트가 없으면 simulationLoop 는 잠들고 시계는 정지한다(버그 조건 재현).
    Thread.Sleep(150)
    engine.AdvanceSimulationToRealTime()
    Assert.True(
        engine.CurrentTimeMs >= 80L,
        $"CurrentTimeMs={engine.CurrentTimeMs}ms — 벽시계(~150ms)를 추적하지 못함(stale clock)")

[<Fact>]
let ``AdvanceSimulationToRealTime keeps clock frozen when not running`` () =
    use engine = buildEngine ()
    // Start 안 함 — Running 이 아니면 시계 전진 없이 due drain 만 해야 한다.
    Thread.Sleep(60)
    engine.AdvanceSimulationToRealTime()
    Assert.Equal(0L, engine.CurrentTimeMs)
