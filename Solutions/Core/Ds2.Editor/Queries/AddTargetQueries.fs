module Ds2.Editor.AddTargetQueries

open System
open Ds2.Core
open Ds2.Core.Store


let private resolveFromEntity
    (resolve: DsStore -> EntityKind -> Guid -> Guid option)
    (store: DsStore)
    (entityKind: EntityKind option)
    (entityId: Guid option)
    : Guid option =
    match entityKind, entityId with
    | Some ek, Some eid -> resolve store ek eid
    | _ -> None

let private resolveFromActiveTab
    (resolve: DsStore -> EntityKind -> Guid -> Guid option)
    (store: DsStore)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    : Guid option =
    match activeTabKind, activeTabRootId with
    | Some tabKind, Some tabRootId ->
        resolve store (EditorNavigation.entityKindForTabKind tabKind) tabRootId
    | _ -> None

/// Add System target resolver for UI. Priority:
/// 1) selected entity context, 2) active tab context, 3) single project in store.
let tryResolveAddSystemTarget
    (store: DsStore)
    (selectedEntityKind: EntityKind option)
    (selectedEntityId: Guid option)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    : Guid option =

    let fromSelection =
        resolveFromEntity StoreHierarchyQueries.tryFindProjectIdForEntity store selectedEntityKind selectedEntityId

    let fromTab =
        resolveFromActiveTab StoreHierarchyQueries.tryFindProjectIdForEntity store activeTabKind activeTabRootId

    let singleProjectInStore =
        match Queries.allProjects store with
        | [ single ] -> Some single.Id
        | _ -> None

    fromSelection
    |> Option.orElse fromTab
    |> Option.orElse singleProjectInStore

let private systemIdsOfProject (project: Project) =
    Seq.append project.ActiveSystemIds project.PassiveSystemIds
    |> Seq.distinct
    |> Seq.toList

/// Add Flow target resolver for UI. Priority:
/// 1) selected entity context, 2) active tab context, 3) single system in store.
let tryResolveAddFlowTarget
    (store: DsStore)
    (selectedEntityKind: EntityKind option)
    (selectedEntityId: Guid option)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    : Guid option =

    let fromSelection =
        match selectedEntityKind, selectedEntityId with
        | Some EntityKind.Project, Some projectId ->
            Queries.getProject projectId store
            |> Option.bind (fun project ->
                match systemIdsOfProject project with
                | [ systemId ] -> Some systemId
                | _ -> None)
        | _ -> resolveFromEntity StoreHierarchyQueries.tryFindSystemIdForEntity store selectedEntityKind selectedEntityId

    let fromTab =
        resolveFromActiveTab StoreHierarchyQueries.tryFindSystemIdForEntity store activeTabKind activeTabRootId

    let singleSystemInStore =
        Queries.allProjects store
        |> List.collect systemIdsOfProject
        |> List.distinct
        |> function
            | [ systemId ] -> Some systemId
            | _ -> None

    fromSelection
    |> Option.orElse fromTab
    |> Option.orElse singleSystemInStore

/// 주어진 Flow 가 현재 ActiveTab 컨텍스트에 속하는지 판정.
/// - ActiveTab 없음 → 통과(가드 비활성)
/// - Flow 탭 → 자기 자신
/// - System 탭 → 그 System 산하 Flow 인지
/// - 기타 탭 → 통과
let isFlowInActiveTab
    (store: DsStore)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    (flowId: Guid)
    : bool =
    match activeTabKind, activeTabRootId with
    | Some TabKind.Flow,   Some rootId -> rootId = flowId
    | Some TabKind.System, Some rootId -> Queries.flowsOf rootId store |> List.exists (fun f -> f.Id = flowId)
    | _ -> true

/// ActiveTab 이 System 탭이면 그 System 의 첫 Flow Id (없으면 None)
let private firstFlowInSystemTab
    (store: DsStore)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    : Guid option =
    match activeTabKind, activeTabRootId with
    | Some TabKind.System, Some rootId ->
        let flows = Queries.flowsOf rootId store
        if flows.IsEmpty then None else Some flows.Head.Id
    | _ -> None

/// AddWork 시 target Flow 결정 (canvas/tree 공용).
/// 우선순위:
/// 1) 선택된 노드가 Flow 이고 ActiveTab 컨텍스트 안이면 그 Flow
/// 2) 직전 AddWork 가 사용한 Flow 가 유효 + ActiveTab 컨텍스트 안이면 그 Flow
/// 3) ActiveTab 이 Flow 탭이면 ActiveTab.RootId
/// 4) ActiveTab 이 System 탭이면 그 System 의 첫 Flow
let tryResolveAddWorkTargetFlow
    (store: DsStore)
    (selectedEntityKind: EntityKind option)
    (selectedEntityId: Guid option)
    (lastAddWorkTargetFlowId: Guid option)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    : Guid option =

    let fromSelection =
        match selectedEntityKind, selectedEntityId with
        | Some EntityKind.Flow, Some flowId
            when isFlowInActiveTab store activeTabKind activeTabRootId flowId ->
                Some flowId
        | _ -> None

    let fromLast =
        match lastAddWorkTargetFlowId with
        | Some lastId
            when Queries.getFlow lastId store |> Option.isSome
                 && isFlowInActiveTab store activeTabKind activeTabRootId lastId ->
                Some lastId
        | _ -> None

    let fromActiveFlowTab =
        match activeTabKind, activeTabRootId with
        | Some TabKind.Flow, Some rootId -> Some rootId
        | _ -> None

    fromSelection
    |> Option.orElse fromLast
    |> Option.orElse fromActiveFlowTab
    |> Option.orElse (firstFlowInSystemTab store activeTabKind activeTabRootId)

/// C# 호출용 어댑터 — `Nullable<T>` 시그니처. 내부에서 `tryResolveAddWorkTargetFlow` 위임.
[<CompiledName("ResolveAddWorkTargetFlow")>]
let resolveAddWorkTargetFlow
    (store: DsStore)
    (selectedEntityKind: Nullable<EntityKind>)
    (selectedEntityId: Nullable<Guid>)
    (lastAddWorkTargetFlowId: Nullable<Guid>)
    (activeTabKind: Nullable<TabKind>)
    (activeTabRootId: Nullable<Guid>)
    : Nullable<Guid> =
    tryResolveAddWorkTargetFlow store
        (Option.ofNullable selectedEntityKind)
        (Option.ofNullable selectedEntityId)
        (Option.ofNullable lastAddWorkTargetFlowId)
        (Option.ofNullable activeTabKind)
        (Option.ofNullable activeTabRootId)
    |> function
       | Some g -> Nullable(g)
       | None   -> Nullable()
