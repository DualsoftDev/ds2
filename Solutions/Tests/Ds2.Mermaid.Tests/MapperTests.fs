module Ds2.Mermaid.Tests.MapperTests

open Xunit
open Ds2.Core
open Ds2.UI.Core
open Ds2.Mermaid

// ─── 헬퍼 ───

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

/// 2-depth: subgraph 있음
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

/// 1-depth: subgraph 없이 노드만
let private mermaid1Depth =
    """
graph TD
    X["WorkX"]
    Y["WorkY"]
    X --> Y
"""

// ─── System 2-depth ───

[<Fact>]
let ``System 2-depth — Flow + Work + ArrowBetweenWorks 생성`` () =
    let store = createStore()
    let _, systemId = setupProject store

    match MermaidParser.parse mermaid2Depth with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->

    let result = MermaidImporter.importIntoStore store graph SystemLevel systemId
    Assert.True(Result.isOk result)

    // Flow 2개 생성 확인
    let flows = store.Flows.Values |> Seq.filter (fun f -> f.ParentId = systemId) |> Seq.toList
    Assert.Equal(2, flows.Length)

    // Work 3개 생성 확인 (A, B, C)
    let works = store.Works.Values |> Seq.toList
    Assert.True(works.Length >= 3)

    // ArrowBetweenWorks (A→B는 같은 subgraph 내부 = 같은 Flow 내 Work 간)
    let arrows = store.ArrowWorks.Values |> Seq.filter (fun a -> a.ParentId = systemId) |> Seq.toList
    Assert.True(arrows.Length >= 1)

// ─── Flow 2-depth ───

[<Fact>]
let ``Flow 2-depth — Work + Call + Arrow 생성`` () =
    let store = createStore()
    let _, systemId, flowId = setupProjectWithFlow store

    match MermaidParser.parse mermaid2Depth with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->

    let result = MermaidImporter.importIntoStore store graph FlowLevel flowId
    Assert.True(Result.isOk result)

    // Work 2개 (W1, W2)
    let works = store.Works.Values |> Seq.filter (fun w -> w.ParentId = flowId) |> Seq.toList
    Assert.Equal(2, works.Length)

    // Call 3개 (A, B, C)
    let calls = store.Calls.Values |> Seq.toList
    Assert.True(calls.Length >= 3)

    // ArrowBetweenCalls (A→B, 같은 Work 내부)
    Assert.True(store.ArrowCalls.Count >= 1)

    // ArrowBetweenWorks (W1→W2, GlobalEdge)
    let workArrows = store.ArrowWorks.Values |> Seq.filter (fun a -> a.ParentId = systemId) |> Seq.toList
    Assert.True(workArrows.Length >= 1)

// ─── Flow 1-depth ───

[<Fact>]
let ``Flow 1-depth — GlobalNode → Work + ArrowBetweenWorks`` () =
    let store = createStore()
    let _, systemId, flowId = setupProjectWithFlow store

    match MermaidParser.parse mermaid1Depth with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->

    let result = MermaidImporter.importIntoStore store graph FlowLevel flowId
    Assert.True(Result.isOk result)

    // Work 2개 (X, Y)
    let works = store.Works.Values |> Seq.filter (fun w -> w.ParentId = flowId) |> Seq.toList
    Assert.Equal(2, works.Length)

    // ArrowBetweenWorks 1개 (X→Y)
    let arrows = store.ArrowWorks.Values |> Seq.filter (fun a -> a.ParentId = systemId) |> Seq.toList
    Assert.Equal(1, arrows.Length)

// ─── Work 1-depth ───

[<Fact>]
let ``Work 1-depth — GlobalNode → Call + ArrowBetweenCalls`` () =
    let store = createStore()
    let _, _, _, workId = setupProjectWithWork store

    match MermaidParser.parse mermaid1Depth with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->

    let result = MermaidImporter.importIntoStore store graph WorkLevel workId
    Assert.True(Result.isOk result)

    // Call 2개 (X, Y)
    let calls = store.Calls.Values |> Seq.filter (fun c -> c.ParentId = workId) |> Seq.toList
    Assert.Equal(2, calls.Length)

    // ArrowBetweenCalls 1개 (X→Y)
    let arrows = store.ArrowCalls.Values |> Seq.filter (fun a -> a.ParentId = workId) |> Seq.toList
    Assert.Equal(1, arrows.Length)

