module Ds2.UI.Core.PasteResolvers

open System
open Ds2.Core

let isCopyableEntityType (entityType: string) =
    match entityType with
    | "Flow"
    | "Work"
    | "Call" -> true
    | _ -> false

let entityTypeForTabKind (tabKind: TabKind) : string option =
    EntityHierarchyQueries.entityTypeForTabKind tabKind

let offsetPosition (pos: Xywh option) : Xywh option =
    pos |> Option.map (fun p -> Xywh(p.X + 30, p.Y + 30, p.W, p.H))

let resolveSystemTarget (store: DsStore) (targetEntityType: string) (targetEntityId: Guid) : Guid option =
    match targetEntityType with
    | "System" -> Some targetEntityId
    | _ -> EntityHierarchyQueries.tryFindSystemIdForEntity store targetEntityType targetEntityId

let resolveFlowTarget (store: DsStore) (targetEntityType: string) (targetEntityId: Guid) : Guid option =
    match targetEntityType with
    | "Flow" -> Some targetEntityId
    | "System" ->
        DsQuery.flowsOf targetEntityId store
        |> List.tryHead
        |> Option.map (fun f -> f.Id)
    | _ -> EntityHierarchyQueries.tryFindFlowIdForEntity store targetEntityType targetEntityId

let resolveWorkTarget (store: DsStore) (targetEntityType: string) (targetEntityId: Guid) : Guid option =
    match targetEntityType with
    | "Work" -> Some targetEntityId
    | "Flow" ->
        DsQuery.worksOf targetEntityId store
        |> List.tryHead
        |> Option.map (fun w -> w.Id)
    | _ -> EntityHierarchyQueries.tryFindWorkIdForEntity store targetEntityType targetEntityId
