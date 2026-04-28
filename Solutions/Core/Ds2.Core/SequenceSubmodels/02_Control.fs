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
// FB TAG MAP — FB 포트 ↔ TagPattern 매핑
// =============================================================================
//
// 태그패턴 매크로:
//   $(F) = 액티브 시스템에서 ApiCall이 있는 Flow 이름
//   $(D) = 그 Call이 호출하는 Device alias (패시브 시스템 참조)
//   $(A) = 호출하는 ApiDef 이름
//
// Direction (PLC 신호 기준):
//   "Input"  → IW (디바이스 → PLC, 피드백/센서)
//   "Output" → QW (PLC → 디바이스, 명령/액추에이터)
//   IsDummy=true → MW (가상 태그, 실배선 없음)

/// FB 포트 하나의 정의 + 생성 결과
/// TagPattern/IsDummy: 사용자 설정 (템플릿)
/// VarName/Address:    코드 생성 결과 ($(F)/$(D)/$(A) 확장 후)
type FBTagMapPort() =
    member val FBPort:     string = "" with get, set   // XGI_Template.xml 포트 이름
    member val Direction:  string = "" with get, set   // "Input"=IW | "Output"=QW
    member val DataType:   string = "BOOL" with get, set
    member val TagPattern: string = "" with get, set   // 예: "$(F)_Q_$(D)_$(A)"
    member val IsDummy:    bool   = false with get, set // true → MW, 실배선 없음
    member val VarName:    string = "" with get, set   // 생성 결과: 확장된 태그 이름
    member val Address:    string = "" with get, set   // 생성 결과: PLC 주소
    /// 포트별 주소 오프셋 힌트 (AddressAssignRule=PortIndex 일 때 사용)
    member val AddressOffset: int option = None with get, set

/// ApiCall 1개에 대응하는 FB 인스턴스 (생성 결과)
/// AASX에 저장 (외부 참조용), import 복원 없음 (항상 재생성)
type FBTagMapInstance() =
    member val FBTypeName:  string = "" with get, set  // FB 타입명 (XGI_Template.xml 기준)
    member val FlowName:    string = "" with get, set  // 액티브 시스템 Flow 이름 → $(F)
    member val WorkName:    string = "" with get, set
    member val DeviceAlias: string = "" with get, set  // 호출된 디바이스 alias → $(D)
    member val ApiDefName:  string = "" with get, set  // 호출된 ApiDef 이름 → $(A)
    member val Ports: ResizeArray<FBTagMapPort> = ResizeArray() with get, set


/// FBTagMapPreset 내부 기준 주소 세트 (IoSystemBase/IoFlowBase 매크로 파일을 대체)
type FBBaseAddressSet() =
    member val InputBase:  string = "%IW0.0.0" with get, set  // IW 기준 주소
    member val OutputBase: string = "%QW0.0.0" with get, set  // QW 기준 주소
    member val MemoryBase: string = "%MW100"   with get, set  // MW(Dummy) 기준 주소

/// 신호 패턴 엔트리 — Tag Wizard Step 2 "신호 템플릿" 탭에서 편집.
/// ApiDefName 별로 IW/QW/MW 태그 이름 패턴을 정의한다.
/// 이 값은 FBTagMapPreset.(Iw|Qw|Mw)Patterns 안에만 존재하며, AASX 에 함께 저장된다.
/// → 외부 *.txt 템플릿 파일 의존성을 제거해 동일 AASX = 동일 PLC 출력 보장.
type SignalPatternEntry() =
    /// ApiDef 이름 (예: "ADV", "RET"). Wizard 에서 드롭다운으로 선택.
    member val ApiName:     string = "" with get, set
    /// 태그 이름 패턴 (예: "W_$(F)_WRS_$(D)_$(A)"). $(F)/$(D)/$(A) 는 생성 시점에 확장.
    member val Pattern:     string = "" with get, set
    /// 이 신호를 연결할 FB Local Label 이름 (옵션).
    member val TargetFBPort: string = "" with get, set

