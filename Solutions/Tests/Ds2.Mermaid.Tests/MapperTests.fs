module Ds2.Mermaid.Tests.MapperTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Mermaid

let private createStore () = DsStore()

let private createProjectOnly (store: DsStore) =
    store.AddProject("TestProject")

let private createProjectWithFlow (store: DsStore) =
    let projectId = createProjectOnly store
    let systemId = store.AddSystem("ActiveSystem", projectId, true)
    let flowId = store.AddFlow("TestFlow", systemId)
    projectId, systemId, flowId

let private createProjectWithWork (store: DsStore) =
    let projectId, systemId, flowId = createProjectWithFlow store
    let workId = store.AddWork("TargetWork", flowId)
    projectId, systemId, flowId, workId

let private parseOk content =
    match MermaidParser.parse content with
    | Ok graph -> graph
    | Error errors -> failwithf "Parse failed: %A" errors

let private buildPlan store graph level parentId =
    match MermaidImporter.buildImportPlan store graph level parentId with
    | Ok plan -> plan
    | Error errors -> failwithf "Import plan failed: %A" errors

let private applyPlan store graph level parentId =
    let plan = buildPlan store graph level parentId
    store.ApplyImportPlan("Mermaid import", plan)

[<Fact>]
let ``buildImportPlan does not mutate store before apply`` () =
    let store = createStore()
    let _, _, flowId = createProjectWithFlow store
    let beforeWorks = store.Works.Count
    let beforeCalls = store.Calls.Count
    let beforeArrows = store.ArrowWorks.Count + store.ArrowCalls.Count

    let graph =
        parseOk """
graph TD
    subgraph W1["Work1"]
        A["DEV.ON"]
        B["DEV.OFF"]
        A --> B
    end
    subgraph W2["Work2"]
        C["MOTOR.RUN"]
    end
    A --> C
"""

    let plan = buildPlan store graph FlowLevel flowId

    Assert.NotEmpty(plan.Operations)
    Assert.Equal(beforeWorks, store.Works.Count)
    Assert.Equal(beforeCalls, store.Calls.Count)
    Assert.Equal(beforeArrows, store.ArrowWorks.Count + store.ArrowCalls.Count)

[<Fact>]
let ``flow level import creates exact work and arrow topology`` () =
    let store = createStore()
    let _, systemId, flowId = createProjectWithFlow store

    let graph =
        parseOk """
graph TD
    subgraph W1["Work1"]
        A["DEV.ON"]
        B["DEV.OFF"]
        A --> B
    end
    subgraph W2["Work2"]
        C["MOTOR.RUN"]
    end
    A --> C
"""

    applyPlan store graph FlowLevel flowId

    let works =
        Queries.worksOf flowId store
        |> List.sortBy (fun work -> work.LocalName)
    Assert.Equal<string list>(["Work1"; "Work2"], works |> List.map (fun work -> work.LocalName))

    let work1 = works |> List.find (fun work -> work.LocalName = "Work1")
    let work2 = works |> List.find (fun work -> work.LocalName = "Work2")

    let work1Calls =
        Queries.callsOf work1.Id store
        |> List.sortBy (fun call -> call.Name)
    Assert.Equal<string list>(["DEV.OFF"; "DEV.ON"], work1Calls |> List.map (fun call -> call.Name))

    let work2Calls = Queries.callsOf work2.Id store
    Assert.Single work2Calls |> ignore
    let work2Call = List.exactlyOne work2Calls
    Assert.Equal("MOTOR.RUN", work2Call.Name)

    let workArrows =
        store.ArrowWorks.Values
        |> Seq.filter (fun arrow -> arrow.ParentId = systemId)
        |> Seq.toList
    Assert.Single workArrows |> ignore
    let workArrow = List.exactlyOne workArrows
    Assert.Equal(systemId, workArrow.ParentId)
    Assert.Equal(work1.Id, workArrow.SourceId)
    Assert.Equal(work2.Id, workArrow.TargetId)
    Assert.Equal(ArrowType.Start, workArrow.ArrowType)

    Assert.Single store.ArrowCalls.Values |> ignore
    let callArrow = store.ArrowCalls.Values |> Seq.toList |> List.exactlyOne
    Assert.Equal(work1.Id, callArrow.ParentId)
    Assert.Equal(ArrowType.Start, callArrow.ArrowType)

    let sourceCall = store.Calls.[callArrow.SourceId]
    let targetCall = store.Calls.[callArrow.TargetId]
    Assert.Equal("DEV.ON", sourceCall.Name)
    Assert.Equal("DEV.OFF", targetCall.Name)

