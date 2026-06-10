namespace Ds2.Runtime.Engine.Abnormal

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.IO
open Ds2.Runtime.Engine.Core

// =============================================================================
// v12 P3c — Monitoring abnormal adapter (IO-edge 방식).
//
//   적용 계획: samples/Abnormal-v12-Apply-Plan.md (§2.2, §5.2, R2·R4·R6·R7).
//
//   설계(사용자 확정):
//     · 기존 PassiveInferenceSession 의 cycle 학습/정렬은 건드리지 않는다. abnormal timing 은 별개.
//     · ApiCall 마다 OutTag(시작)·InTag(완료)가 박혀 있고 ApiCall→ApiDef.RxGuid→Device Work 로
//       duration(Min/Max)이 모델에 있다. cycle 학습 없이 IO 주소 edge 만 직접 본다:
//         OutTag On(rising) = going 시작 → 시각 기록 / InTag On(rising) = finish → elapsed vs range
//     · OnTick 은 없다(passive). over/under 는 InTag rising 한 경로에서만 낸다(spec 의 tick 보조는 active 전용).
//     · gating(§2.3/§4): AbnormalDetector.canEvaluate (자동 Flow ∧ Real ∧ 비인터락) + Plan 활성은
//       going clock/Finish 분기로 판정. Sensor* 발행 전 ISensorDebouncer(V14).
//     · 발행: ILatchPolicy(Core, P7) 경유 dedup → sink.
// =============================================================================

/// v12 자동 줄자(학습) — device Work 별 OUT→IN 실측 elapsed(ms)를 minSamples 사이클 모아
/// widened [Min,Max] + avg 를 산출한다. 모델 duration 은 참고/dead-reckoning 값일 뿐
/// 실설비와 안 맞으므로 Under/Over 줄자로 쓰지 않고, 관측 실측에서 줄자를 만든다.
///   band = avg ± max(k·σ, avg·floorRatio). 표본이 적어 σ 만으로는 band 가 0 으로 붕괴하는 것 방지.
///   Min 은 0 하한(음수 금지). 학습 전(샘플 < minSamples)엔 TryGetRange = None → 판정 보류.
type DeviceDurationLearner(minSamples: int, k: float, floorRatio: float) =
    let samples = Dictionary<Guid, ResizeArray<int>>()
    let learned = Dictionary<Guid, RxTimingRange * int>()   // workGuid → (range, avg ms)

    member _.HasLearned(workGuid: Guid) = learned.ContainsKey workGuid

    member _.TryGetRange(workGuid: Guid) : RxTimingRange option =
        match learned.TryGetValue workGuid with
        | true, (r, _) -> Some r
        | _ -> None

    /// elapsed(ms) 샘플 추가. 이번 호출로 학습이 *확정*되면 Some(range, avg), 아니면 None.
    member _.Observe(workGuid: Guid, elapsedMs: int) : (RxTimingRange * int) option =
        if learned.ContainsKey workGuid || elapsedMs < 0 then None
        else
            let arr =
                match samples.TryGetValue workGuid with
                | true, a -> a
                | _ -> let a = ResizeArray<int>() in samples.[workGuid] <- a; a
            arr.Add elapsedMs
            if arr.Count >= minSamples then
                let n = float arr.Count
                let avg = (arr |> Seq.sumBy float) / n
                let var = (arr |> Seq.sumBy (fun x -> let d = float x - avg in d * d)) / n
                let sigma = sqrt var
                let margin = max (k * sigma) (avg * floorRatio)
                let range = { MinMs = max 0 (int (avg - margin)); MaxMs = int (avg + margin) }
                learned.[workGuid] <- (range, int avg)
                samples.Remove workGuid |> ignore
                Some(range, int avg)
            else None

    member _.Clear() =
        samples.Clear()
        learned.Clear()