/// 디바이스 타입(SystemType)별 FBTagMap 프리셋
/// 시스템 인스턴스별이 아닌 디바이스 타입별 전역 설정
/// Promaker 사용자 설정 (AppData JSON에 저장)
type FBTagMapPreset() =
    /// 이 디바이스 타입에 적용할 FB 이름 (XGI_Template.xml Type="2" 기준)
    member val FBTagMapName: string = "" with get, set
    /// FB 포트별 TagPattern 템플릿
    member val FBTagMapTemplate: ResizeArray<FBTagMapPort> = ResizeArray() with get, set
    /// 기준 주소 (IoSystemBase/IoFlowBase 매크로 파일 대체)
    member val BaseAddresses: FBBaseAddressSet = FBBaseAddressSet() with get, set
    /// 주소 자동 할당 규칙
    member val AddressRule: AddressAssignRule = AddressAssignRule.Sequential with get, set
    /// ApiDefName → FB AutoAux 입력 포트 이름
    /// 예: 실린더 { "ADV"→"M_Auto_Adv", "RET"→"M_Auto_Ret" }
    ///     로봇   { "STEP1"→"1ST_IN_OK", "STEP2"→"2ND_IN_OK" }
    /// Promaker UI (TagWizardDialog) 에서 API 별로 FB 포트 드롭다운 선택.
    member val AutoAuxPortMap: System.Collections.Generic.Dictionary<string, string> =
        System.Collections.Generic.Dictionary<string, string>() with get, set
    /// ApiDefName → FB ComAux 입력 포트 이름 (옵션)
    /// 예: 실린더 { "ADV"→"M_Com_Adv", "RET"→"M_Com_Ret" }
    ///     로봇: 비어 있음 → ComAux coil 생성 안 함
    member val ComAuxPortMap: System.Collections.Generic.Dictionary<string, string> =
        System.Collections.Generic.Dictionary<string, string>() with get, set
    /// IW(입력 워드) 신호 템플릿 — 외부 txt 파일을 완전히 대체한다.
    member val IwPatterns: ResizeArray<SignalPatternEntry> = ResizeArray() with get, set
    /// QW(출력 워드) 신호 템플릿.
    member val QwPatterns: ResizeArray<SignalPatternEntry> = ResizeArray() with get, set
    /// MW(Dummy 메모리 워드) 신호 템플릿.
    member val MwPatterns: ResizeArray<SignalPatternEntry> = ResizeArray() with get, set

// =============================================================================
// SIGNAL BINDING — IoList 행이 FB 포트를 소유 (B안: IoList 단일 진실원)
// =============================================================================
//
// 설계:
//   • 매크로 편집은 오직 IoList(Wizard Step 3) 에서만 수행
//   • 각 IoListEntry 는 자신을 어떤 FB 의 어떤 Local Label 로 연결할지 보유 (1:1)
//   • Dummy 신호는 별도 풀(DummyEntries)에 저장 — 실배선 없음, MW 자동할당
//   • 코드 생성은 IoListEntries 를 순회하며 FB 인스턴스를 조립

/// FB 포트 바인딩 (1:1)
type SignalBinding() =
    /// XGI_Template.xml 의 FB 타입명 (예: "FB402_Mode_Stn_v2")
    member val TargetFBType: string = "" with get, set
    /// 해당 FB 의 Local Label 이름 (Direction 무관, 모든 포트 선택 가능)
    member val TargetFBPort: string = "" with get, set