// ─── Call 이름 분리 ───

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

    MermaidImporter.importIntoStore store graph WorkLevel workId |> ignore

    let call = store.Calls.Values |> Seq.find (fun c -> c.ParentId = workId)
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

    MermaidImporter.importIntoStore store graph WorkLevel workId |> ignore

    let call = store.Calls.Values |> Seq.find (fun c -> c.ParentId = workId)
    Assert.Equal("imported", call.DevicesAlias)
    Assert.Equal("SimpleCall", call.ApiName)

// ─── Undo ───

[<Fact>]
let ``Undo 1회로 Mermaid 임포트 전체 롤백`` () =
    let store = createStore()
    let _, _systemId, flowId = setupProjectWithFlow store

    let beforeWorkCount = store.Works.Count
    let beforeCallCount = store.Calls.Count
    let beforeArrowCallCount = store.ArrowCalls.Count

    match MermaidParser.parse mermaid2Depth with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->

    MermaidImporter.importIntoStore store graph FlowLevel flowId |> ignore

    // 임포트 후 엔티티가 증가했는지 확인
    Assert.True(store.Works.Count > beforeWorkCount)
    Assert.True(store.Calls.Count > beforeCallCount)

    // Undo 1회
    store.Undo()

    // 원래 상태로 복원
    Assert.Equal(beforeWorkCount, store.Works.Count)
    Assert.Equal(beforeCallCount, store.Calls.Count)
    Assert.Equal(beforeArrowCallCount, store.ArrowCalls.Count)

// ─── 검증 ───

[<Fact>]
let ``WorkLevel에 subgraph가 있으면 Invalid`` () =
    match MermaidParser.parse mermaid2Depth with
    | Error _ -> ()
    | Ok graph ->
        match MermaidAnalyzer.validate graph WorkLevel with
        | Valid -> Assert.Fail("subgraph가 있는데 WorkLevel이 Valid")
        | Invalid errors -> Assert.True(errors.Length > 0)

// ─── ArrowType 매핑 ───

[<Fact>]
let ``ArrowLabel → ArrowType 변환`` () =
    Assert.Equal(ArrowType.Start, MermaidMapper.mapArrowType NoLabel)
    Assert.Equal(ArrowType.Reset, MermaidMapper.mapArrowType Interlock)
    Assert.Equal(ArrowType.Reset, MermaidMapper.mapArrowType SelfReset)
    Assert.Equal(ArrowType.StartReset, MermaidMapper.mapArrowType StartReset)
    Assert.Equal(ArrowType.Start, MermaidMapper.mapArrowType StartEdge)
    Assert.Equal(ArrowType.Reset, MermaidMapper.mapArrowType ResetEdge)

// ─── System 레벨 — Passive subgraph ───

let private mermaidWithPassiveDevices =
    """
flowchart TD
    subgraph STN1["STN1"]
        STN1_Work1["Work1"]
        STN1_Work2["Work2"]
    end
    subgraph STN2["STN2"]
        STN2_Work3["Work3"]
    end
    STN1_Work1 -->|Reset| STN1_Work2
    subgraph Devices["Passive Devices"]
        Device1["Device1"]
        Device2["Device2"]
    end
"""

