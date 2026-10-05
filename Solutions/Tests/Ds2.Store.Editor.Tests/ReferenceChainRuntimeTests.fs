module Ds2.Store.Editor.Tests.ReferenceChainRuntimeTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Model
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

let private workFixture chain =
    let store = createStore ()
    let project, _, flow, original = setupBasicHierarchy store
    original.Duration <- Some (TimeSpan.FromMilliseconds 100.)
    original.MinDuration <- Some (TimeSpan.FromMilliseconds 80.)
    original.MaxDuration <- Some (TimeSpan.FromMilliseconds 120.)
    let first = addWork store "First" flow.Id
    let second = addWork store "Second" flow.Id
    first.ReferenceOf <- Some original.Id
    second.ReferenceOf <- Some (if chain then first.Id else original.Id)
    store, project, flow, original, first, second

let private callFixture chain copiedBindings =
    let store = createStore ()
    let project, _, _, owner = setupBasicHierarchy store
    let device = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "Motion" device.Id
    let target = addWork store "Move" deviceFlow.Id
    target.Duration <- Some (TimeSpan.FromMilliseconds 100.)
    target.MinDuration <- Some (TimeSpan.FromMilliseconds 80.)
    target.MaxDuration <- Some (TimeSpan.FromMilliseconds 120.)
    let api = addApiDef store "Run" device.Id
    api.TxGuid <- Some target.Id
    api.RxGuid <- Some target.Id
    let original = store.AddCallWithLinkedApiDefs(owner.Id, "Device", "Run", [ api.Id ])
    let first = store.AddReferenceCall original
    let second = store.AddReferenceCall original
    store.Calls[second].ReferenceOf <- Some (if chain then first else original)
    if copiedBindings then
        // Studio compiler retains the original ApiCall IDs on aliases; editor
        // references may instead have empty ApiCalls. Both represent one event.
        for id in [ first; second ] do
            for binding in store.Calls[original].ApiCalls do store.Calls[id].ApiCalls.Add binding
    store, owner, target, api, original, first, second

let private runUntil (sim: ISimulationEngine) timeMs =
    sim.AdvanceSimulationTo sim.CurrentTimeMs
    while sim.CurrentTimeMs < timeMs do
        sim.AdvanceSimulationTo(min timeMs (sim.CurrentTimeMs + 10L))

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``Work direct and chained references share original duration state and identity`` chain =
    let store, _, _, original, first, second = workFixture chain
    let before = [ first.ReferenceOf; second.ReferenceOf ]
    let index = SimIndex.build store 10
    let ids = [ original.Id; first.Id; second.Id ]
    for id in ids do
        Assert.Equal(original.Id, Queries.resolveOriginalWorkId id store)
        Assert.Equal(original.Id, SimIndex.canonicalWorkGuid index id)
        Assert.Equal<Set<Guid>>(Set.ofList ids, SimIndex.referenceGroupOf index id |> Set.ofList)
        Assert.Equal<Set<Guid>>(Set.ofList ids, Queries.referenceGroupOf id store |> Set.ofList)
        Assert.Equal(100., index.WorkDuration[id])
        Assert.Equal(80, index.WorkDurationRange[id].MinMs)
        Assert.Equal(120, index.WorkDurationRange[id].MaxMs)
    use engine = new EventDrivenEngine(index, RuntimeMode.Simulation, None, SimulationExecutionOptions.Explicit)
    let sim = engine :> ISimulationEngine
    sim.ForceWorkState(second.Id, Status4.Going)
    runUntil sim 90L
    for id in ids do Assert.Equal(Some Status4.Going, sim.GetWorkState id)
    runUntil sim 100L
    for id in ids do Assert.Equal(Some Status4.Finish, sim.GetWorkState id)
    Assert.Equal<Guid option list>(before, [ first.ReferenceOf; second.ReferenceOf ])

[<Fact>]
let ``entry on terminal Work reference seeds the original reference group`` () =
    let store, _, _, original, first, second = workFixture true
    second.TokenRole <- TokenRole.Source
    let index = SimIndex.build store 10
    for id in [ original.Id; first.Id; second.Id ] do
        Assert.True(index.WorkTokenRole[id].HasFlag TokenRole.Source)
        Assert.True(SimIndex.isTokenSource index id)
    Assert.Contains(original.Id, index.TokenSourceGuids)

