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

let findHwParent (store: DsStore) (hwId: Guid) : Guid option =
    match store.HwButtons.TryGetValue(hwId) with
    | true, b -> Some b.ParentId
    | _ ->
        match store.HwLamps.TryGetValue(hwId) with
        | true, l -> Some l.ParentId
        | _ ->
            match store.HwConditions.TryGetValue(hwId) with
            | true, c -> Some c.ParentId
            | _ ->
                match store.HwActions.TryGetValue(hwId) with
                | true, a -> Some a.ParentId
                | _ -> None

let isCallInFlow (store: DsStore) (callId: Guid) (flowId: Guid) : bool =
    match DsQuery.getCall callId store with
    | Some call ->
        match DsQuery.getWork call.ParentId store with
        | Some work -> work.ParentId = flowId
        | None -> false
    | None -> false

// 계층 탐색 primitive — 각 엔티티 타입에서 부모 ID 한 단계 이동
let private stepCallToWork  (store: DsStore) (callId: Guid)  = DsQuery.getCall callId  store |> Option.map (fun c -> c.ParentId)
let private stepWorkToFlow  (store: DsStore) (workId: Guid)  = DsQuery.getWork workId  store |> Option.map (fun w -> w.ParentId)
let private stepFlowToSystem(store: DsStore) (flowId: Guid)  = DsQuery.getFlow flowId  store |> Option.map (fun f -> f.ParentId)

let tryFindWorkIdForEntity (store: DsStore) (entityType: string) (entityId: Guid) : Guid option =
    match entityType with
    | EntityTypeNames.Work -> DsQuery.getWork entityId store |> Option.map (fun w -> w.Id)
    | EntityTypeNames.Call -> stepCallToWork store entityId
    | _ -> None

let tryFindFlowIdForEntity (store: DsStore) (entityType: string) (entityId: Guid) : Guid option =
    match entityType with
    | EntityTypeNames.Flow -> DsQuery.getFlow entityId store |> Option.map (fun f -> f.Id)
    | _ -> tryFindWorkIdForEntity store entityType entityId |> Option.bind (stepWorkToFlow store)

let tryFindSystemIdForEntity (store: DsStore) (entityType: string) (entityId: Guid) : Guid option =
    match entityType with
    | EntityTypeNames.System -> DsQuery.getSystem entityId store |> Option.map (fun s -> s.Id)
    | _ -> tryFindFlowIdForEntity store entityType entityId |> Option.bind (stepFlowToSystem store)

let tryFindProjectIdForEntity (store: DsStore) (entityType: string) (entityId: Guid) : Guid option =
    match entityType with
    | EntityTypeNames.Project ->
        DsQuery.getProject entityId store
        |> Option.map (fun project -> project.Id)
    | _ ->
        tryFindSystemIdForEntity store entityType entityId
        |> Option.bind (findProjectOfSystem store)

let resolveSystemForEntity (store: DsStore) (entityType: string) (entityId: Guid) : (Guid * Guid) option =
    match entityType with
    | EntityTypeNames.System ->
        DsQuery.flowsOf entityId store
        |> List.tryHead
        |> Option.map (fun f -> (entityId, f.Id))
    | EntityTypeNames.Flow ->
        DsQuery.getFlow entityId store
        |> Option.map (fun f -> (f.ParentId, entityId))
    | EntityTypeNames.Work ->
        DsQuery.getWork entityId store
        |> Option.bind (fun w -> DsQuery.getFlow w.ParentId store)
        |> Option.map (fun f -> (f.ParentId, f.Id))
    | _ -> None

let tryOpenTabForEntity (store: DsStore) (entityType: string) (entityId: Guid) : TabOpenInfo option =
    match entityType with
    | EntityTypeNames.System ->
        DsQuery.getSystem entityId store
        |> Option.map (fun s ->
            { Kind = TabKind.System
              RootId = s.Id
              Title = s.Name })
    | EntityTypeNames.Flow ->
        DsQuery.getFlow entityId store
        |> Option.map (fun f ->
            { Kind = TabKind.Flow
              RootId = f.Id
              Title = f.Name })
    | EntityTypeNames.Work ->
        DsQuery.getWork entityId store
        |> Option.map (fun w ->
            { Kind = TabKind.Work
              RootId = w.Id
              Title = w.Name })
    | _ -> None

let tabTitle (store: DsStore) (tabKind: TabKind) (rootId: Guid) : string option =
    match tabKind with
    | TabKind.System ->
        DsQuery.getSystem rootId store
        |> Option.map (fun s -> s.Name)
    | TabKind.Flow ->
        DsQuery.getFlow rootId store
        |> Option.map (fun f -> f.Name)
    | TabKind.Work ->
        DsQuery.getWork rootId store
        |> Option.map (fun w -> w.Name)
    | _ -> None

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
