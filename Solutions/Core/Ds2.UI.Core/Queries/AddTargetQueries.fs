module Ds2.UI.Core.AddTargetQueries

open System
open Ds2.Core

let private resolveFromEntity
    (resolve: DsStore -> string -> Guid -> Guid option)
    (store: DsStore)
    (entityType: string option)
    (entityId: Guid option)
    : Guid option =
    match entityType, entityId with
    | Some et, Some eid -> resolve store et eid
    | _ -> None

let private resolveFromActiveTab
    (resolve: DsStore -> string -> Guid -> Guid option)
    (store: DsStore)
    (activeTabKind: TabKind option)
    (activeTabRootId: Guid option)
    : Guid option =
    match activeTabKind, activeTabRootId with
    | Some tabKind, Some tabRootId ->
        EntityHierarchyQueries.entityTypeForTabKind tabKind
        |> Option.bind (fun et -> resolve store et tabRootId)
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
        resolveFromEntity EntityHierarchyQueries.tryFindProjectIdForEntity store selectedEntityType selectedEntityId

    let fromTab =
        resolveFromActiveTab EntityHierarchyQueries.tryFindProjectIdForEntity store activeTabKind activeTabRootId

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
    | _ -> resolveFromEntity EntityHierarchyQueries.tryFindSystemIdForEntity store selectedEntityType selectedEntityId

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
        resolveFromActiveTab EntityHierarchyQueries.tryFindSystemIdForEntity store activeTabKind activeTabRootId

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