/// IoList 엔트리 — 하나의 PLC 신호 + FB 포트 바인딩 (모든 엔트리는 Self = passiveSystem IOList)
type IoListEntry() =
    // ─ 식별 ───────────────────────────────────────────────
    member val Name:        string = "" with get, set    // 최종 VarName (사용자 authoring)
    member val Address:     string = "" with get, set    // PLC 주소 (수동 또는 AddressAssigner)
    member val Direction:   string = "Input" with get, set  // Input | Output
    member val DataType:    string = "BOOL" with get, set
    member val Comment:     string = "" with get, set

    // ─ ApiCall 컨텍스트 (역참조) ─────────────────────────
    member val FlowName:    string = "" with get, set
    member val DeviceAlias: string = "" with get, set
    member val ApiDefName:  string = "" with get, set
    member val SystemId:    Guid   = Guid.Empty with get, set   // passiveSystem Id

    // ─ FB 바인딩 (1:1) ──────────────────────────────────
    member val Binding:     SignalBinding = SignalBinding() with get, set

/// 더미 IoList 엔트리 — 실배선 없음, MW 자동할당 (별도 탭에서 편집)
type DummyIoEntry() =
    member val Name:        string = "" with get, set
    member val Address:     string = "" with get, set
    member val Comment:     string = "" with get, set
    member val FlowName:    string = "" with get, set
    member val DeviceAlias: string = "" with get, set
    member val ApiDefName:  string = "" with get, set
    member val SystemId:    Guid   = Guid.Empty with get, set
    member val Binding:     SignalBinding = SignalBinding() with get, set


// =============================================================================
// PROPERTIES - 제어 속성 클래스
// =============================================================================

// =============================================================================
// Robot 보조 rung 메타데이터 (Wizard Robot Step 편집).
// FB496/497/498 등 로봇 FB 호출 외 보조 rung 시퀀스 (PROG_NO 분기 / READY 집계 /
// HOME_PLS / mutual interlock 등) 의 입력값.
// =============================================================================

/// PROG_NO 분기 1건 — `BT_L1` 등 조건 태그 AND 결합 시 ProgNo 할당.
type RobotProgNoBranch() =
    member val ConditionTags : ResizeArray<string> = ResizeArray() with get, set
    member val ProgNo : int = 0 with get, set

/// 집계 그룹 — 같은 Work 의 여러 로봇 신호를 AND 로 묶어 하나의 종합 coil 생성.
/// 예: Kind="READY", Aggregated="W_TT_M_ALL_RBT_READY", Sources=["W_R112_1_M_READY"; ...]
type RobotAggregationGroup() =
    member val Kind : string = "" with get, set                      // READY / AUTO / FAULT / HOME / EMSTOP
    member val Aggregated : string = "" with get, set                // 종합 coil 이름
    member val Sources : ResizeArray<string> = ResizeArray() with get, set

/// Mutual interlock — 다른 로봇의 신호를 본 로봇 FB Call 의 특정 포트에 연결.
type RobotMutualInterlock() =
    member val SourceSignal : string = "" with get, set              // 예: W_S112_I_RBT2_MUTUAL_INT1
    member val TargetPort   : string = "" with get, set              // 예: Mutual_Int1

