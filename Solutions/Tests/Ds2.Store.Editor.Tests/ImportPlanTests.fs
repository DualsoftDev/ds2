module Ds2.Store.Editor.Tests.ImportPlanTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.CSV

module CsvImportPlanTests =

    let private entry flow work device system api : CsvEntry =
        { FlowName = flow; WorkName = work; DeviceName = device; DeviceAlias = device
          SystemName = system; ApiName = api; IsSyntheticApi = false
          InName = None; InAddress = None; OutName = None; OutAddress = None; SourceLines = [] }

    let private entryWithIO flow work device system api inName inAddr outName outAddr : CsvEntry =
        { FlowName = flow; WorkName = work; DeviceName = device; DeviceAlias = device
          SystemName = system; ApiName = api; IsSyntheticApi = false
          InName = Some inName; InAddress = Some inAddr
          OutName = Some outName; OutAddress = Some outAddr; SourceLines = [] }

    [<Fact>]
    let ``buildSystemImportPlan does not mutate store before apply`` () =
        let store = DsStore()
        let projectId = store.AddProject("Project")
        let systemId = store.AddSystem("System", projectId, true)
        let before = store.Flows.Count, store.Works.Count, store.Calls.Count, store.ApiCalls.Count

        let doc = { Entries = [
            entryWithIO "FlowA" "WorkA" "Valve" "Valve" "Open" "In" "%IX0.0" "Out" "%QX0.0"
            entryWithIO "FlowA" "WorkA" "Valve" "Valve" "Close" "In" "%IX0.1" "Out" "%QX0.1"
        ] }

        match CsvImporter.buildSystemImportPlan store doc systemId with
        | Error errors -> Assert.Fail(System.String.Join("\n", errors))
        | Ok plan ->
            Assert.NotEmpty(plan.Operations)
            Assert.Equal(before, (store.Flows.Count, store.Works.Count, store.Calls.Count, store.ApiCalls.Count))

    [<Fact>]
    let ``ApplyImportPlan applies CSV import as one undo unit`` () =
        let store = DsStore()
        let projectId = store.AddProject("Project")
        let systemId = store.AddSystem("System", projectId, true)
        let before = store.Flows.Count, store.Works.Count, store.Calls.Count, store.ApiCalls.Count

        let doc = { Entries = [
            entryWithIO "FlowA" "WorkA" "Valve" "Valve" "Open" "In" "%IX0.0" "Out" "%QX0.0"
            entryWithIO "FlowA" "WorkA" "Valve" "Valve" "Close" "In" "%IX0.1" "Out" "%QX0.1"
        ] }

        match CsvImporter.buildSystemImportPlan store doc systemId with
        | Error errors -> Assert.Fail(System.String.Join("\n", errors))
        | Ok plan -> store.ApplyImportPlan("CSV 임포트", plan)

        Assert.True(store.Flows.Count > (let a,_,_,_ = before in a))
        Assert.True(store.Works.Count > (let _,b,_,_ = before in b))

        store.Undo()
        Assert.Equal(before, (store.Flows.Count, store.Works.Count, store.Calls.Count, store.ApiCalls.Count))

    [<Fact>]
    let ``cross-flow same DeviceAlias maps to single Device System`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)

        let doc = { Entries = [
            entry "Flow1" "W1" "dev" "dev" "ADV"
            entry "Flow1" "W1" "dev" "dev" "RET"
            entry "Flow2" "W2" "dev" "dev" "ADV"
        ] }

        match CsvImporter.buildSystemImportPlan store doc systemId with
        | Error e -> Assert.Fail(System.String.Join("\n", e))
        | Ok plan -> store.ApplyImportPlan("CSV", plan)

        let passive = Queries.passiveSystemsOf projectId store
        Assert.Equal(1, passive.Length)

    [<Fact>]
    let ``different DeviceAliases with same SystemName map to single Device System`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)

        // dev와 dev1이 다른 DeviceAlias지만 같은 SystemName "dev"
        let doc = { Entries = [
            entry "Flow1" "W1" "dev" "dev" "ADV"
            entry "Flow1" "W1" "dev" "dev" "RET"
            entry "Flow1" "W2" "dev1" "dev" "ADV"    // dev1 alias → dev system
        ] }

        match CsvImporter.buildSystemImportPlan store doc systemId with
        | Error e -> Assert.Fail(System.String.Join("\n", e))
        | Ok plan -> store.ApplyImportPlan("CSV", plan)

        let passive = Queries.passiveSystemsOf projectId store
        Assert.Equal(1, passive.Length)

    [<Fact>]
    let ``different SystemNames map to separate Device Systems`` () =
        let store = DsStore()
        let projectId = store.AddProject("P")
        let systemId = store.AddSystem("S", projectId, true)

        let doc = { Entries = [
            entry "Flow1" "W1" "dev1" "dev1" "ADV"
            entry "Flow1" "W1" "dev2" "dev2" "RET"
        ] }

        match CsvImporter.buildSystemImportPlan store doc systemId with
        | Error e -> Assert.Fail(System.String.Join("\n", e))
        | Ok plan -> store.ApplyImportPlan("CSV", plan)

        let passive = Queries.passiveSystemsOf projectId store
        Assert.Equal(2, passive.Length)

