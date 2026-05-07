namespace Ds2.Core

open System
open System.Text.Json.Serialization

// =============================================================================
// SEQUENCE CONTROL SUBMODEL
// PLC 연동 및 하드웨어 제어
// =============================================================================
//
// 목적:
//   AAS 디지털 트윈과 실제 PLC 하드웨어를 연결하는 핵심 브리지
//   - I/O Tag Mapping: Work/Call → PLC Tag 자동 생성
//   - Hardware Components: Button/Lamp/Condition/Action HMI 요소
//   - Multi-Vendor Support: Mitsubishi, Siemens, Rockwell AB 통합
//
// 핵심 가치:
//   - 자동 태그 매핑: 설정 시간 95% 단축
//   - 양방향 I/O: 실시간 피드백 제어
//   - 오류율 감소: 30% → 0.5% (60배 개선)
//
// =============================================================================


// =============================================================================
// ENUMERATIONS - 제어 타입 정의
// =============================================================================


/// 태그 매칭 모드
type TagMatchMode =
    | ByAddress     // 주소 기반 매칭 (권장) - M100, D200
    | ByName        // 이름 기반 매칭 - "Welding_Start"

/// 이름 변환 규칙
type NameTransform =
    | UpperCase     // WORK_START
    | LowerCase     // work_start
    | CamelCase     // workStart
    | PascalCase    // WorkStart
    | NoTransform   // 원본 유지

/// PLC 벤더
type PlcVendor =
    | Mitsubishi    // MC Protocol (MELSEC)
    | Siemens       // S7 Protocol
    | RockwellAB    // EtherNet/IP (Allen-Bradley)
    | Omron         // FINS Protocol
    | Generic       // Generic Protocol

/// FB Port mapping status (how the port was resolved)
type MappingStatus =
    | AutoMapped   = 0  // 자동 매핑 성공 (SignalLookup)
    | UserMapped   = 1  // 사용자 지정 매핑
    | Fallback     = 2  // 기본값 매핑 (Template의 DefaultVar 사용)

/// 주소 자동 할당 규칙
type AddressAssignRule =
    | Sequential = 0   // 포트 순서대로 +1 할당
    | PortIndex  = 1   // 포트별 오프셋 Map 참조
    | Manual     = 2   // 사용자가 직접 Address 입력




// =============================================================================
// HARDWARE COMPONENTS - HMI 요소
// =============================================================================

/// 하드웨어 컴포넌트 베이스 클래스
[<AbstractClass>]
type HwComponent(name: string) =

    member val Name = name with get, set
    member val InTag: IOTag option = None with get, set
    member val OutTag: IOTag option = None with get, set
    member val FlowGuids = ResizeArray<Guid>() with get, set
    member val IsEnabled = true with get, set
    member val Priority = 0 with get, set              // 우선순위 (높을수록 우선)

/// HMI 버튼 (사용자 입력)
type HwButton [<JsonConstructor>] internal (name: string) =
    inherit HwComponent(name)

    member val ButtonType = "Momentary" with get, set  // "Momentary" | "Toggle" | "Emergency"
    member val DebounceMs = 50 with get, set           // 디바운싱 시간 (ms)


/// HMI 램프 (상태 표시)
type HwLamp [<JsonConstructor>] internal (name: string) =
    inherit HwComponent(name)

    member val LampColor = "Green" with get, set       // "Green" | "Red" | "Yellow" | "Blue"
    member val BlinkEnabled = false with get, set      // 깜빡임 활성화
    member val BlinkIntervalMs = 500 with get, set     // 깜빡임 간격 (ms)


/// HMI 조건 (상태 모니터링)
type HwCondition [<JsonConstructor>] internal (name: string) =
    inherit HwComponent(name)

    member val ConditionType = "Digital" with get, set // "Digital" | "Analog" | "Comparison"
    member val Threshold: float option = None with get, set
    member val Hysteresis: float option = None with get, set


/// HMI 액션 (제어 출력)
type HwAction [<JsonConstructor>] internal (name: string) =
    inherit HwComponent(name)

    member val ActionType = "Digital" with get, set    // "Digital" | "Analog" | "Pulse"
    member val HoldTime: TimeSpan option = None with get, set
    member val RetryCount = 0 with get, set            // 재시도 횟수


