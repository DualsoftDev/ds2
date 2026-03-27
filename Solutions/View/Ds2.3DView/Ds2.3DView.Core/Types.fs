namespace Ds2.ThreeDView

open System

// =============================================================================
// 3D View Core Types — UI/Renderer independent data contracts
// =============================================================================

/// 3D 좌표
[<Struct>]
type Vec3 =
    { X: float; Y: float; Z: float }

    static member Zero = { X = 0.0; Y = 0.0; Z = 0.0 }

    static member Create(x, y, z) = { X = x; Y = y; Z = z }

/// Scene display mode
type SceneMode =
    | Empty             = 0
    | WorkGraph         = 1
    | DevicePlacement   = 2

/// Device type classification
type DeviceType =
    | Robot   = 0
    | Small   = 1
    | General = 2

/// Layout algorithm
type LayoutMode =
    | Grid              = 0
    | FlowGroupedGrid   = 1

/// Node kind discriminator
type NodeKind =
    | WorkNode   = 0
    | DeviceNode = 1

// ─────────────────────────────────────────────────────────────────────
// View DTOs — DsStore 엔티티와 별도의 3D 뷰 전용 레코드
// ─────────────────────────────────────────────────────────────────────

/// ApiDef summary for device node
type ApiDefNode =
    {
        Id: Guid
        Name: string
        CallerCount: int
        State: Ds2.Core.Status4
    }

/// Call summary for work node
type CallNode =
    {
        Id: Guid
        WorkId: Guid
        WorkName: string
        FlowName: string
        State: Ds2.Core.Status4
        DevicesAlias: string
        ApiDefName: string
        NextCallId: Guid option
        PrevCallId: Guid option
    }

/// Work node in 3D scene
type WorkNode =
    {
        Id: Guid
        Name: string
        FlowName: string
        FlowId: Guid
        State: Ds2.Core.Status4
        IncomingWorkIds: Guid list
        OutgoingWorkIds: Guid list
        Calls: CallNode list
        Position: Vec3 option
    }

/// Device node in 3D scene (corresponds to DsSystem)
type DeviceNode =
    {
        Id: Guid
        Name: string
        DeviceType: DeviceType
        FlowName: string
        SystemType: string option
        State: Ds2.Core.Status4
        ApiDefs: ApiDefNode list
        IsUsedInSimulation: bool
        Position: Vec3 option
    }

/// Flow zone bounding box for floor coloring
type FlowZone =
    {
        FlowName: string
        CenterX: float
        CenterZ: float
        SizeX: float
        SizeZ: float
        Color: string
    }

/// Persisted layout position
type LayoutPosition =
    {
        NodeId: Guid
        NodeKind: NodeKind
        X: float
        Y: float
        Z: float
    }

// ─────────────────────────────────────────────────────────────────────
// Selection Events
// ─────────────────────────────────────────────────────────────────────

type ArrowKind =
    | Incoming = 0
    | Outgoing = 1

type SelectionEvent =
    | WorkSelected of workId: Guid
    | CallSelected of callId: Guid
    | DeviceSelected of deviceId: Guid
    | ApiDefSelected of deviceId: Guid * apiDefName: string
    | EmptySpaceSelected

// ─────────────────────────────────────────────────────────────────────
// Generation Result
// ─────────────────────────────────────────────────────────────────────

type SceneData =
    {
        Mode: SceneMode
        WorkNodes: WorkNode list
        DeviceNodes: DeviceNode list
        FlowZones: FlowZone list
        Positions: LayoutPosition list
    }

    static member Empty =
        {
            Mode = SceneMode.Empty
            WorkNodes = []
            DeviceNodes = []
            FlowZones = []
            Positions = []
        }
