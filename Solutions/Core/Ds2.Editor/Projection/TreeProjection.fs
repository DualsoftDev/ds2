module Ds2.Editor.EditorTreeProjection

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store

let inline private namedLeafNodes entityType parentId items =
    items
    |> List.map (fun item ->
        { Id = (^a: (member Id: Guid) item)
          EntityKind = entityType
          Name = (^a: (member Name: string) item)
          ParentId = Some parentId
          Children = [] })

/// PassiveSystem 하위 노드: Flow(+Work) 목록 + ApiDefs 카테고리 폴더
let private buildDeviceSystemChildren (store: DsStore) (systemId: Guid) : TreeNodeInfo list =
    let flows =
        DsQuery.flowsOf systemId store
        |> List.map (fun flow ->
            let works =
                DsQuery.originalWorksOf flow.Id store
                |> List.map (fun work ->
                    { Id = work.Id
                      EntityKind = EntityKind.Work
                      Name = work.LocalName
                      ParentId = Some flow.Id
                      Children = [] })
            { Id = flow.Id
              EntityKind = EntityKind.Flow
              Name = flow.Name
              ParentId = Some systemId
              Children = works })

    let apiDefs = DsQuery.apiDefsOf systemId store
    let apiDefsCategory =
        if apiDefs.IsEmpty then []
        else
            let catId = UiDefaults.apiDefCategoryId systemId
            [ { Id = catId
                EntityKind = EntityKind.ApiDefCategory
                Name = "ApiDefs"
                ParentId = Some systemId
                Children =
                    apiDefs |> List.map (fun a ->
                        { Id = a.Id
                          EntityKind = EntityKind.ApiDef
                          Name = a.Name
                          ParentId = Some catId
                          Children = [] }) } ]

    flows @ apiDefsCategory

let private buildSystemChildren (store: DsStore) (systemId: Guid) =
    let flows =
        DsQuery.flowsOf systemId store
        |> List.map (fun flow ->
            let works =
                DsQuery.originalWorksOf flow.Id store
                |> List.map (fun work ->
                    let calls =
                        DsQuery.callsOf work.Id store
                        |> List.map (fun c ->
                            { Id = c.Id
                              EntityKind = EntityKind.Call
                              Name = c.Name
                              ParentId = Some work.Id
                              Children = [] })
                    { Id = work.Id
                      EntityKind = EntityKind.Work
                      Name = work.LocalName
                      ParentId = Some flow.Id
                      Children = calls })
            { Id = flow.Id
              EntityKind = EntityKind.Flow
              Name = flow.Name
              ParentId = Some systemId
              Children = works })

    let hwAndApi = [
        yield! namedLeafNodes EntityKind.ApiDef    systemId (DsQuery.apiDefsOf    systemId store)
        yield! namedLeafNodes EntityKind.Button    systemId (DsQuery.buttonsOf    systemId store)
        yield! namedLeafNodes EntityKind.Lamp      systemId (DsQuery.lampsOf      systemId store)
        yield! namedLeafNodes EntityKind.Condition systemId (DsQuery.conditionsOf systemId store)
        yield! namedLeafNodes EntityKind.Action    systemId (DsQuery.actionsOf    systemId store)
    ]

    flows @ hwAndApi

let private buildControlRoots (store: DsStore) : TreeNodeInfo list =
    DsQuery.allProjects store
    |> List.collect (fun project ->
        DsQuery.activeSystemsOf project.Id store
        |> List.map (fun system ->
            { Id = system.Id
              EntityKind = EntityKind.System
              Name = system.Name
              ParentId = None
              Children = buildSystemChildren store system.Id }))

let private buildDeviceTree (store: DsStore) : TreeNodeInfo list =
    let deviceRootId = UiDefaults.DeviceTreeRootId

    let deviceSystems =
        DsQuery.allProjects store
        |> List.collect (fun p -> DsQuery.passiveSystemsOf p.Id store)
        |> List.distinctBy (fun s -> s.Id)
        |> List.sortBy (fun s -> s.Name)

    let deviceSystemNodes =
        deviceSystems
        |> List.map (fun system ->
            { Id = system.Id
              EntityKind = EntityKind.System
              Name = system.Name
              ParentId = Some deviceRootId
              Children = buildDeviceSystemChildren store system.Id })

    [ { Id = deviceRootId
        EntityKind = EntityKind.DeviceRoot
        Name = "Device system"
        ParentId = None
        Children = deviceSystemNodes } ]

[<CompiledName("BuildTrees")>]
let buildTrees (store: DsStore) : TreeNodeInfo list * TreeNodeInfo list =
    let controlTree = buildControlRoots store
    let deviceTree = buildDeviceTree store

    controlTree, deviceTree
