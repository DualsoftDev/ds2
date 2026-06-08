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

    /// Work 시작 가능 여부: predecessor + 토큰 조건 (AND)
    let canStartWork (index: SimIndex) (state: SimState) (workGuid: Guid) : bool =
        predecessorSatisfied index state workGuid && tokenReady index state workGuid

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

    /// 단일 ConditionEntry 기본 평가 (RxWork 상태 + ValueSpec 비교)
    let private checkConditionSpecBase (state: SimState) (spec: ConditionEntry) : bool =
        if ValueSpec.isFalse spec.InputSpec then
            state.WorkStates |> Map.tryFind spec.RxWorkGuid = Some Status4.Ready
        else
            match state.WorkStates |> Map.tryFind spec.RxWorkGuid with
            | Some s when s = Status4.Finish ->
                match spec.ApiCallGuid with
                | Some apiCallGuid ->
                    match state.IOValues |> Map.tryFind apiCallGuid with
                    | Some currentValue -> ValueSpec.evaluate spec.InputSpec currentValue
                    | None -> false
                | None -> true
            | _ -> false

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

    /// SkipAction 공통 helper: 조건 expr 이 false → skip 해야 함을 의미.
    let private shouldSkipByExpr (state: SimState) (exprOpt: ConditionExpression option) : bool =
        match exprOpt with
        | Some (And []) | None -> false
        | Some expr -> not (evaluateConditionExpression state expr)

    /// SkipAction (Call): ValueSpec 기준 unmatch 시 Going 없이 Finish로 skip
    let shouldSkipCall (index: SimIndex) (state: SimState) (callGuid: Guid) : bool =
        shouldSkipByExpr state (Map.tryFind callGuid index.CallSkipActionConditions)

    /// SkipAction (Work): ValueSpec 기준 unmatch 시 Work 가 Going 없이 Finish 로 skip
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
            if not allFinish then false
            else
                let callType = index.CallTypeMap |> Map.tryFind callGuid |> Option.defaultValue CallType.WaitForCompletion
                if callType = CallType.SkipIfCompleted then true
                else
                    match state.CallRxEpochSnapshot |> Map.tryFind callGuid with
                    | None -> true
                    | Some epochMap ->
                        workGuids |> List.forall (fun workGuid ->
                            let canonical = SimIndex.canonicalWorkGuid index workGuid
                            let savedEpoch = epochMap |> Map.tryFind workGuid |> Option.defaultValue 0
                            let currentEpoch = SimState.getWorkEpoch canonical state
                            currentEpoch > savedEpoch)

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
            (match apiDef.ActionType  with ActionType.Real _  -> apiCall.OutTag.IsNone | _ -> false)
            || (match apiDef.SensingType with SensingType.Real _ -> apiCall.InTag.IsNone  | _ -> false)
        if isSimulation && ioMissingReal then
            virtualWorkCompletion index state callGuid
        else
            try
                match RuntimeSemantics.completionTrigger apiDef apiCall with
                | RuntimeSemantics.WaitPassiveDuration _
                | RuntimeSemantics.WaitPassiveDurationPlus _ ->
                    // v10 §10(NORMATIVE): SensingType=Virtual 은 종료 시점을 SensingType 이 단독 결정한다 →
                    //   Duration(=WorkRx 종점) 완료. 물리 InTag 가 없어 In 을 기다릴 수 없으므로 Control/
                    //   Monitoring 이라도 mode 무관하게 duration 으로 Finish 한다.
                    //   ("device plan-duration 으로 Call 완료 금지(In-only)" 규칙은 실제 센서가 있는
                    //    SensingType=Real 의 WaitInput* 분기(runtimeInputSatisfied)에만 적용된다.)
                    virtualWorkCompletion index state callGuid
                | RuntimeSemantics.WaitInput _
                | RuntimeSemantics.WaitInputLatched _ ->
                    runtimeInputSatisfied index state callGuid apiCall isExternalIn
                | RuntimeSemantics.WaitInputStable (_, ms) ->
                    // v10 §5/§10 — Real(Level, Append n) = "센서 ON 후 n ms 연속 유지" debounce. 종료는 SensingType 이
                    //   단독 결정하므로 mode 무관하게 In 안정 n ms 를 적용한다. (ActionType.Append=출력 유지와는 직교
                    //   축이라 이중 아님.) Control 도 Composition 이 debounce ms 후 ConditionEval 재평가를 schedule.
                    runtimeInputSatisfied index state callGuid apiCall isExternalIn
                    && SimState.getIOStableMs apiCall.Id state >= ms
                | RuntimeSemantics.WaitInputEdge _ ->
                    runtimeInputEdgeSatisfied index state callGuid apiCall isExternalIn
                | RuntimeSemantics.WaitInputEdgeStable (_, ms) ->
                    // v10 §5/§10 — Real(OneShot, Append n) = "edge 이후 n ms 안정". 종료는 SensingType 단독 결정이라
                    //   mode 무관 적용(ActionType.Append 와는 직교 축).
                    runtimeInputEdgeSatisfied index state callGuid apiCall isExternalIn
                    && SimState.getIOStableMs apiCall.Id state >= ms
            with
            | _ ->
                // Control/Monitoring(외부 In): completionTrigger 실패해도 device(rx) 로 Call 완료 금지 — In 만으로.
                //   (Simulation/VP 는 not isExternalIn 이라 기존대로 device rxCompletion fallback 유지.)
                let hasRxWork = SimIndex.rxWorkGuids index callGuid |> List.isEmpty |> not
                not isExternalIn && hasRxWork && legacyRxCompletion index state callGuid

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
