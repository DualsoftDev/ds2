namespace Ds2.Store

open System
open Ds2.Core

// =============================================================================
// UI 라벨 · 기본값 · 공유 타입
// =============================================================================

/// XAML에서 x:Static으로 참조하는 도메인 개념 디스플레이 라벨.
/// Store에서 한 번 정의 -> C# XAML 자동 전파.
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

[<Sealed>]
type ApiDefMatch(apiDefId: Guid, apiDefName: string, systemId: Guid, systemName: string) =
    member _.ApiDefId = apiDefId
    member _.ApiDefName = apiDefName
    member _.SystemId = systemId
    member _.SystemName = systemName
    member _.DisplayName = systemName + "." + apiDefName

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