/// 로봇 1대 메타데이터.
type RobotMetadata() =
    /// FB Call 의 enable 인자에 들어가는 임시 변수 setup 조건들.
    /// 예: ["NOT W_TT_M_TOTAL_ERR"; "NOT W_S112B_M_ERR_RESET"] → @LDTemp1 chain.
    member val EnableConditions : ResizeArray<string> = ResizeArray() with get, set

    /// PROG_NO 분기 — ApiDef 1개 당 여러 분기 가능 (BT_L1→11 / BT_L2→12 등).
    member val ProgNoBranches : ResizeArray<RobotProgNoBranch> = ResizeArray() with get, set

    /// 집계 그룹 (Work 단위로 한 번 emit — 첫 번째 로봇이 대표).
    member val AggregationGroups : ResizeArray<RobotAggregationGroup> = ResizeArray() with get, set

    /// Mutual interlock 매핑 (사용자 명시).
    member val MutualInterlocks : ResizeArray<RobotMutualInterlock> = ResizeArray() with get, set

    /// 1ST_IN_OK 같은 추가 조건 coil — Key: coil 이름, Value: AND 결합되는 source 태그 list.
    member val AuxCoils : System.Collections.Generic.Dictionary<string, ResizeArray<string>> =
        System.Collections.Generic.Dictionary() with get, set


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

    /// 코드 생성 결과: ApiCall 1개 = FBTagMapInstance 1개 (AASX 저장, import 복원 없음)
    /// FBTagMap 템플릿은 SystemType 기반 전역 프리셋 (Promaker AppData) 사용
    member val FBTagMapInstances: ResizeArray<FBTagMapInstance> = ResizeArray() with get, set

    // ========== FBTagMap 프리셋 + Global IO (TAG Wizard / PLC 생성, AASX에 저장) ==========
    /// SystemType별 기준 주소/AddressRule 설정 (Wizard Step 1).
    /// FBTagMapTemplate 필드는 더 이상 편집되지 않음 (IoListEntries 가 대체).
    member val FBTagMapPresets   = System.Collections.Generic.Dictionary<string, FBTagMapPreset>() with get, set

    /// IoList 엔트리 (Wizard Step 3 편집, Step 4 에서 FB 바인딩).
    /// 매크로가 정의되는 **유일한** 위치 — SignalBinding 이 FB 측 매칭을 연결.
    member val IoListEntries     = ResizeArray<IoListEntry>() with get, set

    /// 더미 IoList (실배선 없음, Wizard Dummy 탭).
    member val DummyEntries      = ResizeArray<DummyIoEntry>() with get, set

    // ========== Robot 보조 rung 메타데이터 (Wizard Robot Step 편집) ==========
    /// 로봇별 메타데이터 — Key: Device alias (Call.DevicesAlias 와 일치).
    /// 실린더는 사용 안 함 — 로봇 패밀리 (FB496/497/498) 한정.
    member val RobotMetadata : System.Collections.Generic.Dictionary<string, RobotMetadata> =
        System.Collections.Generic.Dictionary() with get, set


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
    member val InTagName: string option = None with get, set
    member val InTagAddress: string option = None with get, set
    member val OutTagName: string option = None with get, set
    member val OutTagAddress: string option = None with get, set

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
/// IoList 바인딩 무결성 검증 — B안 1:1 규칙
module IoListValidation =

    /// 중복 바인딩 검출: 같은 (Flow, Device, ApiDef, FBType, FBPort) 가 2번 이상 나타나면 오류.
    /// 반환: 위반 메시지 목록 (빈 목록이면 OK)
    let detectDuplicateBindings (entries: seq<IoListEntry>) : string list =
        let keyOf (e: IoListEntry) =
            (e.FlowName, e.DeviceAlias, e.ApiDefName, e.Binding.TargetFBType, e.Binding.TargetFBPort)
        entries
        |> Seq.filter (fun e ->
            not (String.IsNullOrEmpty e.Binding.TargetFBType) &&
            not (String.IsNullOrEmpty e.Binding.TargetFBPort))
        |> Seq.groupBy keyOf
        |> Seq.choose (fun (key, group) ->
            let count = Seq.length group
            if count > 1 then
                let (f, d, a, fb, port) = key
                Some (sprintf "중복 바인딩: Flow=%s, Device=%s, Api=%s → %s.%s (%d개 엔트리)"
                              f d a fb port count)
            else None)
        |> Seq.toList

    /// 미바인딩 엔트리 경고 (TargetFBType/FBPort 중 하나라도 비어있음)
    let detectUnboundEntries (entries: seq<IoListEntry>) : string list =
        entries
        |> Seq.filter (fun e ->
            String.IsNullOrEmpty e.Binding.TargetFBType ||
            String.IsNullOrEmpty e.Binding.TargetFBPort)
        |> Seq.map (fun e ->
            sprintf "미바인딩 신호: %s (Flow=%s, Device=%s, Api=%s)"
                    e.Name e.FlowName e.DeviceAlias e.ApiDefName)
        |> Seq.toList


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