// =============================================================================
// FB TAG MAP — Preset 기반 FB 포트 ↔ TagPattern 매핑 (V2)
// =============================================================================
//
// 태그패턴 매크로 ($(F)/$(D)/$(A)) 와 SignalPatternEntry 가
// SignalEnumerator (AAStoXGI) 의 단일 진실원.
// 코드 생성 결과 (VarName/Address) 는 어디에도 영구 저장하지 않으며
// run-time 에 in-memory 로 계산된다.

/// FBTagMapPreset 내부 기준 주소 세트 (IoSystemBase/IoFlowBase 매크로 파일을 대체)
type FBBaseAddressSet() =
    member val InputBase:  string = "%IW0.0.0" with get, set  // IW 기준 주소
    member val OutputBase: string = "%QW0.0.0" with get, set  // QW 기준 주소
    member val MemoryBase: string = "%MW100"   with get, set  // MW(Dummy) 기준 주소

/// Pre-FB 입력 식 노드 종류 — FB 인풋 핀에 LD contact 로 직접 와이어할 부울식.
type FbInputExprKind =
    | Var      = 0   // ─┤ ├─ Symbol
    | NegVar   = 1   // ─┤/├─ Symbol
    | And      = 2   // 직렬 결합 (Children)
    | Or       = 3   // 병렬 결합 (Children)
    | Not      = 4   // Children[0] 의 부정
    | Rising   = 5   // ─|P|─ Symbol
    | Falling  = 6   // ─|N|─ Symbol
    | Raw      = 7   // 백엔드가 해석하는 raw 식 (Symbol)

/// FB 인풋 식 — 매크로 ($(F)/$(D)/$(A)) 포함된 변수명 leaf + AND/OR/NOT/Edge 결합.
/// SignalPatternEntry.PreFbCondition 에서 사용. JSON 직렬화 호환을 위해 mutable class.
type FbInputExpr() =
    member val Kind:     FbInputExprKind            = FbInputExprKind.Var with get, set
    /// Var/NegVar/Rising/Falling/Raw 일 때 변수 이름 (매크로 포함 가능).
    member val Symbol:   string                     = ""                  with get, set
    /// And/Or 일 때 자식들. Not 은 Children.[0] 만 사용. Var 류는 빈 리스트.
    member val Children: ResizeArray<FbInputExpr>   = ResizeArray()       with get, set

/// 신호 패턴 엔트리 — Tag Wizard Step 2 "신호 템플릿" 탭에서 편집.
/// ApiDefName 별로 IW/QW/MW 태그 이름 패턴을 정의한다.
/// 이 값은 FBTagMapPreset.(Iw|Qw|Mw)Patterns 안에만 존재하며, AASX 에 함께 저장된다.
/// → 외부 *.txt 템플릿 파일 의존성을 제거해 동일 AASX = 동일 PLC 출력 보장.
type SignalPatternEntry() =
    /// ApiDef 이름 (예: "ADV", "RET"). Wizard 에서 드롭다운으로 선택.
    member val ApiName:     string = "" with get, set
    /// 태그 이름 패턴 (예: "W_$(F)_WRS_$(D)_$(A)"). $(F)/$(D)/$(A) 는 생성 시점에 확장.
    /// Pattern 에 $(C:ApiName) 토큰을 쓰면 ControlCallProperties.SignalCounts[ApiName]
    /// 값으로 치환됨 (FB 의 Adv_Amount/Ret_Amount 등 카운트 입력에 사용).
    member val Pattern:     string = "" with get, set
    /// 이 신호를 연결할 FB Local Label 이름 (옵션).
    member val TargetFBPort: string = "" with get, set
    /// 주소 할당을 건너뜀 — true 면 IO 슬롯을 소비하지 않고 Address 를 빈값으로 emit.
    /// 예: _T1S, _OFF_TIME, T#200MS 같이 외부 정의 공용 변수 / IEC 리터럴.
    member val SkipAddressAlloc: bool = false  with get, set
    /// 예비(Spare) 슬롯 — true 면 주소 1비트 예약 후 신호 미생성. ApiName/Pattern/TargetFBPort 무시.
    member val IsSpare:          bool = false  with get, set
    /// Pre-FB 입력 식 — 비어있지 않으면 Pattern→VarName 단일 와이어 대신 LD contact 트리로 FB 포트에 연결.
    /// PLC 코드 생성 전용 메타데이터 — DS2 시뮬레이터는 무시.
    member val PreFbCondition:   FbInputExpr option = None with get, set
    /// 사용자 지정 데이터 타입 — FB Local Label 미설정 시 적용. 설정 시 FB 포트 타입이 우선.
    /// 예: "BOOL", "WORD", "DINT", "REAL". 빈 문자열이면 폴백 (BOOL).
    member val UserDataType:     string = "" with get, set

