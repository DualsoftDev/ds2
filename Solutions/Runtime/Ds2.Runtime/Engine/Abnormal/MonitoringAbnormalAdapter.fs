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
///   band = avg ± (avg·marginRatio + scanPeriodMs·quantFactor).
///   · avg·marginRatio(기본 5%) = 공정 자연변동 비례 여유.
///   · scanPeriodMs·quantFactor(기본 1.5스캔) = **폴링 양자화 흡수**. 실 PLC 는 스캔주기(P) 격자로만
///     OUT/IN 을 관측하므로 일정한 동작도 관측 elapsed 가 ±1스캔 흔들린다(실기: 386ms 동작이 100ms
///     폴링에서 300/400 두 봉우리=가짜 bimodal). 이 항이 그 ±스캔을 정상 범위로 흡수 → 양자화 오탐 제거.
///     스캔주기를 바꾸면 마진이 자동 추종(값 재저장 불필요). σ 항은 제거 — 폴링 환경 변동의 대부분이
///     양자화라 scanPeriod 항이 덮는다(2026-06-13 양버전 28h+22h 실측 규명, 사용자 확정 설계).
///   Min 은 0 하한. 학습 전(샘플 < minSamples)엔 TryGetRange = None → 판정 보류.
type DeviceDurationLearner(minSamples: int, marginRatio: float, scanPeriodMs: int, quantFactor: float) =
    // workGuid → 최근 실측 elapsed(ms) rolling 윈도우. confirmed 후에도 계속 누적해 range 를 추종한다.
    let window = Dictionary<Guid, ResizeArray<int>>()
    let learned = Dictionary<Guid, RxTimingRange * int>()   // workGuid → (range, avg ms)
    // prime(모델 Duration) 으로만 채워진 잠정 상태 — 실측이 minSamples 모이면 실측이 덮어쓴다.
    let provisional = HashSet<Guid>()
    // 실측으로 한 번이라도 확정된 work — Prime 이 이걸 덮지 않는다(엉터리 모델값이 실측을 못 밀어냄).
    let confirmed = HashSet<Guid>()
    // rolling 윈도우 상한 — 최근 N 사이클로만 range 산정(설비 변화 추종 + 메모리 상한).
    let windowCap = max 12 (minSamples * 4)

    /// avg(중앙 추정) → 마진식 적용 range. 학습 확정/외부 prime 공용.
    let rangeOf (avg: float) =
        let margin = avg * marginRatio + float scanPeriodMs * quantFactor
        { MinMs = max 0 (int (avg - margin)); MaxMs = int (avg + margin) }

    /// 이상치(통신 지연으로 부풀려진 elapsed 등) 제외 평균 — median 기준 [1/3×, 3×] 밖은 표본에서 뺀다.
    /// range 가 한 건의 14초짜리 통신오염으로 망가지는 것을 막는다(그 14초는 판정 시점에 over 로는 잡힘).
    let robustAvg (arr: ResizeArray<int>) =
        let sorted = arr |> Seq.sort |> Seq.toArray
        let med = float sorted.[sorted.Length / 2]
        let clean = sorted |> Array.filter (fun e -> float e <= med * 3.0 && float e >= med / 3.0)
        if clean.Length = 0 then med else (clean |> Array.averageBy float)

    member _.HasLearned(workGuid: Guid) = learned.ContainsKey workGuid

    member _.TryGetRange(workGuid: Guid) : RxTimingRange option =
        match learned.TryGetValue workGuid with
        | true, (r, _) -> Some r
        | _ -> None

    /// AASX 확정값(avg) 으로 학습을 *잠정* prime — 학습 전 첫 사이클부터 판정하기 위한 임시값.
    /// 실측이 확정(confirmed)된 work 는 덮지 않는다 — 엉터리 모델 duration 이 실측을 밀어내지 못하게.
    /// range 는 현재 스캔주기 마진식으로 재계산되므로 스캔주기 변경에도 정합.
    member _.Prime(workGuid: Guid, avgMs: int) =
        if avgMs > 0 && not (confirmed.Contains workGuid) then
            learned.[workGuid] <- (rangeOf (float avgMs), avgMs)
            provisional.Add workGuid |> ignore

    /// elapsed(ms) 실측 샘플 추가. rolling 윈도우 + 이상치 제외로 range 를 매 사이클 갱신한다.
    /// prime(provisional) 상태는 실측이 minSamples 모이면 실측 range 로 교체되고, confirmed 후에도
    /// rolling 으로 계속 추종한다(한 번 굳으면 안 바뀌던 결함 제거). 첫 확정 때만 통지(이후 갱신은 조용히).
    member _.Observe(workGuid: Guid, elapsedMs: int) : (RxTimingRange * int) option =
        if elapsedMs < 0 then None
        else
            let arr =
                match window.TryGetValue workGuid with
                | true, a -> a
                | _ -> let a = ResizeArray<int>() in window.[workGuid] <- a; a
            arr.Add elapsedMs
            if arr.Count > windowCap then arr.RemoveAt 0
            if arr.Count >= minSamples then
                let avg = robustAvg arr
                let range = rangeOf avg
                let wasConfirmed = confirmed.Contains workGuid
                learned.[workGuid] <- (range, int avg)
                provisional.Remove workGuid |> ignore
                confirmed.Add workGuid |> ignore
                if wasConfirmed then None else Some(range, int avg)   // 첫 확정만 client push
            else None

    member _.Clear() =
        window.Clear()
        learned.Clear()
        provisional.Clear()
        confirmed.Clear()

