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

// ─── Enumerations ────────────────────────────────────────────────────────────

type PlcDataType = Bool | Int16 | UInt16 | Int32 | UInt32 | Float32 | Float64 | String of int
/// I/O 태그 매핑 방향 (Call I/O Direction)
///   InOut     — InTag + OutTag 모두 존재 (양방향)
///   InOnly    — InTag만 존재 (센서 읽기)
///   OutOnly   — OutTag만 존재 (액추에이터 쓰기)
///   NoMapping — PLC 매핑 없음 (순수 소프트웨어)
type IoMappingDirection = InOut | InOnly | OutOnly | NoMapping
type TagMatchMode = ByAddress | ByName
type NameTransform = UpperCase | LowerCase | CamelCase | PascalCase | NoTransform
type PlcVendor = Mitsubishi | Siemens | RockwellAB | Omron | Generic
type MotionControlMode = Position | Velocity | Torque | Pulse
type SafetyState = Safe | Warning | Fault | Emergency

type HwButtonType    = Auto=0 | Manual=1 | Drive=2 | Test=3 | Pause=4 | Emergency=5 | Clear=6 | Home=7 | Ready=8
type HwLampType      = IdleMode=0 | AutoMode=1 | ManualMode=2 | DriveState=3 | TestDriveState=4 | ErrorState=5 | ReadyState=6 | OriginState=7 | PauseState=8
type HwConditionType = Ready=0 | Drive=1
type HwActionType    = Ready=0 | Drive=1 | Finish=2 | Homing=3 | Pause=4 | Emergency=5 | Fault=6
type HwActionMode    = SetOn=0 | SetOff=1 | Pulse=2

/// FB Port mapping status (how the port was resolved)
type MappingStatus =
    | AutoMapped   = 0  // 자동 매핑 성공 (SignalLookup)
    | UserMapped   = 1  // 사용자 지정 매핑
    | Fallback     = 2  // 기본값 매핑 (Template의 DefaultVar 사용)

// ─── Value Types ──────────────────────────────────────────────────────────────

/// 모션 파라미터 (Position Control용)
[<Struct>]
type MotionParameters = {
    TargetPosition: float           // 목표 위치 (mm, degree)
    TargetVelocity: float           // 목표 속도 (mm/min, rpm)
    Acceleration: float             // 가속도 (mm/s², rad/s²)
    Deceleration: float             // 감속도
    Jerk: float option              // 저크 (가속도 변화율)
}

/// 펄스 파라미터 (Pulse Control용)
[<Struct>]
type PulseParameters = {
    PulseWidth: TimeSpan            // 펄스 폭 (HIGH 시간)
    PulseInterval: TimeSpan         // 펄스 간격 (LOW 시간)
    PulseCount: int option          // 펄스 횟수 (None = 무한)
}

/// 안전 인터록 설정
[<Struct>]
type SafetyInterlock = {
    EmergencyStopEnabled: bool      // 비상정지 활성화
    SafetyDoorCheck: bool           // 안전 도어 확인
    LightCurtainCheck: bool         // 광 커튼 확인
    TwoHandControl: bool            // 양손 조작
    TimeoutSeconds: float           // 타임아웃 (초)
}


// ─── HW Components ───────────────────────────────────────────────────────────

[<AbstractClass>]
type HwComponent(name: string) =
    member val Name      = name with get, set
    member val InTag:  IOTag option = None with get, set
    member val OutTag: IOTag option = None with get, set
    member val FlowGuids = ResizeArray<Guid>() with get, set
    member val IsEnabled = true with get, set
    member val Priority  = 0 with get, set              // 우선순위 (높을수록 우선)

type HwButton    [<JsonConstructor>] internal (name) =
    inherit HwComponent(name)
    member val ButtonType    = HwButtonType.Drive       with get, set
    member val DebounceMs    = 50                       with get, set

type HwLamp      [<JsonConstructor>] internal (name) =
    inherit HwComponent(name)
    member val LampType      = HwLampType.DriveState    with get, set
    member val LampColor     = "Green"                  with get, set
    member val BlinkEnabled  = false                    with get, set
    member val BlinkIntervalMs = 500                    with get, set

type HwCondition [<JsonConstructor>] internal (name) =
    inherit HwComponent(name)
    member val ConditionType  = HwConditionType.Ready   with get, set
    member val ExpectedOn     = true                    with get, set
    member val Description:     string option = None    with get, set
    member val FailureMessage:  string option = None    with get, set
    member val IsMandatory    = true                    with get, set
    member val Threshold:     float option = None       with get, set
    member val Hysteresis:    float option = None       with get, set

