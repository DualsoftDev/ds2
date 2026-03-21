module Ds2.Mermaid.Tests.MapperTests

open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Editor
open Ds2.Mermaid

let private createStore () = DsStore()

let private setupProject (store: DsStore) =
    let projectId = store.AddProject("TestProject")
    let systemId = store.AddSystem("TestSystem", projectId, true)
    (projectId, systemId)

let private setupProjectWithFlow (store: DsStore) =
    let projectId, systemId = setupProject store
    let flowId = store.AddFlow("TestFlow", systemId)
    (projectId, systemId, flowId)

let private setupProjectWithWork (store: DsStore) =
    let projectId, systemId, flowId = setupProjectWithFlow store
    let workId = store.AddWork("TestWork", flowId)
    (projectId, systemId, flowId, workId)

let private applyImportPlan (store: DsStore) (graph: MermaidGraph) (level: ImportLevel) (parentId: System.Guid) =
    match MermaidImporter.buildImportPlan store graph level parentId with
    | Error errors ->
        let joined = System.String.Join("\n", errors)
        Assert.Fail($"임포트 플랜 생성 실패: {joined}")
    | Ok plan ->
        store.ApplyImportPlan("Mermaid 임포트", plan)

let private mermaid2Depth =
    """
graph TD
    subgraph W1 ["작업1"]
        A["SV.ON"]
        B["SV.OFF"]
        A --> B
    end
    subgraph W2 ["작업2"]
        C["MT.RUN"]
    end
    A --> C
"""

let private mermaid1Depth =
    """
graph TD
    X["WorkX"]
    Y["WorkY"]
    X --> Y
"""

[<Fact>]
let ``buildImportPlan does not mutate store before apply`` () =
    let store = createStore()
    let _, _, flowId = setupProjectWithFlow store
    let beforeWorks = store.Works.Count
    let beforeCalls = store.Calls.Count
    let beforeArrows = store.ArrowWorks.Count + store.ArrowCalls.Count

    match MermaidParser.parse mermaid2Depth with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        match MermaidImporter.buildImportPlan store graph FlowLevel flowId with
        | Error errors ->
            let joined = System.String.Join("\n", errors)
            Assert.Fail($"플랜 생성 실패: {joined}")
        | Ok plan ->
            Assert.NotEmpty(plan.Operations)
            Assert.Equal(beforeWorks, store.Works.Count)
            Assert.Equal(beforeCalls, store.Calls.Count)
            Assert.Equal(beforeArrows, store.ArrowWorks.Count + store.ArrowCalls.Count)

[<Fact>]
let ``Flow 2-depth — Work + Call + Arrow 생성`` () =
    let store = createStore()
    let _, systemId, flowId = setupProjectWithFlow store

    match MermaidParser.parse mermaid2Depth with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        applyImportPlan store graph FlowLevel flowId

    let works = store.Works.Values |> Seq.filter (fun work -> work.ParentId = flowId) |> Seq.toList
    Assert.Equal(2, works.Length)

    let calls = store.Calls.Values |> Seq.toList
    Assert.True(calls.Length >= 3)

    Assert.True(store.ArrowCalls.Count >= 1)

    let workArrows = store.ArrowWorks.Values |> Seq.filter (fun arrow -> arrow.ParentId = systemId) |> Seq.toList
    Assert.True(workArrows.Length >= 1)

[<Fact>]
let ``Flow 1-depth — GlobalNode → Work + ArrowBetweenWorks`` () =
    let store = createStore()
    let _, systemId, flowId = setupProjectWithFlow store

    match MermaidParser.parse mermaid1Depth with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        applyImportPlan store graph FlowLevel flowId

    let works = store.Works.Values |> Seq.filter (fun work -> work.ParentId = flowId) |> Seq.toList
    Assert.Equal(2, works.Length)

    let arrows = store.ArrowWorks.Values |> Seq.filter (fun arrow -> arrow.ParentId = systemId) |> Seq.toList
    Assert.Equal(1, arrows.Length)

[<Fact>]
let ``Work 1-depth — GlobalNode → Call + ArrowBetweenCalls`` () =
    let store = createStore()
    let _, _, _, workId = setupProjectWithWork store

    match MermaidParser.parse mermaid1Depth with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        applyImportPlan store graph WorkLevel workId

    let calls = store.Calls.Values |> Seq.filter (fun call -> call.ParentId = workId) |> Seq.toList
    Assert.Equal(2, calls.Length)

    let arrows = store.ArrowCalls.Values |> Seq.filter (fun arrow -> arrow.ParentId = workId) |> Seq.toList
    Assert.Equal(1, arrows.Length)

