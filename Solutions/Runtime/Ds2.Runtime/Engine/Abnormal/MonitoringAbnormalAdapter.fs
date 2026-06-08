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
//     · 기존 PassiveInferenceSession 의 cycle 학습/정렬(synced, 3사이클)은 건드리지
//       않는다. 그건 Work/Call 상태 추론용이고, abnormal timing 은 그것과 "별개"로 돈다.
//     · ApiCall 마다 OutTag(시작 주소)·InTag(완료 주소)가 박혀 있고, ApiCall →
//       ApiDef.RxGuid → Device Work 로 duration(Min/Max)이 모델에 박혀 있다.
//       그러니 cycle 학습을 기다릴 필요 없이, IO 주소 edge 만 직접 본다:
//         OutTag On(rising)  = going 시작 → 시각 기록
//         InTag  On(rising)  = finish    → elapsed = finish - going, range 와 비교 → Under/Over
//     · going 은 off→on rising 으로만 잡으므로, 관측 도중(사이클 중간)에 시작된
//       1cycle 은 going edge 를 못 봐서 자동으로 버려진다("1cycle 부정확 → 버림").
//     · 같은 주소를 여러 ApiCall 이 공유할 수 있으므로(OutAddressToMappings), rising
//       판정은 주소+방향당 1회만 하고 매핑된 모든 ApiCall 에 적용한다.
//     · SensorShort = InTag On 인데 going clock 없고 그 Out 도 현재 off ("Going 없이 Finish").
//       Out 이 현재 on 이면 going rising 만 놓친 사이클 중간 진입이라 short 아님(위 1cycle 버림과 동일).
//       SensorOpen(Finish 후 reset 전 falling)은 추후 단계.
//
//   timeout 워치독(OnTick): 완료(InTag rising)는 늦게 오거나 영영 안 올 수 있으므로(라인 정지 등),
//     완료 엣지에서만 elapsed 를 계산하면 ActionOver 가 "나중에/영영 안" 나온다. 그래서 going clock 이
//     살아있는 동작의 경과가 Max 를 넘으면 완료를 기다리지 않고 즉시 ActionOver 를 발행한다(틱 주기 지연).
//     going episode 당 1회만(timedOut 마킹), 완료 엣지가 정리하도록 goingClock 은 그대로 둔다.
//
//   본체 무침투: ioMap/timestamp/sink 를 주입받아 단위 검증 가능.
//   wiring(PLC scan/Observe 경로에서 OnObservedIo 호출)은 P4(R6)에서 연결한다.
//   OnTick wiring(주기 호출)은 DSPilot StateReconcileService 가 담당한다.
// =============================================================================

