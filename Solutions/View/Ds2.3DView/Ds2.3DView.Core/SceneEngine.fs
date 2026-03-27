namespace Ds2.ThreeDView

open System
open Ds2.Store

// =============================================================================
// Scene Engine — C#/Blazor에서 호출 가능한 통합 Public API
// =============================================================================

type SceneEngine(store: DsStore, layoutStore: ILayoutStore) =

    let mutable selectionState = SelectionState.empty
    let mutable cachedWorkNodes: WorkNode list = []
    let mutable cachedDeviceNodes: DeviceNode list = []

    // ─────────────────────────────────────────────────────────────────────
    // Query — DsStore에서 3D 뷰 노드 빌드
    // ─────────────────────────────────────────────────────────────────────

    /// Flow 내의 Work 노드 목록
    member _.GetWorkNodes(flowId: Guid) : WorkNode list =
        let nodes = ContextBuilder.buildWorkNodes store flowId
        cachedWorkNodes <- nodes
        nodes

    /// Project 내의 Device 노드 목록 (Active + Passive Systems)
    member _.GetDeviceNodes(projectId: Guid) : DeviceNode list =
        let nodes = ContextBuilder.buildDeviceNodes store projectId
        cachedDeviceNodes <- nodes
        nodes

    // ─────────────────────────────────────────────────────────────────────
    // Placement — 자동 배치
    // ─────────────────────────────────────────────────────────────────────

    /// Device 자동 배치 (Flow별 그룹 그리드)
    member this.PlaceAllDevices(sceneId: string, projectId: Guid, ?_mode: LayoutMode) : LayoutPosition list =
        let devices = this.GetDeviceNodes(projectId)

        // 저장된 레이아웃이 있으면 사용
        let saved = layoutStore.LoadLayout(sceneId, SceneMode.DevicePlacement)
        if not saved.IsEmpty then
            saved
        else
            let positions = LayoutAlgorithm.layoutDevices devices
            layoutStore.SaveLayout(sceneId, SceneMode.DevicePlacement, positions)
            positions

    /// Work 자동 배치
    member this.PlaceAllWorks(sceneId: string, flowId: Guid, ?_mode: LayoutMode) : LayoutPosition list =
        let works = this.GetWorkNodes(flowId)

        let saved = layoutStore.LoadLayout(sceneId, SceneMode.WorkGraph)
        if not saved.IsEmpty then
            saved
        else
            let positions = LayoutAlgorithm.layoutWorks works
            layoutStore.SaveLayout(sceneId, SceneMode.WorkGraph, positions)
            positions

    /// 단일 Device 수동 배치
    member _.PlaceDevice(sceneId: string, deviceId: Guid, x: float, z: float) : LayoutPosition =
        let pos =
            {
                NodeId = deviceId
                NodeKind = NodeKind.DeviceNode
                X = x
                Y = 0.0
                Z = z
            }

        // 기존 위치 업데이트
        let existing = layoutStore.LoadLayout(sceneId, SceneMode.DevicePlacement)
        let updated =
            pos :: (existing |> List.filter (fun p -> p.NodeId <> deviceId))
        layoutStore.SaveLayout(sceneId, SceneMode.DevicePlacement, updated)
        pos

    /// 단일 Work 수동 배치
    member _.PlaceWork(sceneId: string, workId: Guid, x: float, z: float) : LayoutPosition =
        let pos =
            {
                NodeId = workId
                NodeKind = NodeKind.WorkNode
                X = x
                Y = 0.0
                Z = z
            }

        let existing = layoutStore.LoadLayout(sceneId, SceneMode.WorkGraph)
        let updated =
            pos :: (existing |> List.filter (fun p -> p.NodeId <> workId))
        layoutStore.SaveLayout(sceneId, SceneMode.WorkGraph, updated)
        pos

    // ─────────────────────────────────────────────────────────────────────
    // Selection
    // ─────────────────────────────────────────────────────────────────────

    /// 선택 이벤트 적용, 새 상태 반환
    member _.Select(event: SelectionEvent) : SelectionState.State =
        selectionState <- SelectionState.applyEvent event cachedWorkNodes cachedDeviceNodes
        selectionState

    /// 선택 해제
    member _.ClearSelection() : SelectionState.State =
        selectionState <- SelectionState.empty
        selectionState

    /// 현재 선택 상태
    member _.CurrentSelection : SelectionState.State = selectionState

    // ─────────────────────────────────────────────────────────────────────
    // Flow Zones
    // ─────────────────────────────────────────────────────────────────────

    /// Device 배치 기반 FlowZone 목록
    member _.GetFlowZones(sceneId: string) : FlowZone list =
        let positions = layoutStore.LoadLayout(sceneId, SceneMode.DevicePlacement)
        ContextBuilder.buildFlowZones cachedDeviceNodes positions

    // ─────────────────────────────────────────────────────────────────────
    // Persistence
    // ─────────────────────────────────────────────────────────────────────

    /// 현재 레이아웃 저장 (이미 PlaceAll에서 자동 저장되지만 명시적 호출용)
    member _.SaveLayout(_sceneId: string, _mode: SceneMode) : unit =
        () // PlaceAll* 메서드에서 이미 저장됨

    /// 저장된 레이아웃 로드
    member _.LoadLayout(sceneId: string, mode: SceneMode) : LayoutPosition list =
        layoutStore.LoadLayout(sceneId, mode)

    /// 레이아웃 초기화
    member _.ClearLayout(sceneId: string, mode: SceneMode) : unit =
        layoutStore.ClearLayout(sceneId, mode)

    // ─────────────────────────────────────────────────────────────────────
    // Scene Data (한 번에 전체 데이터 반환)
    // ─────────────────────────────────────────────────────────────────────

    /// Device Placement 모드의 전체 SceneData
    member this.BuildDeviceScene(sceneId: string, projectId: Guid) : SceneData =
        let devices = this.GetDeviceNodes(projectId)
        let positions = this.PlaceAllDevices(sceneId, projectId)
        let zones = ContextBuilder.buildFlowZones devices positions
        {
            Mode = SceneMode.DevicePlacement
            WorkNodes = []
            DeviceNodes = devices
            FlowZones = zones
            Positions = positions
        }

    /// Work Graph 모드의 전체 SceneData
    member this.BuildWorkScene(sceneId: string, flowId: Guid) : SceneData =
        let works = this.GetWorkNodes(flowId)
        let positions = this.PlaceAllWorks(sceneId, flowId)
        {
            Mode = SceneMode.WorkGraph
            WorkNodes = works
            DeviceNodes = []
            FlowZones = []
            Positions = positions
        }
