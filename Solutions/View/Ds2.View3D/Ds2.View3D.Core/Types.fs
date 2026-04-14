namespace Ds2.View3D

open System

// =============================================================================
// Coordinate Types
// =============================================================================

/// XZ 평면 좌표 (Y = 0 고정, 지면)
type Position = { X: float; Z: float }

module Position =
    let zero = { X = 0.0; Z = 0.0 }
    let create x z = { X = x; Z = z }

// =============================================================================
// Scene Node Types
// =============================================================================

/// Device에 속하는 ApiDef 정보
type ApiDefInfo = {
    Id: Guid
    Name: string
    CallerCount: int
}

/// Device 노드 (System 1:1 매핑)
type DeviceInfo = {
    Id: Guid
    Name: string
    /// 원본 SystemType 문자열 (DsSystem.Properties.SystemType)
    SystemType: string option
    /// Lib3D 모델명 — DevicePresets.Entries에서 추론 (3D 모델 로딩에 사용)
    ModelType: string
    FlowName: string
    ParticipatingFlows: string list
    IsUsedInSimulation: bool
    ApiDefs: ApiDefInfo list
    Position: Position option
}

module DeviceInfo =
    let withPosition pos device = { device with Position = Some pos }

/// Flow 그룹 시각화 영역
type FlowZone = {
    FlowName: string
    CenterX: float
    CenterZ: float
    SizeX: float
    SizeZ: float
    Color: string  // "#RRGGBB"
}

// =============================================================================
// Scene Data
// =============================================================================

type SceneData = {
    Devices: DeviceInfo list
    FlowZones: FlowZone list
}

module SceneData =
    let empty = { Devices = []; FlowZones = [] }

// =============================================================================
// Connection Types
// =============================================================================

type ConnectionItem = {
    DeviceName: string
    ApiDefName: string
    Direction: string  // "outgoing" | "incoming"
} with
    member this.Display = $"{this.DeviceName}.{this.ApiDefName}"

module ConnectionItem =
    let create deviceName apiDefName direction =
        { DeviceName = deviceName; ApiDefName = apiDefName; Direction = direction }

// =============================================================================
// Selection Types
// =============================================================================

type SelectionEvent =
    | DeviceSelected of deviceId: Guid
    | ApiDefSelected of deviceId: Guid * apiDefName: string

module SelectionEvent =
    let newDeviceSelected (deviceId: Guid) = DeviceSelected deviceId
    let newApiDefSelected (deviceId: Guid) (apiDefName: string) = ApiDefSelected(deviceId, apiDefName)

// =============================================================================
// Layout Persistence
// =============================================================================

type StoredLayout = {
    ProjectId: Guid
    Positions: Map<Guid, Position>
    FlowZones: Map<string, FlowZone>
    Version: int
}

type ILayoutStore =
    abstract member LoadLayout: projectId: Guid -> Result<StoredLayout option, exn>
    abstract member SaveLayout: layout: StoredLayout -> Result<unit, exn>

// =============================================================================
// Error Types
// =============================================================================

type SceneError =
    | ProjectNotFound of Guid
    | SystemNotFound of Guid
    | InvalidSystemType of systemId: Guid * systemType: string
    | ApiDefNotFound of Guid
    | LayoutStoreError of exn

module SceneError =
    let projectNotFound id = ProjectNotFound id
    let systemNotFound id = SystemNotFound id
    let layoutStoreError ex = LayoutStoreError ex

// =============================================================================
// Device Presets (3D Model Mapping)
// =============================================================================

/// 3D 모델로 매핑 가능한 SystemType 집합
module DevicePresets =
    /// 알려진 Device 타입 목록 (하드코딩된 Lib3D 모델)
    let KnownNames =
        Set.ofList [
            "Robot"
            "Conveyor"
            "Unit"
            "AGV"
            "Gripper"
            "Lifter"
            "Crane"
            "Stacker"
            "Sorter"
            "Transfer"
            "Barrier"
            "Door"
            "Gate"
            "Elevator"
            "Hoist"
            "Pusher"
            "Rotary"
            "Turntable"
            "Tilter"
        ]

    /// JSON으로 등록된 커스텀 모델명 (런타임에 추가됨)
    let mutable CustomNames : Set<string> = Set.empty

    /// 커스텀 모델명 등록
    let registerCustomName (name: string) =
        CustomNames <- Set.add name CustomNames

    /// 커스텀 모델명 일괄 등록
    let registerCustomNames (names: string seq) =
        CustomNames <- names |> Seq.fold (fun s n -> Set.add n s) CustomNames

    /// 전체 알려진 이름 (하드코딩 + 커스텀)
    let allKnownNames () =
        Set.union KnownNames CustomNames
