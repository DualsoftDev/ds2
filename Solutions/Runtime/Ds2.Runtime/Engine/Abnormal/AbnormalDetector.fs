namespace Ds2.Runtime.Engine.Abnormal

open System
open Ds2.Core
open Ds2.Core.Store            // DsStore / Queries
open Ds2.Runtime.Engine.Core   // SimIndex
open Ds2.Runtime.IO            // SignalMapping / SignalIOMap

// =============================================================================
// v12 P3a — Control/Monitoring 공통 abnormal detector 자산.
//
//   적용 계획: samples/Abnormal-v12-Apply-Plan.md (§4 R1·R2·R7, §6 P3a).
//
//   여기에는 mode 비의존 + side-effect 없는(또는 state in-place 만) 공통 자산만 둔다:
//     · Device Work range resolver (SSOT = SimIndex.WorkDurationRange)
//     · SensingType gate (Real 만 평가)
//     · observed clock / timing quality (R7)
//     · canEvaluate gating (자동 Flow ∧ Real ∧ 비인터락 ∧ Plan 활성)
//     · ILatchPolicy(Core) 경유 dedup 발행 helper
//
//   Policy Hook 인터페이스/기본 구현(ILatchPolicy/ISensorDebouncer/ISeverity/IResponse/
//   IAbnormalSink + Default*)은 Ds2.Core.Abnormal 로 통합됨(spec §6). 여기선 소비만 한다.
// =============================================================================

/// R7 — 관측 시각 신뢰도. Control 은 항상 Reliable, Monitoring 은 scan/broadcast latency 반영.
type TimingQuality =
    | Reliable
    | Degraded of reason: string

/// elapsed 계산 결과 + 신뢰도. TimingReliable=false 면 timing(Action*) 판정 보류.
type ObservedClockInfo =
    { ElapsedMs : int
      TimingReliable : bool
      Quality : TimingQuality }

/// detector 누적 상태 — (Kind,Target)별 직전 발행 record. adapter 가 들고 다니며
/// ILatchPolicy.ShouldEmit(previous, current) 의 previous 로 넘긴다.
type AbnormalDetectorState =
    { mutable LastEmitted : Map<AbnormalLatchKey, AbnormalRecord> }
    static member Empty = { LastEmitted = Map.empty }

/// 순수 공통 helper. side effect 없음 (latch 상태 갱신만 in-place).
module AbnormalDetector =

    // --- timing range resolver (SSOT: Device Work range, SimIndex.WorkDurationRange) ---

    /// Work id 직접 조회.
    let tryResolveWorkRange (index: SimIndex) (workId: Guid) : RxTimingRange option =
        index.WorkDurationRange |> Map.tryFind workId

    /// active Call → active Work → range (Control adapter 용).
    let tryResolveRangeFromCall (index: SimIndex) (callId: Guid) : RxTimingRange option =
        index.CallWorkGuid |> Map.tryFind callId |> Option.bind (tryResolveWorkRange index)

    /// SignalMapping.RxWorkGuid(Device Work) → range.
    /// v12 timing source 축 (ApiCall → ApiDef.RxGuid → Device Work) 를 그대로 따른다.
    let tryResolveRangeFromMapping (index: SimIndex) (mapping: SignalMapping) : RxTimingRange option =
        mapping.RxWorkGuid |> Option.bind (tryResolveWorkRange index)

    /// ApiCall id → mapping → Device Work range.
    let tryResolveRangeFromApiCall (index: SimIndex) (ioMap: SignalIOMap) (apiCallId: Guid) : RxTimingRange option =
        ioMap.Mappings
        |> List.tryFind (fun m -> m.ApiCallGuid = apiCallId)
        |> Option.bind (tryResolveRangeFromMapping index)

    // --- SensingType gate (R3/R4: Real 만 평가, Virtual 은 물리 InTag 부재가 정상) ---

    let isPhysicalSensing (def: ApiDef) : bool =
        match def.SensingType with
        | SensingType.Real _ -> true
        | SensingType.Virtual _ -> false

    // --- observed clock / timing quality (R7) ---

    /// Control: 관측 latency 없음 → 항상 Reliable.
    let reliableClock (elapsedMs: int) : ObservedClockInfo =
        { ElapsedMs = elapsedMs; TimingReliable = true; Quality = Reliable }

    /// Monitoring: 관측 지연이 range 폭보다 크면 timing 판정 보류 (R7).
    /// scan jitter 를 model range 에 더하지 않고, 신뢰도 layer 에서 별도 처리한다.
    let observedClock (range: RxTimingRange) (elapsedMs: int) (observationLatencyMs: int) : ObservedClockInfo =
        let rangeWidth = max 0 (range.MaxMs - range.MinMs)
        if observationLatencyMs > rangeWidth then
            { ElapsedMs = elapsedMs
              TimingReliable = false
              Quality = Degraded(sprintf "관측 지연 %dms > range 폭 %dms" observationLatencyMs rangeWidth) }
        else
            { ElapsedMs = elapsedMs; TimingReliable = true; Quality = Reliable }

    // --- v12 §2.3/§4 GATING: 자동 Flow ∧ Real sensing ∧ 비인터락 ∧ Plan 활성 ---

    /// Call → Work → Flow 부모 체인으로 Flow.IsAuto 조회. 부모 못 찾으면 자동(true) 가정.
    let private flowIsAutoOfCall (store: DsStore) (callId: Guid) : bool =
        match Queries.getCall callId store with
        | Some call ->
            match Queries.getWork call.ParentId store with
            | Some work ->
                match Queries.getFlow work.ParentId store with
                | Some flow -> flow.IsAuto
                | None -> true
            | None -> true
        | None -> true

    let private callInterlocked (store: DsStore) (callId: Guid) : bool =
        match Queries.getCall callId store with
        | Some call -> call.Interlocked
        | None -> false

    /// spec §2.3/§4 canEvaluate 중 mode 비의존분: IsAuto ∧ Real.IsActive ∧ ¬Interlocked.
    /// Plan 활성(PS∧¬PE)은 adapter 가 자기 context(Call Going / goingClock 분기)로 판정한다 —
    /// SensorShort 는 Ready 에서도(spec EX-02 case A) 평가해야 해서 여기에 묶지 않는다.
    let canEvaluate (store: DsStore) (callId: Guid) (def: ApiDef) : bool =
        isPhysicalSensing def
        && flowIsAutoOfCall store callId
        && not (callInterlocked store callId)

    // --- latch dedup (Core ILatchPolicy 경유) ---

    /// ILatchPolicy 로 dedup 판정 → 통과 시 sink + (Kind,Target)별 직전발행 갱신.
    let emitThroughLatch
        (state: AbnormalDetectorState)
        (policy: ILatchPolicy)
        (sink: AbnormalRecord -> unit)
        (record: AbnormalRecord) : unit =
        let key = Abnormal.latchKeyOf record
        let prev = state.LastEmitted |> Map.tryFind key
        if policy.ShouldEmit(prev, record) then
            state.LastEmitted <- state.LastEmitted.Add(key, record)
            sink record

    /// 사이클 종료(CallTransition) 시 해당 Call 의 직전발행 기록 제거 → 다음 사이클 재판정.
    let clearLatchForCall (state: AbnormalDetectorState) (callId: Guid) : unit =
        state.LastEmitted <-
            state.LastEmitted |> Map.filter (fun key _ -> key.Target.CallId <> Some callId)
