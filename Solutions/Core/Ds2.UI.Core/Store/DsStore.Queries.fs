namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core

// =============================================================================
// DsStore 쿼리 확장 — DsQuery/Projection/Queries 모듈에 위임
// =============================================================================

[<Extension>]
type DsStoreQueriesExtensions =

    // ─── TreeProjection ──────────────────────────────────────────────
    [<Extension>]
    static member BuildTrees(store: DsStore) : TreeNodeInfo list * TreeNodeInfo list =
        TreeProjection.buildTrees store

    // ─── CanvasProjection ────────────────────────────────────────────
    [<Extension>]
    static member CanvasContentForTab(store: DsStore, kind: TabKind, rootId: Guid) : CanvasContent =
        CanvasProjection.canvasContentForTab store kind rootId

    // ─── EntityHierarchyQueries ──────────────────────────────────────
    [<Extension>]
    static member TryOpenTabForEntity(store: DsStore, entityKind: EntityKind, entityId: Guid) : TabOpenInfo option =
        EntityHierarchyQueries.tryOpenTabForEntity store entityKind entityId

    [<Extension>]
    static member TryOpenTabForEntityOrNull(store: DsStore, entityKind: EntityKind, entityId: Guid) : TabOpenInfo =
        EntityHierarchyQueries.tryOpenTabForEntity store entityKind entityId
        |> Option.toObj

    [<Extension>]
    static member TryOpenParentTabOrNull(store: DsStore, entityKind: EntityKind, entityId: Guid) : TabOpenInfo =
        EntityHierarchyQueries.tryOpenParentTab store entityKind entityId
        |> Option.toObj

    [<Extension>]
    static member FlowIdsForTab(store: DsStore, kind: TabKind, rootId: Guid) : Guid list =
        EntityHierarchyQueries.flowIdsForTab store kind rootId

    [<Extension>]
    static member TabTitleOrNull(store: DsStore, kind: TabKind, rootId: Guid) : string =
        EntityHierarchyQueries.tabTitle store kind rootId
        |> Option.toObj

    [<Extension>]
    static member FindApiDefsByName(store: DsStore, filterName: string) : ApiDefMatch list =
        EntityHierarchyQueries.findApiDefs store filterName

    [<Extension>]
    static member AddedEntityIdOrNull(store: DsStore, evt: EditorEvent) : Nullable<Guid> =
        store.TryGetAddedEntityId(evt)
        |> Option.toNullable

    // ─── CanvasProjection (단건 쿼리) ─────────────────────────────────
    [<Extension>]
    static member GetCallConditionTypes(store: DsStore, callId: Guid) : CallConditionType list =
        match DsQuery.getCall callId store with
        | Some call -> CallConditionQueries.conditionTypes call
        | None -> []

    // ─── ApiCall → Call 역참조 쿼리 ──────────────────────────────────
    [<Extension>]
    static member FindCallsByApiCallId(store: DsStore, apiCallId: Guid) : struct(Guid * string) list =
        store.CallsReadOnly.Values
        |> Seq.filter (CallConditionQueries.containsApiCallReference apiCallId)
        |> Seq.map (fun call -> struct(call.Id, call.Name))
        |> Seq.toList

    // ─── CanvasLayout ──────────────────────────────────────────────────
    [<Extension>]
    static member ComputeAutoLayout(store: DsStore, kind: TabKind, rootId: Guid) : MoveEntityRequest list =
        let content = CanvasProjection.canvasContentForTab store kind rootId
        CanvasLayout.computeLayout content

    // ─── ArrowPathCalculator ─────────────────────────────────────────
    [<Extension>]
    static member GetFlowArrowPaths(store: DsStore, flowId: Guid) : Map<Guid, ArrowPathCalculator.ArrowVisual> =
        ArrowPathCalculator.computeFlowArrowPaths store flowId

    // ─── SelectionQueries ────────────────────────────────────────────
    [<Extension>]
    static member OrderCanvasSelectionKeys(_store: DsStore, nodes: seq<CanvasSelectionCandidate>) : SelectionKey list =
        SelectionQueries.orderCanvasSelectionKeys nodes

    [<Extension>]
    static member OrderCanvasSelectionKeysForBox(_store: DsStore, startX, startY, endX, endY, nodes) : SelectionKey list =
        SelectionQueries.orderCanvasSelectionKeysForBox startX startY endX endY nodes

    [<Extension>]
    static member ApplyNodeSelection(_store: DsStore, currentSelection, anchor, target, ctrlPressed, shiftPressed, orderedKeys) : NodeSelectionResult =
        SelectionQueries.applyNodeSelection currentSelection anchor target ctrlPressed shiftPressed orderedKeys

    [<Extension>]
    static member ApplyNodeSelection(_store: DsStore, currentSelection, anchor: SelectionKey, target: SelectionKey, ctrlPressed, shiftPressed, orderedKeys) : NodeSelectionResult =
        SelectionQueries.applyNodeSelection currentSelection (Option.ofObj anchor) (Option.ofObj target) ctrlPressed shiftPressed orderedKeys