[<Fact>]
let ``Call 이름 — 점이 있으면 DevicesAlias.ApiName 분리`` () =
    let store = createStore()
    let _, _, _, workId = setupProjectWithWork store

    let mermaid = """
graph TD
    A["CAR_SV.ON"]
"""
    match MermaidParser.parse mermaid with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        applyImportPlan store graph WorkLevel workId

    let call = store.Calls.Values |> Seq.find (fun current -> current.ParentId = workId)
    Assert.Equal("CAR_SV", call.DevicesAlias)
    Assert.Equal("ON", call.ApiName)

[<Fact>]
let ``Call 이름 — 점이 없으면 imported.Label`` () =
    let store = createStore()
    let _, _, _, workId = setupProjectWithWork store

    let mermaid = """
graph TD
    A["SimpleCall"]
"""
    match MermaidParser.parse mermaid with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        applyImportPlan store graph WorkLevel workId

    let call = store.Calls.Values |> Seq.find (fun current -> current.ParentId = workId)
    Assert.Equal("imported", call.DevicesAlias)
    Assert.Equal("SimpleCall", call.ApiName)

[<Fact>]
let ``Undo 1회로 Mermaid 임포트 전체 롤백`` () =
    let store = createStore()
    let _, _, flowId = setupProjectWithFlow store

    let beforeWorkCount = store.Works.Count
    let beforeCallCount = store.Calls.Count
    let beforeArrowCallCount = store.ArrowCalls.Count

    match MermaidParser.parse mermaid2Depth with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        applyImportPlan store graph FlowLevel flowId

    Assert.True(store.Works.Count > beforeWorkCount)
    Assert.True(store.Calls.Count > beforeCallCount)

    store.Undo()

    Assert.Equal(beforeWorkCount, store.Works.Count)
    Assert.Equal(beforeCallCount, store.Calls.Count)
    Assert.Equal(beforeArrowCallCount, store.ArrowCalls.Count)

[<Fact>]
let ``WorkLevel에 subgraph가 있으면 Invalid`` () =
    match MermaidParser.parse mermaid2Depth with
    | Error _ -> ()
    | Ok graph ->
        match MermaidAnalyzer.validate graph WorkLevel with
        | Valid -> Assert.Fail("subgraph가 있는데 WorkLevel이 Valid")
        | Invalid errors -> Assert.True(errors.Length > 0)

[<Fact>]
let ``ArrowLabel → ArrowType 변환`` () =
    Assert.Equal(ArrowType.Start, MermaidMapper.mapArrowType NoLabel)
    Assert.Equal(ArrowType.Reset, MermaidMapper.mapArrowType Interlock)
    Assert.Equal(ArrowType.Reset, MermaidMapper.mapArrowType SelfReset)
    Assert.Equal(ArrowType.StartReset, MermaidMapper.mapArrowType StartReset)
    Assert.Equal(ArrowType.Start, MermaidMapper.mapArrowType StartEdge)
    Assert.Equal(ArrowType.Reset, MermaidMapper.mapArrowType ResetEdge)
    Assert.Equal(ArrowType.ResetReset, MermaidMapper.mapArrowType ResetReset)
    Assert.Equal(ArrowType.Group, MermaidMapper.mapArrowType Group)
    Assert.Equal(ArrowType.Start, MermaidMapper.mapArrowType AutoPre)
    Assert.Equal(ArrowType.Start, MermaidMapper.mapArrowType (Custom "anything"))

[<Fact>]
let ``SystemLevel Import — subgraph가 System으로 매핑`` () =
    let store = createStore()
    let projectId, _ = setupProject store

    let mermaid = """
graph TD
    subgraph STN1["Station1"]
        subgraph F1["Flow1"]
            subgraph W1["Work1"]
                A["Dev.ON"]
                B["Dev.OFF"]
                A --> B
            end
        end
    end
    subgraph STN2["Station2"]
        subgraph F2["Flow2"]
            subgraph W2["Work2"]
                C["Dev.Run"]
            end
        end
    end
"""
    match MermaidParser.parse mermaid with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        applyImportPlan store graph SystemLevel projectId

    let project = store.Projects.[projectId]
    Assert.True(project.ActiveSystemIds.Count >= 3)

    Assert.True(store.Systems.Count >= 3)
    Assert.True(store.Flows.Count >= 3)
    Assert.True(store.Works.Count >= 3)
    Assert.True(store.Calls.Count >= 3)

[<Fact>]
let ``Work 레벨 — Device.ApiName 형식 Call이면 Device System 자동 생성`` () =
    let store = createStore()
    let _projectId, _systemId, _flowId, workId = setupProjectWithWork store

    let mermaid = """
graph TD
    A["SV.ON"]
    B["SV.OFF"]
    A --> B
"""
    match MermaidParser.parse mermaid with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        applyImportPlan store graph WorkLevel workId

    let calls = store.Calls.Values |> Seq.filter (fun call -> call.ParentId = workId) |> Seq.toList
    Assert.Equal(2, calls.Length)

    let project = store.Projects.Values |> Seq.head
    let passiveSystems =
        project.PassiveSystemIds
        |> Seq.choose (fun id -> match store.Systems.TryGetValue(id) with | true, system -> Some system | _ -> None)
        |> Seq.toList
    Assert.True(passiveSystems.Length >= 1)

    let apiDefs = store.ApiDefs.Values |> Seq.toList
    Assert.True(apiDefs.Length >= 2)

    let apiCalls = store.ApiCalls.Values |> Seq.toList
    Assert.True(apiCalls.Length >= 2)