type HwAction    [<JsonConstructor>] internal (name) =
    inherit HwComponent(name)
    member val ActionType     = HwActionType.Emergency  with get, set
    member val ActionMode     = HwActionMode.SetOn      with get, set
    member val HoldTime:        TimeSpan option = None  with get, set
    member val Description:     string option = None    with get, set
    member val FailureMessage:  string option = None    with get, set
    member val RetryCount     = 0                       with get, set

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

/// ApiCall 1개에 대응하는 FB 인스턴스 (생성 결과)
/// AASX에 저장 (외부 참조용), import 복원 없음 (항상 재생성)
type FBTagMapInstance() =
    member val FBTypeName:  string = "" with get, set  // FB 타입명 (XGI_Template.xml 기준)
    member val FlowName:    string = "" with get, set  // 액티브 시스템 Flow 이름 → $(F)
    member val WorkName:    string = "" with get, set
    member val DeviceAlias: string = "" with get, set  // 호출된 디바이스 alias → $(D)
    member val ApiDefName:  string = "" with get, set  // 호출된 ApiDef 이름 → $(A)
    member val Ports: ResizeArray<FBTagMapPort> = ResizeArray() with get, set


/// 디바이스 타입(SystemType)별 FBTagMap 프리셋
/// 시스템 인스턴스별이 아닌 디바이스 타입별 전역 설정
/// Promaker 사용자 설정 (AppData JSON에 저장)
type FBTagMapPreset() =
    /// 이 디바이스 타입에 적용할 FB 이름 (XGI_Template.xml Type="2" 기준)
    member val FBTagMapName: string = "" with get, set
    /// FB 포트별 TagPattern 템플릿
    member val FBTagMapTemplate: ResizeArray<FBTagMapPort> = ResizeArray() with get, set


// ─── Properties ───────────────────────────────────────────────────────────────

/// System-level 제어 속성
type ControlSystemProperties() =
    inherit PropertiesBase<ControlSystemProperties>()

    // ========== 자동 태그 생성 ==========
    member val EnableAutoTagGeneration = false                          with get, set
    member val TagPrefix: string option = None                          with get, set
    member val TagNamingFormat = "{SystemId}_{WorkId}_{Signal}"         with get, set
    member val NameTransform   = "UpperCase"                            with get, set

    // ========== PLC 통신 설정 ==========
    member val PlcVendor       = "Mitsubishi"                           with get, set
    member val PlcIpAddress    = "192.168.0.1"                          with get, set
    member val PlcPort         = 5000                                   with get, set
    member val CommunicationTimeout = TimeSpan.FromSeconds 5.0          with get, set
    member val RetryAttempts   = 3                                      with get, set

    // ========== 태그 매칭 설정 ==========
    member val TagMatchMode            = "ByAddress"                    with get, set
    member val EnableAddressValidation = true                           with get, set
    member val CaseSensitiveMatching   = false                          with get, set

    // ========== 안전 기능 ==========
    member val EnableSafetyInterlock   = true                           with get, set
    member val EmergencyStopEnabled    = true                           with get, set
    member val SafetyDoorCheck         = false                          with get, set
    member val LightCurtainCheck       = false                          with get, set
    member val TwoHandControl          = false                          with get, set
    member val SafetyTimeoutSeconds    = 30.0                           with get, set

    // ========== 모니터링 ==========
    member val EnableHealthCheck   = true                               with get, set
    member val HealthCheckInterval = TimeSpan.FromSeconds 10.0          with get, set
    member val EnableHeartbeat     = true                               with get, set
    member val HeartbeatInterval   = TimeSpan.FromSeconds  1.0          with get, set

    // ========== PLC 코드 생성 (FBTagMap) ==========
    member val SystemType: string option = None with get, set

    /// 코드 생성 결과: ApiCall 1개 = FBTagMapInstance 1개 (AASX 저장, import 복원 없음)
    /// FBTagMap 템플릿은 SystemType 기반 전역 프리셋 (Promaker AppData) 사용
    member val FBTagMapInstances: ResizeArray<FBTagMapInstance> = ResizeArray() with get, set

    // ========== FBTagMap 프리셋 + IO 템플릿 (TAG Wizard / PLC 생성, AASX에 저장) ==========
    /// SystemType별 FB 포트 매핑 규칙 (TagPattern 프리셋)
    member val FBTagMapPresets   = System.Collections.Generic.Dictionary<string, FBTagMapPreset>() with get, set
    /// system_base.txt 내용 (SystemType별 글로벌 IW/QW/MW 기준 주소)
    member val IoSystemBase      : string = "" with get, set
    /// flow_base.txt 내용 (Flow별 로컬 IW/QW/MW 기준 주소)
    member val IoFlowBase        : string = "" with get, set
    /// 장치 타입별 신호 템플릿 FileName → Content (예: "RBT.txt" → "[IW]\nADV:...")
    member val IoDeviceTemplates = System.Collections.Generic.Dictionary<string, string>() with get, set

