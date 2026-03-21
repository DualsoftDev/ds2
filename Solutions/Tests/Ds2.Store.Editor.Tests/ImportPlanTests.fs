module Ds2.Store.Editor.Tests.ImportPlanTests

open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Editor
open Ds2.CSV

module CsvImportPlanTests =

    let private fst4 (a, _, _, _) = a
    let private snd4 (_, b, _, _) = b
    let private trd4 (_, _, c, _) = c
    let private frt4 (_, _, _, d) = d

    let private createDocument () =
        {
            Entries =
                [
                    {
                        FlowName = "FlowA"
                        WorkName = "WorkA"
                        DeviceName = "Valve"
                        DeviceAlias = "Valve"
                        ApiName = "Open"
                        IsSyntheticApi = false
                        InName = Some "In"
                        InAddress = Some "%IX0.0"
                        OutName = Some "Out"
                        OutAddress = Some "%QX0.0"
                        SourceLines = [ 1 ]
                    }
                    {
                        FlowName = "FlowA"
                        WorkName = "WorkA"
                        DeviceName = "Valve"
                        DeviceAlias = "Valve"
                        ApiName = "Close"
                        IsSyntheticApi = false
                        InName = None
                        InAddress = None
                        OutName = Some "Out"
                        OutAddress = Some "%QX0.1"
                        SourceLines = [ 2 ]
                    }
                ]
        }

    [<Fact>]
    let ``buildSystemImportPlan does not mutate store before apply`` () =
        let store = DsStore()
        let projectId = store.AddProject("Project")
        let systemId = store.AddSystem("System", projectId, true)
        let beforeCounts = store.Flows.Count, store.Works.Count, store.Calls.Count, store.ApiCalls.Count

        match CsvImporter.buildSystemImportPlan store (createDocument ()) systemId with
        | Error errors -> Assert.Fail(System.String.Join("\n", errors))
        | Ok plan ->
            Assert.NotEmpty(plan.Operations)
            Assert.Equal(beforeCounts, (store.Flows.Count, store.Works.Count, store.Calls.Count, store.ApiCalls.Count))

    [<Fact>]
    let ``ApplyImportPlan applies CSV import as one undo unit`` () =
        let store = DsStore()
        let projectId = store.AddProject("Project")
        let systemId = store.AddSystem("System", projectId, true)

        let beforeCounts = store.Flows.Count, store.Works.Count, store.Calls.Count, store.ApiCalls.Count

        match CsvImporter.buildSystemImportPlan store (createDocument ()) systemId with
        | Error errors -> Assert.Fail(System.String.Join("\n", errors))
        | Ok plan ->
            store.ApplyImportPlan("CSV 임포트", plan)

        Assert.True(store.Flows.Count > fst4 beforeCounts)
        Assert.True(store.Works.Count > snd4 beforeCounts)
        Assert.True(store.Calls.Count > trd4 beforeCounts)
        Assert.True(store.ApiCalls.Count > frt4 beforeCounts)

        store.Undo()

        Assert.Equal(beforeCounts, (store.Flows.Count, store.Works.Count, store.Calls.Count, store.ApiCalls.Count))
