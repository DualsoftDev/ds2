namespace Ds2.LlmAgent.Internal

// =============================================================================
// PLC metadata leaf SSOT — production (`ModelProtocol.fs` emit / apply / hasNonDefault)
// + capturer (`Ds2.LlmAgent.Tests/Helpers/ModelEquivalence.fs`) 양측이 같은 4 leaves table
// 을 참조. `Ds2.LlmAgent.Internal` namespace 정책 (assembly 내부 한정 / public 노출 금지)
// 정합 — `[<assembly: InternalsVisibleTo("Ds2.LlmAgent.Tests")>]` 으로 capturer 만 접근.
//
// **#25 (todo §10.2)** — 기존 ModelProtocol.fs 안 file-scoped private 으로 정의되었으나
// capturer (`summarizePlcXxx` 4 함수) 가 같은 54 leaf 를 hardcode 중복 → SSOT 4 위치 분산
// → 1 위치로 통합. capturer 는 leaves table traverse + Kind 별 stringify 만 수행.
//
// **scope**: leaf 정의 (Key + Kind + getter/setter) 만. emit/apply/진단 로직은 production
// (ModelProtocol.fs) 가 ApplyContext 의존성으로 자체 보유. capturer 는 stringify (read-only)
// 만 보유 — SSOT-of-types vs SSOT-of-logic 분리.
// =============================================================================

open System
open Ds2.Core

