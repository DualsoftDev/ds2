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
//   판정은 P1 Abnormal(순수 분류) + P3a AbnormalDetector(range/latch/clock) 만 쓴다.
//   발행은 P5 — 여기서는 sink 로 흘려보낼 뿐 SignalR 을 모른다.
//
//   rev3 핵심: rising 이 "현재 Going active Call 의 정상 completion trigger InTag" 일 때만
//   timing 으로 보고, 그 외 rising 은 SensorShort. 정상 완료를 Short 로 오탐하지 않는다.
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
    let latchWindowMs = 5000

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
    /// InTag 미지정(Real ⇒ invalidOp)일 때는 trigger 로 보지 않는다 — 방어적 가드.
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

    let emitLatched (record: AbnormalRecord) (nowMs: int) =
        let key = Abnormal.latchKeyOf record
        if AbnormalDetector.tryLatch detectorState key nowMs latchWindowMs then
            sink record

    /// active Call Ready→Going = PS. goingClock 기록.
    member _.OnCallGoing(callId: Guid, nowMs: int) =
        goingClock.[callId] <- nowMs

    /// Call 사이클 종료(Ready/Finish 정리) 시 goingClock 해제.
    member _.OnCallReset(callId: Guid) =
        goingClock.Remove(callId) |> ignore

    /// InTag rising. expected completion → timing(Action*), 아니면 SensorShort.
    member _.OnInputRising(apiCallId: Guid, nowMs: int) =
        match mappingOfApiCall apiCallId, apiCallAndDef apiCallId with
        | Some mapping, Some(apiCall, def) when AbnormalDetector.isPhysicalSensing def ->
            let callId = mapping.CallGuid
            let target = Abnormal.target (Some callId) (Some apiCallId) None
            let expected =
                getCallState callId = Status4.Going && isCompletionTrigger apiCall def
            if expected then
                match goingMsOf callId, AbnormalDetector.tryResolveRangeFromMapping index mapping with
                | Some goingMs, Some range ->
                    let elapsed = nowMs - goingMs
                    match Abnormal.classifyExpectedRising range elapsed with
                    | Some AbnormalKind.ActionUnder -> emitLatched (Abnormal.actionUnder target elapsed (now ())) nowMs
                    | Some AbnormalKind.ActionOver  -> emitLatched (Abnormal.actionOver target elapsed (now ())) nowMs
                    | _ -> ()   // 경계 포함 정상 완료 — 오탐 0
                | _ -> ()       // range/goingClock 미해결 → timing 평가 안 함(정상 완료 허용)
            else
                emitLatched (Abnormal.sensorShort target (now ())) nowMs
        | _ -> ()

    /// InTag falling. level-like 센서가 expected active hold(Call Going) 중 꺼지면 SensorOpen.
    member _.OnInputFalling(apiCallId: Guid, nowMs: int) =
        match mappingOfApiCall apiCallId, apiCallAndDef apiCallId with
        | Some mapping, Some(_, def) when AbnormalDetector.isPhysicalSensing def ->
            let isLevelLike =
                match def.SensingType with
                | SensingType.Real(Level, _)
                | SensingType.Real(Latched, _) -> true
                | _ -> false   // OneShot falling 은 정상 pulse off 일 수 있으므로 제외
            let callId = mapping.CallGuid
            if isLevelLike && getCallState callId = Status4.Going then
                let target = Abnormal.target (Some callId) (Some apiCallId) None
                emitLatched (Abnormal.sensorOpen target (now ())) nowMs
        | _ -> ()

    /// tick. Going active Call 의 completion 입력이 아직 안 들어왔는데 Max 초과면 ActionOver.
    member _.OnTick(nowMs: int) =
        for mapping in ioMap.Mappings do
            let callId = mapping.CallGuid
            if getCallState callId = Status4.Going then
                match apiCallAndDef mapping.ApiCallGuid with
                | Some(apiCall, def) when AbnormalDetector.isPhysicalSensing def && isCompletionTrigger apiCall def ->
                    match goingMsOf callId, AbnormalDetector.tryResolveRangeFromMapping index mapping with
                    | Some goingMs, Some range ->
                        let elapsed = nowMs - goingMs
                        match Abnormal.classifyTick range elapsed (isInputActive mapping.ApiCallGuid) with
                        | Some AbnormalKind.ActionOver ->
                            let target = Abnormal.target (Some callId) (Some mapping.ApiCallGuid) None
                            emitLatched (Abnormal.actionOver target elapsed (now ())) nowMs
                        | _ -> ()
                    | _ -> ()
                | _ -> ()
