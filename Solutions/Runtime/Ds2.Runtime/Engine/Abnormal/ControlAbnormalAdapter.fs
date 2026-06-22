namespace Ds2.Runtime.Engine.Abnormal

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.IO
open Ds2.Runtime.Engine.Core

// =============================================================================
// v12 P3b — Control abnormal adapter.
//
//   적용 계획: samples/Abnormal-v12-Apply-Plan.md (§2.1, §5.1·5.3·5.4, R1·R3·R5).
//
//   active EventDriven 상태 + Hub input edge 를 결합해 4 케이스를 분류한다.
//   본체(EventDrivenEngine)에 침투하지 않도록, 상태 조회·시각·sink 를 생성자
//   콜백으로 주입받는다 → 단위 테스트로 "정상 completion 오탐 0" 을 증명 가능.
//
//   gating(§2.3/§4): AbnormalDetector.canEvaluate (자동 Flow ∧ Real ∧ 비인터락) + Plan 활성
//     (Call Going / goingClock)은 adapter 가 분기로 판정. Sensor* 발행 전 ISensorDebouncer(V14).
//   발행: ILatchPolicy(Core, P7) 경유 dedup → sink (P5). SignalR 은 모른다.
//
//   ActionOver 는 Max 시점 OnTick 에서만 낸다(SSOT). InTag 가 Max 이후 늦게 센싱될 때의
//     재발행은 의미 없어 제외(사용자 확정) — InTag rising 경로는 ActionUnder 만 판정한다.
// =============================================================================

