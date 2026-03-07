namespace Ds2.UI.Core

open System
open Ds2.Core

// =============================================================================
// Undo 기록 타입
// =============================================================================

/// 증분 Undo 기록 단위. 클로저가 typed dict 참조를 캡처하여 타입 안전하게 Undo/Redo 실행.
type UndoRecord = {
    Undo: unit -> unit
    Redo: unit -> unit
    Description: string
}

/// 하나의 사용자 동작에 대한 Undo 트랜잭션 (여러 UndoRecord를 묶음).
type UndoTransaction = {
    Label: string
    Records: UndoRecord list
}

// =============================================================================
// UI 라벨 · 기본값 · 공유 타입
// =============================================================================

/// XAML에서 x:Static으로 참조하는 도메인 개념 디스플레이 라벨.
/// UI.Core에서 한 번 정의 → C# XAML 자동 전파.
module Labels =
    [<Literal>]
    let WorkPeriod = "Work Period"
    [<Literal>]
    let PeriodFormat = "밀리초(ms) 단위 정수"
    [<Literal>]
    let TimeoutMs = "Timeout (ms)"
    [<Literal>]
    let PeriodMs = "Period (ms)"
    [<Literal>]
    let TxWork = "TX Work"
    [<Literal>]
    let RxWork = "RX Work"
    [<Literal>]
    let Push = "Push"
    [<Literal>]
    let Poll = "Poll"
    [<Literal>]
    let ApiDef = "ApiDef"
    [<Literal>]
    let OutAddress = "Out address"
    [<Literal>]
    let InAddress = "In address"
    [<Literal>]
    let OutSpec = "Out spec"
    [<Literal>]
    let InSpec = "In spec"

module UiDefaults =
    /// Fixed logical root ID for the device tree. Keep stable across sessions/files.
    let DeviceTreeRootId = Guid "11111111-1111-1111-1111-111111111111"

    [<Literal>]
    let DefaultNodeX = 0
    [<Literal>]
    let DefaultNodeY = 0
    [<Literal>]
    let DefaultNodeWidth = 120
    [<Literal>]
    let DefaultNodeHeight = 40
    [<Literal>]
    let DefaultNodeXf = 0.0
    [<Literal>]
    let DefaultNodeYf = 0.0
    [<Literal>]
    let DefaultNodeWidthf = 120.0
    [<Literal>]
    let DefaultNodeHeightf = 40.0

    let createDefaultNodeBounds () =
        Xywh(DefaultNodeX, DefaultNodeY, DefaultNodeWidth, DefaultNodeHeight)

    /// PassiveSystem의 ApiDef 카테고리 노드에 쓰이는 고정 ID (systemId에서 결정론적으로 파생).
    let apiDefCategoryId (systemId: Guid) =
        let bytes = systemId.ToByteArray()
        bytes.[0] <- bytes.[0] ^^^ 0xCAuy
        bytes.[15] <- bytes.[15] ^^^ 0xFEuy
        Guid(bytes)

[<RequireQualifiedAccess>]
type UiArrowType =
    | None = 0
    | Start = 1
    | Reset = 2
    | StartReset = 3
    | ResetReset = 4
    | Group = 5

module UiArrowType =
    let ofCore (value: ArrowType) : UiArrowType =
        enum<UiArrowType>(int value)

    let toCore (value: UiArrowType) : ArrowType =
        enum<ArrowType>(int value)

[<RequireQualifiedAccess>]
type UiCallConditionType =
    | Auto = 0
    | Common = 1
    | Active = 2

module UiCallConditionType =
    let ofCore (value: CallConditionType) : UiCallConditionType =
        enum<UiCallConditionType>(int value)

    let toCore (value: UiCallConditionType) : CallConditionType =
        enum<CallConditionType>(int value)

[<Sealed>]
type UiMoveEntityRequest(entityType: string, id: Guid, hasPosition: bool, x: int, y: int, w: int, h: int) =
    member _.EntityType = entityType
    member _.Id = id
    member _.HasPosition = hasPosition
    member _.X = x
    member _.Y = y
    member _.W = w
    member _.H = h

[<Sealed>]
[<AllowNullLiteral>]
type UiNodeMoveInfo(entityId: Guid, hasPosition: bool, x: int, y: int, w: int, h: int) =
    member _.EntityId = entityId
    member _.HasPosition = hasPosition
    member _.X = x
    member _.Y = y
    member _.W = w
    member _.H = h