module CsvRoundTripTests =
    open Ds2.Store.Editor.Tests.TestHelpers

    /// csv_test.aasx와 동일한 구조: 1개 Device System(dev), 2개 Flow, 고급탭 DeviceAlias 변경(dev1) 포함
    let private buildCsvTestStore () =
        let store = createStore()
        let project = addProject store "P"
        let activeSys = addSystem store "Active" project.Id true

        let flow1 = addFlow store "Flow1" activeSys.Id
        let w1 = addWork store "NewWork" flow1.Id
        let w2 = addWork store "NewWork1" flow1.Id

        let flow2 = addFlow store "Flow2" activeSys.Id
        let w3 = addWork store "NewWork" flow2.Id

        // Device System: 1개 ("dev")
        let devSys = addSystem store "dev" project.Id false
        let devFlow = addFlow store "dev_Flow" devSys.Id
        let advWork = addWork store "ADV" devFlow.Id
        let retWork = addWork store "RET" devFlow.Id

        let advDef = addApiDef store "ADV" devSys.Id
        advDef.TxGuid <- Some advWork.Id; advDef.RxGuid <- Some advWork.Id
        let retDef = addApiDef store "RET" devSys.Id
        retDef.TxGuid <- Some retWork.Id; retDef.RxGuid <- Some retWork.Id

        // Flow1.NewWork: dev.RET, dev.ADV, dev.ADV (고급탭 추가)
        store.AddCallWithLinkedApiDefs(w1.Id, "dev", "RET", [retDef.Id]) |> ignore
        store.AddCallWithLinkedApiDefs(w1.Id, "dev", "ADV", [advDef.Id]) |> ignore
        store.AddCallWithLinkedApiDefs(w1.Id, "dev", "ADV", [advDef.Id]) |> ignore

        // Flow1.NewWork1: dev1.ADV (DeviceAlias 변경 — 같은 System), dev.RET, dev.ADV
        store.AddCallWithLinkedApiDefs(w2.Id, "dev1", "ADV", [advDef.Id]) |> ignore
        store.AddCallWithLinkedApiDefs(w2.Id, "dev", "RET", [retDef.Id]) |> ignore
        store.AddCallWithLinkedApiDefs(w2.Id, "dev", "ADV", [advDef.Id]) |> ignore

        // Flow2.NewWork: dev.ADV
        store.AddCallWithLinkedApiDefs(w3.Id, "dev", "ADV", [advDef.Id]) |> ignore

        store, project.Id, activeSys.Id

    [<Fact>]
    let ``CSV roundtrip preserves Device System count`` () =
        let store, projectId, systemId = buildCsvTestStore()

        let passiveBefore = Queries.passiveSystemsOf projectId store
        Assert.Equal(1, passiveBefore.Length)

        // Export
        let csv = CsvExporter.systemToCsv store systemId
        Assert.Contains("System", csv.Split('\n').[0])  // System 칼럼 존재

        // Import into fresh store
        let store2 = DsStore()
        let projectId2 = store2.AddProject("P2")
        let systemId2 = store2.AddSystem("Active2", projectId2, true)

        match CsvImporter.buildSystemImportPlan store2 with
        | _ -> ()  // 이 방식 아님

        match CsvParser.parse csv with
        | Error errors -> Assert.Fail($"Parse failed: {errors}")
        | Ok doc ->
            match CsvImporter.buildSystemImportPlan store2 doc systemId2 with
            | Error errors -> Assert.Fail(System.String.Join("\n", errors))
            | Ok plan -> store2.ApplyImportPlan("CSV roundtrip", plan)

        let passiveAfter = Queries.passiveSystemsOf projectId2 store2
        Assert.Equal(1, passiveAfter.Length)

    [<Fact>]
    let ``CSV roundtrip preserves Flow and Work counts`` () =
        let store, _projectId, systemId = buildCsvTestStore()

        let flowsBefore = Queries.flowsOf systemId store |> List.length
        let worksBefore =
            Queries.flowsOf systemId store
            |> List.collect (fun f -> Queries.worksOf f.Id store)
            |> List.length

        let csv = CsvExporter.systemToCsv store systemId

        let store2 = DsStore()
        let projectId2 = store2.AddProject("P2")
        let systemId2 = store2.AddSystem("Active2", projectId2, true)

        match CsvParser.parse csv with
        | Error errors -> Assert.Fail($"Parse failed: {errors}")
        | Ok doc ->
            match CsvImporter.buildSystemImportPlan store2 doc systemId2 with
            | Error errors -> Assert.Fail(System.String.Join("\n", errors))
            | Ok plan -> store2.ApplyImportPlan("CSV roundtrip", plan)

        let flowsAfter = Queries.flowsOf systemId2 store2 |> List.length
        let worksAfter =
            Queries.flowsOf systemId2 store2
            |> List.collect (fun f -> Queries.worksOf f.Id store2)
            |> List.length

        Assert.Equal(flowsBefore, flowsAfter)
        Assert.Equal(worksBefore, worksAfter)

    [<Fact>]
    let ``CSV export includes System column matching actual Device System name`` () =
        let store, _, systemId = buildCsvTestStore()
        let csv = CsvExporter.systemToCsv store systemId
        let lines = csv.Split('\n') |> Array.filter (fun l -> l.Trim().Length > 0)

        // 모든 데이터 행의 System 칼럼이 "dev" (실제 Device System 이름)
        for line in lines |> Array.skip 1 do
            let cols = line.Split(',')
            Assert.Equal("dev", cols.[3].Trim())
