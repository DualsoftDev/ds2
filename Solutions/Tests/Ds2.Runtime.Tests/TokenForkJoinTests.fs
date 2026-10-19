module Ds2.Runtime.Tests.TokenForkJoinTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.TestKit.ModelBuilder
open Ds2.Runtime.Model
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core

// FS1 CAT-11-R06 — 같은 제품이 갈라졌다 다시 합치면 동기화이지 두 제품이 아니다.
// `P > (A, B); (A, B) => C;` 에서 P 의 토큰은 A·B 두 가지에 같은 값으로 퍼지고 C 에서 다시 만난다.
// 먼저 끝난 가지의 토큰이 C 로 들어가면, 늦게 끝난 가지의 사본은 막을 것이 아니라 합쳐야 한다.
// 막으면 그 가지의 Work 가 토큰을 쥔 채 다시 준비(homing)를 끝내지 못해 영구히 선다(V&V 20·23, #8934).

let private model () =
    let store = createStore ()
    let project = addProject store "갈래합류"
    let system = addSystem store "라인" project.Id true
    let flow = addFlow store "공정" system.Id
    let p = addWork store "투입" flow.Id
    let a = addWork store "좌" flow.Id
    let b = addWork store "우" flow.Id
    let c = addWork store "합류" flow.Id
    p.TokenRole <- TokenRole.Source
    c.TokenRole <- TokenRole.Sink
    addWorkArrow store system.Id p.Id a.Id ArrowType.Start |> ignore
    addWorkArrow store system.Id p.Id b.Id ArrowType.Start |> ignore
    // `(좌, 우) => 합류;` — 합류 시작이 좌·우를 모두 되돌린다(StartReset 둘, 한 AND 진입).
    addWorkArrow store system.Id a.Id c.Id ArrowType.StartReset |> ignore
    addWorkArrow store system.Id b.Id c.Id ArrowType.StartReset |> ignore
    store, p, a, b, c

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``갈라졌다 합친 같은 제품의 두 가지는 합류가 되돌린 뒤 둘 다 다시 준비된다(ModelOnly·Legacy)`` explicitOptions =
    let store, p, a, b, c = model ()
    let index = SimIndex.build store 10
    let options = if explicitOptions then SimulationExecutionOptions.Explicit else SimulationExecutionOptions.Legacy
    use engine = new EventDrivenEngine(index, RuntimeMode.Simulation, None, options) :> ISimulationEngine
    engine.StartWithHomingPhase() |> ignore
    // entry(Source) 는 토큰을 넣어 세운다 — WASM 세션의 F5 와 같은 경로.
    engine.StepWithSourcePriming(p.Id, true) |> ignore
    let step () =
        engine.AdvanceSimulationTo(engine.CurrentTimeMs + 50L)
        engine.Step() |> ignore
    let mutable n = 0
    while engine.GetWorkState(c.Id) <> Some Status4.Finish && n < 40 do
        step ()
        n <- n + 1
    Assert.True(engine.GetWorkState(c.Id) = Some Status4.Finish,
        $"합류가 끝나지 않았습니다. 좌={engine.GetWorkState(a.Id)} 우={engine.GetWorkState(b.Id)} 합류={engine.GetWorkState(c.Id)}")
    for _ in 1 .. 20 do step ()
    // 수정 전에는 늦게 끝난 가지 하나가 Homing 에 남았다.
    Assert.Equal(Some Status4.Ready, engine.GetWorkState(a.Id))
    Assert.Equal(Some Status4.Ready, engine.GetWorkState(b.Id))
    engine.Stop()
