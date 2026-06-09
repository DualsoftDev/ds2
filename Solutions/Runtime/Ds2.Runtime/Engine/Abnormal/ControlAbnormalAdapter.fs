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
            | RuntimeSemantics.WaitInputEdge _
            | RuntimeSemantics.WaitInputEdgeStable _
            | RuntimeSemantics.WaitInputLatched _ -> true
            | RuntimeSemantics.WaitPassiveDuration _
            | RuntimeSemantics.WaitPassiveDurationPlus _ -> false

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
                // SensorShort — debounce 는 SensingType(WaitInputStable)이 SSOT, 여기선 즉시 발행.
                emit (Abnormal.sensorShort target (now ()))
        | _ -> ()

    /// InTag falling. level-like 센서가 Finish(reset 전, active hold) 중 꺼지면 SensorOpen.
    member _.OnInputFalling(apiCallId: Guid, _nowMs: int) =
        match mappingOfApiCall apiCallId, apiCallAndDef apiCallId with
        | Some mapping, Some(_, def) when AbnormalDetector.canEvaluate store mapping.CallGuid def ->
            let isLevelLike =
                match def.SensingType with
                | SensingType.Real(Level, _)
                | SensingType.Real(Latched, _) -> true
                | _ -> false   // OneShot falling 은 정상 pulse off 일 수 있으므로 제외
            let callId = mapping.CallGuid
            // v12 §3.2 — RxWork ≠ Ready(reset 전: Going/Finish) 에 level 센서가 빠지면 단선/이탈 = SensorOpen.
            //   reset→Ready 면 정상 사이클 종료라 Open 아님.
            if isLevelLike && getCallState callId <> Status4.Ready then
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