type ControlAbnormalAdapter
    ( index: SimIndex,
      ioMap: SignalIOMap,
      getCallState: Guid -> Status4,
      isInputActive: Guid -> bool,
      now: unit -> DateTime,
      sink: AbnormalRecord -> unit,
      ?warmupCycles: int ) =

    let store = index.Store
    let detectorState = AbnormalDetectorState.Empty
    let goingClock = Dictionary<Guid, int>()   // callId → Ready→Going clock(ms)
    let latchPolicy : ILatchPolicy = DefaultLatchPolicy()   // P7 — Sensor 즉시 / Action 5s dedup

    /// 워밍업 — Call 별 첫 N 완주 사이클은 판정하지 않는다(모드 합의: Control=1, VP=2, Monitoring=3).
    /// 시작 직후는 신호 순서/초기 상태(이전 운전의 잔류 high 등)가 정착 전이라 첫 사이클 판정은 오탐.
    /// 기본 0(게이트 없음) — 실전 배선(Composition)이 모드별 값을 명시한다.
    let warmupCycles = defaultArg warmupCycles 0
    let completedCycles = Dictionary<Guid, int>()   // callId → Going 을 거쳐 Ready 로 복귀한 횟수
    // ActionUnder(시간 미만) 게이트 — Work 의 Min 실측 확정 여부(호스트가 calibration-state 로 주입).
    // 기본 false → 미확정 Work 의 ActionUnder 비활성(오탐 차단). ActionOver(Max)는 영향 없음.
    let mutable isMinMeasured : Guid -> bool = fun _ -> false

    let mappingOfApiCall (apiCallId: Guid) =
        ioMap.Mappings |> List.tryFind (fun m -> m.ApiCallGuid = apiCallId)

    let apiCallAndDef (apiCallId: Guid) =
        match Queries.getApiCall apiCallId store with
        | Some apiCall ->
            apiCall.ApiDefId
            |> Option.bind (fun defId -> Queries.getApiDef defId store)
            |> Option.map (fun def -> apiCall, def)
        | None -> None

    /// 이 InTag 가 owning Call 의 정상 completion trigger 인가 (Virtual passive 는 제외).
    let isCompletionTrigger (apiCall: ApiCall) (def: ApiDef) =
        match apiCall.InTag with
        | None -> false
        | Some _ ->
            match RuntimeSemantics.completionTrigger def apiCall with
            | RuntimeSemantics.WaitInput _
            | RuntimeSemantics.WaitInputStable _
            | RuntimeSemantics.WaitInputLatched _ -> true
            | RuntimeSemantics.WaitOutputPlus _ -> false

    let goingMsOf (callId: Guid) =
        match goingClock.TryGetValue callId with
        | true, ms -> Some ms
        | _ -> None

    /// 워밍업 완료 여부 — Target.CallId 기준. Call 미지목 record 는 게이트 없이 발행.
    let isWarmedUp (callId: Guid option) =
        match callId with
        | None -> true
        | Some cid ->
            match completedCycles.TryGetValue cid with
            | true, n -> n >= warmupCycles
            | _ -> warmupCycles <= 0

    /// ILatchPolicy(Core) 경유 dedup 발행. previous=같은 (Kind,Target) 직전발행.
    /// 워밍업 미달 Call 의 판정은 버린다 — 첫 사이클 오탐 차단.
    let emit (record: AbnormalRecord) =
        if isWarmedUp record.Target.CallId then
            AbnormalDetector.emitThroughLatch detectorState latchPolicy sink record

    /// ActionUnder 게이트 주입 — workGuid 의 Min 이 실측 확정(calibration-state)됐는지. 기본 false(비활성).
    member _.IsMinMeasured with get () = isMinMeasured and set v = isMinMeasured <- v

    /// active Call Ready→Going = PS. goingClock 기록.
    member _.OnCallGoing(callId: Guid, nowMs: int) =
        goingClock.[callId] <- nowMs

    /// Call 사이클 종료(Ready/Finish 정리) 시 goingClock 해제 + latch 비움(다음 사이클 재판정).
    /// Going 을 거쳐 돌아온 경우만 완주 사이클로 집계 — 워밍업 게이트의 진행 카운터.
    member _.OnCallReset(callId: Guid) =
        if goingClock.Remove(callId) then
            completedCycles.[callId] <-
                match completedCycles.TryGetValue callId with
                | true, n -> n + 1
                | _ -> 1
        AbnormalDetector.clearLatchForCall detectorState callId
        latchPolicy.ResetOn(LatchResetTrigger.CallTransition)

    /// 통신 blackout(PLC 단절) — 진행 중 goingClock/latch 무효화. Monitoring 어댑터의
    /// InvalidateObservations 와 동형. Control 모드 배선은 후속(단절 시 제어 불능이 더 큰 이슈) —
    /// 메서드만 먼저 둔다. elapsed 에 단절 시간이 포함된 Action* 오탐을 차단하고,
    /// 평가 재개는 Call 별 다음 Ready→Going(OnCallGoing)부터.
    member _.InvalidateObservations() =
        goingClock.Clear()
        detectorState.LastEmitted <- Map.empty
        latchPolicy.ResetOn(LatchResetTrigger.ManualClear)

    /// InTag rising. expected completion → timing(Action*), 아니면 SensorShort.
    member _.OnInputRising(apiCallId: Guid, nowMs: int) =
        match mappingOfApiCall apiCallId, apiCallAndDef apiCallId with
        | Some mapping, Some(apiCall, def) when AbnormalDetector.canEvaluate store mapping.CallGuid def ->
            let callId = mapping.CallGuid
            let target = Abnormal.target (Some callId) (Some apiCallId) None
            let expected =
                getCallState callId = Status4.Going && isCompletionTrigger apiCall def
            if expected then
                match goingMsOf callId, AbnormalDetector.tryResolveRangeFromMapping index mapping with
                | Some goingMs, Some range ->
                    let elapsed = nowMs - goingMs
                    match Abnormal.classifyExpectedRising range elapsed with
                    | Some AbnormalKind.ActionUnder ->
                        // Min 실측 확정(calibration-state)된 Work 만 ActionUnder 발행 — 미확정/모델임의 Min 오탐 차단.
                        match mapping.RxWorkGuid with
                        | Some w when isMinMeasured w -> emit (Abnormal.actionUnder target elapsed (now ()))
                        | _ -> ()
                    // Over 는 Max 시점 OnTick 이 SSOT — InTag 가 Max 이후 늦게 센싱될 때의 재발행은
                    //   의미 없어 안 낸다(사용자 확정). ActionOver/None 모두 무시.
                    | _ -> ()   // 경계 포함 정상 완료 + 늦은 over — 오탐 0
                | _ -> ()       // range/goingClock 미해결 → timing 평가 안 함(정상 완료 허용)
            else
                // 디바이스 공유 — 같은 InTag 주소를 여러 Call 이 단계별로 호출하는 모델에선,
                // 이 신호를 기대 중(Going + completion trigger)인 동거 Call 이 있으면 그 Call 의
                // 정상 완료 신호다. Ready 인 동거 Call 마다 Short 를 내던 오탐(사이클당 공유 Call 수만큼) 차단.
                // 아무 Call 도 기대하지 않을 때만 진짜 SensorShort.
                // ※ 동거 판정은 Call 단위(CallGuid) — ds2 는 같은 ApiDef 를 링크한 Call 들이
                //   ApiCall 인스턴스 자체를 공유한다(개별 Tester1.ADV 와 묶음 Tester.ADV 가 같은
                //   ApiCall guid). ApiCallGuid 로 거르면 기대 중인 묶음 Call 의 매핑이 "자기 자신"
                //   으로 오인 제외되어 억제가 무력화된다(실기 Control 사이클마다 4건 Short 의 원인).
                let expectedElsewhere =
                    match apiCall.InTag with
                    | None -> false
                    | Some tag ->
                        match ioMap.InAddressToMappings |> Map.tryFind tag.Address with
                        | None -> false
                        | Some siblings ->
                            siblings
                            |> List.exists (fun sib ->
                                sib.CallGuid <> callId
                                && getCallState sib.CallGuid = Status4.Going
                                && (match apiCallAndDef sib.ApiCallGuid with
                                    | Some(sibCall, sibDef) -> isCompletionTrigger sibCall sibDef
                                    | None -> false))
                if not expectedElsewhere then
                    // SensorShort — debounce 는 SensingType(WaitInputStable)이 SSOT, 여기선 즉시 발행.
                    emit (Abnormal.sensorShort target (now ()))
        | _ -> ()

    /// InTag falling. Only latched sensing promises that the input must remain active.
    member _.OnInputFalling(apiCallId: Guid, _nowMs: int) =
        match mappingOfApiCall apiCallId, apiCallAndDef apiCallId with
        | Some mapping, Some(_, def) when AbnormalDetector.canEvaluate store mapping.CallGuid def ->
            // Normal(T) 감지 진행 중 off(채터링) = SensorOff abnormal (완료 취소는 ConditionChecker 의
            // stable 메커니즘이 담당). Latch(T) 는 채터링 허용이라 falling 을 abnormal 로 보지 않는다.
            let requiresHeldInput =
                match def.SensingType with
                | SensingType.Normal (Some _) -> true
                | _ -> false
            let callId = mapping.CallGuid
            if requiresHeldInput && getCallState callId <> Status4.Ready then
                let target = Abnormal.target (Some callId) (Some apiCallId) None
                emit (Abnormal.sensorOpen target (now ()))
        | _ -> ()

    /// tick. Going active Call 의 completion 입력이 아직 안 들어왔는데 Max 초과면 ActionOver.
    member _.OnTick(nowMs: int) =
        for mapping in ioMap.Mappings do
            let callId = mapping.CallGuid
            if getCallState callId = Status4.Going then
                match apiCallAndDef mapping.ApiCallGuid with
                | Some(apiCall, def) when AbnormalDetector.canEvaluate store callId def && isCompletionTrigger apiCall def ->
                    match goingMsOf callId, AbnormalDetector.tryResolveRangeFromMapping index mapping with
                    | Some goingMs, Some range ->
                        let elapsed = nowMs - goingMs
                        match Abnormal.classifyTick range elapsed (isInputActive mapping.ApiCallGuid) with
                        | Some AbnormalKind.ActionOver ->
                            let target = Abnormal.target (Some callId) (Some mapping.ApiCallGuid) None
                            emit (Abnormal.actionOver target elapsed (now ()))
                        | _ -> ()
                    | _ -> ()
                | _ -> ()
