namespace Ds2.Core

open System

// =============================================================================
// v12 경로이탈 이상감지 — 정책 독립 코어 타입 + 순수 분류 (P1).
//
//   SSOT 사양:  D:/dualsoft/Abnormal-Spec-v12.html  (4 케이스 · 5 필드 · Plan window).
//   적용 계획:  samples/Abnormal-v12-Apply-Plan.md   (§6 P1).
//
//   이 파일에는 SignalR / EventDrivenEngine / PassiveInferenceSession / WPF /
//   실제 debouncer 구현을 두지 않는다. mode adapter(P3) 와 정책 구현체(P7) 가
//   이 순수 타입/함수를 소비한다.
// =============================================================================

/// v12 §1 — 경로이탈 4 케이스. int 값 잠금 (DSPilot/직렬화 계약).
type AbnormalKind =
    | SensorOpen  = 0   // 계획상 active 유지돼야 할 센서가 꺼짐 (단선/이탈)
    | SensorShort = 1   // 계획상 기대 안 한 시점에 센서가 켜짐 (오감지/조기)
    | ActionOver  = 2   // 동작 완료가 MaxDuration 초과 (너무 늦음)
    | ActionUnder = 3   // 동작 완료가 MinDuration 미만 (너무 빠름)

// NOTE: severity/response enum 및 ISeverityPolicy/IResponsePolicy 는 두지 않는다(미사용).
//   spec §6 "5 policy hook" 은 인터페이스 강제가 아니라 "교체 가능한 정책" 수준으로 해석.
//   abnormal 등급/대응은 현재 client 가 Kind 로 판단하며, 필요해지면 그때 추가한다.

/// v12 5 필드 중 Target — 이상이 귀속되는 모델 좌표.
/// Control 은 (CallId, ApiCallId), Monitoring 은 가능하면 동일 + Work-only 보강.
type AbnormalTarget =
    { CallId    : Guid option
      ApiCallId : Guid option
      WorkId    : Guid option }

/// v12 timing range — Device Work 의 Min/MaxDuration 에서 해석 (P2 이후).
/// 코어 분류는 ms 정수만 본다. TimeSpan 변환은 호출부 책임.
type RxTimingRange =
    { MinMs : int
      MaxMs : int }

/// v12 5 필드 record. Kind/Target/TimestampUtc 는 항상, ElapsedMs/Observed 는 케이스별.
///   Sensor* → Observed=Some, ElapsedMs=None
///   Action* → ElapsedMs=Some, Observed=None
/// 이 invariant 는 아래 smart ctor 가 강제한다.
type AbnormalRecord =
    { Kind         : AbnormalKind
      Target       : AbnormalTarget
      TimestampUtc : DateTime
      ElapsedMs    : int option
      Observed     : bool option }

/// ILatchPolicy(P7) dedup 의 코어 키. Mode 차원은 adapter(P3) 가 덧붙인다.
type AbnormalLatchKey =
    { Kind   : AbnormalKind
      Target : AbnormalTarget }

