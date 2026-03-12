module Ds2.UI.Core.AddTargetQueries

open System
open Ds2.Core

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
        resolve store (EntityHierarchyQueries.entityKindForTabKind tabKind) tabRootId
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
        resolveFromEntity EntityHierarchyQueries.tryFindProjectIdForEntity store selectedEntityKind selectedEntityId

    let fromTab =
        resolveFromActiveTab EntityHierarchyQueries.tryFindProjectIdForEntity store activeTabKind activeTabRootId

    let singleProjectInStore =
        match DsQuery.allProjects store with
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
            DsQuery.getProject projectId store
            |> Option.bind (fun project ->
                match systemIdsOfProject project with
                | [ systemId ] -> Some systemId
                | _ -> None)
        | _ -> resolveFromEntity EntityHierarchyQueries.tryFindSystemIdForEntity store selectedEntityKind selectedEntityId

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