module internal PlcMetadata =

    /// PLC leaf 의 wire 타입 분류. getter/setter pair 로 entity property 접근.
    /// 각 case 의 setter 는 emit 측 default-skip / apply 측 setter / capturer 의 read-only
    /// (setter 무시) 모두에서 공유. capturer 는 패턴매칭 시 setter 를 `_` 로 무시.
    type PlcLeafKind<'cp> =
        | LBool        of getter: ('cp -> bool)            * setter: ('cp -> bool -> unit)
        | LInt         of getter: ('cp -> int)             * setter: ('cp -> int -> unit)
        | LFloat       of getter: ('cp -> float)           * setter: ('cp -> float -> unit)
        | LString      of getter: ('cp -> string)          * setter: ('cp -> string -> unit)
        | LStringOpt   of getter: ('cp -> string option)   * setter: ('cp -> string option -> unit)
        | LIntOpt      of getter: ('cp -> int option)      * setter: ('cp -> int option -> unit)
        | LFloatOpt    of getter: ('cp -> float option)    * setter: ('cp -> float option -> unit)
        | LTimeSpan    of getter: ('cp -> TimeSpan)        * setter: ('cp -> TimeSpan -> unit)
        | LTimeSpanOpt of getter: ('cp -> TimeSpan option) * setter: ('cp -> TimeSpan option -> unit)

    type PlcLeaf<'cp> = { Key: string; Kind: PlcLeafKind<'cp> }

    let plcSystemLeaves : PlcLeaf<ControlSystemProperties> list = [
        { Key = "enableAutoTagGeneration"; Kind = LBool      ((fun cp -> cp.EnableAutoTagGeneration),  (fun cp v -> cp.EnableAutoTagGeneration <- v)) }
        { Key = "tagPrefix";               Kind = LStringOpt ((fun cp -> cp.TagPrefix),                (fun cp v -> cp.TagPrefix <- v)) }
        { Key = "tagNamingFormat";         Kind = LString    ((fun cp -> cp.TagNamingFormat),          (fun cp v -> cp.TagNamingFormat <- v)) }
        { Key = "nameTransform";           Kind = LString    ((fun cp -> cp.NameTransform),            (fun cp v -> cp.NameTransform <- v)) }
        { Key = "plcVendor";               Kind = LString    ((fun cp -> cp.PlcVendor),                (fun cp v -> cp.PlcVendor <- v)) }
        { Key = "plcIpAddress";            Kind = LString    ((fun cp -> cp.PlcIpAddress),             (fun cp v -> cp.PlcIpAddress <- v)) }
        { Key = "plcPort";                 Kind = LInt       ((fun cp -> cp.PlcPort),                  (fun cp v -> cp.PlcPort <- v)) }
        { Key = "communicationTimeout";    Kind = LTimeSpan  ((fun cp -> cp.CommunicationTimeout),     (fun cp v -> cp.CommunicationTimeout <- v)) }
        { Key = "retryAttempts";           Kind = LInt       ((fun cp -> cp.RetryAttempts),            (fun cp v -> cp.RetryAttempts <- v)) }
        { Key = "tagMatchMode";            Kind = LString    ((fun cp -> cp.TagMatchMode),             (fun cp v -> cp.TagMatchMode <- v)) }
        { Key = "enableAddressValidation"; Kind = LBool      ((fun cp -> cp.EnableAddressValidation),  (fun cp v -> cp.EnableAddressValidation <- v)) }
        { Key = "caseSensitiveMatching";   Kind = LBool      ((fun cp -> cp.CaseSensitiveMatching),    (fun cp v -> cp.CaseSensitiveMatching <- v)) }
        { Key = "enableSafetyInterlock";   Kind = LBool      ((fun cp -> cp.EnableSafetyInterlock),    (fun cp v -> cp.EnableSafetyInterlock <- v)) }
        { Key = "emergencyStopEnabled";    Kind = LBool      ((fun cp -> cp.EmergencyStopEnabled),     (fun cp v -> cp.EmergencyStopEnabled <- v)) }
        { Key = "safetyDoorCheck";         Kind = LBool      ((fun cp -> cp.SafetyDoorCheck),          (fun cp v -> cp.SafetyDoorCheck <- v)) }
        { Key = "lightCurtainCheck";       Kind = LBool      ((fun cp -> cp.LightCurtainCheck),        (fun cp v -> cp.LightCurtainCheck <- v)) }
        { Key = "twoHandControl";          Kind = LBool      ((fun cp -> cp.TwoHandControl),           (fun cp v -> cp.TwoHandControl <- v)) }
        { Key = "safetyTimeoutSeconds";    Kind = LFloat     ((fun cp -> cp.SafetyTimeoutSeconds),     (fun cp v -> cp.SafetyTimeoutSeconds <- v)) }
        { Key = "enableHealthCheck";       Kind = LBool      ((fun cp -> cp.EnableHealthCheck),        (fun cp v -> cp.EnableHealthCheck <- v)) }
        { Key = "healthCheckInterval";     Kind = LTimeSpan  ((fun cp -> cp.HealthCheckInterval),      (fun cp v -> cp.HealthCheckInterval <- v)) }
        { Key = "enableHeartbeat";         Kind = LBool      ((fun cp -> cp.EnableHeartbeat),          (fun cp v -> cp.EnableHeartbeat <- v)) }
        { Key = "heartbeatInterval";       Kind = LTimeSpan  ((fun cp -> cp.HeartbeatInterval),        (fun cp v -> cp.HeartbeatInterval <- v)) }
        { Key = "systemType";              Kind = LStringOpt ((fun cp -> cp.SystemType),               (fun cp v -> cp.SystemType <- v)) }
    ]

    let plcFlowLeaves : PlcLeaf<ControlFlowProperties> list = [
        { Key = "flowControlEnabled";      Kind = LBool      ((fun cp -> cp.FlowControlEnabled),       (fun cp v -> cp.FlowControlEnabled <- v)) }
        { Key = "flowPriority";            Kind = LInt       ((fun cp -> cp.FlowPriority),             (fun cp v -> cp.FlowPriority <- v)) }
    ]

    let plcWorkLeaves : PlcLeaf<ControlWorkProperties> list = [
        { Key = "enableHardwareControl";   Kind = LBool        ((fun cp -> cp.EnableHardwareControl),   (fun cp v -> cp.EnableHardwareControl <- v)) }
        { Key = "controlMode";             Kind = LString      ((fun cp -> cp.ControlMode),             (fun cp v -> cp.ControlMode <- v)) }
        { Key = "inTagName";               Kind = LStringOpt   ((fun cp -> cp.InTagName),               (fun cp v -> cp.InTagName <- v)) }
        { Key = "inTagAddress";            Kind = LStringOpt   ((fun cp -> cp.InTagAddress),            (fun cp v -> cp.InTagAddress <- v)) }
        { Key = "outTagName";              Kind = LStringOpt   ((fun cp -> cp.OutTagName),              (fun cp v -> cp.OutTagName <- v)) }
        { Key = "outTagAddress";           Kind = LStringOpt   ((fun cp -> cp.OutTagAddress),           (fun cp v -> cp.OutTagAddress <- v)) }
        { Key = "callDirection";           Kind = LString      ((fun cp -> cp.CallDirection),           (fun cp v -> cp.CallDirection <- v)) }
        { Key = "workTimeout";             Kind = LTimeSpanOpt ((fun cp -> cp.WorkTimeout),             (fun cp v -> cp.WorkTimeout <- v)) }
        { Key = "enableTimeout";           Kind = LBool        ((fun cp -> cp.EnableTimeout),           (fun cp v -> cp.EnableTimeout <- v)) }
        { Key = "timeoutAction";           Kind = LString      ((fun cp -> cp.TimeoutAction),           (fun cp v -> cp.TimeoutAction <- v)) }
        { Key = "requiresSafetyCheck";     Kind = LBool        ((fun cp -> cp.RequiresSafetyCheck),     (fun cp v -> cp.RequiresSafetyCheck <- v)) }
        { Key = "enableMotionControl";     Kind = LBool        ((fun cp -> cp.EnableMotionControl),     (fun cp v -> cp.EnableMotionControl <- v)) }
        { Key = "motionControlMode";       Kind = LStringOpt   ((fun cp -> cp.MotionControlMode),       (fun cp v -> cp.MotionControlMode <- v)) }
        { Key = "targetPosition";          Kind = LFloatOpt    ((fun cp -> cp.TargetPosition),          (fun cp v -> cp.TargetPosition <- v)) }
        { Key = "targetVelocity";          Kind = LFloatOpt    ((fun cp -> cp.TargetVelocity),          (fun cp v -> cp.TargetVelocity <- v)) }
        { Key = "acceleration";            Kind = LFloatOpt    ((fun cp -> cp.Acceleration),            (fun cp v -> cp.Acceleration <- v)) }
        { Key = "deceleration";            Kind = LFloatOpt    ((fun cp -> cp.Deceleration),            (fun cp v -> cp.Deceleration <- v)) }
        { Key = "usePulseControl";         Kind = LBool        ((fun cp -> cp.UsePulseControl),         (fun cp v -> cp.UsePulseControl <- v)) }
        { Key = "pulseWidthMs";            Kind = LIntOpt      ((fun cp -> cp.PulseWidthMs),            (fun cp v -> cp.PulseWidthMs <- v)) }
        { Key = "pulseIntervalMs";         Kind = LIntOpt      ((fun cp -> cp.PulseIntervalMs),         (fun cp v -> cp.PulseIntervalMs <- v)) }
        { Key = "pulseCount";              Kind = LIntOpt      ((fun cp -> cp.PulseCount),              (fun cp v -> cp.PulseCount <- v)) }
    ]

    let plcCallLeaves : PlcLeaf<ControlCallProperties> list = [
        { Key = "callDirection";           Kind = LString      ((fun cp -> cp.CallDirection),           (fun cp v -> cp.CallDirection <- v)) }
        { Key = "enableRetry";             Kind = LBool        ((fun cp -> cp.EnableRetry),             (fun cp v -> cp.EnableRetry <- v)) }
        { Key = "maxRetryCount";           Kind = LInt         ((fun cp -> cp.MaxRetryCount),           (fun cp v -> cp.MaxRetryCount <- v)) }
        { Key = "retryDelayMs";            Kind = LInt         ((fun cp -> cp.RetryDelayMs),            (fun cp v -> cp.RetryDelayMs <- v)) }
        { Key = "callTimeout";             Kind = LTimeSpanOpt ((fun cp -> cp.CallTimeout),             (fun cp v -> cp.CallTimeout <- v)) }
        { Key = "waitForCompletion";       Kind = LBool        ((fun cp -> cp.WaitForCompletion),       (fun cp v -> cp.WaitForCompletion <- v)) }
        { Key = "enableConditional";       Kind = LBool        ((fun cp -> cp.EnableConditional),       (fun cp v -> cp.EnableConditional <- v)) }
        { Key = "conditionExpression";     Kind = LStringOpt   ((fun cp -> cp.ConditionExpression),     (fun cp v -> cp.ConditionExpression <- v)) }
    ]
