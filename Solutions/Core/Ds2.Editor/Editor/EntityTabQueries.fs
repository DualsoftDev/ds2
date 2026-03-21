module Ds2.Editor.EntityTabQueries

open System
open Ds2.Core
open Ds2.Store

let entityKindForTabKind (tabKind: TabKind) : EntityKind =
    match tabKind with
    | TabKind.System -> EntityKind.System
    | TabKind.Flow -> EntityKind.Flow
    | TabKind.Work -> EntityKind.Work
    | _ -> invalidArg (nameof tabKind) $"Unknown TabKind: {tabKind}"

let private lookupEntity (store: DsStore) (tabKind: TabKind) (id: Guid) : (Guid * string) option =
    match tabKind with
    | TabKind.System -> DsQuery.getSystem id store |> Option.map (fun system -> system.Id, system.Name)
    | TabKind.Flow   -> DsQuery.getFlow id store |> Option.map (fun flow -> flow.Id, flow.Name)
    | TabKind.Work   -> DsQuery.getWork id store |> Option.map (fun work -> work.Id, work.Name)
    | _              -> None

let tryOpenTabForEntity (store: DsStore) (entityKind: EntityKind) (entityId: Guid) : TabOpenInfo option =
    let tabKindForEntityKind =
        match entityKind with
        | EntityKind.System -> Some TabKind.System
        | EntityKind.Flow   -> Some TabKind.Flow
        | EntityKind.Work   -> Some TabKind.Work
        | _                 -> None
    tabKindForEntityKind
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
        StoreHierarchyQueries.parentIdOf store entityKind entityId
        |> Option.bind (fun parentId -> tryOpenTabForEntity store pk parentId))

let tabTitle (store: DsStore) (tabKind: TabKind) (rootId: Guid) : string option =
    lookupEntity store tabKind rootId |> Option.map snd

let flowIdsForTab (store: DsStore) (tabKind: TabKind) (rootId: Guid) : Guid list =
    match tabKind with
    | TabKind.System ->
        StoreHierarchyQueries.flowsForSystem store rootId
        |> List.map fst
    | TabKind.Flow -> [ rootId ]
    | TabKind.Work ->
        DsQuery.getWork rootId store
        |> Option.map (fun work -> [ work.ParentId ])
        |> Option.defaultValue []
    | _ -> []