/// 순수 smart ctor + 분류. side effect / IO / 정책 매핑 없음.
module Abnormal =

    /// Target 편의 생성자.
    let target (callId: Guid option) (apiCallId: Guid option) (workId: Guid option) : AbnormalTarget =
        { CallId = callId; ApiCallId = apiCallId; WorkId = workId }

    // --- smart ctor: 5-field invariant 강제 ---

    /// 계획상 active 여야 할 센서가 꺼짐. Observed=Some false, ElapsedMs=None.
    let sensorOpen (target: AbnormalTarget) (timestampUtc: DateTime) : AbnormalRecord =
        { Kind = AbnormalKind.SensorOpen; Target = target
          TimestampUtc = timestampUtc; ElapsedMs = None; Observed = Some false }

    /// 계획상 기대 안 한 센서가 켜짐. Observed=Some true, ElapsedMs=None.
    let sensorShort (target: AbnormalTarget) (timestampUtc: DateTime) : AbnormalRecord =
        { Kind = AbnormalKind.SensorShort; Target = target
          TimestampUtc = timestampUtc; ElapsedMs = None; Observed = Some true }

    /// 동작 완료가 MaxDuration 초과. ElapsedMs=Some, Observed=None.
    let actionOver (target: AbnormalTarget) (elapsedMs: int) (timestampUtc: DateTime) : AbnormalRecord =
        { Kind = AbnormalKind.ActionOver; Target = target
          TimestampUtc = timestampUtc; ElapsedMs = Some elapsedMs; Observed = None }

    /// 동작 완료가 MinDuration 미만. ElapsedMs=Some, Observed=None.
    let actionUnder (target: AbnormalTarget) (elapsedMs: int) (timestampUtc: DateTime) : AbnormalRecord =
        { Kind = AbnormalKind.ActionUnder; Target = target
          TimestampUtc = timestampUtc; ElapsedMs = Some elapsedMs; Observed = None }

    // --- pure classify ---

    /// 기대 완료(rising)에서 elapsed 를 range 와 비교.
    ///   elapsed < Min → ActionUnder, elapsed > Max → ActionOver, 경계 포함 정상 → None.
    let classifyExpectedRising (range: RxTimingRange) (elapsedMs: int) : AbnormalKind option =
        if elapsedMs < range.MinMs then Some AbnormalKind.ActionUnder
        elif elapsedMs > range.MaxMs then Some AbnormalKind.ActionOver
        else None

    /// 기대하지 않은 입력 rising → 항상 SensorShort.
    let classifyUnexpectedRising : AbnormalKind = AbnormalKind.SensorShort

    /// 기대 active 유지 중 falling → 항상 SensorOpen.
    let classifyExpectedFalling : AbnormalKind = AbnormalKind.SensorOpen

    /// tick 감시: 입력이 아직 active 아닌데 Max 초과 → ActionOver, 그 외 None.
    let classifyTick (range: RxTimingRange) (elapsedMs: int) (inputActive: bool) : AbnormalKind option =
        if not inputActive && elapsedMs > range.MaxMs then Some AbnormalKind.ActionOver
        else None

    /// dedup latch 키 추출 (Mode 는 adapter 가 별도로 조합).
    let latchKeyOf (record: AbnormalRecord) : AbnormalLatchKey =
        { Kind = record.Kind; Target = record.Target }


// =============================================================================
// v12 §6 Policy Hooks — 인터페이스(잠금) + 기본 구현체(FLEX, 배포 시 교체).
//   spec §6/§1.1. 전부 코어 타입(AbnormalKind/Severity/Response/Record)만 참조하므로
//   RuntimeMode 비의존 → Core 에 둔다. mode adapter(P3)·정책 inject(P7)가 소비한다.
// =============================================================================

/// latch reset 트리거 (spec §6).
type LatchResetTrigger =
    | CallTransition
    | TimeoutElapsed
    | ManualClear
    | Custom of string

/// (Kind,Target) dedup 정책 (spec §6). previous=같은 키 직전 발행(없으면 None), current=현재 후보.
type ILatchPolicy =
    abstract member ShouldEmit : previous: AbnormalRecord option * current: AbnormalRecord -> bool
    abstract member ResetOn    : trigger: LatchResetTrigger -> unit

// NOTE: spec §6 의 ISensorDebouncer 는 두지 않는다. debounce(센서 ON 후 n ms 안정)는 v10
//   SensingType.Real(_, Some(Append n)) 로 ApiDef 에 지정하고 completionTrigger(WaitInputStable/
//   WaitInputEdgeStable)가 처리한다 = SSOT. abnormal 에 별도 debouncer 를 두면 이중이라 제외.

// NOTE: spec §6 의 IAbnormalSink 인터페이스는 두지 않는다. 발행 출구는 adapter 생성자에
//   `AbnormalRecord -> unit` 함수(broadcastAbnormal)로 주입 — 함수 주입으로 충분히 대체된다.
//   현재 실제 정책 hook 은 ILatchPolicy(dedup) 하나뿐(나머지는 "교체 가능성" 수준으로 낮춤).

/// 기본 latch 정책 (spec §6.1): Sensor*=즉시(0ms), Action*=5000ms dedup window.
/// 상태는 호출부(adapter)가 (Kind,Target)별 직전 발행을 추적해 previous 로 넘긴다.
type DefaultLatchPolicy() =
    interface ILatchPolicy with
        member _.ShouldEmit(previous, current) =
            match previous with
            | None -> true
            | Some prev ->
                let windowMs =
                    match current.Kind with
                    | AbnormalKind.SensorShort
                    | AbnormalKind.SensorOpen -> 0.0
                    | _ -> 5000.0   // Action* (Over/Under) — 사이클당 1회 dedup
                (current.TimestampUtc - prev.TimestampUtc).TotalMilliseconds >= windowMs
        // CallTransition reset 은 호출부가 직전-발행 맵을 비워 처리. 기본 정책은 추가 상태 없음.
        member _.ResetOn(_trigger) = ()