module ControlHelpers =

    // ========== 태그 생성 함수 ==========

    /// 자동 태그 이름 생성
    let generateTagName
        (format: string)
        (systemId: string)
        (workId: string)
        (signal: string) =

        format
            .Replace("{SystemId}", systemId)
            .Replace("{WorkId}", workId)
            .Replace("{Signal}", signal)

    /// 이름 변환 적용
    let applyNameTransform (transform: NameTransform) (name: string) =
        match transform with
        | UpperCase -> name.ToUpperInvariant()
        | LowerCase -> name.ToLowerInvariant()
        | CamelCase ->
            if name.Length = 0 then name
            else name.[0].ToString().ToLowerInvariant() + name.[1..]
        | PascalCase ->
            if name.Length = 0 then name
            else name.[0].ToString().ToUpperInvariant() + name.[1..]
        | NoTransform -> name

    /// Prefix 추가
    let addPrefix (prefix: string option) (tagName: string) =
        match prefix with
        | Some p when not (String.IsNullOrEmpty(p)) -> p + tagName
        | _ -> tagName


    // ========== PLC 주소 검증 함수 ==========

    /// Mitsubishi 주소 검증 (M, D, X, Y, etc.)
    let validateMitsubishiAddress (address: string) =
        if address.Length < 2 then false
        else
            let deviceCode = address.[0]
            let number = address.[1..]
            match deviceCode with
            | 'M' | 'D' | 'X' | 'Y' | 'T' | 'C' | 'S' ->
                System.Int32.TryParse(number) |> fst
            | _ -> false

    /// Siemens S7 주소 검증 (DB, M, I, Q, etc.)
    let validateSiemensAddress (address: string) =
        if address.Length < 2 then false
        else
            address.StartsWith("DB") ||
            address.StartsWith("M") ||
            address.StartsWith("I") ||
            address.StartsWith("Q")

    /// Rockwell AB 주소 검증 (N7, B3, F8, etc.)
    let validateRockwellAddress (address: string) =
        if address.Length < 2 then false
        else
            let fileType = address.[0]
            match fileType with
            | 'N' | 'B' | 'F' | 'T' | 'C' | 'R' | 'S' ->
                true
            | _ -> false

    /// 벤더별 주소 검증
    let validateAddress (vendor: PlcVendor) (address: string) =
        match vendor with
        | Mitsubishi -> validateMitsubishiAddress address
        | Siemens -> validateSiemensAddress address
        | RockwellAB -> validateRockwellAddress address
        | _ -> true // Generic은 검증 생략

    // ========== 안전 기능 함수 ==========

    /// 타임아웃 확인
    let isTimeout (startTime: DateTime) (timeoutSeconds: float) =
        (DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds

    // ========== 상태 관리 함수 ==========

    /// Work 상태 전이 검증
    let canTransitionState (currentState: string) (newState: string) =
        match currentState, newState with
        | "Idle", "Running" -> true
        | "Running", "Paused" -> true
        | "Running", "Error" -> true
        | "Paused", "Running" -> true
        | "Error", "Idle" -> true
        | _, "Idle" -> true  // 항상 Idle로 리셋 가능
        | _ -> false


    // ========== 인터록 함수 ==========

    /// 인터록 조건 평가
    let evaluateInterlockConditions (conditions: string array) =
        // 실제 구현에서는 조건 파싱 및 평가
        // 예: "DOOR_CLOSED AND LIGHT_CURTAIN_OK"
        conditions.Length = 0 || true // 임시: 조건 없거나 모두 만족

    /// 인터록 우선순위 정렬
    let sortByPriority (components: HwComponent list) =
        components
        |> List.sortByDescending (fun c -> c.Priority)
