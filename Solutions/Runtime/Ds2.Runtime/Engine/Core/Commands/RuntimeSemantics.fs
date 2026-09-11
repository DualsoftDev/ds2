namespace Ds2.Runtime.Engine.Core

open Ds2.Core

/// <summary>v16 RUNTIME SEMANTICS — emitOutput / completionTrigger dispatch (Action/Sensing Type v16 매트릭스).
/// pure decision (effect/trigger DU). 실제 출력 적용은 호출자가 effect 받아 처리.</summary>
module RuntimeSemantics =

    /// ValueSpec → 비활성/reset 상태 대표 문자열 (type 별).
    /// Bool/Undefined → "false", String → "", 그 외 (Int/Float) → "0".
    /// HubSession 의 VP echo reset / Execution 의 reset effect 가 공유하는 단일 룰.
    let resetValueForSpec (spec: ValueSpec) =
        match spec with
        | UndefinedValue
        | BoolValue _ -> "false"
        | StringValue _ -> ""
        | _ -> "0"

    let activeOutputValue (call: ApiCall) =
        ValueSpec.toDefaultString call.OutputSpec

    let resetOutputValue (call: ApiCall) =
        resetValueForSpec call.OutputSpec

    let activeInputValue (call: ApiCall) =
        ValueSpec.toDefaultString call.InputSpec

    let resetInputValue (call: ApiCall) =
        resetValueForSpec call.InputSpec

    /// 수신 string 값이 *active* (=ApiCall.OutputSpec 조건 충족) 인지 판정 — VP 측 output→input echo trigger 용.
    /// OutputSpec=UndefinedValue 는 legacy bool coil 의미로만 인정한다. ValueSpec.evaluate 의 "모든 값 OK"
    /// 의미를 그대로 쓰면 Control 의 OUT reset "false" 도 active 로 인정되어 VP 가 새 duration echo 를 예약한다.
    let isActiveOutputValue (call: ApiCall) (value: string) : bool =
        match call.OutputSpec with
        | UndefinedValue -> value = "true"
        | _ -> ValueSpec.evaluate call.OutputSpec value

    /// 수신 string 값이 *active input* (=ApiCall.InputSpec 조건 충족) 인지 판정 — Control 측 RxWork Finish trigger 용.
    /// InputSpec=UndefinedValue 는 legacy compat — *value="true" 만 active*. ValueSpec.evaluate 의 "모든 값 OK"
    /// 의미와 분리 (VP 의 WorkResetPreds reset value="false" 송출이 ADV Call Finish 를 잘못 trigger 하던 문제 차단).
    let isActiveInputValue (call: ApiCall) (value: string) : bool =
        match call.InputSpec with
        | UndefinedValue -> value = "true"
        | _ -> ValueSpec.evaluate call.InputSpec value

    /// v16 — ActionType case 별 출력 effect.
    type OutputEffect =
        | OutCoil          of IOTag                  // Normal None — coil 유지, Call 완료(센서 감지) 시 off
        | CoilAfterDelay   of IOTag * int            // Normal Some T — coil 유지 + 완료(센서 감지) 후 T(ms) 연장 뒤 off
        | EdgePulse        of IOTag                  // Pulse None — 1 scan 펄스
        | EdgePulseHold    of IOTag * int            // Pulse Some T — T(ms) 유지 후 off
        | SetCoil          of IOTag                  // Latch — SET (같은 Device 의 다른 Api 호출이 해제, §7 Mutex)
        | NoOp                                       // Virtual — 출력 없음

    /// v16 — SensingType case 별 완료 인정 trigger.
    type CompletionTrigger =
        | WaitInput        of IOTag                  // Normal None — InTag 감지 즉시 완료
        | WaitInputStable  of IOTag * int            // Normal Some T — 감지 후 T(ms) 유지 확인, T 중 off = 완료취소 + SensorOff abnormal
        | WaitInputLatched of IOTag * int            // Latch T — 감지 latch 후 T(ms) 지연 완료 (T 구간 채터링 허용)
        | WaitOutputPlus   of ApiCall * int          // Virtual T — 출력 발생(Call 시작) 시점 + T(ms) 후 완료 (센서 없음)

    /// v16 emitOutput dispatch — ActionType 매트릭스 그대로.
    /// V1 invariant (≠Virtual ⇒ OutTag required) 미충족이면 None.
    /// 예외를 쓰지 않는다 — Call 실행마다 도는 hot path 라 throw/catch 비용과
    /// 디버거 first-chance 중단을 유발하고, 진짜 오류까지 같이 삼키게 된다.
    /// 강한 검증은 V10Validation.validateApiCallV1 (모델 검증 시점) 이 담당한다.
    let tryEmitOutput (def: ApiDef) (call: ApiCall) : OutputEffect option =
        match def.ActionType, call.OutTag with
        | ActionType.Virtual, _                  -> Some NoOp
        | ActionType.Normal None,     Some tag   -> Some (OutCoil tag)
        | ActionType.Normal (Some n), Some tag   -> Some (CoilAfterDelay (tag, n))
        | ActionType.Pulse None,      Some tag   -> Some (EdgePulse tag)
        | ActionType.Pulse (Some n),  Some tag   -> Some (EdgePulseHold (tag, n))
        | ActionType.Latch,           Some tag   -> Some (SetCoil tag)
        | _, None                                -> None

    /// tryEmitOutput 의 엄격 버전 — V1 미충족 시 invalidOp.
    /// 모델이 V1 을 만족한다고 보장된 경로(검증 후 변환 등)에서만 쓴다.
    let emitOutput (def: ApiDef) (call: ApiCall) : OutputEffect =
        match tryEmitOutput def call with
        | Some effect -> effect
        | None -> invalidOp $"E-V1: ApiCall '{call.Name}' — ActionType≠Virtual ⇒ OutTag 필수"

    /// v16 completionTrigger dispatch — SensingType 매트릭스 그대로.
    /// V2 invariant (≠Virtual ⇒ InTag required) 미충족이면 None (tryEmitOutput 과 대칭).
    let tryCompletionTrigger (def: ApiDef) (call: ApiCall) : CompletionTrigger option =
        match def.SensingType, call.InTag with
        | SensingType.Virtual n, _               -> Some (WaitOutputPlus (call, n))
        | SensingType.Normal None,     Some tag  -> Some (WaitInput tag)
        | SensingType.Normal (Some n), Some tag  -> Some (WaitInputStable (tag, n))
        | SensingType.Latch n,         Some tag  -> Some (WaitInputLatched (tag, n))
        | _, None                                -> None

    /// tryCompletionTrigger 의 엄격 버전 — V2 미충족 시 invalidOp.
    let completionTrigger (def: ApiDef) (call: ApiCall) : CompletionTrigger =
        match tryCompletionTrigger def call with
        | Some trigger -> trigger
        | None -> invalidOp $"E-V2: ApiCall '{call.Name}' — SensingType≠Virtual ⇒ InTag 필수"