[<Fact>]
let ``work level import splits call alias and api name exactly`` () =
    let store = createStore()
    let _, _, _, workId = createProjectWithWork store

    let graph =
        parseOk """
graph TD
    A["CAR_SV.ON"]
    B["CAR_SV.OFF"]
    A --> B
"""

    applyPlan store graph WorkLevel workId

    let calls =
        Queries.callsOf workId store
        |> List.sortBy (fun call -> call.ApiName)

    Assert.Equal(2, calls.Length)
    Assert.Equal("CAR_SV", calls.[0].DevicesAlias)
    Assert.Equal("OFF", calls.[0].ApiName)
    Assert.Equal("CAR_SV", calls.[1].DevicesAlias)
    Assert.Equal("ON", calls.[1].ApiName)

    let callArrow = Assert.Single store.ArrowCalls.Values
    let sourceCall = store.Calls.[callArrow.SourceId]
    let targetCall = store.Calls.[callArrow.TargetId]
    Assert.Equal("CAR_SV.ON", sourceCall.Name)
    Assert.Equal("CAR_SV.OFF", targetCall.Name)

[<Fact>]
let ``system level import creates exact active and passive structure`` () =
    let store = createStore()
    let projectId = createProjectOnly store

    let graph =
        parseOk """
graph TD
    subgraph ACTIVE["ActiveSystem"]
        subgraph AF["ActiveFlow"]
            subgraph AW["ActiveWork"]
                A["DEV.ON"]
            end
        end
    end
    %% [Passive]
    subgraph DEVICE["DeviceSystem"]
        subgraph DF["DeviceFlow"]
            subgraph DW["DeviceWork"]
                B["DEV.RUN"]
            end
        end
    end
"""

    applyPlan store graph SystemLevel projectId

    let project = store.Projects.[projectId]
    let activeSystem =
        store.Systems.Values
        |> Seq.find (fun system -> system.Name = "ActiveSystem")
    let passiveSystem =
        store.Systems.Values
        |> Seq.find (fun system -> system.Name = "DeviceSystem")

    Assert.Contains(activeSystem.Id, project.ActiveSystemIds)
    Assert.Contains(passiveSystem.Id, project.PassiveSystemIds)

    let systems =
        store.Systems.Values
        |> Seq.sortBy (fun system -> system.Name)
        |> Seq.toList
    Assert.Contains("ActiveSystem", systems |> List.map (fun system -> system.Name))
    Assert.Contains("DeviceSystem", systems |> List.map (fun system -> system.Name))

    let flows =
        store.Flows.Values
        |> Seq.sortBy (fun flow -> flow.Name)
        |> Seq.toList
    let flowNames = flows |> List.map (fun flow -> flow.Name)
    Assert.Contains("ActiveFlow", flowNames)
    Assert.Contains("DeviceFlow", flowNames)

    let works =
        store.Works.Values
        |> Seq.sortBy (fun work -> work.LocalName)
        |> Seq.toList
    let workNames = works |> List.map (fun work -> work.LocalName)
    Assert.Contains("ActiveWork", workNames)
    Assert.Contains("DeviceWork", workNames)

    let calls =
        store.Calls.Values
        |> Seq.sortBy (fun call -> call.Name)
        |> Seq.toList
    let callNames = calls |> List.map (fun call -> call.Name)
    Assert.Contains("DEV.ON", callNames)
    Assert.Contains("DEV.RUN", callNames)

[<Fact>]
let ``undo rolls back one mermaid import unit`` () =
    let store = createStore()
    let _, _, flowId = createProjectWithFlow store

    let graph =
        parseOk """
graph TD
    subgraph W1["Work1"]
        A["DEV.ON"]
    end
"""

    let beforeWorks = store.Works.Count
    let beforeCalls = store.Calls.Count

    applyPlan store graph FlowLevel flowId
    Assert.True(store.Works.Count > beforeWorks)
    Assert.True(store.Calls.Count > beforeCalls)

    store.Undo()

    Assert.Equal(beforeWorks, store.Works.Count)
    Assert.Equal(beforeCalls, store.Calls.Count)
