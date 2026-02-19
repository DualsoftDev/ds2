module Ds2.UI.Core.TreeProjection

open System
open Ds2.Core

let private leafNodes entityType parentId (items: 'a list) getId getName =
    items
    |> List.map (fun item ->
        { Id = getId item
          EntityType = entityType
          Name = getName item
          ParentId = Some parentId
          Children = [] })

/// PassiveSystem 하위 노드: Flow(+Work) 목록 + ApiDefs 카테고리 폴더
let private buildDeviceSystemChildren (store: DsStore) (systemId: Guid) : TreeNodeInfo list =
    let flows =
        DsQuery.flowsOf systemId store
        |> List.map (fun flow ->
            let works =
                DsQuery.worksOf flow.Id store
                |> List.map (fun work ->
                    { Id = work.Id
                      EntityType = "Work"
                      Name = work.Name
                      ParentId = Some flow.Id
                      Children = [] })
            { Id = flow.Id
              EntityType = "Flow"
              Name = flow.Name
              ParentId = Some systemId
              Children = works })

    let apiDefs = DsQuery.apiDefsOf systemId store
    let apiDefsCategory =
        if apiDefs.IsEmpty then []
        else
            let catId = UiDefaults.apiDefCategoryId systemId
            [ { Id = catId
                EntityType = "ApiDefCategory"
                Name = "ApiDefs"
                ParentId = Some systemId
                Children =
                    apiDefs |> List.map (fun a ->
                        { Id = a.Id
                          EntityType = "ApiDef"
                          Name = a.Name
                          ParentId = Some catId
                          Children = [] }) } ]

    flows @ apiDefsCategory

let private buildSystemChildren (store: DsStore) (systemId: Guid) =
    let flows =
        DsQuery.flowsOf systemId store
        |> List.map (fun flow ->
            let works =
                DsQuery.worksOf flow.Id store
                |> List.map (fun work ->
                    let calls =
                        DsQuery.callsOf work.Id store
                        |> List.map (fun c ->
                            { Id = c.Id
                              EntityType = "Call"
                              Name = c.Name
                              ParentId = Some work.Id
                              Children = [] })
                    { Id = work.Id
                      EntityType = "Work"
                      Name = work.Name
                      ParentId = Some flow.Id
                      Children = calls })
            { Id = flow.Id
              EntityType = "Flow"
              Name = flow.Name
              ParentId = Some systemId
              Children = works })

    let hwAndApi = [
        yield! leafNodes "ApiDef" systemId (DsQuery.apiDefsOf systemId store) (fun a -> a.Id) (fun a -> a.Name)
        yield! leafNodes "Button" systemId (DsQuery.buttonsOf systemId store) (fun b -> b.Id) (fun b -> b.Name)
        yield! leafNodes "Lamp" systemId (DsQuery.lampsOf systemId store) (fun l -> l.Id) (fun l -> l.Name)
        yield! leafNodes "Condition" systemId (DsQuery.conditionsOf systemId store) (fun c -> c.Id) (fun c -> c.Name)
        yield! leafNodes "Action" systemId (DsQuery.actionsOf systemId store) (fun a -> a.Id) (fun a -> a.Name)
    ]

    flows @ hwAndApi

let private buildProjectTree (store: DsStore) (systems: DsSystem list) (project: Project) =
    let systemNodes =
        systems
        |> List.map (fun sys ->
            { Id = sys.Id
              EntityType = "System"
              Name = sys.Name
              ParentId = Some project.Id
              Children = buildSystemChildren store sys.Id })

    { Id = project.Id
      EntityType = "Project"
      Name = project.Name
      ParentId = None
      Children = systemNodes }

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
              EntityType = "System"
              Name = system.Name
              ParentId = Some deviceRootId
              Children = buildDeviceSystemChildren store system.Id })

    [ { Id = deviceRootId
        EntityType = "DeviceRoot"
        Name = "Device system"
        ParentId = None
        Children = deviceSystemNodes } ]

let buildTree (store: DsStore) : TreeNodeInfo list =
    DsQuery.allProjects store
    |> List.map (fun proj ->
        buildProjectTree store (DsQuery.projectSystemsOf proj.Id store) proj)

let buildTrees (store: DsStore) : TreeNodeInfo list * TreeNodeInfo list =
    let controlTree =
        DsQuery.allProjects store
        |> List.map (fun proj -> buildProjectTree store (DsQuery.activeSystemsOf proj.Id store) proj)

    let deviceTree = buildDeviceTree store

    controlTree, deviceTree
