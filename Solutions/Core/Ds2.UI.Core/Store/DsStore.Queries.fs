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
    static member CanvasContentForTabUi(store: DsStore, kind: TabKind, rootId: Guid) : UiCanvasContent =
        let content = CanvasProjection.canvasContentForTab store kind rootId
        { Nodes = content.Nodes
          Arrows =
            content.Arrows
            |> List.map (fun arrow ->
                { Id = arrow.Id
                  SourceId = arrow.SourceId
                  TargetId = arrow.TargetId
                  ArrowType = UiArrowType.ofCore arrow.ArrowType }) }

    // ─── EntityHierarchyQueries ──────────────────────────────────────
    [<Extension>]
    static member TryOpenTabForEntity(store: DsStore, entityType: string, entityId: Guid) : TabOpenInfo option =
        EntityHierarchyQueries.tryOpenTabForEntity store entityType entityId

    [<Extension>]
    static member TryOpenTabForEntityOrNull(store: DsStore, entityType: string, entityId: Guid) : TabOpenInfo =
        DsStoreQueriesExtensions.TryOpenTabForEntity(store, entityType, entityId) |> Option.toObj

    [<Extension>]
    static member FlowIdsForTab(store: DsStore, kind: TabKind, rootId: Guid) : Guid list =
        EntityHierarchyQueries.flowIdsForTab store kind rootId

    [<Extension>]
    static member TabExists(store: DsStore, kind: TabKind, rootId: Guid) : bool =
        EntityHierarchyQueries.tabExists store kind rootId

    [<Extension>]
    static member TabTitle(store: DsStore, kind: TabKind, rootId: Guid) : string option =
        EntityHierarchyQueries.tabTitle store kind rootId

    [<Extension>]
    static member TabTitleOrNull(store: DsStore, kind: TabKind, rootId: Guid) : string =
        DsStoreQueriesExtensions.TabTitle(store, kind, rootId) |> Option.toObj

    [<Extension>]
    static member FindApiDefsByName(store: DsStore, filterName: string) : ApiDefMatch list =
        EntityHierarchyQueries.findApiDefs store "" filterName

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

