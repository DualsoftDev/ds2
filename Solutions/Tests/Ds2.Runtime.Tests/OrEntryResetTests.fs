module Ds2.Runtime.Tests.OrEntryResetTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.TestKit.ModelBuilder
open Ds2.Runtime.Model
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core

// FS1 CAT-05-R04 — 자동 Ref 는 같은 Work 의 다른 진입 주소일 뿐 따로 시작하지 않는다.
// `P1 > C; P2 > C;` 는 C(원본, P1 진입)와 `C@2`(Ref, P2 진입)로 저장된다. Work 이름으로 쓴 Reset
// `C |> P2` 의 원인은 C 의 시작이므로, C 가 `C@2` 진입으로 시작해도 걸어야 한다(#8936 재현 중 발견).
// 반면 `P2 => C` 의 되돌림은 그 진입 주소(`C@2`)에 붙어 그 진입이 선택될 때만 건다(CAT-05-R07).

type private Shape =
    | NamedReset      // P1 > C; P2 > C; C |> P1; C |> P2;
    | EntryStartReset // P1 => C; P2 => C;

let private model shape =
    let store = createStore ()
    let project = addProject store "두진입"
    let system = addSystem store "라인" project.Id true
    let flow = addFlow store "공정" system.Id
    let p1 = addWork store "P1" flow.Id
    let p2 = addWork store "P2" flow.Id
    let c = addWork store "C" flow.Id
    let c2 = addWork store "C@2" flow.Id
    c2.ReferenceOf <- Some c.Id
    c2.AutoEntry <- true
    // P1 이 늦게 끝나 C 는 P2 진입(`C@2`)으로 먼저 시작한다.
    p1.Duration <- Some(System.TimeSpan.FromMilliseconds 150.0)
    p2.Duration <- Some(System.TimeSpan.FromMilliseconds 50.0)
    c.Duration <- Some(System.TimeSpan.FromMilliseconds 50.0)
    p1.TokenRole <- TokenRole.Source
    p2.TokenRole <- TokenRole.Source
    c.TokenRole <- TokenRole.Sink
    match shape with
    | NamedReset ->
        addWorkArrow store system.Id p1.Id c.Id ArrowType.Start |> ignore
        addWorkArrow store system.Id p2.Id c2.Id ArrowType.Start |> ignore
        addWorkArrow store system.Id c.Id p1.Id ArrowType.Reset |> ignore
        addWorkArrow store system.Id c.Id p2.Id ArrowType.Reset |> ignore
    | EntryStartReset ->
        addWorkArrow store system.Id p1.Id c.Id ArrowType.StartReset |> ignore
        addWorkArrow store system.Id p2.Id c2.Id ArrowType.StartReset |> ignore
    store, p1, p2, c

let private runUntilCFinish shape explicitOptions =
    let store, p1, p2, c = model shape
    let index = SimIndex.build store 10
    let options = if explicitOptions then SimulationExecutionOptions.Explicit else SimulationExecutionOptions.Legacy
    let engine = new EventDrivenEngine(index, RuntimeMode.Simulation, None, options) :> ISimulationEngine
    let homed = System.Collections.Generic.HashSet<System.Guid>()
    engine.WorkStateChanged.Add(fun e -> if e.NewState = Status4.Homing then homed.Add(e.WorkGuid) |> ignore)
    engine.StartWithHomingPhase() |> ignore
    engine.StepWithSourcePriming(p1.Id, true) |> ignore
    engine.StepWithSourcePriming(p2.Id, true) |> ignore
    let step () =
        engine.AdvanceSimulationTo(engine.CurrentTimeMs + 10L)
        engine.Step() |> ignore
    let mutable n = 0
    while engine.GetWorkState(c.Id) <> Some Status4.Finish && n < 100 do
        step ()
        n <- n + 1
    Assert.True(engine.GetWorkState(c.Id) = Some Status4.Finish, $"C 가 끝나지 않았습니다. C={engine.GetWorkState(c.Id)}")
    engine, homed, p1, p2

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``Work 이름으로 쓴 Reset 은 C 가 Ref 진입으로 시작해도 건다(ModelOnly·Legacy)`` explicitOptions =
    let engine, homed, _, p2 = runUntilCFinish NamedReset explicitOptions
    // 수정 전에는 C 가 `C@2` 로 시작해 `C |> P2` 가 빠졌고 P2 는 Finish 로 남았다.
    Assert.True(homed.Contains p2.Id, "C 의 시작이 P2 를 되돌리지 않았습니다.")
    engine.Stop()
    engine.Dispose()

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``StartReset 의 되돌림은 그 진입이 선택될 때만 건다(ModelOnly·Legacy)`` explicitOptions =
    let engine, homed, p1, p2 = runUntilCFinish EntryStartReset explicitOptions
    // C 는 P2 진입으로 시작했다 — 그 진입의 원인 P2 만 되돌린다. P1 은 그때 아직 진행 중이었다.
    Assert.True(homed.Contains p2.Id, "선택된 진입의 원인 P2 가 되돌려지지 않았습니다.")
    Assert.False(homed.Contains p1.Id, "선택되지 않은 진입의 원인 P1 이 되돌려졌습니다.")
    engine.Stop()
    engine.Dispose()

