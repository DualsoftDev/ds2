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

type MonitoringAbnormalAdapter
    ( index: SimIndex,
      ioMap: SignalIOMap,
      getCallState: Guid -> Status4,   // SensorOpen 판정용: In falling 시점에 Call 이 Finish(reset 전) 인가.
      nowUtc: unit -> DateTime,
      sink: AbnormalRecord -> unit ) =

    let store = index.Store
    let detectorState = AbnormalDetectorState.Empty
    let goingClock = Dictionary<Guid, int>()      // apiCallId → OutTag On(going) 관측시각(ms)
    let prevActive = Dictionary<string, bool>()   // 방향+address → 직전 active (rising edge 판정)
    let latchPolicy : ILatchPolicy = DefaultLatchPolicy()   // P7 — Sensor 즉시 / Action 5s dedup

    // ApiCall → ApiDef (SensorOpen level-like 판정 + gating 용).
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
                                // going clock 있음 = going 을 거쳤다 → elapsed 로 Under/Over (정상 사이클).
                                match AbnormalDetector.tryResolveRangeFromMapping index m with
                                | Some range ->
                                    let elapsed = nowMs - goingAt
                                    match Abnormal.classifyExpectedRising range elapsed with
                                    | Some AbnormalKind.ActionUnder -> emit (Abnormal.actionUnder target elapsed (nowUtc ()))
                                    // Over 는 Max 시점 watchdog(engine onDeviceDurationExpired)이 SSOT — InTag 가 Max
                                    //   이후 늦게 센싱될 때의 재발행은 의미 없어 안 낸다(사용자 확정). ActionOver/None 모두 무시.
                                    | _ -> ()       // 경계 포함 정상 + 늦은 over — 오탐 0
                                | None -> ()
                                goingClock.Remove m.ApiCallGuid |> ignore
                            | false, _ ->
                                // going clock 없음. 그 Out 이 현재도 off 면 "Going 없이 Finish" = SensorShort.
                                // Out 이 현재 on 이면 going rising 만 놓친 사이클 중간 진입이므로 버림(오탐 방지).
                                let outActive =
                                    if System.String.IsNullOrEmpty m.OutAddress then false
                                    else
                                        match prevActive.TryGetValue("OUT:" + m.OutAddress) with
                                        | true, b -> b
                                        | _ -> false
                                // SensorShort — debounce 는 SensingType 이 SSOT. 여기선 즉시 발행.
                                if not outActive then
                                    emit (Abnormal.sensorShort target (nowUtc ()))
                        | _ -> ()
                elif wasInActive && not active then
                    // In falling: Finish(reset 전) + level/latched 센서면 단선/이탈 = SensorOpen.
                    for m in inMappings do
                        match apiDefOf m.ApiCallGuid with
                        | Some def when AbnormalDetector.canEvaluate store m.CallGuid def
                                        && isLevelLike def
                                        && getCallState m.CallGuid <> Status4.Ready ->   // v12 §3.2 RxWork≠Ready
                            let target = Abnormal.target (Some m.CallGuid) (Some m.ApiCallGuid) m.RxWorkGuid
                            emit (Abnormal.sensorOpen target (nowUtc ()))
                        | _ -> ()
            | None -> ()

    /// observed cycle 재시작/연결 reload 등으로 going 관측을 무효화할 때.
    member _.Reset() =
        goingClock.Clear()
        prevActive.Clear()
        detectorState.LastEmitted <- Map.empty
        latchPolicy.ResetOn(LatchResetTrigger.ManualClear)

    /// C#(DSPilot) 에서 쓰기 위한 팩토리 — System.Func/Action 을 F# 함수로 래핑.
    static member FromDelegates(index: SimIndex, ioMap: SignalIOMap, nowUtc: System.Func<DateTime>, sink: System.Action<AbnormalRecord>) : MonitoringAbnormalAdapter =
        let nowFn () = nowUtc.Invoke()
        let sinkFn r = sink.Invoke r
        // DSPilot 경로는 Call state 주입이 없어 SensorOpen 비활성(Ready 고정 → Finish 분기 안 탐). short/timing 은 그대로.
        MonitoringAbnormalAdapter(index, ioMap, (fun _ -> Status4.Ready), nowFn, sinkFn)