/// FB 입력 포트 와이어링 entry — Cylinder/Robot 등 모든 FB 통합 모델.
/// Kind 2가지 + AuxKind (AuxCoil 일 때 user 조건 소스 선택):
///   DirectFB           = FB 포트에 ApiDef 변수 직접 와이어 (코일 emit 없음). Robot 류.
///   AuxCoil + AutoAux  = WorkGoing ∧ preds ∧ CallCondition.AutoAux  → 코일 비트 → FB 포트.
///   AuxCoil + ComAux   = CallCondition.ComAux                       → 코일 비트 → FB 포트.
type AuxPortMapEntry() =
    member val ApiName:      string = "" with get, set         // 예: "ADV" / "WORK_COMP_RST"
    member val TargetFBPort: string = "" with get, set         // 예: "M_Auto_Adv" / "WORK_COMP_RST"
    /// "DirectFB" / "AuxCoil"
    member val Kind:         string = "DirectFB" with get, set
    /// AuxCoil 일 때만 의미 — Ds2 CallCondition 의 어느 Type 속성값(수식)을 코일 조건으로 사용:
    ///   "AutoAux" = WorkGoing ∧ preds ∧ CallCondition.AutoAux 값 — Cylinder M_Auto_X 류.
    ///   "ComAux"  = CallCondition.ComAux 값 — Cylinder M_Com_X 류 (게이팅 없음).
    /// DirectFB 면 무시.
    member val AuxKind:      string = "AutoAux" with get, set
    /// 사용자 정의 추가 수식 — 자동 합성 조건과 AND 결합.
    /// DirectFB: FB 포트에 식이 LD contact 트리로 직접 와이어.
    /// AuxCoil: 코일 조건에 추가 AND 로 합성됨.
    /// 매크로 $(F)/$(D)/$(A)/$(S) 자동 치환.
    member val Condition:    FbInputExpr option = None with get, set