// FS1 CAT-05-R07 — 토큰/결과는 선택 진입의 근거를 쓴다. 두 entry 가 같은 걸음에 끝나 `P1 => C; P2 => C;` 의
// 두 진입이 함께 서면, C 는 **제 자리에 들어온 토큰을 넘긴 원인**의 진입으로 시작해야 한다. 전에는 선언 순서상 앞선
// 진입이 남의 토큰으로 시작해 제 원인을 되돌렸고, 그 원인은 토큰을 쥔 채 homing 에 갇혔다(#8936 재현).
[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``두 진입이 함께 서면 토큰을 넘긴 원인의 진입으로 시작하고 아무도 homing 에 갇히지 않는다(ModelOnly·Legacy)`` explicitOptions =
    let store = createStore ()
    let project = addProject store "두진입"
    let system = addSystem store "라인" project.Id true
    let flow = addFlow store "공정" system.Id
    let p1 = addWork store "P1" flow.Id
    let p2 = addWork store "P2" flow.Id
    let c = addWork store "C" flow.Id
    let c2 = addWork store "C@2" flow.Id
    c2.ReferenceOf <- Some c.Id
    c2.AutoEntry <- true
    for w in [ p1; p2; c ] do w.Duration <- Some(System.TimeSpan.FromMilliseconds 50.0)
    p1.TokenRole <- TokenRole.Source
    p2.TokenRole <- TokenRole.Source
    c.TokenRole <- TokenRole.Sink
    addWorkArrow store system.Id p1.Id c.Id ArrowType.StartReset |> ignore
    addWorkArrow store system.Id p2.Id c2.Id ArrowType.StartReset |> ignore
    let index = SimIndex.build store 10
    let options = if explicitOptions then SimulationExecutionOptions.Explicit else SimulationExecutionOptions.Legacy
    use engine = new EventDrivenEngine(index, RuntimeMode.Simulation, None, options) :> ISimulationEngine
    engine.StartWithHomingPhase() |> ignore
    engine.StepWithSourcePriming(p1.Id, true) |> ignore
    engine.StepWithSourcePriming(p2.Id, true) |> ignore
    for _ in 1 .. 60 do
        engine.AdvanceSimulationTo(engine.CurrentTimeMs + 10L)
        engine.Step() |> ignore
    Assert.Equal(Some Status4.Finish, engine.GetWorkState(c.Id))
    let s1, s2 = engine.GetWorkState(p1.Id), engine.GetWorkState(p2.Id)
    // 선택된 진입의 원인 하나는 되돌려져 Ready, 다른 하나는 제 토큰을 쥐고 Finish 로 C 의 다음 준비를 기다린다.
    Assert.True(s1 <> Some Status4.Homing && s2 <> Some Status4.Homing, $"homing 에 갇힘: P1={s1} P2={s2}")
    Assert.True((s1 = Some Status4.Ready && s2 = Some Status4.Finish) || (s1 = Some Status4.Finish && s2 = Some Status4.Ready),
        $"한 원인만 되돌려져야 합니다: P1={s1} P2={s2}")
    engine.Stop()