[<RequireQualifiedAccess>]
module EntityTypeNames =
    [<Literal>]
    let Project = "Project"
    [<Literal>]
    let System = "System"
    [<Literal>]
    let Flow = "Flow"
    [<Literal>]
    let Work = "Work"
    [<Literal>]
    let Call = "Call"
    [<Literal>]
    let ApiDef = "ApiDef"
    [<Literal>]
    let Button = "Button"
    [<Literal>]
    let Lamp = "Lamp"
    [<Literal>]
    let Condition = "Condition"
    [<Literal>]
    let Action = "Action"
    [<Literal>]
    let ApiDefCategory = "ApiDefCategory"
    [<Literal>]
    let DeviceRoot = "DeviceRoot"

// =============================================================================
// EntityKind — 엔티티 타입 DU (컴파일 시점 완전성 보장)
// =============================================================================

[<Struct>]
type EntityKind =
    | Project | System | Flow | Work | Call
    | ApiDef | Button | Lamp | Condition | Action

module EntityKind =
    let tryOfString (s: string) : EntityKind voption =
        match s with
        | EntityTypeNames.Project   -> ValueSome Project
        | EntityTypeNames.System    -> ValueSome System
        | EntityTypeNames.Flow      -> ValueSome Flow
        | EntityTypeNames.Work      -> ValueSome Work
        | EntityTypeNames.Call      -> ValueSome Call
        | EntityTypeNames.ApiDef    -> ValueSome ApiDef
        | EntityTypeNames.Button    -> ValueSome Button
        | EntityTypeNames.Lamp      -> ValueSome Lamp
        | EntityTypeNames.Condition -> ValueSome Condition
        | EntityTypeNames.Action    -> ValueSome Action
        | _           -> ValueNone

    let toString (k: EntityKind) =
        match k with
        | Project -> EntityTypeNames.Project
        | System -> EntityTypeNames.System
        | Flow -> EntityTypeNames.Flow
        | Work -> EntityTypeNames.Work
        | Call -> EntityTypeNames.Call
        | ApiDef -> EntityTypeNames.ApiDef
        | Button -> EntityTypeNames.Button
        | Lamp -> EntityTypeNames.Lamp
        | Condition -> EntityTypeNames.Condition
        | Action -> EntityTypeNames.Action

// =============================================================================
// EditorEvent — UI에 발행하는 변경 이벤트
// =============================================================================

type EditorEvent =
    // --- 엔티티 생명주기 ---
    | ProjectAdded    of Project
    | ProjectRemoved  of Guid
    | SystemAdded     of DsSystem
    | SystemRemoved   of Guid
    | FlowAdded       of Flow
    | FlowRemoved     of Guid
    | WorkAdded       of Work
    | WorkRemoved     of Guid
    | CallAdded       of Call
    | CallRemoved     of Guid
    | ApiDefAdded     of ApiDef
    | ApiDefRemoved   of Guid
    | ArrowWorkAdded    of ArrowBetweenWorks
    | ArrowWorkRemoved  of Guid
    | ArrowCallAdded    of ArrowBetweenCalls
    | ArrowCallRemoved  of Guid

    // --- 프로퍼티 변경 ---
    | EntityRenamed      of id: Guid * newName: string
    | WorkMoved          of id: Guid * newPos: Xywh option
    | CallMoved          of id: Guid * newPos: Xywh option
    | WorkPropsChanged   of id: Guid
    | CallPropsChanged   of id: Guid
    | ApiDefPropsChanged of id: Guid

    // --- HW ---
    | HwComponentAdded   of entityType: string * id: Guid * name: string
    | HwComponentRemoved of entityType: string * id: Guid

    // --- Undo/Redo 히스토리 ---
    | HistoryChanged of undoLabels: string list * redoLabels: string list

    // --- 전체 갱신 ---
    | StoreRefreshed

// =============================================================================
// CallCopyContext — Call 복사 시 ApiCall 공유/복제 정책
// =============================================================================

/// Call을 붙여넣을 위치에 따른 복사 컨텍스트.
/// - SameWork    : 동일 Work 내 복사 → ApiCall 공유 (AddSharedApiCallToCall)
/// - DifferentWork: 다른 Work(같은 Flow) 복사 → ApiCall 새 GUID
/// - DifferentFlow: 다른 Flow 복사 → ApiCall 새 GUID
///                  (향후: ApiDef/Device도 별도 생성 필요, 현재는 DifferentWork와 동일)
type CallCopyContext =
    | SameWork
    | DifferentWork
    | DifferentFlow