[<Fact>]
let ``duration reload follows the original through a chain without rewriting references`` () =
    let store, _, _, original, first, second = workFixture true
    let index = SimIndex.build store 10
    original.Duration <- Some (TimeSpan.FromMilliseconds 240.)
    original.MinDuration <- Some (TimeSpan.FromMilliseconds 210.)
    original.MaxDuration <- Some (TimeSpan.FromMilliseconds 260.)
    SimIndex.reloadDurations index Set.empty
    for id in [ original.Id; first.Id; second.Id ] do
        Assert.Equal(240., index.WorkDuration[id])
        Assert.Equal(210, index.WorkDurationRange[id].MinMs)
        Assert.Equal(260, index.WorkDurationRange[id].MaxMs)
    Assert.Equal(Some first.Id, second.ReferenceOf)

[<Theory>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
let ``Call direct and chained aliases execute once without inflating parent duration`` chain copiedBindings =
    let store, owner, target, _, original, first, second = callFixture chain copiedBindings
    let ids = [ original; first; second ]
    let index = SimIndex.build store 10
    Assert.Equal(Some 100, Queries.tryGetDeviceDurationMs owner.Id store)
    Assert.Equal(100., index.WorkDuration[owner.Id])
    for id in ids do
        Assert.Equal(original, Queries.resolveOriginalCallId id store)
        Assert.Equal(original, SimIndex.canonicalCallGuid index id)
        Assert.Equal<Set<Guid>>(Set.ofList ids, SimIndex.callReferenceGroupOf index id |> Set.ofList)
        Assert.Equal<Set<Guid>>(Set.ofList ids, Queries.callReferenceGroupOf id store |> Set.ofList)
        Assert.Equal<Guid list>(index.CallApiCallGuids[original], index.CallApiCallGuids[id])
    use engine = new EventDrivenEngine(index, RuntimeMode.Simulation, None, SimulationExecutionOptions.Explicit)
    let sim = engine :> ISimulationEngine
    let deviceEvents = ResizeArray<int64 * Status4>()
    sim.WorkStateChanged.Add(fun e -> if e.WorkGuid = target.Id then deviceEvents.Add(int64 e.Clock.TotalMilliseconds, e.NewState))
    sim.ForceWorkState(owner.Id, Status4.Going)
    runUntil sim 90L
    for id in ids do Assert.Equal(Some Status4.Going, sim.GetCallState id)
    runUntil sim 100L
    for id in ids do Assert.Equal(Some Status4.Finish, sim.GetCallState id)
    Assert.Equal(Some Status4.Finish, sim.GetWorkState owner.Id)
    Assert.Equal<(int64 * Status4) list>([ 0L, Status4.Going; 100L, Status4.Finish ], List.ofSeq deviceEvents)
    Assert.Equal(Some (if chain then first else original), store.Calls[second].ReferenceOf)

[<Fact>]
let ``reference-only parent resolves source Call binding conditions timeout and duration`` () =
    let store, owner, _, _, original, first, second = callFixture true false
    let flow = store.Flows[owner.ParentId]
    let other = addWork store "Other" flow.Id
    store.Calls[second].ParentId <- other.Id
    let properties = SimulationCallProperties()
    properties.CallType <- CallType.SkipIfCompleted
    properties.Timeout <- Some (TimeSpan.FromMilliseconds 250.)
    store.Calls[original].SetSimulationProperties properties
    let binding = store.Calls[original].ApiCalls[0].Id
    store.AddConditionWithApiCalls(original, ConditionType.AutoAux, [ binding ]) |> ignore
    let index = SimIndex.build store 10
    Assert.Equal(Some 100, Queries.tryGetDeviceDurationMs other.Id store)
    Assert.Equal(100., index.WorkDuration[other.Id])
    Assert.Equal(80, index.WorkDurationRange[other.Id].MinMs)
    Assert.Equal(120, index.WorkDurationRange[other.Id].MaxMs)
    Assert.Equal<Guid list>(index.CallApiCallGuids[original], index.CallApiCallGuids[second])
    Assert.Equal(index.CallAutoAuxConditions[original], index.CallAutoAuxConditions[second])
    Assert.Equal(CallType.SkipIfCompleted, index.CallTypeMap[second])
    Assert.Equal(TimeSpan.FromMilliseconds 250., index.CallTimeoutMap[second])
    Assert.Equal(other.Id, index.CallWorkGuid[second])
    Assert.Equal(Some first, store.Calls[second].ReferenceOf)

[<Fact>]
let ``Work reference chain inherits original child Calls and skip conditions`` () =
    let store, owner, _, _, originalCall, _, _ = callFixture true false
    let first = addWork store "First" owner.ParentId
    let second = addWork store "Second" owner.ParentId
    first.ReferenceOf <- Some owner.Id
    second.ReferenceOf <- Some first.Id
    store.AddWorkConditionWithApiCalls(owner.Id, ConditionType.SkipAction, [ store.Calls[originalCall].ApiCalls[0].Id ]) |> ignore
    let index = SimIndex.build store 10
    Assert.Equal<Guid list>(index.WorkCallGuids[owner.Id], index.WorkCallGuids[second.Id])
    Assert.Equal(index.WorkSkipActionConditions[owner.Id], index.WorkSkipActionConditions[second.Id])
    Assert.Equal(100., index.WorkDuration[second.Id])
    Assert.Equal(Some first.Id, second.ReferenceOf)

[<Fact>]
let ``distinct original Calls using the same API remain separate resource users`` () =
    let store, owner, _, api, _, _, _ = callFixture true true
    store.AddCallWithLinkedApiDefs(owner.Id, "AnotherAlias", "Run", [ api.Id ]) |> ignore
    Assert.Equal(Some 200, Queries.tryGetDeviceDurationMs owner.Id store)

[<Fact>]
let ``Group members keep independent state and durations`` () =
    let store = createStore ()
    let _, _, flow, first = setupBasicHierarchy store
    let second = addWork store "Second" flow.Id
    first.Duration <- Some (TimeSpan.FromMilliseconds 100.)
    second.Duration <- Some (TimeSpan.FromMilliseconds 200.)
    store.ConnectSelectionInOrder([ first.Id; second.Id ], ArrowType.Group) |> ignore
    let index = SimIndex.build store 10
    Assert.Equal(first.Id, SimIndex.canonicalWorkGuid index first.Id)
    Assert.Equal(second.Id, SimIndex.canonicalWorkGuid index second.Id)
    let state = StateManager(index, 10)
    state.ApplyWorkTransition(first.Id, Status4.Going, fun _ -> false) |> ignore
    Assert.Equal(Status4.Going, state.GetWorkState first.Id)
    Assert.Equal(Status4.Ready, state.GetWorkState second.Id)
    Assert.Equal(100., index.WorkDuration[first.Id])
    Assert.Equal(200., index.WorkDuration[second.Id])

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``Work Reference cycles and dangling targets reject before runtime`` dangling =
    let store, _, _, original, _, second = workFixture true
    if dangling then second.ReferenceOf <- Some (Guid.NewGuid())
    else original.ReferenceOf <- Some second.Id
    let error = Assert.Throws<InvalidOperationException>(fun () -> SimIndex.build store 10 |> ignore)
    Assert.Contains((if dangling then "target does not exist" else "Reference cycle"), error.Message)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``Call Reference cycles and dangling targets reject before runtime`` dangling =
    let store, _, _, _, original, _, second = callFixture true false
    if dangling then store.Calls[second].ReferenceOf <- Some (Guid.NewGuid())
    else store.Calls[original].ReferenceOf <- Some second
    let error = Assert.Throws<InvalidOperationException>(fun () -> SimIndex.build store 10 |> ignore)
    Assert.Contains((if dangling then "target does not exist" else "Reference cycle"), error.Message)

[<Fact>]
let ``deep Work Reference lookup is iterative and preserves every direct link`` () =
    let store, _, flow, original, _, _ = workFixture false
    let mutable last = original.Id
    for i in 1 .. 2048 do
        let work = addWork store $"Deep{i}" flow.Id
        work.ReferenceOf <- Some last
        last <- work.Id
    let direct = store.Works[last].ReferenceOf
    Assert.Equal(original.Id, Queries.resolveOriginalWorkId last store)
    Assert.Equal(direct, store.Works[last].ReferenceOf)

/// Work 레퍼런스가 뜻하는 것은 **OR** 이다 — 한 사건에 들어오는 길이 여럿이고,
/// 그중 **어느 하나**만 열려도 사건이 선다.
///
/// 이것을 증명하는 검사가 없었다. 「상태를 공유한다」까지만 재고 있었는데,
/// 공유는 OR 의 **결과**이지 OR 자체가 아니다. OR 이 성립하려면 레퍼런스가
/// **자기 선행을 따로 가질 수 있어야** 한다 — 그래야 길이 둘이 된다.
///
///     길A ──▶ 공유사건 ──▶ 뒷일
///     길B ──▶ 공유사건(ref)
///
/// 길B 만 끝내도 공유사건이 서야 한다. 길A 는 Ready 그대로여야 한다.
///
/// **엔진을 띄우지 않는다.** 처음엔 `EventDrivenEngine` 으로 재다가 전체 실행에서만
/// 떨어졌다 — 엔진 스레드가 「설 수 있는 Work」를 스스로 Going 으로 올려 강제 설정과
/// 경합했다. 단독 실행에서만 통과하는 검사는 **없느니만 못하다.**
/// 여기서 묻는 것은 스케줄링이 아니라 **조건 판정**이므로 상태를 손으로 짓고
/// `WorkConditionChecker` 를 직접 부른다.
[<Fact>]
let ``Work reference means OR - either inbound path can raise the shared event`` () =
    let store = createStore ()
    let _, _, flow, shared = setupBasicHierarchy store
    let pathA = addWork store "PathA" flow.Id
    let pathB = addWork store "PathB" flow.Id
    let after = addWork store "After" flow.Id
    let sharedRef = store.Works.[store.AddReferenceWork shared.Id]
    store.ConnectSelectionInOrder([ pathA.Id; shared.Id ], ArrowType.Start) |> ignore
    store.ConnectSelectionInOrder([ pathB.Id; sharedRef.Id ], ArrowType.Start) |> ignore
    store.ConnectSelectionInOrder([ shared.Id; after.Id ], ArrowType.Start) |> ignore

    let index = SimIndex.build store 10
    let ids = [ shared.Id; pathA.Id; pathB.Id; after.Id; sharedRef.Id ]
    let blank = SimState.create 10 ids [] [ flow.Id ]
    let withFinished guids = guids |> List.fold (fun st g -> SimState.setWorkState g Status4.Finish st) blank
    let canStart st id = WorkConditionChecker.canStartWorkPredOnly index st id

    // 선행이 서로 **다르게** 잡혀야 길이 둘이다 — 섞이면 이 검사는 뜻을 잃는다
    let preds id = SimIndex.findOrEmpty id index.WorkStartPreds
    Assert.Equal<Guid list>([ pathB.Id ], preds sharedRef.Id)
    Assert.Equal<Guid list>([ pathA.Id ], preds shared.Id)

    // 아직 아무 길도 안 열렸다 — 어느 쪽으로도 못 선다
    Assert.False(canStart blank shared.Id)
    Assert.False(canStart blank sharedRef.Id)
    Assert.False(canStart blank after.Id)

    // **길B 만** 끝냈다 — 레퍼런스는 자기 선행으로 서고, 원본은 못 선다
    let onlyB = withFinished [ pathB.Id ]
    Assert.True(canStart onlyB sharedRef.Id, "길B 가 끝났는데 레퍼런스가 서지 못한다")
    Assert.False(canStart onlyB shared.Id, "원본이 자기 선행(길A) 없이 섰다 — 레퍼런스가 선행을 덮어썼다")

    // ── 이것이 OR ──────────────────────────────────────────────────
    // 뒷일의 선행은 **원본 하나**뿐이다. 그런데 레퍼런스 쪽이 끝나도 뒷일이 선다.
    Assert.Equal<Guid list>([ shared.Id ], preds after.Id)
    Assert.True(canStart (withFinished [ sharedRef.Id ]) after.Id, "길B 로 온 완료를 뒷일이 못 본다")
    Assert.True(canStart (withFinished [ shared.Id ]) after.Id, "길A 로 온 완료를 뒷일이 못 본다")

    // 반대쪽도 본다 — **아무 길도 안 끝났으면** 뒷일은 서면 안 된다.
    // 이것이 없으면 위 둘은 「늘 참인 검사」일 수 있다.
    Assert.False(canStart (withFinished [ pathA.Id; pathB.Id ]) after.Id,
        "공유사건이 아직 안 끝났는데 뒷일이 선다 — OR 가 아니라 아무거나 통과시키고 있다")

/// Work 의 `skip when` 을 글로 열어도 되는가 — **교착 가설을 실측으로 가린다.**
///
/// 가설(DS2_CONDITION_TEXT_STRATEGY Stage 3): 건너뛴 Work 는 epoch 이 오르지 않아
/// 뒷일이 영영 서지 못한다. 추정으로 막거나 열지 않기로 했으므로 재어 본다.
///
/// 엔진 스레드는 쓰지 않는다 — 전에 `ForceWorkState` 와 경합해 간헐 실패했다.
/// StateManager 의 전이 한 지점과 선행 판정만 직접 두드린다.
[<Fact>]
let ``skipped Work finishes and its successor still starts - no epoch deadlock`` () =
    let store = createStore ()
    let project, _, flow, before = setupBasicHierarchy store
    let target = addWork store "Target" flow.Id
    let after = addWork store "After" flow.Id
    store.ConnectSelectionInOrder([ before.Id; target.Id; after.Id ], ArrowType.Start) |> ignore

    // 조건이 가리킬 신호 — 장치 Work 가 Finish 면 「켜짐」으로 읽힌다(IO 없는 모드).
    let device = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "Motion" device.Id
    let move = addWork store "Move" deviceFlow.Id
    move.Duration <- Some (TimeSpan.FromMilliseconds 10.)
    let api = addApiDef store "Run" device.Id
    api.TxGuid <- Some move.Id
    api.RxGuid <- Some move.Id
    let gate = store.AddCallWithLinkedApiDefs(before.Id, "Device", "Run", [ api.Id ])
    store.AddWorkConditionWithApiCalls(target.Id, ConditionType.SkipAction,
                                       [ store.Calls[gate].ApiCalls[0].Id ]) |> ignore

    let index = SimIndex.build store 10
    let manager = StateManager(index, 10)
    let skipper g = WorkConditionChecker.shouldSkipWork index (manager.GetState()) g
    let canStart g = WorkConditionChecker.canStartWorkPredOnly index (manager.GetState()) g

    // ── ① 조건이 거짓일 때 — 건너뛰지 **않는다** ──────────────────────
    // 반대쪽을 먼저 본다. 이것이 없으면 ②는 「늘 참인 검사」다.
    Assert.False(skipper target.Id, "신호가 꺼졌는데 건너뛴다")
    let going = manager.ApplyWorkTransition(target.Id, Status4.Going, skipper)
    Assert.Equal(Status4.Going, going.ActualNewState)
    Assert.False(going.IsSkipped)
    Assert.False(canStart after.Id, "Target 이 돌고 있는데 뒷일이 섰다")

    // Target 을 Ready 로 되돌려 ②를 같은 출발선에서 잰다
    manager.ApplyWorkTransition(target.Id, Status4.Ready, (fun _ -> false)) |> ignore

    // ── ② 신호를 켠다 — 건너뛰고 **Finish 가 된다** ───────────────────
    manager.ApplyWorkTransition(move.Id, Status4.Finish, (fun _ -> false)) |> ignore
    Assert.True(skipper target.Id, "신호가 켜졌는데 건너뛰지 않는다")
    let skipped = manager.ApplyWorkTransition(target.Id, Status4.Going, skipper)
    Assert.Equal(Status4.Finish, skipped.ActualNewState)
    Assert.True(skipped.IsSkipped, "건너뛴 사실이 관측되지 않는다")
    Assert.True(skipped.HasChanged)

    // ── ③ 교착 가설의 핵심 — 뒷일이 **선다** ──────────────────────────
    // 건너뛴 Work 는 Call 을 하나도 돌리지 않았다. 그래도 뒷일의 선행 판정은
    // Work 상태(Finish)로 하므로 Call epoch 과 무관하다. 가설은 **반증됐다.**
    Assert.Equal(Status4.Finish, manager.GetWorkState target.Id)
    Assert.True(canStart after.Id, "건너뛴 Work 뒤에서 뒷일이 서지 못한다 — 교착")
