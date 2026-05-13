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
//   사용: DevicePresets.Entries3 / DefaultFBNames

/// 각 entry 는 (SystemType, ApiList, DefaultFBName).
/// ApiList: ';' 구분 API 이름 목록. DefaultFBName: XGI_Template.xml 의 FB 이름.
/// Cylinder_N: 센서 N쌍 (LS_AdvN/LS_RetN) 을 가진 N-실린더 FB. XGI_Template 에 1/2/3/4/6/8/10 FB 존재.
module DevicePresets =
    /// SystemType preset entry — (SystemType, ApiList, DefaultFBName).
    /// ApiList 는 ';' 구분 API 이름. DefaultFBName 은 XGI_Template.xml 의 FB 이름.
    type Entry = string * string * string

    /// 외부 모듈 (예: AAStoPLC.TagWizard) 이 동적으로 등록하는 entry — manifest/JSON 기반.
    /// SystemType 을 key 로 dedup — 같은 SystemType 재등록 시 후입선출.
    let private registered = System.Collections.Generic.Dictionary<string, Entry>(System.StringComparer.OrdinalIgnoreCase)

    /// 외부에서 entry 추가/갱신. SystemType 중복은 후입선출 (idempotent).
    let register (entries: Entry seq) =
        for ((sysType, _, _) as e) in entries do
            if not (System.String.IsNullOrEmpty sysType) then
                registered.[sysType] <- e

    /// 등록 초기화 — 테스트 / 재로드 용도.
    let clearRegistered () = registered.Clear()

    /// Ds2.Core 에 하드코딩된 기본 entry — manifest/JSON 외부 파일이 없는 타입들.
    /// • Unit/Part: FB 없음 (passive marker)
    /// • ModeStn: FB 있으나 임베디드 JSON 없음 (Operation Mode FB 단일)
    /// • Cylinder_N: AAStoPLC.TagWizard.CylinderManifest 가 register
    /// • Robot*: AAStoPLC.TagWizard.FBTagMapEmbeddedDefaults 의 ApiList scan 으로 register
    let BaseEntries : Entry[] = [|
        ("Unit",    "ADV;RET", "")
        ("Part",    "ADV;RET", "")
        ("ModeStn", "",        "FB402_Mode_Stn_v2")
    |]

    /// 단일 진실원 — BaseEntries (하드코딩 3개: Unit/Part/ModeStn) + register 된 외부 entry 합산.
    /// 호출 시점마다 평가 — register 가 늦게 호출돼도 즉시 반영됨.
    /// 순서: "Unit" → 등록 entry (Cylinder_N + Robot* 등) → "Part" → "ModeStn".
    /// register 와 BaseEntries 가 같은 SystemType 가지면 register 우선.
    let Entries () : Entry[] =
        let regEntries =
            registered.Values
            |> Seq.sortBy (fun (k, _, _) -> k)  // 안정적 순서 (SystemType 알파벳)
            |> Seq.toArray
        let regNames = registered.Keys |> Set.ofSeq
        let unitArr  = BaseEntries |> Array.filter (fun (k, _, _) -> k = "Unit"    && not (regNames.Contains k))
        let baseRest = BaseEntries |> Array.filter (fun (k, _, _) -> k <> "Unit"   && not (regNames.Contains k))
        Array.concat [ unitArr; regEntries; baseRest ]

    /// 호환용 별칭 — 옛 호출부 무수정 유지 (`Entries3` 가 함수가 아니라 배열인 점은 변함없음).
    [<System.Obsolete("Use Entries() instead")>]
    let Entries3 () = Entries ()

    /// SystemType → 기본 FB 이름 lookup (XGI_Template.xml 기준).
    let DefaultFBNames () : Map<string, string> =
        Entries ()
        |> Array.map (fun (sysType, _, fb) -> (sysType, fb))
        |> Map.ofArray


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
