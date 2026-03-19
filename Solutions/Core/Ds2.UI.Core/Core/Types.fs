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
    let WorkPeriod   = "Work Duration"
    [<Literal>]
    let PeriodFormat = "밀리초(ms) 단위 정수"
    [<Literal>]
    let TimeoutMs    = "Timeout (ms)"
    [<Literal>]
    let PeriodMs     = "Period (ms)"
    [<Literal>]
    let TxWork       = "TX Work"
    [<Literal>]
    let RxWork       = "RX Work"
    [<Literal>]
    let Push         = "Push"
    [<Literal>]
    let Normal       = "Normal"
    [<Literal>]
    let ApiDef       = "ApiDef"
    [<Literal>]
    let OutTag       = "Out tag"
    [<Literal>]
    let OutAddress   = "Out address"
    [<Literal>]
    let InTag        = "In tag"
    [<Literal>]
    let InAddress    = "In address"
    [<Literal>]
    let OutSpec      = "Out spec"
    [<Literal>]
    let InSpec       = "In spec"

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

    let createDefaultNodeBounds () =
        Xywh(DefaultNodeX, DefaultNodeY, DefaultNodeWidth, DefaultNodeHeight)

    /// PassiveSystem의 ApiDef 카테고리 노드에 쓰이는 고정 ID (systemId에서 결정론적으로 파생).
    let apiDefCategoryId (systemId: Guid) =
        let bytes = systemId.ToByteArray()
        bytes.[0] <- bytes.[0] ^^^ 0xCAuy
        bytes.[15] <- bytes.[15] ^^^ 0xFEuy
        Guid(bytes)


// =============================================================================
// EntityKind — 엔티티/노드 타입 열거형 (C# == 비교 가능)
// =============================================================================

type EntityKind =
    | Project        = 0
    | System         = 1
    | Flow           = 2
    | Work           = 3
    | Call           = 4
    | ApiDef         = 5
    | Button         = 6
    | Lamp           = 7
    | Condition      = 8
    | Action         = 9
    | ApiDefCategory = 10
    | DeviceRoot     = 11

[<Sealed>]
type MoveEntityRequest(id: Guid, position: Xywh) =
    member _.Id = id
    member _.Position = position

// =============================================================================
// EditorEvent — UI에 발행하는 변경 이벤트
// =============================================================================

type EditorEvent =
    // --- 엔티티 생명주기 ---
    | ProjectAdded       of Project
    | ProjectRemoved     of Guid
    | SystemAdded        of DsSystem
    | SystemRemoved      of Guid
    | FlowAdded          of Flow
    | FlowRemoved        of Guid
    | WorkAdded          of Work
    | WorkRemoved        of Guid
    | CallAdded          of Call
    | CallRemoved        of Guid
    | ApiDefAdded        of ApiDef
    | ApiDefRemoved      of Guid
    | ArrowWorkAdded     of ArrowBetweenWorks
    | ArrowWorkRemoved   of Guid
    | ArrowCallAdded     of ArrowBetweenCalls
    | ArrowCallRemoved   of Guid

    // --- 프로퍼티 변경 ---
    | EntityRenamed      of id: Guid * newName: string
    | ProjectPropsChanged of id: Guid
    | WorkPropsChanged   of id: Guid
    | CallPropsChanged   of id: Guid
    | ApiDefPropsChanged of id: Guid

    // --- HW ---
    | HwComponentAdded   of entityKind: EntityKind * id: Guid * name: string
    | HwComponentRemoved of entityKind: EntityKind * id: Guid

    // --- Undo/Redo 히스토리 ---
    | HistoryChanged     of undoLabels: string list * redoLabels: string list

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
