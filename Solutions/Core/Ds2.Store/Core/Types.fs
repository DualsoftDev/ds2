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
    let WorkPeriod   = "Work Duration (ms)"
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
// DevicePresets — 3D 모델 프리셋 레지스트리 (단일 정의 위치)
// =============================================================================
//
//   C# 사용: Ds2.Store.DevicePresets.Entries / DefaultMappingStrings
//   F# 사용: DevicePresets.KnownNames / DefaultMappingStrings (open Ds2.Store 후)

/// 등록된 3D 모델 프리셋 레지스트리
module DevicePresets =
    /// (modelType, canonicalSystemType) 쌍 배열 — Dummy 포함
    let Entries : (string * string)[] = [|
        ("Unit",        "ADV;RET")
        ("Robot",       "START;1ST_IN_OK;2ND_IN_OK;WORK_COMMP_RST")
        ("Lifter",      "UP;DOWN")
        ("Pusher",      "FWD;BWD")
        ("Conveyor",    "MOVE;STOP")
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
