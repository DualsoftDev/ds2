namespace Ds2.UI.Core

open System
open Ds2.Core

// =============================================================================
// EditorQueryApi — 읽기 전용 쿼리/프로젝션 진입점 (exec 불필요)
// =============================================================================

type EditorQueryApi(store: DsStore) =

    member _.BuildTrees() : TreeNodeInfo list * TreeNodeInfo list =
        TreeProjection.buildTrees store

    member _.CanvasContentForTabUi(kind: TabKind, rootId: Guid) : UiCanvasContent =
        let content = CanvasProjection.canvasContentForTab store kind rootId
        { Nodes = content.Nodes
          Arrows =
            content.Arrows
            |> List.map (fun arrow ->
                { Id = arrow.Id
                  SourceId = arrow.SourceId
                  TargetId = arrow.TargetId
                  ArrowType = UiArrowType.ofCore arrow.ArrowType }) }

    member _.TryOpenTabForEntity(entityType: string, entityId: Guid) : TabOpenInfo option =
        EntityHierarchyQueries.tryOpenTabForEntity store entityType entityId

    member this.TryOpenTabForEntityOrNull(entityType: string, entityId: Guid) : TabOpenInfo =
        this.TryOpenTabForEntity(entityType, entityId) |> Option.toObj

    member _.FlowIdsForTab(kind: TabKind, rootId: Guid) : Guid list =
        EntityHierarchyQueries.flowIdsForTab store kind rootId

    member _.TabExists(kind: TabKind, rootId: Guid) : bool =
        EntityHierarchyQueries.tabExists store kind rootId

    member _.TabTitle(kind: TabKind, rootId: Guid) : string option =
        EntityHierarchyQueries.tabTitle store kind rootId

    member this.TabTitleOrNull(kind: TabKind, rootId: Guid) : string =
        this.TabTitle(kind, rootId) |> Option.toObj

    member _.TryFindProjectIdForEntity(entityType: string, entityId: Guid) : Guid option =
        EntityHierarchyQueries.tryFindProjectIdForEntity store entityType entityId

    member _.GetEntityParentId(entityType: string, entityId: Guid) : Guid option =
        EntityHierarchyQueries.parentIdOf store entityType entityId

    member _.FindApiDefsByName(filterName: string) : ApiDefMatch list =
        EntityHierarchyQueries.findApiDefs store "" filterName

    member _.TryResolveAddSystemTarget
        (selectedEntityType: string option)
        (selectedEntityId: Guid option)
        (activeTabKind: TabKind option)
        (activeTabRootId: Guid option)
        : Guid option =
        AddTargetQueries.tryResolveAddSystemTarget store selectedEntityType selectedEntityId activeTabKind activeTabRootId

    member _.TryResolveAddFlowTarget
        (selectedEntityType: string option)
        (selectedEntityId: Guid option)
        (activeTabKind: TabKind option)
        (activeTabRootId: Guid option)
        : Guid option =
        AddTargetQueries.tryResolveAddFlowTarget store selectedEntityType selectedEntityId activeTabKind activeTabRootId

    member _.GetFlowArrowPaths(flowId: Guid) : Map<Guid, ArrowPathCalculator.ArrowVisual> =
        ArrowPathCalculator.computeFlowArrowPaths store flowId
