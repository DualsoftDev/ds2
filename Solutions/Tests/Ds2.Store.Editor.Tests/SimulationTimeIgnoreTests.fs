module Ds2.Store.Editor.Tests.SimulationTimeIgnoreTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

[<Fact>]
let ``StartSourceWork under time ignore does not replay a stale forced Going`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSys = addSystem store "Active" project.Id true
    let flow = addFlow store "F" activeSys.Id
    let work = addWork store "W" flow.Id
    store.UpdateWorkTokenRole(work.Id, TokenRole.Source)
    work.Duration <- Some (TimeSpan.FromMilliseconds 100.)

    let deviceSys = addSystem store "Dev" project.Id false
    let deviceFlow = addFlow store "DF" deviceSys.Id
    let advWork = addWork store "ADV" deviceFlow.Id
    advWork.Duration <- Some (TimeSpan.FromMilliseconds 100.)
    let retWork = addWork store "RET" deviceFlow.Id
    retWork.Duration <- Some (TimeSpan.FromMilliseconds 100.)
    let retProps = SimulationWorkProperties()
    retProps.IsFinished <- true
    retWork.SetSimulationProperties(retProps)

    let advDef = addApiDef store "ADV" deviceSys.Id
    advDef.TxGuid <- Some advWork.Id
    advDef.RxGuid <- Some advWork.Id
    let retDef = addApiDef store "RET" deviceSys.Id
    retDef.TxGuid <- Some retWork.Id
    retDef.RxGuid <- Some retWork.Id

    store.ConnectSelectionInOrder([ advWork.Id; retWork.Id ], ArrowType.ResetReset) |> ignore

    let retCall = store.AddCallWithLinkedApiDefs(work.Id, "Dev", "RET", [ retDef.Id ])
    let advCall = store.AddCallWithLinkedApiDefs(work.Id, "Dev", "ADV", [ advDef.Id ])
    store.ConnectSelectionInOrder([ retCall; advCall ], ArrowType.Start) |> ignore

    let index = SimIndex.build store 10
    use engine = new EventDrivenEngine(index, RuntimeMode.Simulation) :> ISimulationEngine

    let mutable homingDone = false
    let mutable sourceGoingCount = 0

    engine.HomingPhaseCompleted.AddHandler(fun _ _ -> homingDone <- true)
    engine.WorkStateChanged.AddHandler(fun _ args ->
        if args.WorkGuid = work.Id && args.NewState = Status4.Going then
            sourceGoingCount <- sourceGoingCount + 1)

    engine.TimeIgnore <- true

    let hasHoming = engine.StartWithHomingPhase()
    Assert.True(hasHoming, "expected homing target for RET/ADV device pair")
    Assert.True(waitUntil 1000 (fun () -> homingDone), "homing should complete under time ignore")

    engine.StartSourceWork(work.Id)

    let completed = waitUntil 1000 (fun () ->
        engine.GetWorkState(work.Id) = Some Status4.Finish
        && engine.GetCallState(retCall) = Some Status4.Finish
        && engine.GetCallState(advCall) = Some Status4.Finish
        && not engine.HasStartableWork
        && not engine.HasActiveDuration)

    Assert.True(completed, "source work should finish cleanly under time ignore")

    System.Threading.Thread.Sleep(50)

    Assert.Equal(1, sourceGoingCount)
    Assert.Equal(Some Status4.Finish, engine.GetWorkState(work.Id))

[<Fact>]
let ``TimeIgnore reset returns every finished call in a StartReset cycle to Ready`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSys = addSystem store "Active" project.Id true
    let flow1 = addFlow store "F1" activeSys.Id
    let w1 = addWork store "W1" flow1.Id
    let flow2 = addFlow store "F2" activeSys.Id
    let w2 = addWork store "W2" flow2.Id

    let addDevicePair name =
        let sys = addSystem store name project.Id false
        let flow = addFlow store (name + "Flow") sys.Id
        let advWork = addWork store "ADV" flow.Id
        advWork.Duration <- Some (TimeSpan.FromMilliseconds 100.)
        let retWork = addWork store "RET" flow.Id
        retWork.Duration <- Some (TimeSpan.FromMilliseconds 100.)
        let retProps = SimulationWorkProperties()
        retProps.IsFinished <- true
        retWork.SetSimulationProperties(retProps)
        let advDef = addApiDef store "ADV" sys.Id
        advDef.TxGuid <- Some advWork.Id
        advDef.RxGuid <- Some advWork.Id
        let retDef = addApiDef store "RET" sys.Id
        retDef.TxGuid <- Some retWork.Id
        retDef.RxGuid <- Some retWork.Id
        store.ConnectSelectionInOrder([ advWork.Id; retWork.Id ], ArrowType.ResetReset) |> ignore
        advDef.Id, retDef.Id

    let d1AdvDef, d1RetDef = addDevicePair "D1"
    let d2AdvDef, d2RetDef = addDevicePair "D2"
    let d3AdvDef, d3RetDef = addDevicePair "D3"

    let w1Ret = store.AddCallWithLinkedApiDefs(w1.Id, "D1", "RET", [ d1RetDef ])
    let w1Adv = store.AddCallWithLinkedApiDefs(w1.Id, "D1", "ADV", [ d1AdvDef ])
    let w1Ret2 = store.AddCallWithLinkedApiDefs(w1.Id, "D2", "RET", [ d2RetDef ])
    let w1Adv2 = store.AddCallWithLinkedApiDefs(w1.Id, "D2", "ADV", [ d2AdvDef ])
    store.ConnectSelectionInOrder([ w1Ret; w1Adv; w1Ret2; w1Adv2 ], ArrowType.Start) |> ignore

    let w2Ret = store.AddCallWithLinkedApiDefs(w2.Id, "D3", "RET", [ d3RetDef ])
    let w2Adv = store.AddCallWithLinkedApiDefs(w2.Id, "D3", "ADV", [ d3AdvDef ])
    store.ConnectSelectionInOrder([ w2Ret; w2Adv ], ArrowType.Start) |> ignore

    store.ConnectSelectionInOrder([ w1.Id; w2.Id ], ArrowType.StartReset) |> ignore

    let index = SimIndex.build store 10
    use engine = new EventDrivenEngine(index, RuntimeMode.Simulation) :> ISimulationEngine

    engine.TimeIgnore <- true
    engine.Start()
    engine.ForceWorkState(w1.Id, Status4.Going)

    let w1ReturnedReady =
        waitUntil 1000 (fun () ->
            engine.GetWorkState(w1.Id) = Some Status4.Ready
            && engine.GetWorkState(w2.Id) = Some Status4.Going)

    Assert.True(w1ReturnedReady, "W1 should reset to Ready when W2 starts under StartReset")

    let w1Calls = [ w1Ret; w1Adv; w1Ret2; w1Adv2 ]
    let unfinishedCalls =
        w1Calls
        |> List.choose (fun callGuid ->
            match engine.GetCallState(callGuid) with
            | Some Status4.Ready -> None
            | state -> Some(callGuid, state))

    Assert.True(
        unfinishedCalls.IsEmpty,
        $"All W1 calls should be Ready after reset, but found: {unfinishedCalls}")
