module Ds2.Store.Editor.Tests.SimulationConnectionReloadTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core

let private buildTwoWorkScenario arrowType =
    let store = createStore ()
    let _, system, flow, work1 = setupBasicHierarchy store
    let work2 = addWork store "Work2" flow.Id

    store.UpdateWorkTokenRole(work1.Id, TokenRole.Source)
    store.UpdateWorkPeriodMs(work1.Id, Some 100)
    store.UpdateWorkPeriodMs(work2.Id, Some 100)
    store.ConnectSelectionInOrder([ work1.Id; work2.Id ], arrowType) |> ignore

    store, system.Id, work1.Id, work2.Id

let private removeDirectWorkArrows store systemId sourceId targetId =
    let arrowIds =
        Queries.arrowWorksOf systemId store
        |> List.filter (fun arrow -> arrow.SourceId = sourceId && arrow.TargetId = targetId)
        |> List.map (fun arrow -> arrow.Id)
    store.RemoveArrows(arrowIds)

module ConnectionReloadTests =

    [<Fact>]
    let ``removing pure Start predecessor freezes going work but preserves token`` () =
        let store, systemId, work1Id, work2Id = buildTwoWorkScenario ArrowType.Start
        let index = SimIndex.build store 10
        use engine = new EventDrivenEngine(index, RuntimeMode.Simulation) :> ISimulationEngine

        engine.Start()
        let token = engine.NextToken()
        engine.SeedToken(work1Id, token)

        Assert.True(waitUntil 4000 (fun () ->
            engine.GetWorkState(work2Id) = Some Status4.Going
            && engine.GetWorkToken(work2Id) = Some token))

        removeDirectWorkArrows store systemId work1Id work2Id |> ignore
        engine.ReloadConnections()

        Assert.Equal(Some Status4.Going, engine.GetWorkState(work2Id))
        Assert.Equal(Some token, engine.GetWorkToken(work2Id))
        Assert.False(waitUntil 700 (fun () -> engine.GetWorkState(work2Id) = Some Status4.Finish))
        Assert.Equal(Some token, engine.GetWorkToken(work2Id))

    [<Fact>]
    let ``reconnecting pure Start predecessor resumes frozen work and completes it`` () =
        let store, systemId, work1Id, work2Id = buildTwoWorkScenario ArrowType.Start
        let index = SimIndex.build store 10
        use engine = new EventDrivenEngine(index, RuntimeMode.Simulation) :> ISimulationEngine

        engine.Start()
        let token = engine.NextToken()
        engine.SeedToken(work1Id, token)

        Assert.True(waitUntil 4000 (fun () ->
            engine.GetWorkState(work2Id) = Some Status4.Going
            && engine.GetWorkToken(work2Id) = Some token))

        removeDirectWorkArrows store systemId work1Id work2Id |> ignore
        engine.ReloadConnections()

        Assert.Equal(Some Status4.Going, engine.GetWorkState(work2Id))
        Assert.Equal(Some token, engine.GetWorkToken(work2Id))
        Assert.True(waitUntil 2500 (fun () -> engine.GetWorkState(work1Id) = Some Status4.Finish))

        store.ConnectSelectionInOrder([ work1Id; work2Id ], ArrowType.Start) |> ignore
        engine.ReloadConnections()

        Assert.True(waitUntil 2500 (fun () -> engine.GetWorkState(work2Id) = Some Status4.Finish))
        Assert.Equal(Some token, engine.GetWorkToken(work2Id))

    [<Fact>]
    let ``removing StartReset predecessor does not freeze already going work`` () =
        let store, systemId, work1Id, work2Id = buildTwoWorkScenario ArrowType.StartReset
        let index = SimIndex.build store 10
        use engine = new EventDrivenEngine(index, RuntimeMode.Simulation) :> ISimulationEngine

        engine.Start()
        let token = engine.NextToken()
        engine.SeedToken(work1Id, token)

        Assert.True(waitUntil 4000 (fun () ->
            engine.GetWorkState(work2Id) = Some Status4.Going
            && engine.GetWorkToken(work2Id) = Some token))

        removeDirectWorkArrows store systemId work1Id work2Id |> ignore
        engine.ReloadConnections()

        Assert.True(waitUntil 2500 (fun () -> engine.GetWorkState(work2Id) = Some Status4.Finish))