type MonitoringAbnormalAdapter
    ( index: SimIndex,
      ioMap: SignalIOMap,
      getCallState: Guid -> Status4,   // SensorOpen 판정용: In falling 시점에 Call 이 Finish(reset 전) 인가.
      nowUtc: unit -> DateTime,   // abnormal record timestamp. elapsed/latch 는 OnObservedIo 의 nowMs 사용(R7).
      sink: AbnormalRecord -> unit ) =

    let store = index.Store
    let detectorState = AbnormalDetectorState.Empty
    let goingClock = Dictionary<Guid, int>()      // apiCallId → OutTag On(going) 관측시각(ms)
    let prevActive = Dictionary<string, bool>()   // 방향+address → 직전 active (rising edge 판정)
    let latchWindowMs = 5000
    let timedOut = HashSet<Guid>()                // OnTick 워치독이 이미 ActionOver 발행한 going (재발행/완료엣지 재계산 방지)
    let locker = obj()                            // OnObservedIo(hub 소비 스레드) ↔ OnTick(reconcile 스레드) 동시접근 가드

    // ApiCall → ApiDef (SensorOpen level-like 판정용).
    let apiDefOf (apiCallId: Guid) : ApiDef option =
        match Queries.getApiCall apiCallId store with
        | Some apiCall -> apiCall.ApiDefId |> Option.bind (fun defId -> Queries.getApiDef defId store)
        | None -> None

    // level/latched 센서만 SensorOpen 대상. OneShot falling 은 정상 pulse off 일 수 있어 제외.
    let isLevelLike (def: ApiDef) =
        match def.SensingType with
        | SensingType.Real(Level, _)
        | SensingType.Real(Latched, _) -> true
        | _ -> false

    let emitLatched (record: AbnormalRecord) (nowMs: int) =
        let key = Abnormal.latchKeyOf record
        if AbnormalDetector.tryLatch detectorState key nowMs latchWindowMs then
            sink record

    /// off→on rising 판정. 첫 관측은 baseline(rising 아님) → 중간시작 배제.
    let risingEdge (key: string) (active: bool) : bool =
        let wasActive =
            match prevActive.TryGetValue key with
            | true, b -> b
            | _ -> active        // 첫 관측 = baseline → rising 으로 보지 않음
        prevActive.[key] <- active
        (not wasActive) && active

    /// PLC scan 으로 관측된 IO 값. OutTag On=going 시작, InTag On=finish.
    /// (PassiveInference 와 독립적으로 같은 Observe 경로에서 병행 호출한다.)
    member _.OnObservedIo(address: string, value: string, nowMs: int) =
      lock locker (fun () ->
        // 시작측: OutAddress rising → 매핑된 모든 ApiCall 의 going clock 기록.
        match ioMap.GetByOutAddress(address) with
        | [] -> ()
        | outMappings ->
            match Queries.getApiCall outMappings.Head.ApiCallGuid store with
            | Some apiCall ->
                let active = RuntimeSemantics.isActiveOutputValue apiCall value
                if risingEdge ("OUT:" + address) active then
                    for m in outMappings do
                        goingClock.[m.ApiCallGuid] <- nowMs
                        timedOut.Remove m.ApiCallGuid |> ignore   // 새 going episode — 직전 watchdog 마킹 해제(재무장)
            | None -> ()

        // 완료측: InAddress rising → 매핑된 각 ApiCall finish. going 있으면 elapsed vs range.
        //         InAddress falling → Finish(reset 전) level/latched 센서가 빠지면 단선 = SensorOpen
        //         (Control OnInputFalling 과 동일 의미. reset→Ready 면 정상 종료라 Open 아님).
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
                        let target = Abnormal.target (Some m.CallGuid) (Some m.ApiCallGuid) m.RxWorkGuid
                        match goingClock.TryGetValue m.ApiCallGuid with
                        | true, goingAt ->
                            // going clock 있음 = going 을 거쳤다 → elapsed 로 Under/Over 판정 (정상 사이클).
                            // 단, 워치독(OnTick)이 이미 이 going 을 ActionOver 로 발행했으면 완료 엣지에서
                            // 재계산/재발행하지 않는다(중복 방지). goingClock 은 살아있던 채라 아래 SensorShort
                            // 오탐 분기로도 새지 않는다.
                            if not (timedOut.Remove m.ApiCallGuid) then
                                match AbnormalDetector.tryResolveRangeFromMapping index m with
                                | Some range ->
                                    let elapsed = nowMs - goingAt
                                    match Abnormal.classifyExpectedRising range elapsed with
                                    | Some AbnormalKind.ActionUnder -> emitLatched (Abnormal.actionUnder target elapsed (nowUtc ())) nowMs
                                    | Some AbnormalKind.ActionOver  -> emitLatched (Abnormal.actionOver target elapsed (nowUtc ())) nowMs
                                    | _ -> ()       // 경계 포함 정상 — 오탐 0
                                | None -> ()
                            goingClock.Remove m.ApiCallGuid |> ignore
                        | false, _ ->
                            // going clock 없음 = going rising 을 못 봄. 이때 그 Out 이 현재도 off 면
                            // "Going 없이 Finish" = SensorShort. Out 이 현재 on 이면 going 중인데 rising
                            // 만 놓친 사이클 중간 진입이므로 버린다(오탐 방지).
                            let outActive =
                                if System.String.IsNullOrEmpty m.OutAddress then false
                                else
                                    match prevActive.TryGetValue("OUT:" + m.OutAddress) with
                                    | true, b -> b
                                    | _ -> false
                            if not outActive then
                                emitLatched (Abnormal.sensorShort target (nowUtc ())) nowMs
                elif wasInActive && not active then
                    // In falling: Finish(reset 전) + level/latched 센서면 단선/이탈 = SensorOpen.
                    for m in inMappings do
                        if getCallState m.CallGuid = Status4.Finish then
                            match apiDefOf m.ApiCallGuid with
                            | Some def when isLevelLike def ->
                                let target = Abnormal.target (Some m.CallGuid) (Some m.ApiCallGuid) m.RxWorkGuid
                                emitLatched (Abnormal.sensorOpen target (nowUtc ())) nowMs
                            | _ -> ()
            | None -> ())

    /// 능동 timeout 워치독 — IO-edge 의 사각(완료 InTag 가 늦게/영영 안 올라옴)을 메운다.
    /// going clock 이 살아있는(완료 rising 미관측) ApiCall 의 경과가 Device Work Max 를 넘으면
    /// 완료를 기다리지 않고 즉시 ActionOver 를 발행한다. going episode 당 1회만(timedOut 마킹),
    /// goingClock 은 완료 엣지가 정리하도록 그대로 둔다(SensorShort 오탐 방지).
    /// reconcile 스레드에서 호출되므로 OnObservedIo(hub 스레드)와 lock 으로 직렬화한다.
    member _.OnTick(nowMs: int) =
      lock locker (fun () ->
        if goingClock.Count > 0 then
            for kv in goingClock do
                let apiCallId = kv.Key
                if not (timedOut.Contains apiCallId) then
                    match ioMap.Mappings |> List.tryFind (fun m -> m.ApiCallGuid = apiCallId) with
                    | Some m ->
                        match AbnormalDetector.tryResolveRangeFromMapping index m with
                        | Some range ->
                            let elapsed = nowMs - kv.Value
                            // goingClock 존재 = 완료 입력 미수신 → inputActive=false.
                            match Abnormal.classifyTick range elapsed false with
                            | Some AbnormalKind.ActionOver ->
                                let target = Abnormal.target (Some m.CallGuid) (Some m.ApiCallGuid) m.RxWorkGuid
                                emitLatched (Abnormal.actionOver target elapsed (nowUtc ())) nowMs
                                timedOut.Add apiCallId |> ignore
                            | _ -> ()
                        | None -> ()
                    | None -> ())

    /// observed cycle 재시작/연결 reload 등으로 going 관측을 무효화할 때.
    member _.Reset() =
      lock locker (fun () ->
        goingClock.Clear()
        prevActive.Clear()
        timedOut.Clear())

    /// C#(DSPilot) 에서 쓰기 위한 팩토리 — System.Func/Action 을 F# 함수로 래핑.
    static member FromDelegates(index: SimIndex, ioMap: SignalIOMap, nowUtc: System.Func<DateTime>, sink: System.Action<AbnormalRecord>) : MonitoringAbnormalAdapter =
        let nowFn () = nowUtc.Invoke()
        let sinkFn r = sink.Invoke r
        // DSPilot 경로는 Call state 주입이 없어 SensorOpen 비활성(Ready 고정 → Finish 분기 안 탐). short/timing 은 그대로.
        MonitoringAbnormalAdapter(index, ioMap, (fun _ -> Status4.Ready), nowFn, sinkFn)