[<Fact>]
let ``인라인 노드 정의 — Arrow에서 A["Label"] 형식도 노드로 등록`` () =
    let mermaid = """
graph TD
    A["SV.ON"] --> B["SV.OFF"]
"""
    match MermaidParser.parse mermaid with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        Assert.Equal(2, graph.GlobalNodes.Length)
        Assert.Contains(graph.GlobalNodes, fun node -> node.Id = "A" && node.Label = "SV.ON")
        Assert.Contains(graph.GlobalNodes, fun node -> node.Id = "B" && node.Label = "SV.OFF")
        Assert.Equal(1, graph.GlobalEdges.Length)
        Assert.Equal("A", graph.GlobalEdges.[0].SourceId)
        Assert.Equal("B", graph.GlobalEdges.[0].TargetId)

[<Fact>]
let ``Export — flowToMermaid 2-depth 구조`` () =
    let store = createStore()
    let _, _, flowId = setupProjectWithFlow store

    match MermaidParser.parse mermaid2Depth with
    | Error _ -> ()
    | Ok graph ->
        applyImportPlan store graph FlowLevel flowId

    let mermaid = MermaidExporter.flowToMermaid store flowId
    Assert.False(System.String.IsNullOrWhiteSpace(mermaid), "flowToMermaid 비어있음")
    Assert.Contains("subgraph", mermaid)

[<Fact>]
let ``Export — systemToMermaid Active + Passive 구분`` () =
    let store = createStore()
    let projectId, _, flowId = setupProjectWithFlow store

    match MermaidParser.parse mermaid2Depth with
    | Error _ -> ()
    | Ok graph ->
        applyImportPlan store graph FlowLevel flowId

    let mermaid = MermaidExporter.systemToMermaid store projectId
    Assert.False(System.String.IsNullOrWhiteSpace(mermaid), "systemToMermaid 비어있음")
    Assert.Contains("subgraph", mermaid)

[<Fact>]
let ``Export and import — duplicate call names keep condition source by node id`` () =
    let store = createStore()
    let projectId, _, flowId = setupProjectWithFlow store

    let sourceAId = store.AddWork("SourceA", flowId)
    let sourceBId = store.AddWork("SourceB", flowId)
    let targetId = store.AddWork("TargetWork", flowId)

    store.AddCallsWithDevice(projectId, sourceAId, [ "Dev.Api" ], true)
    store.AddCallsWithDevice(projectId, sourceBId, [ "Dev.Api" ], true)
    store.AddCallsWithDevice(projectId, targetId, [ "Target.Do" ], true)

    let sourceACall = DsQuery.callsOf sourceAId store |> List.head
    let targetCall = DsQuery.callsOf targetId store |> List.head
    let sourceAApiCall = sourceACall.ApiCalls |> Seq.head

    store.AddCallCondition(targetCall.Id, CallConditionType.ComAux)
    let conditionId =
        store.Calls[targetCall.Id].CallConditions
        |> Seq.head
        |> fun condition -> condition.Id

    store.AddApiCallsToConditionBatch(targetCall.Id, conditionId, [ sourceAApiCall.Id ]) |> ignore

    let mermaid = MermaidExporter.flowToMermaid store flowId

    let importedStore = createStore()
    let _, _, importedFlowId = setupProjectWithFlow importedStore

    match MermaidParser.parse mermaid with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->
        applyImportPlan importedStore graph FlowLevel importedFlowId

    let importedTargetWork =
        importedStore.Works.Values
        |> Seq.find (fun work -> work.ParentId = importedFlowId && work.Name = "TargetWork")

    let importedTargetCall =
        importedStore.Calls.Values
        |> Seq.find (fun call -> call.ParentId = importedTargetWork.Id && call.Name = "Target.Do")

    let importedCondition =
        importedStore.Calls.[importedTargetCall.Id].CallConditions
        |> Seq.head

    let importedSourceApiCall = importedCondition.Conditions |> Seq.head

    let importedSourceCall =
        importedStore.Calls.Values
        |> Seq.find (fun call -> call.ApiCalls |> Seq.exists (fun apiCall -> apiCall.Id = importedSourceApiCall.Id))

    let importedSourceWork = importedStore.Works.[importedSourceCall.ParentId]
    Assert.Equal("SourceA", importedSourceWork.Name)
