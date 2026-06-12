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
      sink: AbnormalRecord -> unit ) =

    let store = index.Store
    let detectorState = AbnormalDetectorState.Empty
    let goingClock = Dictionary<Guid, int>()   // callId → Ready→Going clock(ms)
    let latchPolicy : ILatchPolicy = DefaultLatchPolicy()   // P7 — Sensor 즉시 / Action 5s dedup

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

    /// ILatchPolicy(Core) 경유 dedup 발행. previous=같은 (Kind,Target) 직전발행.
    let emit (record: AbnormalRecord) =
        AbnormalDetector.emitThroughLatch detectorState latchPolicy sink record

    /// active Call Ready→Going = PS. goingClock 기록.
    member _.OnCallGoing(callId: Guid, nowMs: int) =
        goingClock.[callId] <- nowMs

    /// Call 사이클 종료(Ready/Finish 정리) 시 goingClock 해제 + latch 비움(다음 사이클 재판정).
    member _.OnCallReset(callId: Guid) =
        goingClock.Remove(callId) |> ignore
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
                    | Some AbnormalKind.ActionUnder -> emit (Abnormal.actionUnder target elapsed (now ()))
                    // Over 는 Max 시점 OnTick 이 SSOT — InTag 가 Max 이후 늦게 센싱될 때의 재발행은
                    //   의미 없어 안 낸다(사용자 확정). ActionOver/None 모두 무시.
                    | _ -> ()   // 경계 포함 정상 완료 + 늦은 over — 오탐 0
                | _ -> ()       // range/goingClock 미해결 → timing 평가 안 함(정상 완료 허용)
            else
                // 디바이스 공유 — 같은 InTag 주소를 여러 Call 이 단계별로 호출하는 모델에선,
                // 이 신호를 기대 중(Going + completion trigger)인 동거 Call 이 있으면 그 Call 의
                // 정상 완료 신호다. Ready 인 동거 Call 마다 Short 를 내던 오탐(사이클당 공유 Call 수만큼) 차단.
                // 아무 Call 도 기대하지 않을 때만 진짜 SensorShort.
                let expectedElsewhere =
                    match apiCall.InTag with
                    | None -> false
                    | Some tag ->
                        match ioMap.InAddressToMappings |> Map.tryFind tag.Address with
                        | None -> false
                        | Some siblings ->
                            siblings
                            |> List.exists (fun sib ->
                                sib.ApiCallGuid <> apiCallId
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
