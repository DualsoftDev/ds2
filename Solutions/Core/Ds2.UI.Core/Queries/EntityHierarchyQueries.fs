module Ds2.UI.Core.EntityHierarchyQueries

open System
open Ds2.Core

let flowsForSystem (store: DsStore) (systemId: Guid) : (Guid * string) list =
    DsQuery.flowsOf systemId store
    |> List.map (fun f -> (f.Id, f.Name))

let entityTypeForTabKind (tabKind: TabKind) : string option =
    match tabKind with
    | TabKind.System -> Some EntityTypeNames.System
    | TabKind.Flow -> Some EntityTypeNames.Flow
    | TabKind.Work -> Some EntityTypeNames.Work
    | _ -> None

let findProjectOfSystem (store: DsStore) (systemId: Guid) : Guid option =
    DsQuery.allProjects store
    |> List.tryPick (fun p ->
        if p.ActiveSystemIds.Contains(systemId) || p.PassiveSystemIds.Contains(systemId)
        then Some p.Id
        else None)

/// 계층을 거슬러 올라가며 targetKind 수준의 조상 ID를 찾는다.
/// Call → Work → Flow → System 순으로 부모를 추적.
let resolveTarget (store: DsStore) (targetKind: EntityKind) (entityType: string) (entityId: Guid) : Guid option =
    let rec walk kind id =
        if kind = targetKind then Some id
        else
            match kind with
            | EntityKind.Call -> DsQuery.getCall id store |> Option.bind (fun c -> walk EntityKind.Work c.ParentId)
            | EntityKind.Work -> DsQuery.getWork id store |> Option.bind (fun w -> walk EntityKind.Flow w.ParentId)
            | EntityKind.Flow -> DsQuery.getFlow id store |> Option.bind (fun f -> walk EntityKind.System f.ParentId)
            | _ -> None
    match EntityKind.tryOfString entityType with
    | ValueSome kind -> walk kind entityId
    | ValueNone -> None

let parentIdOf (store: DsStore) (entityType: string) (entityId: Guid) : Guid option =
    match entityType with
    | EntityTypeNames.Call -> DsQuery.getCall entityId store |> Option.map (fun c -> c.ParentId)
    | EntityTypeNames.Work -> DsQuery.getWork entityId store |> Option.map (fun w -> w.ParentId)
    | EntityTypeNames.Flow -> DsQuery.getFlow entityId store |> Option.map (fun f -> f.ParentId)
    | _                    -> None

let tryFindWorkIdForEntity   store entityType entityId = resolveTarget store EntityKind.Work   entityType entityId
let tryFindFlowIdForEntity   store entityType entityId = resolveTarget store EntityKind.Flow   entityType entityId
let tryFindSystemIdForEntity store entityType entityId = resolveTarget store EntityKind.System entityType entityId

let tryFindProjectIdForEntity (store: DsStore) (entityType: string) (entityId: Guid) : Guid option =
    match entityType with
    | EntityTypeNames.Project ->
        DsQuery.getProject entityId store
        |> Option.map (fun project -> project.Id)
    | _ ->
        tryFindSystemIdForEntity store entityType entityId
        |> Option.bind (findProjectOfSystem store)

let private lookupEntity (store: DsStore) (tabKind: TabKind) (id: Guid) : (Guid * string) option =
    match tabKind with
    | TabKind.System -> DsQuery.getSystem id store |> Option.map (fun s -> s.Id, s.Name)
    | TabKind.Flow   -> DsQuery.getFlow   id store |> Option.map (fun f -> f.Id, f.Name)
    | TabKind.Work   -> DsQuery.getWork   id store |> Option.map (fun w -> w.Id, w.Name)
    | _              -> None

let private tabKindForEntityType entityType =
    match entityType with
    | EntityTypeNames.System -> Some TabKind.System
    | EntityTypeNames.Flow   -> Some TabKind.Flow
    | EntityTypeNames.Work   -> Some TabKind.Work
    | _                      -> None

let tryOpenTabForEntity (store: DsStore) (entityType: string) (entityId: Guid) : TabOpenInfo option =
    tabKindForEntityType entityType
    |> Option.bind (fun kind ->
        lookupEntity store kind entityId
        |> Option.map (fun (id, name) -> { Kind = kind; RootId = id; Title = name }))

let tabTitle (store: DsStore) (tabKind: TabKind) (rootId: Guid) : string option =
    lookupEntity store tabKind rootId |> Option.map snd

let tabExists (store: DsStore) (tabKind: TabKind) (rootId: Guid) : bool =
    tabTitle store tabKind rootId |> Option.isSome

let flowIdsForTab (store: DsStore) (tabKind: TabKind) (rootId: Guid) : Guid list =
    match tabKind with
    | TabKind.System ->
        flowsForSystem store rootId
        |> List.map fst
    | TabKind.Flow -> [ rootId ]
    | TabKind.Work ->
        DsQuery.getWork rootId store
        |> Option.map (fun w -> [ w.ParentId ])
        |> Option.defaultValue []
    | _ -> []

/// 모든 Passive System 내 ApiDef 검색.
/// - aliasFilter: System 이름 포함 검색 (빈 문자열 = 전체)
/// - apiNameFilter: ApiDef 이름 포함 검색 (빈 문자열 = 전체)
let findApiDefs (store: DsStore) (aliasFilter: string) (apiNameFilter: string) : ApiDefMatch list =
    let aliasFilter = aliasFilter.Trim()
    let apiNameFilter = apiNameFilter.Trim()
    DsQuery.allProjects store
    |> List.collect (fun p -> DsQuery.passiveSystemsOf p.Id store)
    |> List.distinctBy (fun s -> s.Id)
    |> List.filter (fun sys ->
        String.IsNullOrEmpty(aliasFilter) ||
        sys.Name.IndexOf(aliasFilter, StringComparison.OrdinalIgnoreCase) >= 0)
    |> List.collect (fun sys ->
        DsQuery.apiDefsOf sys.Id store
        |> List.filter (fun a ->
            String.IsNullOrEmpty(apiNameFilter) ||
            a.Name.IndexOf(apiNameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
        |> List.map (fun a -> ApiDefMatch(a.Id, a.Name, sys.Id, sys.Name)))
