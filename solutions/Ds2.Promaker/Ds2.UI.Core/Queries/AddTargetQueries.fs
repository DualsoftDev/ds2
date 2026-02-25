module Ds2.UI.Core.AddTargetQueries

open System
open Ds2.Core

let private tryResolveProjectFromEntity
    (store: DsStore)
    (entityType: string option)
    (entityId: Guid option)
    : Guid option =
    match entityType, entityId with
    | Some selectedType, Some selectedId ->
        EntityHierarchyQueries.tryFindProjectIdForEntity store selectedType selectedId
    | _ -> None

let private tryResolveProjectFromActiveTab
    (store: DsStore)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    : Guid option =
    match activeTabKind, activeTabRootId with
    | Some tabKind, Some tabRootId ->
        EntityHierarchyQueries.entityTypeForTabKind tabKind
        |> Option.bind (fun entityType ->
            EntityHierarchyQueries.tryFindProjectIdForEntity store entityType tabRootId)
    | _ -> None

/// Add System target resolver for UI. Priority:
/// 1) selected entity context, 2) active tab context, 3) single project in store.
let tryResolveAddSystemTarget
    (store: DsStore)
    (selectedEntityType: string option)
    (selectedEntityId: Guid option)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    : Guid option =

    let fromSelection =
        tryResolveProjectFromEntity store selectedEntityType selectedEntityId

    let fromTab =
        tryResolveProjectFromActiveTab store activeTabKind activeTabRootId

    let singleProjectInStore =
        DsQuery.allProjects store
        |> List.map (fun p -> p.Id)
        |> List.distinct
        |> function
            | [ projectId ] -> Some projectId
            | _ -> None

    fromSelection
    |> Option.orElse fromTab
    |> Option.orElse singleProjectInStore

let private systemIdsOfProject (project: Project) =
    Seq.append project.ActiveSystemIds project.PassiveSystemIds
    |> Seq.distinct
    |> Seq.toList

let private tryResolveSystemFromSelectedEntity
    (store: DsStore)
    (selectedEntityType: string option)
    (selectedEntityId: Guid option)
    : Guid option =
    match selectedEntityType, selectedEntityId with
    | Some EntityTypeNames.Project, Some projectId ->
        DsQuery.getProject projectId store
        |> Option.bind (fun project ->
            match systemIdsOfProject project with
            | [ systemId ] -> Some systemId
            | _ -> None)
    | Some selectedType, Some selectedId ->
        EntityHierarchyQueries.tryFindSystemIdForEntity store selectedType selectedId
    | _ -> None

let private tryResolveSystemFromActiveTab
    (store: DsStore)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    : Guid option =
    match activeTabKind, activeTabRootId with
    | Some tabKind, Some tabRootId ->
        EntityHierarchyQueries.entityTypeForTabKind tabKind
        |> Option.bind (fun entityType ->
            EntityHierarchyQueries.tryFindSystemIdForEntity store entityType tabRootId)
    | _ -> None

/// Add Flow target resolver for UI. Priority:
/// 1) selected entity context, 2) active tab context, 3) single system in store.
let tryResolveAddFlowTarget
    (store: DsStore)
    (selectedEntityType: string option)
    (selectedEntityId: Guid option)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    : Guid option =

    let fromSelection =
        tryResolveSystemFromSelectedEntity store selectedEntityType selectedEntityId

    let fromTab =
        tryResolveSystemFromActiveTab store activeTabKind activeTabRootId

    let singleSystemInStore =
        DsQuery.allProjects store
        |> List.collect systemIdsOfProject
        |> List.distinct
        |> function
            | [ systemId ] -> Some systemId
            | _ -> None

    fromSelection
    |> Option.orElse fromTab
    |> Option.orElse singleSystemInStore