type MonitoringAbnormalAdapter
    ( index: SimIndex,
      ioMap: SignalIOMap,
      getCallState: Guid -> Status4,   // SensorOpen 판정용: In falling 시점에 Call 이 Finish(reset 전) 인가.
      nowUtc: unit -> DateTime,
      sink: AbnormalRecord -> unit,
      minActionUnderElapsedMs: int ) =

    let store = index.Store
    let detectorState = AbnormalDetectorState.Empty
    let goingClock = Dictionary<Guid, int>()      // apiCallId → OutTag On(going) 관측시각(ms)
    let prevActive = Dictionary<string, bool>()   // 방향+address → 직전 active (rising edge 판정)
    let latchPolicy : ILatchPolicy = DefaultLatchPolicy()   // P7 — Sensor 즉시 / Action 5s dedup
    // 자동 줄자: device 실측 OUT→IN 을 3사이클 학습 → widened band(k=4, floor 30%, Min 0 하한).
    // passive Synced 게이트 이후에만 OnObservedIo 가 호출되므로 표본은 수렴 후 값 = 신뢰 가능.
    let durationLearner = DeviceDurationLearner(3, 4.0, 0.3)
    // 학습 확정 시 (workGuid, avgMs, minMs, maxMs) 통지 — HubSession 이 client(Promaker) broadcast 로 연결.
    let mutable onLearnedCb : Guid -> int -> int -> int -> unit = fun _ _ _ _ -> ()

    // ApiCall → ApiDef (SensorOpen level-like 판정 + gating 용).
    let apiDefOf (apiCallId: Guid) : ApiDef option =
        match Queries.getApiCall apiCallId store with
        | Some apiCall -> apiCall.ApiDefId |> Option.bind (fun defId -> Queries.getApiDef defId store)
        | None -> None

    // Only Latched sensing promises that the input must remain active after detection.
    let requiresHeldInput (def: ApiDef) =
        match def.SensingType with
        | SensingType.Real(Latched, _) -> true
        | _ -> false

    /// ILatchPolicy(Core) 경유 dedup 발행.
    let emit (record: AbnormalRecord) =
        AbnormalDetector.emitThroughLatch detectorState latchPolicy sink record

    /// off→on rising 판정. 첫 관측은 baseline(rising 아님) → 중간시작 배제.
    let risingEdge (key: string) (active: bool) : bool =
        let wasActive =
            match prevActive.TryGetValue key with
            | true, b -> b
            | _ -> active        // 첫 관측 = baseline → rising 으로 보지 않음
        prevActive.[key] <- active
        (not wasActive) && active

    new(index: SimIndex, ioMap: SignalIOMap, getCallState: Guid -> Status4, nowUtc: unit -> DateTime, sink: AbnormalRecord -> unit) =
        MonitoringAbnormalAdapter(index, ioMap, getCallState, nowUtc, sink, 0)

    /// 자동 줄자 학습이 확정될 때마다 (workGuid, avgMs, minMs, maxMs) 통지받을 콜백.
    member _.OnLearnedDuration with set (cb: Guid -> int -> int -> int -> unit) = onLearnedCb <- cb

    /// PLC scan 으로 관측된 IO 값. OutTag On=going 시작, InTag On=finish.
    member _.OnObservedIo(address: string, value: string, nowMs: int) =
        // 시작측: OutAddress rising → 매핑된 모든 ApiCall 의 going clock 기록 + 직전 사이클 latch 비움.
        match ioMap.GetByOutAddress(address) with
        | [] -> ()
        | outMappings ->
            match Queries.getApiCall outMappings.Head.ApiCallGuid store with
            | Some apiCall ->
                let active = RuntimeSemantics.isActiveOutputValue apiCall value
                if risingEdge ("OUT:" + address) active then
                    for m in outMappings do
                        goingClock.[m.ApiCallGuid] <- nowMs
                        // v12 — OUT rising = 새 사이클 시작 → 직전 사이클 abnormal latch 제거. Control 의
                        //   OnCallReset 과 동형. 이게 없으면 DefaultLatchPolicy 5s window 가 사이클간 같은
                        //   (Kind,Target) Under/Over 를 5초 억제해(사이클<5s 면 매 사이클 누락) 즉시 재검출이 안 된다.
                        //   사이클 내 중복(watchdog tick + In rising)은 5s window 가 그대로 coalesce → 1회 유지.
                        AbnormalDetector.clearLatchForCall detectorState m.CallGuid
            | None -> ()

        // 완료측: InAddress rising → finish(elapsed vs range), falling → level 센서 단선 = SensorOpen.
        match ioMap.GetByInAddress(address) with
        | [] -> ()
        | inMappings ->
            match Queries.getApiCall inMappings.Head.ApiCallGuid store with
            | Some apiCall ->
                let active = RuntimeSemantics.isActiveInputValue apiCall value
                // risingEdge 가 prevActive 를 갱신하므로 falling 판정용 직전값은 먼저 캡처.
                let wasInActive =
                    match prevActive.TryGetValue ("IN:" + address) with
                    | true, b -> b
                    | _ -> active        // 첫 관측 baseline → falling 으로 보지 않음
                if risingEdge ("IN:" + address) active then
                    for m in inMappings do
                        match apiDefOf m.ApiCallGuid with
                        | Some def when AbnormalDetector.canEvaluate store m.CallGuid def ->
                            let target = Abnormal.target (Some m.CallGuid) (Some m.ApiCallGuid) m.RxWorkGuid
                            match goingClock.TryGetValue m.ApiCallGuid with
                            | true, goingAt ->
                                // going clock 있음 = going 을 거쳤다 → 실측 elapsed.
                                let elapsed = nowMs - goingAt
                                // 모델 duration(참고/dead-reckoning)이 아니라 *학습된 실측 줄자* 로 판정한다.
                                match m.RxWorkGuid with
                                | Some rxWork ->
                                    // 실측 누적 → 3사이클 도달 시 학습 확정 + store Work duration 기록(AASX 저장이 소비).
                                    match durationLearner.Observe(rxWork, elapsed) with
                                    | Some(range, avg) ->
                                        // 엔진(Agent) store 즉시 반영 — live dead-reckoning/검출에 사용.
                                        match Queries.getWork rxWork store with
                                        | Some w ->
                                            w.Duration    <- Some(TimeSpan.FromMilliseconds(float avg))
                                            w.MinDuration <- Some(TimeSpan.FromMilliseconds(float range.MinMs))
                                            w.MaxDuration <- Some(TimeSpan.FromMilliseconds(float range.MaxMs))
                                        | None -> ()
                                        // client(Promaker)로 push — 정지 시 "업데이트" 선택 → 모델 dirty 반영.
                                        onLearnedCb rxWork avg range.MinMs range.MaxMs
                                    | None -> ()
                                    // 학습 완료 후에만 Under 판정(학습 전엔 보류). Over 는 watchdog(engine) SSOT — change A 유지.
                                    match durationLearner.TryGetRange rxWork with
                                    | Some range ->
                                        match Abnormal.classifyExpectedRising range elapsed with
                                        | Some AbnormalKind.ActionUnder when elapsed >= minActionUnderElapsedMs -> emit (Abnormal.actionUnder target elapsed (nowUtc ()))
                                        | _ -> ()
                                    | None -> ()
                                | None -> ()
                                goingClock.Remove m.ApiCallGuid |> ignore
                            | false, _ ->
                                // No going clock. Emit SensorShort only after this adapter has an
                                // observed OUT baseline. Without that, Monitoring may have attached
                                // mid-cycle and an IN rising is not enough evidence for a short.
                                if not (System.String.IsNullOrEmpty m.OutAddress) then
                                    match prevActive.TryGetValue("OUT:" + m.OutAddress) with
                                    | true, outActive when not outActive ->
                                        emit (Abnormal.sensorShort target (nowUtc ()))
                                    | _ -> ()
                        | _ -> ()
                elif wasInActive && not active then
                    // In falling: normal Level inputs may reset during ordinary cycle changeover.
                    // SensorOpen is reserved for held inputs while their own output is still active.
                    for m in inMappings do
                        match apiDefOf m.ApiCallGuid with
                        | Some def when AbnormalDetector.canEvaluate store m.CallGuid def && requiresHeldInput def ->
                            let outActive =
                                if System.String.IsNullOrEmpty m.OutAddress then false
                                else
                                    match prevActive.TryGetValue("OUT:" + m.OutAddress) with
                                    | true, b -> b
                                    | _ -> false
                            if outActive && getCallState m.CallGuid <> Status4.Ready then
                                let target = Abnormal.target (Some m.CallGuid) (Some m.ApiCallGuid) m.RxWorkGuid
                                emit (Abnormal.sensorOpen target (nowUtc ()))
                        | _ -> ()
            | None -> ()

    /// observed cycle 재시작/연결 reload 등으로 going 관측을 무효화할 때.
    member _.Reset() =
        goingClock.Clear()
        durationLearner.Clear()
        prevActive.Clear()
        detectorState.LastEmitted <- Map.empty
        latchPolicy.ResetOn(LatchResetTrigger.ManualClear)

    /// C#(DSPilot) 에서 쓰기 위한 팩토리 — System.Func/Action 을 F# 함수로 래핑.
    static member FromDelegates(index: SimIndex, ioMap: SignalIOMap, nowUtc: System.Func<DateTime>, sink: System.Action<AbnormalRecord>) : MonitoringAbnormalAdapter =
        let nowFn () = nowUtc.Invoke()
        let sinkFn r = sink.Invoke r
        // DSPilot 경로는 Call state 주입이 없어 SensorOpen 비활성(Ready 고정 → Finish 분기 안 탐). short/timing 은 그대로.
        MonitoringAbnormalAdapter(index, ioMap, (fun _ -> Status4.Ready), nowFn, sinkFn)