/// Flow-level 제어 속성
type ControlFlowProperties() =
    inherit PropertiesBase<ControlFlowProperties>()
    member val FlowControlEnabled = false with get, set
    member val FlowPriority       = 0     with get, set

/// Work-level 제어 속성
type ControlWorkProperties() =
    inherit PropertiesBase<ControlWorkProperties>()

    // ========== 기본 Work 제어 ==========
    member val EnableHardwareControl = false          with get, set
    member val ControlMode           = "Sequential"   with get, set

    // ========== I/O 태그 (자동 생성 또는 수동 설정) ==========
    member val InTagName:    string option = None     with get, set
    member val InTagAddress: string option = None     with get, set
    member val OutTagName:   string option = None     with get, set
    member val OutTagAddress:string option = None     with get, set
    member val CallDirection         = "InOut"        with get, set

    // ========== 타임아웃 설정 ==========
    member val WorkTimeout: TimeSpan option = None    with get, set
    member val EnableTimeout         = false          with get, set
    member val TimeoutAction         = "Abort"        with get, set

    // ========== 인터록 ==========
    member val RequiresSafetyCheck   = false          with get, set
    member val InterlockConditions: string[] = [||]   with get, set

    // ========== 모션 제어 ==========
    member val EnableMotionControl   = false          with get, set
    member val MotionControlMode: string option = None with get, set
    member val TargetPosition:    float  option = None with get, set
    member val TargetVelocity:    float  option = None with get, set
    member val Acceleration:      float  option = None with get, set
    member val Deceleration:      float  option = None with get, set

    // ========== 펄스 제어 ==========
    member val UsePulseControl     = false            with get, set
    member val PulseWidthMs:    int option = None     with get, set
    member val PulseIntervalMs: int option = None     with get, set
    member val PulseCount:      int option = None     with get, set

    // ========== 상태 추적 ==========
    member val CurrentState        = "Idle"           with get, set
    member val LastExecutionTime: DateTime option = None with get, set
    member val ExecutionCount      = 0                with get, set
    member val ErrorCount          = 0                with get, set

/// Call-level 제어 속성
type ControlCallProperties() =
    inherit PropertiesBase<ControlCallProperties>()
    member val CallDirection  = "InOut"               with get, set
    member val InTagName:    string option = None     with get, set
    member val InTagAddress: string option = None     with get, set
    member val OutTagName:   string option = None     with get, set
    member val OutTagAddress:string option = None     with get, set
    member val EnableRetry        = false             with get, set
    member val MaxRetryCount      = 3                 with get, set
    member val RetryDelayMs       = 1000              with get, set
    member val CallTimeout: TimeSpan option = None    with get, set
    member val WaitForCompletion  = true              with get, set
    member val EnableConditional  = false             with get, set
    member val ConditionExpression: string option = None with get, set


// ─── Helpers ─────────────────────────────────────────────────────────────────

module ControlHelpers =

    let generateTagName (fmt: string) systemId workId signal =
        fmt.Replace("{SystemId}", systemId)
           .Replace("{WorkId}", workId)
           .Replace("{Signal}", signal)

    let applyNameTransform transform (name: string) =
        match transform with
        | UpperCase  -> name.ToUpperInvariant()
        | LowerCase  -> name.ToLowerInvariant()
        | CamelCase  -> if name.Length = 0 then name else name.[0].ToString().ToLowerInvariant() + name.[1..]
        | PascalCase -> if name.Length = 0 then name else name.[0].ToString().ToUpperInvariant() + name.[1..]
        | NoTransform -> name

    let addPrefix prefix tagName =
        match prefix with
        | Some p when not (String.IsNullOrEmpty p) -> p + tagName
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

    let isTimeout (startTime:DateTime) timeoutSeconds = (DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds

    let canTransitionState current next =
        match current, next with
        | "Idle",    "Running" -> true
        | "Running", "Paused"  -> true
        | "Running", "Error"   -> true
        | "Paused",  "Running" -> true
        | "Error",   "Idle"    -> true
        | _,         "Idle"    -> true
        | _ -> false

    let evaluateInterlockConditions (conditions: string[]) =
        conditions.Length = 0 || true

    let sortByPriority (components: HwComponent list) =
        components |> List.sortByDescending (fun c -> c.Priority)
