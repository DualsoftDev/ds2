namespace Ds2.Core

open System
open System.Text.Json.Serialization

// =============================================================================
// SEQUENCE CONTROL SUBMODEL
// PLC 연동 및 하드웨어 제어
// =============================================================================

type PlcDataType = Bool | Int16 | UInt16 | Int32 | UInt32 | Float32 | Float64 | String of int
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

type HwButton    [<JsonConstructor>] internal (name) =
    inherit HwComponent(name)
    member val ButtonType    = HwButtonType.Drive       with get, set

type HwLamp      [<JsonConstructor>] internal (name) =
    inherit HwComponent(name)
    member val LampType      = HwLampType.DriveState    with get, set

type HwCondition [<JsonConstructor>] internal (name) =
    inherit HwComponent(name)
    member val ConditionType  = HwConditionType.Ready   with get, set
    member val ExpectedOn     = true                    with get, set
    member val Description:     string option = None    with get, set
    member val FailureMessage:  string option = None    with get, set
    member val IsMandatory    = true                    with get, set

type HwAction    [<JsonConstructor>] internal (name) =
    inherit HwComponent(name)
    member val ActionType     = HwActionType.Emergency  with get, set
    member val ActionMode     = HwActionMode.SetOn      with get, set
    member val HoldTime:        TimeSpan option = None  with get, set
    member val Description:     string option = None    with get, set
    member val FailureMessage:  string option = None    with get, set

// ─── Properties ───────────────────────────────────────────────────────────────

type ControlSystemProperties() =
    inherit PropertiesBase<ControlSystemProperties>()
    member val EnableAutoTagGeneration = false                          with get, set
    member val TagPrefix: string option = None                         with get, set
    member val TagNamingFormat = "{SystemId}_{WorkId}_{Signal}"        with get, set
    member val NameTransform   = "UpperCase"                           with get, set
    member val PlcVendor       = "Mitsubishi"                          with get, set
    member val PlcIpAddress    = "192.168.0.1"                         with get, set
    member val PlcPort         = 5000                                  with get, set
    member val CommunicationTimeout = TimeSpan.FromSeconds 5.0         with get, set
    member val RetryAttempts   = 3                                     with get, set
    member val TagMatchMode            = "ByAddress"                   with get, set
    member val EnableAddressValidation = true                          with get, set
    member val CaseSensitiveMatching   = false                         with get, set
    member val EnableSafetyInterlock   = true                          with get, set
    member val EmergencyStopEnabled    = true                          with get, set
    member val SafetyDoorCheck         = false                         with get, set
    member val LightCurtainCheck       = false                         with get, set
    member val TwoHandControl          = false                         with get, set
    member val SafetyTimeoutSeconds    = 30.0                          with get, set
    member val EnableHealthCheck   = true                              with get, set
    member val HealthCheckInterval = TimeSpan.FromSeconds 10.0         with get, set
    member val EnableHeartbeat     = true                              with get, set
    member val HeartbeatInterval   = TimeSpan.FromSeconds  1.0         with get, set

type ControlFlowProperties() =
    inherit PropertiesBase<ControlFlowProperties>()
    member val FlowControlEnabled = false with get, set
    member val FlowPriority       = 0     with get, set

type ControlWorkProperties() =
    inherit PropertiesBase<ControlWorkProperties>()
    member val EnableHardwareControl = false          with get, set
    member val ControlMode           = "Sequential"   with get, set
    member val InTagName:    string option = None     with get, set
    member val InTagAddress: string option = None     with get, set
    member val OutTagName:   string option = None     with get, set
    member val OutTagAddress:string option = None     with get, set
    member val CallDirection         = "InOut"        with get, set
    member val WorkTimeout: TimeSpan option = None    with get, set
    member val EnableTimeout         = false          with get, set
    member val TimeoutAction         = "Abort"        with get, set
    member val RequiresSafetyCheck   = false          with get, set
    member val InterlockConditions: string[] = [||]   with get, set
    member val EnableMotionControl   = false          with get, set
    member val MotionControlMode: string option = None with get, set
    member val TargetPosition:    float  option = None with get, set
    member val TargetVelocity:    float  option = None with get, set
    member val Acceleration:      float  option = None with get, set
    member val Deceleration:      float  option = None with get, set
    member val CurrentState        = "Idle"           with get, set
    member val LastExecutionTime: DateTime option = None with get, set
    member val ExecutionCount      = 0                with get, set
    member val ErrorCount          = 0                with get, set

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
        components |> List.sortBy (fun c -> c.Name)
