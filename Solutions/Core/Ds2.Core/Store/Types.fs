namespace Ds2.Core.Store
open System
open Ds2.Core

// =============================================================================
// UI 라벨 · 기본값 · 공유 타입
// =============================================================================
/// XAML에서 x:Static으로 참조하는 도메인 개념 디스플레이 라벨.
/// Store에서 한 번 정의 -> C# XAML 자동 전파.
module Labels =
    let [<Literal>] WorkPeriod  = "Work Duration (ms)"
    let [<Literal>] PeriodFormat = "밀리초(ms) 단위 정수"
    let [<Literal>] TimeoutMs   = "Timeout (ms)"
    let [<Literal>] PeriodMs    = "Period (ms)"
    let [<Literal>] TxWork      = "TX Work"
    let [<Literal>] RxWork      = "RX Work"
    let [<Literal>] Push        = "Push"
    let [<Literal>] Normal      = "Normal"
    let [<Literal>] ApiDef      = "ApiDef"
    let [<Literal>] OutTag      = "Out tag"
    let [<Literal>] OutAddress  = "Out address"
    let [<Literal>] InTag       = "In tag"
    let [<Literal>] InAddress   = "In address"
    let [<Literal>] OutSpec     = "Out spec"
    let [<Literal>] InSpec      = "In spec"

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
// DevicePresets — 모델 프리셋 레지스트리 (단일 정의 위치)
// =============================================================================
//
//   C# 사용: Ds2.Store.DevicePresets.Entries / DefaultMappingStrings
//   F# 사용: DevicePresets.KnownNames / DefaultMappingStrings (open Ds2.Store 후)

/// 각 entry 는 (SystemType, ApiList, DefaultFBName).
/// ApiList: ';' 구분 API 이름 목록. DefaultFBName: XGI_Template.xml 의 FB 이름.
/// Cylinder_N: 센서 N쌍 (LS_AdvN/LS_RetN) 을 가진 N-실린더 FB. XGI_Template 에 1/2/3/4/6/8/10 FB 존재.
module DevicePresets =
    let Entries3 : (string * string * string)[] = [|
        ("Unit",        "ADV;RET",     "")
        ("Cylinder_1",  "ADV;RET", "FB421_Com_Cylinder_1_v1")
        ("Cylinder_2",  "ADV;RET", "FB422_Com_Cylinder_2_v1")
        ("Cylinder_3",  "ADV;RET", "FB423_Com_Cylinder_3_v1")
        ("Cylinder_4",  "ADV;RET", "FB424_Com_Cylinder_4_v1")
        ("Cylinder_6",  "ADV;RET", "FB425_Com_Cylinder_6_v1")
        ("Cylinder_8",  "ADV;RET", "FB426_Com_Cylinder_8_v1")
        ("Cylinder_10", "ADV;RET", "FB427_Com_Cylinder_10_v1")
        ("RobotWeldGrip",       "WORK_COMP_RST;START;A_1ST_IN_OK;B_1ST_IN_OK;2ND_IN_OK;3RD_IN_OK;4TH_IN_OK;5TH_IN_OK;6TH_IN_OK;7TH_IN_OK", "FB496_Robot_Kawasaki_v3_260225_용접_그리퍼")
        ("RobotWeldGripPallet", "WORK_COMP_RST;START;A_1ST_IN_OK;B_1ST_IN_OK;2ND_IN_OK;3RD_IN_OK;4TH_IN_OK;5TH_IN_OK;6TH_IN_OK;7TH_IN_OK;PLT1_IN_OK;PLT2_IN_OK;PLT3_IN_OK;PLT4_IN_OK;PLT1_COUNT_RST;PLT2_COUNT_RST;PLT3_COUNT_RST;PLT4_COUNT_RST", "FB496_Robot_Kawasaki_v3_260225_종합")
        ("Part", "ADV;RET", "")
    |]

    /// (SystemType, ApiList) 호환 배열 — 기존 사용처 그대로 동작.
    let Entries : (string * string)[] =
        Entries3 |> Array.map (fun (sysType, apis, _) -> (sysType, apis))

    /// SystemType → 기본 FB 이름 lookup (XGI_Template.xml 기준).
    let DefaultFBNames : Map<string, string> =
        Entries3
        |> Array.map (fun (sysType, _, fb) -> (sysType, fb))
        |> Map.ofArray

    /// 등록된 ModelType 이름 집합 (inferModelType 직접 매칭용)
    let KnownNames : Set<string> =
        Entries |> Array.map fst |> Set.ofArray


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
