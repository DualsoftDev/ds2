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
    projectId, systemId

let private setupProjectWithFlow (store: DsStore) =
    let projectId, systemId = setupProject store
    let flowId = store.AddFlow("TestFlow", systemId)
    projectId, systemId, flowId

let private setupProjectWithWork (store: DsStore) =
    let projectId, systemId, flowId = setupProjectWithFlow store
    let workId = store.AddWork("TestWork", flowId)
    projectId, systemId, flowId, workId

let private applyImportPlan (store: DsStore) (graph: MermaidGraph) (level: ImportLevel) (parentId: System.Guid) =
    match MermaidImporter.buildImportPlan store graph level parentId with
    | Error errors ->
        let joined = System.String.Join("\n", errors)
        Assert.Fail($"Import plan failed: {joined}")
    | Ok plan ->
        store.ApplyImportPlan("Mermaid import", plan)

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
    | Error errors -> Assert.Fail($"Parse failed: {errors}")
    | Ok graph ->
        match MermaidImporter.buildImportPlan store graph FlowLevel flowId with
        | Error buildErrors ->
            let joined = System.String.Join("\n", buildErrors)
            Assert.Fail($"Plan build failed: {joined}")
        | Ok plan ->
            Assert.NotEmpty(plan.Operations)
            Assert.Equal(beforeWorks, store.Works.Count)
            Assert.Equal(beforeCalls, store.Calls.Count)
            Assert.Equal(beforeArrows, store.ArrowWorks.Count + store.ArrowCalls.Count)

[<Fact>]
let ``Flow 2-depth creates works calls and arrows`` () =
    let store = createStore()
    let _, systemId, flowId = setupProjectWithFlow store

    match MermaidParser.parse mermaid2Depth with
    | Error errors -> Assert.Fail($"Parse failed: {errors}")
    | Ok graph -> applyImportPlan store graph FlowLevel flowId

    let works = store.Works.Values |> Seq.filter (fun work -> work.ParentId = flowId) |> Seq.toList
    Assert.Equal(2, works.Length)

    let calls = store.Calls.Values |> Seq.filter (fun call -> works |> List.exists (fun work -> work.Id = call.ParentId)) |> Seq.toList
    Assert.True(calls.Length >= 3)

    let workArrows =
        store.ArrowWorks.Values
        |> Seq.filter (fun arrow -> arrow.ParentId = systemId)
        |> Seq.toList
    Assert.True(workArrows.Length >= 1)
    Assert.True(store.ArrowCalls.Count >= 1)

[<Fact>]
let ``Flow 1-depth maps global nodes to works`` () =
    let store = createStore()
    let _, systemId, flowId = setupProjectWithFlow store

    match MermaidParser.parse mermaid1Depth with
    | Error errors -> Assert.Fail($"Parse failed: {errors}")
    | Ok graph -> applyImportPlan store graph FlowLevel flowId

    let works = store.Works.Values |> Seq.filter (fun work -> work.ParentId = flowId) |> Seq.toList
    Assert.Equal(2, works.Length)

    let arrows =
        store.ArrowWorks.Values
        |> Seq.filter (fun arrow -> arrow.ParentId = systemId)
        |> Seq.toList
    Assert.Single(arrows) |> ignore

[<Fact>]
let ``Work 1-depth maps global nodes to calls`` () =
    let store = createStore()
    let _, _, _, workId = setupProjectWithWork store

    match MermaidParser.parse mermaid1Depth with
    | Error errors -> Assert.Fail($"Parse failed: {errors}")
    | Ok graph -> applyImportPlan store graph WorkLevel workId

    let calls = store.Calls.Values |> Seq.filter (fun call -> call.ParentId = workId) |> Seq.toList
    Assert.Equal(2, calls.Length)

    let arrows = store.ArrowCalls.Values |> Seq.filter (fun arrow -> arrow.ParentId = workId) |> Seq.toList
    Assert.Single(arrows) |> ignore

[<Fact>]
let ``Work call names split device alias and api name`` () =
    let store = createStore()
    let _, _, _, workId = setupProjectWithWork store

    let mermaid =
        """
graph TD
    A["CAR_SV.ON"]
"""

    match MermaidParser.parse mermaid with
    | Error errors -> Assert.Fail($"Parse failed: {errors}")
    | Ok graph -> applyImportPlan store graph WorkLevel workId

    let call = store.Calls.Values |> Seq.find (fun current -> current.ParentId = workId)
    Assert.Equal("CAR_SV", call.DevicesAlias)
    Assert.Equal("ON", call.ApiName)

[<Fact>]
let ``System level maps nested subgraphs to system flow work and call structure`` () =
    let store = createStore()
    let projectId, _ = setupProject store

    let mermaid =
        """
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
    | Error errors -> Assert.Fail($"Parse failed: {errors}")
    | Ok graph -> applyImportPlan store graph SystemLevel projectId

    let project = store.Projects.[projectId]
    Assert.True(project.ActiveSystemIds.Count >= 3)
    Assert.True(store.Systems.Count >= 3)
    Assert.True(store.Flows.Count >= 3)
    Assert.True(store.Works.Count >= 3)
    Assert.True(store.Calls.Count >= 3)

[<Fact>]
let ``Undo rolls back full Mermaid import in one step`` () =
    let store = createStore()
    let _, _, flowId = setupProjectWithFlow store
    let beforeWorkCount = store.Works.Count
    let beforeCallCount = store.Calls.Count
    let beforeArrowCallCount = store.ArrowCalls.Count

    match MermaidParser.parse mermaid2Depth with
    | Error errors -> Assert.Fail($"Parse failed: {errors}")
    | Ok graph -> applyImportPlan store graph FlowLevel flowId

    Assert.True(store.Works.Count > beforeWorkCount)
    Assert.True(store.Calls.Count > beforeCallCount)

    store.Undo()

    Assert.Equal(beforeWorkCount, store.Works.Count)
    Assert.Equal(beforeCallCount, store.Calls.Count)
    Assert.Equal(beforeArrowCallCount, store.ArrowCalls.Count)