/// 디바이스 타입(SystemType)별 FBTagMap 프리셋
/// 시스템 인스턴스별이 아닌 디바이스 타입별 전역 설정
/// Promaker 사용자 설정 (AppData JSON에 저장)
type FBTagMapPreset() =
    /// 이 디바이스 타입에 적용할 FB 이름 (XGI_Template.xml Type="2" 기준)
    member val FBTagMapName: string = "" with get, set
    /// 기준 주소 (IoSystemBase/IoFlowBase 매크로 파일 대체)
    member val BaseAddresses: FBBaseAddressSet = FBBaseAddressSet() with get, set
    /// 주소 자동 할당 규칙
    member val AddressRule: AddressAssignRule = AddressAssignRule.Sequential with get, set
    /// FB 입력 포트 와이어링 매핑 — Cylinder/Robot 등 모든 FB 통합. 단일 진실원.
    /// 예: 실린더 [{ADV, M_Auto_Adv, Auto}, {ADV, M_Com_Adv, Com}, {RET, M_Auto_Ret, Auto}, {RET, M_Com_Ret, Com}]
    /// 예: 로봇   [{WORK_COMP_RST, WORK_COMP_RST, Direct}, {START, START, Direct}, ...]
    member val AuxPortMap: ResizeArray<AuxPortMapEntry> = ResizeArray() with get, set
    /// IW(입력 워드) 신호 템플릿 — 외부 txt 파일을 완전히 대체한다.
    member val IwPatterns: ResizeArray<SignalPatternEntry> = ResizeArray() with get, set
    /// QW(출력 워드) 신호 템플릿.
    member val QwPatterns: ResizeArray<SignalPatternEntry> = ResizeArray() with get, set
    /// MW(Dummy 메모리 워드) 신호 템플릿.
    member val MwPatterns: ResizeArray<SignalPatternEntry> = ResizeArray() with get, set

    /// Active System 의 Operation Mode FB 여부 — true 이면 ApiCall 없이 Call(Api_None) 만 있어도
    /// 이 preset 으로 FB 가 생성된다 (예: ModeStn). false 는 일반 device 별 FB.
    member val IsOperationModeFb: bool = false with get, set

    /// FB 호출 시 의도적으로 미연결로 남길 FB 포트 이름 목록 (예: "로보트기동이상").
    /// CodeGenerator 는 이 목록의 포트에 대해 변수/_OFF/AUX 모두 emit 하지 않고 skip.
    member val SkippedFBPorts: ResizeArray<string> = ResizeArray() with get, set

    /// API 이름 → 해당 API 의 "완료" 를 표시하는 FB 출력 포트 이름.
    /// 인과(예: LW_LATCH.ADV → LW_CLAMP.ADV) PLC 생성 시, 후행 Call 의 자동 게이팅 leaf 로
    /// 선행 Call 의 매핑된 OUT 포트 변수를 사용. preset 단위 1:1 정적 매핑.
    /// 예: { "ADV" → "M_Adv_End"; "RET" → "M_Ret_End" }
    member val EndPortMap: System.Collections.Generic.Dictionary<string, string> =
        System.Collections.Generic.Dictionary<string, string>() with get, set

// =============================================================================
// PROPERTIES - 제어 속성 클래스
//
// V2 단일 진실원: FBTagMapPreset (SystemType별 전역) +
//                  ApiCall.InTag/OutTag.Address (Manual 입력)
// 구 IoListEntry/DummyIoEntry/SignalBinding (Wizard Step 3/4) 은 V2 SignalEnumerator
// 도입으로 폐기됨.
// =============================================================================

/// System-level 제어 속성
type ControlSystemProperties() =
    inherit PropertiesBase<ControlSystemProperties>()

    // ========== 자동 태그 생성 ==========
    member val EnableAutoTagGeneration = false with get, set
    member val TagPrefix: string option = None with get, set
    member val TagNamingFormat = "{SystemId}_{WorkId}_{Signal}" with get, set
    member val NameTransform = "UpperCase" with get, set

    // ========== PLC 통신 설정 ==========
    member val PlcVendor = "Mitsubishi" with get, set
    member val PlcIpAddress = "192.168.0.1" with get, set
    member val PlcPort = 5000 with get, set
    member val CommunicationTimeout = TimeSpan.FromSeconds(5.0) with get, set
    member val RetryAttempts = 3 with get, set

    // ========== 태그 매칭 설정 ==========
    member val TagMatchMode = "ByAddress" with get, set
    member val EnableAddressValidation = true with get, set
    member val CaseSensitiveMatching = false with get, set

    // ========== 안전 기능 ==========
    member val EnableSafetyInterlock = true with get, set
    member val EmergencyStopEnabled = true with get, set
    member val SafetyDoorCheck = false with get, set
    member val LightCurtainCheck = false with get, set
    member val TwoHandControl = false with get, set
    member val SafetyTimeoutSeconds = 30.0 with get, set

    // ========== 모니터링 ==========
    member val EnableHealthCheck = true with get, set
    member val HealthCheckInterval = TimeSpan.FromSeconds(10.0) with get, set
    member val EnableHeartbeat = true with get, set
    member val HeartbeatInterval = TimeSpan.FromSeconds(1.0) with get, set

    // ========== PLC 코드 생성 (FBTagMap) ==========
    member val SystemType: string option = None with get, set

    // ========== FBTagMap 프리셋 + Global IO (TAG Wizard / PLC 생성, AASX에 저장) ==========
    /// SystemType별 기준 주소/AddressRule 설정 (Wizard Step 1).
    /// V2 SignalEnumerator 가 IwPatterns/QwPatterns/MwPatterns 를 진실원으로 사용.
    member val FBTagMapPresets   = System.Collections.Generic.Dictionary<string, FBTagMapPreset>() with get, set

