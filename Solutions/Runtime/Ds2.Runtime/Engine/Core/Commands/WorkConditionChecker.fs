namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.Model

/// Work/Call 상태 전이 조건 검사 모듈 (순수 함수)
module WorkConditionChecker =

    /// ReferenceOf 기반 OR 그룹에서 같은 그룹의 Work ID 목록을 반환
    let private orGroupGuidsOf (index: SimIndex) (workGuid: Guid) : Guid list =
        SimIndex.referenceGroupOf index workGuid

    /// Predecessor 조건 검사 공통 함수
    let private checkPredecessorCondition
        (index: SimIndex) (state: SimState) (predecessorGuids: Guid list)
        (targetState: Status4) (combiner: (Guid -> bool) -> Guid list -> bool) : bool =
        if predecessorGuids.IsEmpty then false
        else
            // 중복 제거: 같은 OR 그룹이면 하나만 확인
            let distinctPreds = predecessorGuids |> List.distinct
            distinctPreds |> combiner (fun predGuid ->
                let orGuids = orGroupGuidsOf index predGuid
                orGuids |> List.exists (fun wg -> Map.tryFind wg state.WorkStates = Some targetState))

   /// 토큰 경로에 있는 Work는 슬롯에 토큰이 있어야 시작 가능
    let tokenReady (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        if index.TokenPathGuids.Contains workGuid then
            SimState.getWorkToken workGuid state |> Option.isSome
        else true

    /// Predecessor 시작 조건 충족 여부 (공용 헬퍼)
    /// - Source + predecessor 없음 → true (자동 시작 가능)
    /// - Source + predecessor 있음 → predecessor AND 조건
    /// - 일반 + predecessor 없음 → false (수동 시작만 가능)
    let predecessorSatisfied (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        let isSource =
            index.WorkTokenRole |> Map.tryFind workGuid
            |> Option.map (fun r -> r.HasFlag(TokenRole.Source)) |> Option.defaultValue false
        let preds = SimIndex.findOrEmpty workGuid index.WorkStartPreds
        if preds.IsEmpty then isSource
        else checkPredecessorCondition index state preds Status4.Finish List.forall

    /// FS1 CAT-03-R04 — 자동 진입은 그 진입에 **현재 Run 의 실제 완료 근거**가 하나 이상 있어야 한다.
    /// 모든 선행이 Finish 여도, 그 Finish 가 전부 `finished` 적재(LoadedFinishWorks)면 서지 않는다.
    /// (A,B)>X 에서 A 가 적재 완료이고 B 가 실제로 끝나면 B 가 근거다.
    let private hasLiveCompletion (index: SimIndex) (state: SimState) (preds: Guid list) : bool =
        preds
        |> List.distinct
        |> List.exists (fun predGuid ->
            orGroupGuidsOf index predGuid
            |> List.exists (fun wg ->
                Map.tryFind wg state.WorkStates = Some Status4.Finish
                && not (state.LoadedFinishWorks.Contains wg)))

    /// 자동 진입 조건 — predecessorSatisfied 에 실제 완료 근거(CAT-03-R04)를 더한다.
    /// 수동 강제 시작·토큰 소스 준비는 predecessorSatisfied 그대로다(사용자 요청은 사건이다).
    let autoEntrySatisfied (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        predecessorSatisfied index state workGuid
        && (state.LoadedFinishWorks.IsEmpty
            || (let preds = SimIndex.findOrEmpty workGuid index.WorkStartPreds
                preds.IsEmpty || hasLiveCompletion index state preds))

    /// FS1 CAT-05-R07 — 토큰/결과는 **선택 진입**의 근거를 쓴다. 여러 진입(원본·자동 Ref)을 가진 Work 가 토큰을
    /// 넘겨받았으면, 그 토큰을 넘긴 Work 가 원인인 진입으로만 시작한다. 전에는 선언 순서상 앞선 진입이 남의 토큰으로
    /// 시작해, 제 원인이 아닌 선행을 되돌리고 그 선행이 토큰을 쥔 채 homing 에 갇혔다(#8936 재현).
    /// 넘긴 Work 가 어느 진입의 원인도 아니면(출처 모름·진입 밖에서 옴) 가리지 않는다.
    let tokenEntryEligible (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        match SimIndex.referenceGroupOf index workGuid with
        | [] | [ _ ] -> true
        | group ->
            match SimState.getWorkTokenFrom (SimIndex.canonicalWorkGuid index workGuid) state with
            | None -> true
            | Some fromWork ->
                let origin = SimIndex.canonicalWorkGuid index fromWork
                let fedBy entry =
                    SimIndex.findOrEmpty entry index.WorkStartPreds
                    |> List.exists (fun p -> SimIndex.canonicalWorkGuid index p = origin)
                fedBy workGuid || not (group |> List.exists fedBy)

    /// Work 시작 가능 여부: 자동 진입 + 토큰 조건 (AND)
    let canStartWork (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        autoEntrySatisfied index state workGuid && tokenReady index state workGuid
        && tokenEntryEligible index state workGuid

    /// FS1-Q25 — 배타 점유. Going Call 은 이번 회차에 Tx 를 이미 명령했거나(실행 중·완료 대기),
    /// 지금 Tx 가 설 수 있으면(곧 명령) 그 배타 상대를 막는다. **다시 준비를 기다리는** Call
    /// (Tx 가 Finish 이고 아직 명령 전)은 막지 않는다 — 그 다시 준비를 해 줄 요청까지 막으면 서로 기다린다.
    let callOccupies (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        match Map.tryFind callGuid state.CallStates with
        | Some Status4.Going ->
            let txGuids = SimIndex.txWorkGuids index callGuid
            let commanded =
                match state.CallTxEpochSnapshot |> Map.tryFind callGuid with
                | Some snap -> snap |> Map.exists (fun tx saved -> SimState.getWorkEpoch (SimIndex.canonicalWorkGuid index tx) state > saved)
                | None -> true
            commanded || txGuids.IsEmpty
            || txGuids |> List.exists (fun tx -> Map.tryFind tx state.WorkStates <> Some Status4.Finish)
        | _ -> false

    /// Predecessor 조건만 체크 (토큰 무시) — 수동 강제 시작 시 사용
    let canStartWorkPredOnly (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        predecessorSatisfied index state workGuid

    /// 같은 OR 그룹(ReferenceOf 기반)을 공유하는 모든 Work의 ResetPreds 수집
    let collectResetPreds (index: SimIndex) (workGuid: Guid) : (string * string * Guid list) option =
        match Map.tryFind workGuid index.WorkSystemName, Map.tryFind workGuid index.WorkName with
        | Some sysName, Some wName ->
            let orGuids = orGroupGuidsOf index workGuid
            let preds =
                orGuids
                |> List.collect (fun wg -> SimIndex.findOrEmpty wg index.WorkResetPreds)
                |> List.distinct
            if preds.IsEmpty then None
            else Some (sysName, wName, preds)
        | _ -> None

    /// Work 리셋 가능 여부 (PredecessorReset 중 하나라도 G)
    let canResetWork (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        match collectResetPreds index workGuid with
        | Some (_, _, preds) -> checkPredecessorCondition index state preds Status4.Going List.exists
        | None -> false

    let private hasChangedAtCurrentClock (state: SimState) apiCallGuid =
        state.IOValueChangedAt
        |> Map.tryFind apiCallGuid
        |> Option.exists ((=) state.Clock)

    /// 단일 ConditionEntry 기본 평가.
    ///
    /// **값이 있으면 값이 기준**이다 — 참조 Work 가 어느 단계인지 묻지 않는다.
    /// 예전에는 Work 가 Finish 여야만 값을 봤는데, 그러면 아날로그·Range 스펙이
    /// «선행 동작이 끝난 직후» 라는 좁은 창에서만 성립했다. 온도·압력·위치는 누가
    /// 끝나야 생기는 값이 아니므로 그 게이트는 맞지 않는다.
    /// 같은 파일의 runtimeInputSatisfied 도 IO 값을 먼저 보고 판정한다 — 그쪽에 맞춘다.
    ///
    /// 값이 없을 때는 **참조 Work 의 상태를 신호로 읽는다**.
    ///   Finish = 동작이 끝났다(켜짐) · Ready = 아직 동작 전(꺼짐)
    /// 시뮬레이션처럼 그 신호를 아무도 만들지 않는 모드에서는 IO 가 영영 비어 있으므로,
    /// 값이 없다는 이유로 거짓을 돌려주면 조건이 영원히 서지 않는다. (참조 Work 가 Finish
    /// 인데도 IO 가 없어 «불만족» 으로 판정되어 건너뛰던 증상이 이것이었다.)
    let private checkConditionSpecBase (state: SimState) (spec: ConditionEntry) : bool =
        let ioValue =
            spec.ApiCallGuid |> Option.bind (fun g -> state.IOValues |> Map.tryFind g)
        match ioValue with
        | Some currentValue -> ValueSpec.evaluate spec.InputSpec currentValue
        | None ->
            let rxState = state.WorkStates |> Map.tryFind spec.RxWorkGuid
            if ValueSpec.isFalse spec.InputSpec then rxState = Some Status4.Ready
            else rxState = Some Status4.Finish

    /// 단일 ConditionEntry 평가. ContactKind 를 런타임에도 적용한다.
    let checkConditionSpec (state: SimState) (spec: ConditionEntry) : bool =
        let matched = checkConditionSpecBase state spec
        match spec.ContactKind with
        | ContactKind.NoContact -> matched
        | ContactKind.NcContact -> not matched
        | ContactKind.RisingPulse ->
            matched
            && spec.ApiCallGuid
               |> Option.exists (hasChangedAtCurrentClock state)
        | ContactKind.FallingPulse ->
            not matched
            && spec.ApiCallGuid
               |> Option.exists (hasChangedAtCurrentClock state)
        | ContactKind.Inverter -> not matched
        | _ -> matched

    /// ConditionExpression 트리 평가 — cc.IsOR / cc.IsInverted 보존된 And/Or/Not 노드 재귀 처리.
    /// 빈 And 는 true (조건 없음 통과), 빈 Or 는 false.
    let rec evaluateConditionExpression (state: SimState) (expr: ConditionExpression) : bool =
        match expr with
        | Const value -> value
        | Leaf entry -> checkConditionSpec state entry
        | And exprs -> exprs |> List.forall (evaluateConditionExpression state)
        | Or exprs ->
            if exprs.IsEmpty then false
            else exprs |> List.exists (evaluateConditionExpression state)
        | Not inner -> not (evaluateConditionExpression state inner)

    /// SkipAction 공통 helper: 조건이 **성립하면** skip.
    ///
    /// leaf 는 ValueSpec 판정에 접점을 입힌 결과다 (NoContact=그대로, NcContact=부정).
    /// 그 최종값이 참이면 건너뛴다 — 접점이 곧 «어느 쪽에서 건너뛸지» 를 고르는 스위치다.
    ///   NoContact + ValueSpec 만족   → leaf true  → skip   (참일 때 skip)
    ///   NcContact + ValueSpec 불만족 → leaf true  → skip   (거짓일 때 skip)
    ///
    /// 예전에는 `not (evaluate ...)` 였다 — 만족하면 실행하고 어긋나면 건너뛰었다.
    /// 그래서 화면의 기대값과 실제 거동이 반대로 읽혔다. 조건이 없으면 건너뛰지 않는다.
    let private shouldSkipByExpr (state: SimState) (exprOpt: ConditionExpression option) : bool =
        match exprOpt with
        | Some (And []) | None -> false
        | Some expr -> evaluateConditionExpression state expr

    /// SkipAction (Call): 조건 성립 시 Going 없이 Finish 로 skip
    let shouldSkipCall (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        shouldSkipByExpr state (Map.tryFind callGuid index.CallSkipActionConditions)

    /// SkipAction (Work): 조건 성립 시 Work 가 Going 없이 Finish 로 skip
    let shouldSkipWork (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        shouldSkipByExpr state (Map.tryFind workGuid index.WorkSkipActionConditions)

    /// Call 시작 가능 여부 (Work G + 선행 Call F + AutoAux/ComAux 조건)
    let canStartCall (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        let callWork = Map.tryFind callGuid index.CallWorkGuid
        let callPreds = SimIndex.findOrEmpty callGuid index.CallStartPreds
        let basicOk =
            callWork |> Option.map (fun wg -> Map.tryFind wg state.WorkStates = Some Status4.Going) |> Option.defaultValue false &&
            callPreds |> List.forall (fun pred ->
                let orGuids = SimIndex.callReferenceGroupOf index pred
                orGuids |> List.exists (fun cg -> Map.tryFind cg state.CallStates = Some Status4.Finish))
        if not basicOk then false
        elif shouldSkipCall index state callGuid then true
        else
            let evalExpr exprMap =
                match Map.tryFind callGuid exprMap with
                | Some expr -> evaluateConditionExpression state expr
                | None -> true
            evalExpr index.CallAutoAuxConditions && evalExpr index.CallComAuxConditions

    /// 기존 Work 완료 기반 판정. v10 Real sensing 은 stale input 방지용으로,
    /// Virtual sensing 은 Tx/Rx duration 완료 판정으로 재사용한다.
    let private workCompletion (index: SimIndex) (state: SimState) (callGuid: Guid) (workGuids: Guid list) : bool =
        if workGuids.IsEmpty then true
        else
            let allFinish = workGuids |> List.forall (fun workGuid -> Map.tryFind workGuid state.WorkStates = Some Status4.Finish)
            let callType = index.CallTypeMap |> Map.tryFind callGuid |> Option.defaultValue CallType.WaitForCompletion
            if callType = CallType.SkipIfCompleted then allFinish
            else
                // FS1-Q25 / CAT-10 — WaitForCompletion 은 **이 Call 회차에 대상이 끝난 사건**으로 완료한다.
                // 대상이 같은 걸음에 다시 준비(Reset)돼 지금 Finish 가 아니어도 사건은 관측됐다.
                // 진행 중이던 대상에 합류한 경우도 그 실행의 완료를 본다(과거 완료는 Finish 횟수가 늘지 않아 불인정).
                match state.CallRxFinishSnapshot |> Map.tryFind callGuid with
                | Some finishMap ->
                    workGuids |> List.forall (fun workGuid ->
                        let canonical = SimIndex.canonicalWorkGuid index workGuid
                        let saved = finishMap |> Map.tryFind workGuid |> Option.defaultValue 0
                        SimState.getWorkFinishEpoch canonical state > saved)
                | None ->
                    allFinish
                    && (match state.CallRxEpochSnapshot |> Map.tryFind callGuid with
                        | None -> true
                        | Some epochMap ->
                            workGuids |> List.forall (fun workGuid ->
                                let canonical = SimIndex.canonicalWorkGuid index workGuid
                                let savedEpoch = epochMap |> Map.tryFind workGuid |> Option.defaultValue 0
                                SimState.getWorkEpoch canonical state > savedEpoch))

    let private legacyRxCompletion (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        workCompletion index state callGuid (SimIndex.rxWorkGuids index callGuid)

    let private virtualWorkCompletion (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        let workGuids = SimIndex.completionWorkGuids index callGuid
        if workGuids.IsEmpty then
            index.CallWorkGuid
            |> Map.tryFind callGuid
            |> Option.map (fun workGuid ->
                state.WorkMinDurationMet.Contains(SimIndex.canonicalWorkGuid index workGuid))
            |> Option.defaultValue true
        else
            workCompletion index state callGuid workGuids

    let private runtimeInputSatisfied (index: SimIndex) (state: SimState) (callGuid: Guid) (apiCall: ApiCall) (isExternalIn: bool) : bool =
        let rxCompletionSatisfied () = legacyRxCompletion index state callGuid
        let hasRxWork = SimIndex.rxWorkGuids index callGuid |> List.isEmpty |> not

        // Control/Monitoring(isExternalIn): In(외부 신호)만으로 Call 완료. device(rxCompletion)는
        //   abnormal under/over 판정의 기준자일 뿐 완료 게이트가 아니다 — In 이 device 보다 빨리(under)/늦게(over)
        //   와도 In 으로만 Finish. In 미도착이면 미완료(Call 은 Going 유지하며 외부 In 을 기다린다).
        // Simulation/VP: self-driven — device 가 In 을 생성하는 주체라 device 완료까지 AND.
        match state.IOValues |> Map.tryFind apiCall.Id with
        | Some currentValue ->
            let inOk = ValueSpec.evaluate apiCall.InputSpec currentValue
            if isExternalIn then
                // Control/Monitoring 완료 = In 평가.
                //   InputSpec=UndefinedValue 면 ValueSpec.evaluate 가 io 값 무관 true(In off "false" 도 통과)라
                //   In 안 들어왔는데 즉시 Finish 되던 버그 → In high(activeInputValue)와 일치할 때만 인정.
                //   정의된 ValueSpec(Single/Multiple/Ranges/String)은 evaluate 가 정확히 평가하므로 추가 문자열
                //   일치를 요구하지 않는다 — Range/Multiple 안의 허용값도 정상 완료해야(v10 ValueSpec 일반성).
                match apiCall.InputSpec with
                | UndefinedValue ->
                    inOk && String.Equals(currentValue, RuntimeSemantics.activeInputValue apiCall, StringComparison.OrdinalIgnoreCase)
                | _ -> inOk
            else inOk && rxCompletionSatisfied ()
        | None when not isExternalIn && hasRxWork -> rxCompletionSatisfied ()
        | None -> false

    let private runtimeInputEdgeSatisfied (index: SimIndex) (state: SimState) (callGuid: Guid) (apiCall: ApiCall) (isExternalIn: bool) : bool =
        let hasRisingValue =
            match state.IOValues |> Map.tryFind apiCall.Id with
            | Some currentValue -> ValueSpec.evaluate apiCall.InputSpec currentValue
            | None -> false

        if not hasRisingValue then false
        else
            let savedEpoch =
                state.CallInputEpochSnapshot
                |> Map.tryFind callGuid
                |> Option.bind (Map.tryFind apiCall.Id)
                |> Option.defaultValue 0

            let currentEpoch =
                state.IOValueEpoch
                |> Map.tryFind apiCall.Id
                |> Option.defaultValue 0

            let edgeOk = currentEpoch > savedEpoch
            // Control/Monitoring: edge(외부 In)만. Simulation/VP: edge && device 완료.
            if isExternalIn then edgeOk
            else edgeOk && legacyRxCompletion index state callGuid

    let private completionTriggerSatisfied (index: SimIndex) (state: SimState) (callGuid: Guid) (apiDef: ApiDef) (apiCall: ApiCall) (isSimulation: bool) (isExternalIn: bool) : bool =
        // Simulation 정책: Real 인데 I/O(OutTag/InTag) 미설정이면 실 센서 신호가 없어 completionTrigger 가
        // invalidOp(V2) → catch fallback(RxWork 없는 ResetReset device 는 false)로 영영 완료 못 해 멈춘다.
        // 가상 시뮬레이션은 I/O 가 불필요하므로 Real 을 Virtual 처럼 Duration 기반 완료로 돌린다("Simulation = Real→Virtual").
        // Control/Monitoring 은 실 I/O 가 진실원이라 기존대로.
        let ioMissingReal =
            (match apiDef.ActionType  with ActionType.Virtual -> false | _ -> apiCall.OutTag.IsNone)
            || (match apiDef.SensingType with SensingType.Virtual _ -> false | _ -> apiCall.InTag.IsNone)
        if isSimulation && ioMissingReal then
            virtualWorkCompletion index state callGuid
        else
            // I/O 미설정이면 예외 대신 None — Control/Monitoring 은 아래 fallback 으로 간다.
            match RuntimeSemantics.tryCompletionTrigger apiDef apiCall with
            | None ->
                // Control/Monitoring(외부 In): trigger 가 없어도 device(rx) 로 Call 완료 금지 — In 만으로.
                let hasRxWork = SimIndex.rxWorkGuids index callGuid |> List.isEmpty |> not
                not isExternalIn && hasRxWork && legacyRxCompletion index state callGuid
            | Some trigger ->
                match trigger with
                | RuntimeSemantics.WaitOutputPlus (_, ms) ->
                    // v16 SensingType.Virtual(T): 출력 발생(Call Going 진입) 시점 + T 후 완료 — 센서 없는 설비.
                    //   종료는 SensingType 단독 결정 — Control/Monitoring 도 mode 무관 T 로 Finish.
                    //   Simulation 은 가상 device 사이클과의 정합을 위해 device(rx) 완료도 함께 본다.
                    let elapsedOk =
                        match SimState.getCallGoingElapsedMs callGuid state with
                        | Some elapsed -> elapsed >= ms
                        | None -> false
                    if isExternalIn then elapsedOk
                    else elapsedOk || virtualWorkCompletion index state callGuid
                | RuntimeSemantics.WaitInput _ ->
                    runtimeInputSatisfied index state callGuid apiCall isExternalIn
                | RuntimeSemantics.WaitInputStable (_, ms) ->
                    // Normal(T): 감지 후 T ms 유지 확인 — T 중 off(채터링)면 ChangedAt 이 리셋되어 완료가
                    //   자연 취소되고(다시 감지 대기), abnormal adapter 가 SensorOff 를 발행한다.
                    runtimeInputSatisfied index state callGuid apiCall isExternalIn
                    && SimState.getIOStableMs apiCall.Id state >= ms
                | RuntimeSemantics.WaitInputLatched (_, ms) ->
                    // Latch(T): 감지가 한 번 관측되면(latch) T ms 지연 후 완료 — T 구간 채터링 허용.
                    //   현재 신호가 off 여도 무방(메모리 latch) — 감지 이력은 Call Going 스냅샷 대비
                    //   epoch 증가로 판정하고, 마지막 신호 변화로부터 T 경과를 요구한다
                    //   (채터링이 완료를 취소하지도, abnormal 을 만들지도 않는다).
                    let savedEpoch =
                        state.CallInputEpochSnapshot
                        |> Map.tryFind callGuid
                        |> Option.bind (Map.tryFind apiCall.Id)
                        |> Option.defaultValue 0
                    let currentEpoch =
                        state.IOValueEpoch
                        |> Map.tryFind apiCall.Id
                        |> Option.defaultValue 0
                    let sensed = currentEpoch > savedEpoch
                    sensed && SimState.getIOStableMs apiCall.Id state >= ms

    /// Call 완료 가능 여부.
    /// v10 SensingType 이 Virtual 이면 Duration/RxWork 수명 주기를 따른다.
    /// Real 이면 RuntimeSemantics.completionTrigger 의 input 계열 trigger 를 IOValue/RxWork epoch 에 연결한다.
    let canCompleteCall (index: SimIndex) (state: SimState) (callGuid: Guid) (isSimulation: bool) (isExternalIn: bool) : bool =
        let apiPairs =
            SimIndex.findOrEmpty callGuid index.CallApiCallGuids
            |> List.choose (fun apiCallId ->
                Queries.getApiCall apiCallId index.Store
                |> Option.bind (fun apiCall ->
                    apiCall.ApiDefId
                    |> Option.bind (fun apiDefId ->
                        Queries.getApiDef apiDefId index.Store
                        |> Option.map (fun apiDef -> apiDef, apiCall))))

        if apiPairs.IsEmpty then
            legacyRxCompletion index state callGuid
        else
            apiPairs
            |> List.forall (fun (apiDef, apiCall) ->
                completionTriggerSatisfied index state callGuid apiDef apiCall isSimulation isExternalIn)

    /// TokenSource 중 Ready 상태이면서 predecessor 조건이 미충족인 Work 목록
    let collectBlockedSources (index: SimIndex) (state: SimState) : (Guid * string) list =
        index.TokenSourceGuids
        |> List.filter (fun g ->
            (state.WorkStates |> Map.tryFind g |> Option.defaultValue Status4.Ready) = Status4.Ready
            && not (canStartWorkPredOnly index state g))
        |> List.choose (fun g ->
            index.WorkName |> Map.tryFind g |> Option.map (fun name -> g, name))