type MonitoringAbnormalAdapter
    ( index: SimIndex,
      ioMap: SignalIOMap,
      getCallState: Guid -> Status4,   // SensorOpen 판정용: In falling 시점에 Call 이 Finish(reset 전) 인가.
      nowUtc: unit -> DateTime,
      sink: AbnormalRecord -> unit,
      minActionUnderElapsedMs: int,
      scanPeriodMs: int ) =          // 폴링 양자화 마진(±스캔) 산정용 — DeviceDurationLearner 로 전달.

    let store = index.Store
    let detectorState = AbnormalDetectorState.Empty
    let goingClock = Dictionary<Guid, int>()      // apiCallId → OutTag On(going) 관측시각(ms)
    let prevActive = Dictionary<string, bool>()   // 방향+address → 직전 active (rising edge 판정)
    // OUT rising 을 *edge 로 직접* 본 주소("OUT:"+addr) — SensorShort 의 전제 증거.
    // prevActive 는 resync baseline 주입(시작/주기)으로도 채워지므로 "관측했다"의 증거가 못 된다 —
    // baseline 을 신뢰하면 합류 직후(Synced 직전에 시작된 사이클)의 정상 완료 In 이
    // goingClock 부재 + OUT=off(baseline) 조합으로 사이클마다 SensorShort 오판된다(실기).
    let everOutRisingSeen = HashSet<string>(StringComparer.Ordinal)
    let latchPolicy : ILatchPolicy = DefaultLatchPolicy()   // P7 — Sensor 즉시 / Action 5s dedup
    // 자동 줄자: device 실측 OUT→IN 을 3사이클 학습 → band = avg ± (5% + 스캔주기×1.5).
    // passive Synced 게이트 이후에만 OnObservedIo 가 호출되므로 표본은 수렴 후 값 = 신뢰 가능.
    let durationLearner = DeviceDurationLearner(3, 0.05, scanPeriodMs, 1.5)
    // 학습 확정 시 (workGuid, avgMs, minMs, maxMs) 통지 — HubSession 이 client(Promaker) broadcast 로 연결.
    let mutable onLearnedCb : Guid -> int -> int -> int -> unit = fun _ _ _ _ -> ()
    // 자동 duration 정합 ON/OFF (런타임 토글, hub 동기화). mutable let — OnObservedIo 클로저에서 직접 읽음.
    let mutable autoCalibrate = true
    // ActionUnder(시간 미만) 게이트 — "이 Work 의 Min 이 실측으로 확정(사용자 FillMin 승인)" 일 때만 true.
    // 호스트가 calibration-state 사이드카(+AASX 해시 stale 판정)를 읽어 주입한다.
    // 기본 false → 미확정 Work 의 ActionUnder 는 발행 안 함(오탐 차단). ActionOver(Max)는 영향 없음.
    let mutable isMinMeasured : Guid -> bool = fun _ -> false
    // ActionOver(시간 초과) 게이트 — 엔진 device-watchdog 의 engineIsMaxMeasured 와 동일 의미/주입원.
    // OUT-falling 발행 경로(아래 OnObservedIo)가 사용. 기본 false → 미확정 Work 발행 안 함.
    let mutable isMaxMeasured : Guid -> bool = fun _ -> false

    // ApiCall → ApiDef (SensorOpen level-like 판정 + gating 용).
    let apiDefOf (apiCallId: Guid) : ApiDef option =
        match Queries.getApiCall apiCallId store with
        | Some apiCall -> apiCall.ApiDefId |> Option.bind (fun defId -> Queries.getApiDef defId store)
        | None -> None

    // Normal(T) 감지는 T 구간 신호 유지가 약속 — 그 사이 falling = SensorOff/Open.
    // Latch(T) 는 채터링 허용이라 falling 을 abnormal 로 보지 않는다.
    let requiresHeldInput (def: ApiDef) =
        match def.SensingType with
        | SensingType.Normal (Some _) -> true
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
        MonitoringAbnormalAdapter(index, ioMap, getCallState, nowUtc, sink, 0, 100)

    /// 자동 줄자 학습이 확정될 때마다 (workGuid, avgMs, minMs, maxMs) 통지받을 콜백.
    member _.OnLearnedDuration with set (cb: Guid -> int -> int -> int -> unit) = onLearnedCb <- cb

    /// 자동 duration 정합 ON/OFF (런타임 토글, hub 동기화). HubSession 이 갱신.
    ///   ON  = 실측 학습값(durationLearner)을 ActionUnder 판정 기준으로. 모델 Min/Max 무시.
    ///   OFF = 모델 WorkDurationRange(AASX 확정값)를 기준으로(ActionOver 와 동일 SSOT). 학습 안 함.
    member _.AutoCalibrate with get () = autoCalibrate and set v = autoCalibrate <- v

    /// ActionUnder 게이트 주입 — workGuid 의 Min 이 실측 확정(calibration-state)됐는지. 기본 false(비활성).
    member _.IsMinMeasured with get () = isMinMeasured and set v = isMinMeasured <- v

    /// ActionOver 게이트 주입 — workGuid 의 Max 가 실측 확정(calibration-state)됐는지. 기본 false(비활성).
    member _.IsMaxMeasured with get () = isMaxMeasured and set v = isMaxMeasured <- v

    /// AASX 확정값(Duration=avg)으로 학습기를 prime — 다음 세션 재학습 없이 첫 사이클부터 판정.
    member _.PrimeLearnedDuration(workGuid: Guid, avgMs: int) = durationLearner.Prime(workGuid, avgMs)

    /// PLC scan 으로 관측된 IO 값. OutTag On=going 시작, InTag On=finish.
    member _.OnObservedIo(address: string, value: string, nowMs: int) =
        // 시작측: OutAddress rising → 매핑된 모든 ApiCall 의 going clock 기록 + 직전 사이클 latch 비움.
        match ioMap.GetByOutAddress(address) with
        | [] -> ()
        | outMappings ->
            match Queries.getApiCall outMappings.Head.ApiCallGuid store with
            | Some apiCall ->
                let active = RuntimeSemantics.isActiveOutputValue apiCall value
                // risingEdge 가 prevActive 를 갱신하므로 falling 판정용 직전값은 먼저 캡처(IN falling 과 동형).
                let wasOutActive =
                    match prevActive.TryGetValue ("OUT:" + address) with
                    | true, b -> b
                    | _ -> active        // 첫 관측 baseline → falling 으로 보지 않음
                if risingEdge ("OUT:" + address) active then
                    everOutRisingSeen.Add("OUT:" + address) |> ignore
                    for m in outMappings do
                        goingClock.[m.ApiCallGuid] <- nowMs
                        // v12 — OUT rising = 새 사이클 시작 → 직전 사이클 abnormal latch 제거. Control 의
                        //   OnCallReset 과 동형. 이게 없으면 DefaultLatchPolicy 5s window 가 사이클간 같은
                        //   (Kind,Target) Under/Over 를 5초 억제해(사이클<5s 면 매 사이클 누락) 즉시 재검출이 안 된다.
                        //   사이클 내 중복(watchdog tick + In rising)은 5s window 가 그대로 coalesce → 1회 유지.
                        AbnormalDetector.clearLatchForCall detectorState m.CallGuid
                elif wasOutActive && not active then
                    // OUT falling(동작 명령 회수)인데 Call 이 여전히 Going + IN 미도달(goingClock 잔존)이고
                    // 경과가 모델 Max 초과면 그 자리에서 ActionOver. 라인 전체 정지(관측 블랙아웃) 중엔 엔진
                    // due-tick 이 평가 기회를 못 얻어 장행정(컨베이어) 타임아웃이 침묵하던 사각을, 블랙아웃
                    // 중에도 실제로 들어오는 OUT-falling 이벤트로 직격한다
                    // (doc/ACTIONOVER_MONITORING_MISS_AGENT_FIX_HANDOFF_2026-07-03.md §7 옵션A).
                    // · range SSOT = 모델 WorkDurationRange(엔진 watchdog 와 동일) — 학습 줄자(durationLearner) 아님.
                    // · elapsed 는 실측(Control adapter 규약) — 엔진 due 경로의 MaxMs+1 과 값이 다를 수 있음.
                    // · goingClock 은 지우지 않는다 — 지우면 뒤늦은 IN rising 이 "goingClock 부재+OUT off"
                    //   조합으로 SensorShort 오판(아래 IN 분기 실기 가드). 정리는 IN rising 정상 경로가 담당.
                    // · 통신 blackout 은 InvalidateObservations 가 goingClock 을 비워 자동 차단(+세션 억제 2중).
                    for m in outMappings do
                        match apiDefOf m.ApiCallGuid with
                        | Some def when AbnormalDetector.canEvaluate store m.CallGuid def
                                        && getCallState m.CallGuid = Status4.Going ->
                            match goingClock.TryGetValue m.ApiCallGuid with
                            | true, goingAt ->
                                match m.RxWorkGuid with
                                | Some rxWork when isMaxMeasured rxWork ->
                                    match index.WorkDurationRange |> Map.tryFind rxWork with
                                    | Some range when range.MaxMs > 0 ->
                                        let elapsed = nowMs - goingAt
                                        if elapsed > range.MaxMs then
                                            let target = Abnormal.target (Some m.CallGuid) (Some m.ApiCallGuid) m.RxWorkGuid
                                            emit (Abnormal.actionOver target elapsed (nowUtc ()))
                                    | _ -> ()
                                | _ -> ()
                            | _ -> ()
                        | _ -> ()
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
                                    // 자동 정합 ON 일 때만 실측 학습 — 3사이클 확정 시 store 기록 + client push.
                                    // OFF 는 모델값을 확정 기준으로 신뢰하므로 학습/덮어쓰기 안 함.
                                    if autoCalibrate then
                                        match durationLearner.Observe(rxWork, elapsed) with
                                        | Some(range, avg) ->
                                            match Queries.getWork rxWork store with
                                            | Some w ->
                                                w.Duration    <- Some(TimeSpan.FromMilliseconds(float avg))
                                                w.MinDuration <- Some(TimeSpan.FromMilliseconds(float range.MinMs))
                                                w.MaxDuration <- Some(TimeSpan.FromMilliseconds(float range.MaxMs))
                                            | None -> ()
                                            // client(Promaker)로 push — 정지 시 "AASX 반영" 선택 → 모델 dirty.
                                            onLearnedCb rxWork avg range.MinMs range.MaxMs
                                        | None -> ()
                                    // ActionUnder 판정 range:
                                    //   ON  = 학습 줄자(durationLearner) — 학습 전엔 None → 보류.
                                    //   OFF = 모델 WorkDurationRange(AASX 확정값) — ActionOver 와 동일 SSOT(일관).
                                    // 어느 경우든 range 는 스캔주기 양자화 마진을 이미 포함(ON: 마진식 / OFF: 모델 Min/Max).
                                    let rangeOpt =
                                        if autoCalibrate then durationLearner.TryGetRange rxWork
                                        else index.WorkDurationRange |> Map.tryFind rxWork
                                    match rangeOpt with
                                    | Some range ->
                                        match Abnormal.classifyExpectedRising range elapsed with
                                        | Some AbnormalKind.ActionUnder when elapsed >= minActionUnderElapsedMs && isMinMeasured rxWork -> emit (Abnormal.actionUnder target elapsed (nowUtc ()))
                                        | _ -> ()
                                    | None -> ()
                                | None -> ()
                                goingClock.Remove m.ApiCallGuid |> ignore
                            | false, _ ->
                                // No going clock. Short 는 이 OUT 의 rising 을 *edge 로 직접* 본 적이
                                // 있고(everOutRisingSeen — baseline 주입은 증거 아님), 현재 OUT 이
                                // off 일 때만. 중간 합류(Synced 직전 시작된 사이클)의 정상 완료 In 이
                                // resync baseline(OUT=off)을 "관측"으로 신뢰해 Short 오판되던 실기 수정.
                                if not (System.String.IsNullOrEmpty m.OutAddress)
                                   && everOutRisingSeen.Contains("OUT:" + m.OutAddress) then
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
        everOutRisingSeen.Clear()
        detectorState.LastEmitted <- Map.empty
        latchPolicy.ResetOn(LatchResetTrigger.ManualClear)

    /// 통신 blackout(PLC 단절) — 관측 *진행 상태* 만 무효화하고 학습된 줄자(durationLearner)는 보존.
    /// goingClock 을 비우면 단절 시간이 포함된 elapsed 가 만들어지지 않아 ActionOver 오탐과
    /// 줄자 오염이 동시에 차단되고, prevActive 를 비우면 재연결 후 첫 관측이 risingEdge 의
    /// "첫 관측 = baseline" 규칙을 타서 누락 edge 가 가짜 rising/falling 으로 보이지 않는다.
    /// 평가 재개는 디바이스별 다음 OUT rising(새 사이클 시작)부터 — 별도 재무장 상태 불필요.
    member _.InvalidateObservations() =
        goingClock.Clear()
        prevActive.Clear()
        // 단절 후 재합류도 시작 합류와 같은 미지 상태 — OUT rising 을 edge 로 다시 본 후에야
        // Short 평가 재개(everOutRisingSeen 재수집). resync baseline 이 다시 채우는
        // prevActive 만으로 Short 가드가 열리지 않게 한다.
        everOutRisingSeen.Clear()
        detectorState.LastEmitted <- Map.empty
        latchPolicy.ResetOn(LatchResetTrigger.ManualClear)

    /// C#(DSPilot) 에서 쓰기 위한 팩토리 — System.Func/Action 을 F# 함수로 래핑.
    static member FromDelegates(index: SimIndex, ioMap: SignalIOMap, nowUtc: System.Func<DateTime>, sink: System.Action<AbnormalRecord>) : MonitoringAbnormalAdapter =
        let nowFn () = nowUtc.Invoke()
        let sinkFn r = sink.Invoke r
        // DSPilot 경로는 Call state 주입이 없어 SensorOpen 비활성(Ready 고정 → Finish 분기 안 탐). short/timing 은 그대로.
        MonitoringAbnormalAdapter(index, ioMap, (fun _ -> Status4.Ready), nowFn, sinkFn)