/// Flow-level 제어 속성
type ControlFlowProperties() =
    inherit PropertiesBase<ControlFlowProperties>()

    member val FlowControlEnabled = false with get, set
    member val FlowPriority = 0 with get, set          // Flow 우선순위

    /// Flow별 기준 주소 오버라이드 (레거시 IoFlowBase @FLOW 섹션 역할).
    /// Some → FBTagMapPreset.BaseAddresses 대신 이 값을 사용.
    /// None → Preset 기본값 상속.
    member val BaseAddressOverride: FBBaseAddressSet option = None with get, set

/// Work-level 제어 속성
type ControlWorkProperties() =
    inherit PropertiesBase<ControlWorkProperties>()

    // ========== 기본 Work 제어 ==========
    member val EnableHardwareControl = false with get, set
    member val ControlMode = "Sequential" with get, set // "Sequential" | "Parallel" | "Conditional"

    // ========== I/O 태그 (자동 생성 또는 수동 설정) ==========
    member val InTagName: string option = None with get, set
    member val InTagAddress: string option = None with get, set
    member val OutTagName: string option = None with get, set
    member val OutTagAddress: string option = None with get, set
    member val CallDirection = "InOut" with get, set

    // ========== 타임아웃 설정 ==========
    member val WorkTimeout: TimeSpan option = None with get, set
    member val EnableTimeout = false with get, set
    member val TimeoutAction = "Abort" with get, set   // "Abort" | "Retry" | "Skip"

    // ========== 인터록 ==========
    member val RequiresSafetyCheck = false with get, set
    member val InterlockConditions: string array = [||] with get, set

    // ========== 모션 제어 ==========
    member val EnableMotionControl = false with get, set
    member val MotionControlMode: string option = None with get, set
    member val TargetPosition: float option = None with get, set
    member val TargetVelocity: float option = None with get, set
    member val Acceleration: float option = None with get, set
    member val Deceleration: float option = None with get, set

    // ========== 펄스 제어 ==========
    member val UsePulseControl = false with get, set
    member val PulseWidthMs: int option = None with get, set
    member val PulseIntervalMs: int option = None with get, set
    member val PulseCount: int option = None with get, set

    // ========== 상태 추적 ==========
    member val CurrentState = "Idle" with get, set     // "Idle" | "Running" | "Paused" | "Error"
    member val LastExecutionTime: DateTime option = None with get, set
    member val ExecutionCount = 0 with get, set
    member val ErrorCount = 0 with get, set

/// Call-level 제어 속성
type ControlCallProperties() =
    inherit PropertiesBase<ControlCallProperties>()

    // ========== Call 제어 설정 ==========
    member val CallDirection = "InOut" with get, set


    // ========== 실행 제어 ==========
    member val EnableRetry = false with get, set
    member val MaxRetryCount = 3 with get, set
    member val RetryDelayMs = 1000 with get, set

    // ========== 타임아웃 ==========
    member val CallTimeout: TimeSpan option = None with get, set
    member val WaitForCompletion = true with get, set

    // ========== 조건부 실행 ==========
    member val EnableConditional = false with get, set
    member val ConditionExpression: string option = None with get, set

    // ========== 동적 신호 수량 ==========
    // Promaker AddCall UI 에서 입력한 ApiName 별 신호 수량 (예: "ADV" → 4, "RET" → 2).
    // Preset 에 미리 등록된 (Section, ApiName) entry 들 중 첫 N 개만 emit, 나머지는 skip.
    // 또한 SignalPatternEntry.Pattern 의 $(C:ApiName) 토큰을 이 값으로 치환 (FB 의 Adv_Amount 등).
    // Call-level 이라 같은 Call 안의 모든 ApiCall 이 공유.
    member val SignalCounts =
        System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        with get, set



// =============================================================================
// HELPER FUNCTIONS - 제어 함수
// =============================================================================

