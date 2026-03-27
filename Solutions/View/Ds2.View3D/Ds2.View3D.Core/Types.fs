namespace Ds2.View3D

open System

// =============================================================================
// Preset Registry — 단일 정의 위치
// =============================================================================
//
// ModelType(3D 모델 이름) ↔ 기본 SystemType 매핑.
// 모든 곳(C#, F#)에서 이 모듈만 참조한다.
//
//   C# 사용:
//     - DevicePresets.Entries          → ComboBox 항목 생성 (Item1=modelType, Item2=sysType)
//     - DevicePresets.DefaultMappingStrings → ProjectProperties 기본값 ("SysType:ModelType")
//
//   F# 사용:
//     - DevicePresets.KnownNames       → inferModelType 직접 매칭
//     - DevicePresets.DefaultMappingStrings → 동일

/// 등록된 3D 모델 프리셋 레지스트리
module DevicePresets =
    /// (modelType, canonicalSystemType) 쌍 배열 — Dummy 포함
    let Entries : (string * string)[] = [|
        ("Unit",        "ADV;RET")
        ("Lifter",      "UP;DOWN")
        ("Pusher",      "FWD;BWD")
        ("Conveyor",    "MOVE;STOP")
        ("Robot_6Axis", "CMD1;CMD2;HOME")
        ("Robot_SCARA", "POS1;POS2;HOME")
        ("Dummy",       "")
    |]

    /// 등록된 ModelType 이름 집합 (inferModelType 직접 매칭용)
    let KnownNames : Set<string> =
        Entries |> Array.map fst |> Set.ofArray

    /// "SystemType:ModelType" 기본 매핑 문자열 배열 (ProjectProperties 초기값, Dummy 제외)
    let DefaultMappingStrings : string[] =
        Entries
        |> Array.filter (fun (_, s) -> s <> "")
        |> Array.map (fun (model, sysType) -> $"{sysType}:{model}")

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
