namespace Ds2.Runtime.Engine.Core

open Ds2.Core

/// <summary>v10 spec §11 RUNTIME SEMANTICS — emitOutput / completionTrigger dispatch.
/// pure decision (effect/trigger DU). 실제 출력 적용은 호출자가 effect 받아 처리.</summary>
module RuntimeSemantics =

    let activeOutputValue (call: ApiCall) =
        ValueSpec.toDefaultString call.OutputSpec

    let resetOutputValue (call: ApiCall) =
        match call.OutputSpec with
        | UndefinedValue
        | BoolValue _ -> "false"
        | StringValue _ -> ""
        | _ -> "0"

    /// v10 §11.1 — ActionType case 별 출력 effect.
    type OutputEffect =
        | OutCoil          of IOTag                  // Real(Level, None) — coil 유지 (현재 ds2 의 OUT "true" 동작과 동일)
        | CoilAfterDelay   of IOTag * int            // Real(Level, Some Append ms) — coil 유지 + ms 후 자동 off
        | EdgePulse        of IOTag                  // Real(OneShot, None) — 1 scan rising edge
        | EdgePulseHold    of IOTag * int            // Real(OneShot, Some Append ms) — edge 후 ms hold
        | SetCoil          of IOTag                  // Real(Latched, None) — SET latch (§7 Device Mutex 로 해제)
        | NoOp                                       // Virtual None — 출력 없음
        | NoOpAppend       of int                    // Virtual (Some Append ms) — 출력 없음 + ms 추가 대기

    /// v10 §11.2 — SensingType case 별 완료 인정 trigger.
    type CompletionTrigger =
        | WaitInput               of IOTag           // Real(Level, None) — InTag 신호 ON 시 인정
        | WaitInputStable         of IOTag * int     // Real(Level, Some Append ms) — debounce ms 안정 후 인정
        | WaitInputEdge           of IOTag           // Real(OneShot, None) — 0→1 transition edge 인정
        | WaitInputEdgeStable     of IOTag * int     // Real(OneShot, Some Append ms) — edge 후 ms 안정
        | WaitInputLatched        of IOTag           // Real(Latched, None) — 메모리 latch
        | WaitPassiveDuration     of ApiCall         // Virtual None — Work.Duration 종점 대기
        | WaitPassiveDurationPlus of ApiCall * int   // Virtual (Some Append ms) — Duration + ms 대기

    /// v10 §11.1 emitOutput dispatch — spec 본문 pseudo-code 그대로.
    /// V1 invariant (Real ⇒ OutTag required) 미충족 시 invalidOp (V1 Validation 이 사전에 잡아야 함).
    let emitOutput (def: ApiDef) (call: ApiCall) : OutputEffect =
        match def.ActionType, call.OutTag with
        | ActionType.Real (mode, time), Some tag ->
            match mode, time with
            | Level,   None            -> OutCoil tag
            | Level,   Some (Append n) -> CoilAfterDelay (tag, n)
            | OneShot, None            -> EdgePulse tag
            | OneShot, Some (Append n) -> EdgePulseHold (tag, n)
            | Latched, None            -> SetCoil tag
            | Latched, Some (Append _) ->
                invalidOp "v10 §11: Latched + TimePolicy 조합은 spec 정의 외 (V1 invariant)"
        | ActionType.Virtual None,            _ -> NoOp
        | ActionType.Virtual (Some (Append n)), _ -> NoOpAppend n
        | ActionType.Real _, None ->
            invalidOp $"E-V1: ApiCall '{call.Name}' — ActionType=Real ⇒ OutTag 필수"

    /// v10 §11.2 completionTrigger dispatch — spec 본문 pseudo-code 그대로.
    /// V2 invariant (Real ⇒ InTag required) 미충족 시 invalidOp.
    let completionTrigger (def: ApiDef) (call: ApiCall) : CompletionTrigger =
        match def.SensingType, call.InTag with
        | SensingType.Real (mode, time), Some tag ->
            match mode, time with
            | Level,   None            -> WaitInput tag
            | Level,   Some (Append n) -> WaitInputStable (tag, n)
            | OneShot, None            -> WaitInputEdge tag
            | OneShot, Some (Append n) -> WaitInputEdgeStable (tag, n)
            | Latched, None            -> WaitInputLatched tag
            | Latched, Some (Append _) ->
                invalidOp "v10 §11: Latched + TimePolicy 조합은 spec 정의 외 (V2 invariant)"
        | SensingType.Virtual None,            _ -> WaitPassiveDuration call
        | SensingType.Virtual (Some (Append n)), _ -> WaitPassiveDurationPlus (call, n)
        | SensingType.Real _, None ->
            invalidOp $"E-V2: ApiCall '{call.Name}' — SensingType=Real ⇒ InTag 필수"
