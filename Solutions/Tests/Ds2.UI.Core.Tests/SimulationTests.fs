module Ds2.UI.Core.Tests.SimulationTests

open System
open Xunit
open Ds2.Core
open Ds2.UI.Core
open Ds2.UI.Core.Tests.TestHelpers
open Ds2.Runtime.Sim.Engine.Core
open Ds2.Runtime.Sim.Report

module ReportServiceTests =

    [<Fact>]
    let ``fromStateChanges groups entries and computes metadata`` () =
        let startTime = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        let endTime = startTime.AddSeconds(10)

        let records = [
            {
                NodeId = "work-1"
                NodeName = "Work1"
                NodeType = "Work"
                SystemId = "SystemA"
                State = "R"
                Timestamp = startTime
            }
            {
                NodeId = "call-1"
                NodeName = "Call1"
                NodeType = "Call"
                SystemId = "SystemA"
                State = "R"
                Timestamp = startTime.AddSeconds(1)
            }
            {
                NodeId = "work-1"
                NodeName = "Work1"
                NodeType = "Work"
                SystemId = "SystemA"
                State = "G"
                Timestamp = startTime.AddSeconds(2)
            }
        ]

        let report = ReportService.fromStateChanges startTime endTime records

        Assert.Equal(2, report.Entries.Length)
        Assert.Equal(1, report.Metadata.WorkCount)
        Assert.Equal(1, report.Metadata.CallCount)

        let workEntry = report.Entries |> List.find (fun entry -> entry.Id = "work-1")
        Assert.Equal(2, workEntry.Segments.Length)
        Assert.Equal(startTime.AddSeconds(2), workEntry.Segments[0].EndTime |> Option.defaultValue DateTime.MinValue)
        Assert.Equal(endTime, workEntry.Segments[1].EndTime |> Option.defaultValue DateTime.MinValue)

module SimIndexTests =

    [<Fact>]
    let ``build collects work and call predecessor maps`` () =
        let store = createStore ()
        let project, system, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work1.Id ], ArrowType.Reset) |> ignore
        store.AddCallsWithDevice(project.Id, work1.Id, [ "Dev.Api1"; "Dev.Api2" ], true)

        let callIds =
            DsQuery.callsOf work1.Id store
            |> List.map (fun call -> call.Id)

        store.ConnectSelectionInOrder(callIds, ArrowType.Start) |> ignore

        let index = SimIndex.build store 10

        Assert.Contains(system.Name, index.ActiveSystemNames)
        Assert.Equal<Guid list>(callIds, index.WorkCallGuids[work1.Id])
        Assert.Equal<Guid list>([ work1.Id ], index.WorkStartPreds[work2.Id])
        Assert.Equal<Guid list>([ work2.Id ], index.WorkResetPreds[work1.Id])
        Assert.Equal<Guid list>([ callIds[0] ], index.CallStartPreds[callIds[1]])

    [<Fact>]
    let ``build collects condition specs by call condition type`` () =
        let store = createStore ()
        let project, _, flow, work = setupBasicHierarchy store
        let rxWork = addWork store "RxWork" flow.Id

        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true)

        let calls = DsQuery.callsOf work.Id store
        let sourceCall = calls[0]
        let targetCall = calls[1]
        let sourceApiCall = sourceCall.ApiCalls |> Seq.head

        let apiDefId = sourceApiCall.ApiDefId |> Option.defaultValue Guid.Empty
        let apiDef = store.ApiDefs[apiDefId]
        apiDef.Properties.RxGuid <- Some rxWork.Id

        store.AddCallCondition(targetCall.Id, CallConditionType.ComAux)
        let conditionId =
            store.Calls[targetCall.Id].CallConditions
            |> Seq.head
            |> fun condition -> condition.Id

        store.AddApiCallsToConditionBatch(targetCall.Id, conditionId, [ sourceApiCall.Id ]) |> ignore

        let index = SimIndex.build store 10
        let specs = index.CallComAuxConditions[targetCall.Id]

        Assert.Single(specs) |> ignore
        Assert.Equal(rxWork.Id, specs[0].RxWorkGuid)
        Assert.Equal(Some sourceApiCall.Id, specs[0].ApiCallGuid)
