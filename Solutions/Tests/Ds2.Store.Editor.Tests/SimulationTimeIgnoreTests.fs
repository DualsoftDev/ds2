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
