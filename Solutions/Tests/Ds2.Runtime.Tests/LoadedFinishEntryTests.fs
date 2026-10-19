module Ds2.Runtime.Tests.LoadedFinishEntryTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.TestKit.ModelBuilder
open Ds2.Runtime.Model
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core

// FS1 CAT-03-R04 — 자동 진입에는 그 진입에 현재 Run 의 실제 완료 근거가 하나 이상 있어야 한다.
// `finished` 로 적재한 완료(LoadedFinishWorks)만으로는 서지 않는다(#8927, 예제 07).
// 수동 강제 시작 판정(predecessorSatisfied)은 그대로다 — 사용자 요청은 그 자체로 사건이다.

let private model joint =
    let store = createStore ()
    let project = addProject store "초기완료"
    let system = addSystem store "설비" project.Id true
    let flow = addFlow store "검사공정" system.Id
    let origin = addWork store "원점확인" flow.Id
    let produce = addWork store "생산시작" flow.Id
    let inspect = addWork store "검사" flow.Id
    let props = SimulationWorkProperties()
    props.IsFinished <- true
    origin.SetSimulationProperties props
    let link (source: Work) = addWorkArrow store system.Id source.Id inspect.Id ArrowType.Start |> ignore
    link origin
    if joint then link produce
    store, origin, produce, inspect

let private loaded (index: SimIndex) (origin: Work) =
    SimState.create 10 index.AllWorkGuids Seq.empty Seq.empty
    |> SimState.setWorkState origin.Id Status4.Finish
    |> SimState.markLoadedFinish [ origin.Id ]

[<Fact>]
let ``적재된 finished 만으로는 뒷 Work 가 자동 진입하지 않는다`` () =
    let store, origin, _, inspect = model false
    let index = SimIndex.build store 10
    let state = loaded index origin
    Assert.True(WorkConditionChecker.predecessorSatisfied index state inspect.Id)
    Assert.False(WorkConditionChecker.autoEntrySatisfied index state inspect.Id)
    Assert.False(WorkConditionChecker.canStartWork index state inspect.Id)

[<Fact>]
let ``적재 완료와 실제 완료가 함께인 AND 진입은 선다`` () =
    let store, origin, produce, inspect = model true
    let index = SimIndex.build store 10
    let state = loaded index origin
    Assert.False(WorkConditionChecker.autoEntrySatisfied index state inspect.Id)
    let live = state |> SimState.setWorkState produce.Id Status4.Going |> SimState.setWorkState produce.Id Status4.Finish
    Assert.True(WorkConditionChecker.autoEntrySatisfied index live inspect.Id)

[<Fact>]
let ``적재 완료 Work 가 다시 전이하면 적재 표시가 사라진다`` () =
    let store, origin, _, inspect = model false
    let index = SimIndex.build store 10
    let rerun =
        loaded index origin
        |> SimState.setWorkState origin.Id Status4.Ready
        |> SimState.setWorkState origin.Id Status4.Going
        |> SimState.setWorkState origin.Id Status4.Finish
    Assert.True(rerun.LoadedFinishWorks.IsEmpty)
    Assert.True(WorkConditionChecker.autoEntrySatisfied index rerun inspect.Id)

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``엔진은 명시 finished 뒤 Work 를 자동 시작하지 않는다(ModelOnly·Legacy)`` explicitOptions =
    let store, origin, _, inspect = model false
    let index = SimIndex.build store 10
    let options = if explicitOptions then SimulationExecutionOptions.Explicit else SimulationExecutionOptions.Legacy
    use engine = new EventDrivenEngine(index, RuntimeMode.Simulation, None, options) :> ISimulationEngine
    // WASM SimSession 과 같은 시작 경로 — 초기 상태 적용은 StartWithHomingPhase 안에서 한다.
    engine.StartWithHomingPhase() |> ignore
    Assert.True(
        waitUntil 2000 (fun () -> engine.GetWorkState(origin.Id) = Some Status4.Finish),
        $"원점확인이 적재 완료로 시작하지 않았습니다. 원점확인={engine.GetWorkState(origin.Id)} 검사={engine.GetWorkState(inspect.Id)} 초기완료={SimIndex.findExplicitInitialFinishWorkGuids index |> Set.count}")
    for _ in 1 .. 20 do
        engine.AdvanceSimulationTo(engine.CurrentTimeMs + 50L)
        engine.Step() |> ignore
    Assert.Equal(Some Status4.Ready, engine.GetWorkState(inspect.Id))
    engine.Stop()
