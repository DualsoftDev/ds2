module Ds2.UI.Core.EntityHierarchyQueries

open System
open Ds2.Core

let flowsForSystem (store: DsStore) (systemId: Guid) : (Guid * string) list =
    DsQuery.flowsOf systemId store
    |> List.map (fun f -> (f.Id, f.Name))

let entityKindForTabKind (tabKind: TabKind) : EntityKind =
    match tabKind with
    | TabKind.System -> EntityKind.System
    | TabKind.Flow -> EntityKind.Flow
    | TabKind.Work -> EntityKind.Work
    | _ -> invalidArg (nameof tabKind) $"Unknown TabKind: {tabKind}"

let findProjectOfSystem (store: DsStore) (systemId: Guid) : Guid option =
    DsQuery.allProjects store
    |> List.tryPick (fun p ->
        if p.ActiveSystemIds.Contains(systemId) || p.PassiveSystemIds.Contains(systemId)
        then Some p.Id
        else None)

/// 계층을 거슬러 올라가며 targetKind 수준의 조상 ID를 찾는다.
/// Call → Work → Flow → System 순으로 부모를 추적.
let resolveTarget (store: DsStore) (targetKind: EntityKind) (entityKind: EntityKind) (entityId: Guid) : Guid option =
    let rec walk kind id =
        if kind = targetKind then Some id
        else
            match kind with
            | EntityKind.Call -> DsQuery.getCall id store |> Option.bind (fun c -> walk EntityKind.Work c.ParentId)
            | EntityKind.Work -> DsQuery.getWork id store |> Option.bind (fun w -> walk EntityKind.Flow w.ParentId)
            | EntityKind.Flow -> DsQuery.getFlow id store |> Option.bind (fun f -> walk EntityKind.System f.ParentId)
            | _ -> None
    walk entityKind entityId

let parentIdOf (store: DsStore) (entityKind: EntityKind) (entityId: Guid) : Guid option =
    match entityKind with
    | EntityKind.Call -> DsQuery.getCall entityId store |> Option.map (fun c -> c.ParentId)
    | EntityKind.Work -> DsQuery.getWork entityId store |> Option.map (fun w -> w.ParentId)
    | EntityKind.Flow -> DsQuery.getFlow entityId store |> Option.map (fun f -> f.ParentId)
    | _               -> None

let tryFindSystemIdForEntity store entityKind entityId = resolveTarget store EntityKind.System entityKind entityId

let tryFindProjectIdForEntity (store: DsStore) (entityKind: EntityKind) (entityId: Guid) : Guid option =
    match entityKind with
    | EntityKind.Project ->
        DsQuery.getProject entityId store
        |> Option.map (fun project -> project.Id)
    | _ ->
        tryFindSystemIdForEntity store entityKind entityId
        |> Option.bind (findProjectOfSystem store)

let private lookupEntity (store: DsStore) (tabKind: TabKind) (id: Guid) : (Guid * string) option =
    match tabKind with
    | TabKind.System -> DsQuery.getSystem id store |> Option.map (fun s -> s.Id, s.Name)
    | TabKind.Flow   -> DsQuery.getFlow   id store |> Option.map (fun f -> f.Id, f.Name)
    | TabKind.Work   -> DsQuery.getWork   id store |> Option.map (fun w -> w.Id, w.Name)
    | _              -> None

let private tabKindForEntityKind entityKind =
    match entityKind with
    | EntityKind.System -> Some TabKind.System
    | EntityKind.Flow   -> Some TabKind.Flow
    | EntityKind.Work   -> Some TabKind.Work
    | _                 -> None

let tryOpenTabForEntity (store: DsStore) (entityKind: EntityKind) (entityId: Guid) : TabOpenInfo option =
    tabKindForEntityKind entityKind
    |> Option.bind (fun kind ->
        lookupEntity store kind entityId
        |> Option.map (fun (id, name) -> { Kind = kind; RootId = id; Title = name }))

/// Work → 부모 Flow 탭, Call → 부모 Work 탭을 반환
let tryOpenParentTab (store: DsStore) (entityKind: EntityKind) (entityId: Guid) : TabOpenInfo option =
    let parentKind =
        match entityKind with
        | EntityKind.Call -> Some EntityKind.Work
        | EntityKind.Work -> Some EntityKind.Flow
        | _ -> None
    parentKind
    |> Option.bind (fun pk ->
        parentIdOf store entityKind entityId
        |> Option.bind (fun parentId -> tryOpenTabForEntity store pk parentId))

let tabTitle (store: DsStore) (tabKind: TabKind) (rootId: Guid) : string option =
    lookupEntity store tabKind rootId |> Option.map snd

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
/// - apiNameFilter: ApiDef 이름 포함 검색 (빈 문자열 = 전체)
let findApiDefs (store: DsStore) (apiNameFilter: string) : ApiDefMatch list =
    let apiNameFilter = apiNameFilter.Trim()
    DsQuery.allProjects store
    |> List.collect (fun p -> DsQuery.passiveSystemsOf p.Id store)
    |> List.distinctBy (fun s -> s.Id)
    |> List.collect (fun sys ->
        DsQuery.apiDefsOf sys.Id store
        |> List.filter (fun a ->
            String.IsNullOrEmpty(apiNameFilter) ||
            a.Name.IndexOf(apiNameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
        |> List.map (fun a -> ApiDefMatch(a.Id, a.Name, sys.Id, sys.Name)))