// =============================================================================
// LEGACY MIGRATION — 구 프로젝트의 IoSystemBase/IoFlowBase 텍스트 → 신 모델 이식
// =============================================================================
//
// 구 포맷:
//   @SYSTEM RBT
//   @IW_BASE 0
//   @FLOW Main
//   @IW_BASE 100
// AASX import 시점에 이 텍스트를 읽어 FBTagMapPreset.BaseAddresses /
// ControlFlowProperties.BaseAddressOverride 로 이식한다.

module ControlIoLegacyMigration =

    let private toIwAddr (n: int) = sprintf "%%IW%d.0.0" n
    let private toQwAddr (n: int) = sprintf "%%QW%d.0.0" n
    let private toMwAddr (n: int) = sprintf "%%MW%d" n

    let private applyBase (target: FBBaseAddressSet) (directive: string) (arg: string) =
        match Int32.TryParse arg with
        | true, n ->
            match directive with
            | "@IW_BASE" -> target.InputBase  <- toIwAddr n
            | "@QW_BASE" -> target.OutputBase <- toQwAddr n
            | "@MW_BASE" -> target.MemoryBase <- toMwAddr n
            | _ -> ()
        | _ -> ()

    /// IoSystemBase 텍스트 → SystemType → BaseAddressSet
    let parseSystemBase (text: string) : System.Collections.Generic.Dictionary<string, FBBaseAddressSet> =
        let result =
            System.Collections.Generic.Dictionary<string, FBBaseAddressSet>(
                StringComparer.OrdinalIgnoreCase)
        if not (String.IsNullOrWhiteSpace text) then
            let mutable current : string option = None
            for rawLine in text.Split([|'\n'|]) do
                let line = rawLine.Trim()
                if line.Length > 0 && not (line.StartsWith "#") && not (line.StartsWith "//") && line.StartsWith "@" then
                    let parts = line.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length >= 2 then
                        let directive = parts.[0].ToUpperInvariant()
                        let arg = parts.[1]
                        match directive with
                        | "@SYSTEM" -> current <- Some (arg.ToUpperInvariant())
                        | "@IW_BASE" | "@QW_BASE" | "@MW_BASE" ->
                            match current with
                            | Some sys ->
                                let target =
                                    match result.TryGetValue sys with
                                    | true, v -> v
                                    | _ ->
                                        let v = FBBaseAddressSet()
                                        result.[sys] <- v
                                        v
                                applyBase target directive arg
                            | None -> ()
                        | _ -> ()
        result

    /// IoFlowBase 텍스트 → FlowName → BaseAddressSet
    let parseFlowBase (text: string) : System.Collections.Generic.Dictionary<string, FBBaseAddressSet> =
        let result =
            System.Collections.Generic.Dictionary<string, FBBaseAddressSet>(
                StringComparer.OrdinalIgnoreCase)
        if not (String.IsNullOrWhiteSpace text) then
            let mutable current : string option = None
            for rawLine in text.Split([|'\n'|]) do
                let line = rawLine.Trim()
                if line.Length > 0 && not (line.StartsWith "#") && not (line.StartsWith "//") && line.StartsWith "@" then
                    let parts = line.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length >= 2 then
                        let directive = parts.[0].ToUpperInvariant()
                        let arg = parts.[1]
                        match directive with
                        | "@FLOW" -> current <- Some arg
                        | "@IW_BASE" | "@QW_BASE" | "@MW_BASE" ->
                            match current with
                            | Some flow ->
                                let target =
                                    match result.TryGetValue flow with
                                    | true, v -> v
                                    | _ ->
                                        let v = FBBaseAddressSet()
                                        result.[flow] <- v
                                        v
                                applyBase target directive arg
                            | None -> ()
                        | _ -> ()
        result

    /// Preset 에 SystemBase 텍스트 마이그레이션 적용
    let applySystemBaseToPresets
        (presets: System.Collections.Generic.Dictionary<string, FBTagMapPreset>)
        (legacyText: string) : int =
        let map = parseSystemBase legacyText
        let mutable n = 0
        for KeyValue(sysType, baseSet) in map do
            let preset =
                match presets.TryGetValue sysType with
                | true, p -> p
                | _ ->
                    let p = FBTagMapPreset()
                    presets.[sysType] <- p
                    p
            preset.BaseAddresses <- baseSet
            n <- n + 1
        n

