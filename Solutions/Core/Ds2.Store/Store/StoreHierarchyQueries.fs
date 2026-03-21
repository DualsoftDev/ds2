module Ds2.Store.StoreHierarchyQueries

open System
open Ds2.Core

let flowsForSystem (store: DsStore) (systemId: Guid) : (Guid * string) list =
    DsQuery.flowsOf systemId store
    |> List.map (fun flow -> (flow.Id, flow.Name))

let findProjectOfSystem (store: DsStore) (systemId: Guid) : Guid option =
    DsQuery.allProjects store
    |> List.tryPick (fun project ->
        if project.ActiveSystemIds.Contains(systemId) || project.PassiveSystemIds.Contains(systemId)
        then Some project.Id
        else None)

/// 계층을 거슬러 올라가며 targetKind 수준의 조상 ID를 찾는다.
/// Call → Work → Flow → System 순으로 부모를 추적.
let resolveTarget (store: DsStore) (targetKind: EntityKind) (entityKind: EntityKind) (entityId: Guid) : Guid option =
    let rec walk kind id =
        if kind = targetKind then Some id
        else
            match kind with
            | EntityKind.Call -> DsQuery.getCall id store |> Option.bind (fun call -> walk EntityKind.Work call.ParentId)
            | EntityKind.Work -> DsQuery.getWork id store |> Option.bind (fun work -> walk EntityKind.Flow work.ParentId)
            | EntityKind.Flow -> DsQuery.getFlow id store |> Option.bind (fun flow -> walk EntityKind.System flow.ParentId)
            | _ -> None
    walk entityKind entityId

let parentIdOf (store: DsStore) (entityKind: EntityKind) (entityId: Guid) : Guid option =
    match entityKind with
    | EntityKind.Call -> DsQuery.getCall entityId store |> Option.map (fun call -> call.ParentId)
    | EntityKind.Work -> DsQuery.getWork entityId store |> Option.map (fun work -> work.ParentId)
    | EntityKind.Flow -> DsQuery.getFlow entityId store |> Option.map (fun flow -> flow.ParentId)
    | _ -> None

let tryFindSystemIdForEntity store entityKind entityId =
    resolveTarget store EntityKind.System entityKind entityId

let tryFindProjectIdForEntity (store: DsStore) (entityKind: EntityKind) (entityId: Guid) : Guid option =
    match entityKind with
    | EntityKind.Project ->
        DsQuery.getProject entityId store
        |> Option.map (fun project -> project.Id)
    | _ ->
        tryFindSystemIdForEntity store entityKind entityId
        |> Option.bind (findProjectOfSystem store)

/// 모든 Passive System 내 ApiDef 검색.
/// - apiNameFilter: ApiDef 이름 포함 검색 (빈 문자열 = 전체)
let findApiDefs (store: DsStore) (apiNameFilter: string) : ApiDefMatch list =
    let apiNameFilter = apiNameFilter.Trim()
    DsQuery.allProjects store
    |> List.collect (fun project -> DsQuery.passiveSystemsOf project.Id store)
    |> List.distinctBy (fun system -> system.Id)
    |> List.collect (fun system ->
        DsQuery.apiDefsOf system.Id store
        |> List.filter (fun apiDef ->
            String.IsNullOrEmpty(apiNameFilter) ||
            apiDef.Name.IndexOf(apiNameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
        |> List.map (fun apiDef -> ApiDefMatch(apiDef.Id, apiDef.Name, system.Id, system.Name)))
