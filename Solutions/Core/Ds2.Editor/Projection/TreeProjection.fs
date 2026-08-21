module Ds2.Editor.EditorTreeProjection

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store


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
        Queries.flowsOf systemId store
        |> List.map (fun flow ->
            let works =
                Queries.originalWorksOf flow.Id store
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

    let apiDefs = Queries.apiDefsOf systemId store
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
        Queries.flowsOf systemId store
        |> List.filter (fun f -> not f.IsDisabled)   // 비활성 Flow 는 트리에서 제외 (하단 "비활성화" 섹션으로)
        |> List.map (fun flow ->
            let works =
                Queries.originalWorksOf flow.Id store
                |> List.map (fun work ->
                    let calls =
                        Queries.originalCallsOf work.Id store
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
        yield! namedLeafNodes EntityKind.ApiDef    systemId (Queries.apiDefsOf    systemId store)
    ]

    flows @ hwAndApi

/// Control 트리 루트 = Project 노드, 그 아래 active System N개.
/// 멀티 PLC(라인 1개 = PLC N대) 지원으로 Project > System1..N 계층을 복원 —
/// System 이 루트였던 구 형태는 1 System 제한 시절의 축약이었다.
let private buildControlRoots (store: DsStore) : TreeNodeInfo list =
    Queries.allProjects store
    |> List.map (fun project ->
        let systems =
            Queries.activeSystemsOf project.Id store
            |> List.map (fun system ->
                { Id = system.Id
                  EntityKind = EntityKind.System
                  Name = system.Name
                  ParentId = Some project.Id
                  Children = buildSystemChildren store system.Id })
        { Id = project.Id
          EntityKind = EntityKind.Project
          Name = project.Name
          ParentId = None
          Children = systems })

let private buildDeviceTree (store: DsStore) : TreeNodeInfo list =
    let deviceRootId = UiDefaults.DeviceTreeRootId

    let deviceSystems =
        Queries.allProjects store
        |> List.collect (fun p -> Queries.passiveSystemsOf p.Id store)
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

/// Control(Active) System 의 비활성화(IsDisabled) Flow 목록 — Explorer 하단 "비활성화" 섹션용 (평면 리스트).
[<CompiledName("DisabledFlows")>]
let disabledFlows (store: DsStore) : TreeNodeInfo list =
    Queries.allProjects store
    |> List.collect (fun project ->
        Queries.activeSystemsOf project.Id store
        |> List.collect (fun system ->
            Queries.flowsOf system.Id store
            |> List.filter (fun f -> f.IsDisabled)
            |> List.map (fun flow ->
                { Id = flow.Id
                  EntityKind = EntityKind.Flow
                  Name = flow.Name
                  ParentId = Some system.Id
                  Children = [] })))
