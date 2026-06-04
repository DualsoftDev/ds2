namespace Ds2.Runtime.Engine.Abnormal

open System
open Ds2.Core
open Ds2.Runtime.Engine.Core   // SimIndex
open Ds2.Runtime.IO            // SignalMapping / SignalIOMap

// =============================================================================
// v12 P3a — Control/Monitoring 공통 abnormal detector 자산.
//
//   적용 계획: samples/Abnormal-v12-Apply-Plan.md (§4 R1·R2·R7, §6 P3a).
//
//   여기에는 mode 비의존 + side-effect 없는 공통 자산만 둔다:
//     · Device Work range resolver (SSOT = SimIndex.WorkDurationRange)
//     · SensingType gate (Real 만 평가)
//     · observed clock / timing quality (R7)
//     · latch dedup 순수 helper + 정책 interface (구현 P7)
//
//   source="plc" 신뢰 판별, apiCall→activeWork 매핑, rising/falling edge 추출은
//   mode adapter(P3b Control / P3c Monitoring)가 자기 context 로 수행하고 이 자산을 호출한다.
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

/// dedup latch 정책 (구현 P7). (Mode, Kind, Target) window 내 중복 발행 차단.
type ILatchPolicy =
    abstract member ShouldEmit : key: AbnormalLatchKey * mode: RuntimeMode * nowMs: int -> bool

/// 센서 debounce 정책 (구현 P7). SensingType Append 와 충돌하지 않게 wrapper 로만 적용.
type ISensorDebouncer =
    abstract member IsStable : apiCallId: Guid * nowMs: int -> bool

/// detector 누적 상태 — latch 발행 시각. adapter 가 들고 다닌다.
type AbnormalDetectorState =
    { mutable LatchedAtMs : Map<AbnormalLatchKey, int> }
    static member Empty = { LatchedAtMs = Map.empty }

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

    // --- latch dedup (순수; ILatchPolicy 기본 구현 토대) ---

    /// key 가 windowMs 안에 이미 발행됐으면 false(억제), 아니면 true + 발행시각 갱신.
    let tryLatch (state: AbnormalDetectorState) (key: AbnormalLatchKey) (nowMs: int) (windowMs: int) : bool =
        match state.LatchedAtMs |> Map.tryFind key with
        | Some lastMs when nowMs - lastMs < windowMs -> false
        | _ ->
            state.LatchedAtMs <- state.LatchedAtMs.Add(key, nowMs)
            true