[<Fact>]
let ``System 레벨 — Passive subgraph → Device Tree에 빈 passive system 생성`` () =
    let store = createStore()
    let projectId, systemId = setupProject store

    match MermaidParser.parse mermaidWithPassiveDevices with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->

    let result = MermaidImporter.importIntoStore store graph SystemLevel systemId
    Assert.True(Result.isOk result)

    // Active Flow 2개 (STN1, STN2 — Devices는 passive이므로 Flow 안 됨)
    let flows = store.Flows.Values |> Seq.filter (fun f -> f.ParentId = systemId) |> Seq.toList
    Assert.Equal(2, flows.Length)

    // Work 3개 (Work1, Work2, Work3)
    let works = store.Works.Values |> Seq.toList
    Assert.True(works.Length >= 3)

    // Passive Device System 2개 (Device1, Device2)
    let project = store.Projects.[projectId]
    let passiveSystems = project.PassiveSystemIds |> Seq.choose (fun id -> store.Systems.TryGetValue(id) |> function true, s -> Some s | _ -> None) |> Seq.toList
    Assert.Equal(2, passiveSystems.Length)
    Assert.Contains(passiveSystems, fun s -> s.Name = "Device1")
    Assert.Contains(passiveSystems, fun s -> s.Name = "Device2")

    // ArrowBetweenWorks (STN1 내 Reset)
    let arrows = store.ArrowWorks.Values |> Seq.filter (fun a -> a.ParentId = systemId) |> Seq.toList
    Assert.True(arrows.Length >= 1)

[<Fact>]
let ``System 레벨 Passive Undo — 전체 롤백`` () =
    let store = createStore()
    let _, systemId = setupProject store
    let beforeSystemCount = store.Systems.Count

    match MermaidParser.parse mermaidWithPassiveDevices with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->

    MermaidImporter.importIntoStore store graph SystemLevel systemId |> ignore
    Assert.True(store.Systems.Count > beforeSystemCount)

    store.Undo()
    Assert.Equal(beforeSystemCount, store.Systems.Count)

// ─── Device auto-creation (Work 레벨) ───

[<Fact>]
let ``Work 레벨 — Device.ApiName 형식 Call이면 Device System 자동 생성`` () =
    let store = createStore()
    let projectId, systemId, flowId, workId = setupProjectWithWork store

    let mermaid = """
graph TD
    A["SV.ON"]
    B["SV.OFF"]
    A --> B
"""
    match MermaidParser.parse mermaid with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->

    MermaidImporter.importIntoStore store graph WorkLevel workId |> ignore

    // Call 2개
    let calls = store.Calls.Values |> Seq.filter (fun c -> c.ParentId = workId) |> Seq.toList
    Assert.Equal(2, calls.Length)

    // Device System 생성 확인 (TestFlow_SV)
    let project = store.Projects.Values |> Seq.head
    let passiveSystems = project.PassiveSystemIds |> Seq.choose (fun id -> store.Systems.TryGetValue(id) |> function true, s -> Some s | _ -> None) |> Seq.toList
    Assert.True(passiveSystems.Length >= 1)

    // ApiDef 생성 확인
    let apiDefs = store.ApiDefs.Values |> Seq.toList
    Assert.True(apiDefs.Length >= 2)  // ON, OFF

    // ApiCall 연결 확인
    let apiCalls = store.ApiCalls.Values |> Seq.toList
    Assert.True(apiCalls.Length >= 2)

// ─── 인라인 노드 정의 (naming bug fix) ───

[<Fact>]
let ``인라인 노드 정의 — Arrow에서 A["Label"] 형식도 노드로 등록`` () =
    let mermaid = """
graph TD
    A["SV.ON"] --> B["SV.OFF"]
"""
    match MermaidParser.parse mermaid with
    | Error e -> Assert.Fail($"파싱 실패: {e}")
    | Ok graph ->

    // 노드 2개가 등록되어야 함
    Assert.Equal(2, graph.GlobalNodes.Length)
    Assert.Contains(graph.GlobalNodes, fun n -> n.Id = "A" && n.Label = "SV.ON")
    Assert.Contains(graph.GlobalNodes, fun n -> n.Id = "B" && n.Label = "SV.OFF")

    // Edge는 ID 기준
    Assert.Equal(1, graph.GlobalEdges.Length)
    Assert.Equal("A", graph.GlobalEdges.[0].SourceId)
    Assert.Equal("B", graph.GlobalEdges.[0].TargetId)
