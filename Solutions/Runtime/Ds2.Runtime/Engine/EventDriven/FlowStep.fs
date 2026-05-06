namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Scheduler

module internal EngineFlowStep =

    /// TimeIgnore가 켜지는 순간 (Running 중 + 직전 false) 호출.
    /// 진행 중인 duration/homing을 즉시 완료시키도록 재스케줄.
    let rescheduleOnTimeIgnoreEnabled
        (index: SimIndex)
        (stateManager: StateManager)
        (scheduleDurationCheck: ScheduledEventType -> unit)
        (scheduleHomingCompletion: ScheduledEventType -> unit) =
        for workGuid in index.AllWorkGuids do
            match stateManager.GetWorkState(workGuid) with
            | Status4.Going when not (stateManager.IsMinDurationMet(workGuid)) ->
                scheduleDurationCheck (ScheduledEventType.DurationComplete workGuid)
            | Status4.Homing ->
                scheduleHomingCompletion (ScheduledEventType.HomingComplete workGuid)
            | _ -> ()

    type FlowContext = {
        Index: SimIndex
        StateManager: StateManager
        DurationTracker: DurationTracker
        SyncCurrentTime: unit -> unit
        ScheduleConditionEvaluation: unit -> unit
    }

    let private activeFlowGuids (index: SimIndex) =
        index.AllFlowGuids
        |> List.filter (fun flowGuid ->
            index.AllWorkGuids
            |> List.exists (fun workGuid ->
                index.WorkFlowGuid |> Map.tryFind workGuid = Some flowGuid
                && index.WorkSystemName |> Map.tryFind workGuid
                   |> Option.map (fun systemName -> index.ActiveSystemNames.Contains(systemName))
                   |> Option.defaultValue false))

    let setAllFlowStates (ctx: FlowContext) tag =
        match tag with
        | FlowTag.Pause ->
            ctx.SyncCurrentTime()

            for flowGuid in activeFlowGuids ctx.Index do
                ctx.StateManager.SetFlowState(flowGuid, FlowTag.Pause)

            let goingWorkGuids =
                ctx.Index.AllWorkGuids
                |> List.filter (fun workGuid -> ctx.StateManager.GetWorkState(workGuid) = Status4.Going)

            ctx.DurationTracker.SavePausedDurations(
                goingWorkGuids,
                ctx.StateManager.GetCallState,
                ctx.StateManager.GetFlowState,
                ctx.Index.WorkCallGuids,
                ctx.Index.WorkFlowGuid)
        | FlowTag.Drive ->
            for flowGuid in activeFlowGuids ctx.Index do
                ctx.StateManager.SetFlowState(flowGuid, FlowTag.Drive)
            ctx.DurationTracker.ResumePausedDurations(ctx.StateManager.GetWorkState)
        | _ ->
            ctx.StateManager.SetAllFlowStates(tag)

        ctx.ScheduleConditionEvaluation()

    let hasStartableWork (index: SimIndex) (stateManager: StateManager) (canStartWork: Guid -> bool) (isWorkFrozen: Guid -> bool) =
        let hasStartableReadyWork =
            index.AllWorkGuids
            |> List.exists (fun workGuid ->
                stateManager.GetWorkState(workGuid) = Status4.Ready
                && canStartWork workGuid)

        let hasGoingWorkWithPendingCalls =
            index.AllWorkGuids
            |> List.exists (fun workGuid ->
                stateManager.GetWorkState(workGuid) = Status4.Going
                && not (isWorkFrozen workGuid)
                && (
                    let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
                    not callGuids.IsEmpty
                    && callGuids |> List.exists (fun callGuid -> stateManager.GetCallState(callGuid) <> Status4.Finish)
                ))

        hasStartableReadyWork || hasGoingWorkWithPendingCalls

    let hasActiveDuration (index: SimIndex) (stateManager: StateManager) (isWorkFrozen: Guid -> bool) =
        index.AllWorkGuids
        |> List.exists (fun workGuid ->
            stateManager.GetWorkState(workGuid) = Status4.Going
            && not (isWorkFrozen workGuid)
            && (
                let callGuids = SimIndex.findOrEmpty workGuid index.WorkCallGuids
                callGuids.IsEmpty
                || callGuids |> List.forall (fun callGuid -> stateManager.GetCallState(callGuid) = Status4.Finish)
            ))

    type StepBoundaryContext = {
        Index: SimIndex
        GetState: unit -> SimState
        CurrentTimeMs: unit -> int64
        SetAllFlowStates: FlowTag -> unit
        AdvanceStepRuntime: int64 -> unit
        NextEventTime: unit -> int64 option
    }

    let private stepSnapshot (ctx: StepBoundaryContext) =
        let state = ctx.GetState()
        state.WorkStates, state.CallStates, state.WorkTokens, state.CompletedTokens, ctx.CurrentTimeMs()

    /// 이번 STEP 의 묶음 (batch) 정의:
    ///   - Going Call guids (Call 단위 진행)
    ///   - 자식 Call 이 모두 Finish 인 Going Work guids (leaf Work 또는 self-duration phase)
    /// 자식 Call 아직 진행 중인 Going Work 는 제외 — 자식 Call 들이 batch 의 단위가 됨.
    /// 사용자 예시: Work1 안 Call1->{Call2,Call3}->Call4
    ///   Step1: batch={Call1}, Step2: batch={Call2,Call3}, Step3: batch={Call4}.
    let goingBatchGuids (ctx: StepBoundaryContext) =
        let state = ctx.GetState()
        let goingCalls =
            state.CallStates
            |> Map.toSeq
            |> Seq.choose (fun (g, s) -> if s = Status4.Going then Some g else None)
        let leafLikeGoingWorks =
            state.WorkStates
            |> Map.toSeq
            |> Seq.choose (fun (g, s) ->
                if s <> Status4.Going then None
                else
                    let callGuids = SimIndex.findOrEmpty g ctx.Index.WorkCallGuids
                    if callGuids.IsEmpty
                       || callGuids |> List.forall (fun cg ->
                           state.CallStates |> Map.tryFind cg = Some Status4.Finish)
                    then Some g
                    else None)
        Set.ofSeq (Seq.append goingCalls leafLikeGoingWorks)

    let isAnyBatchStillGoing (ctx: StepBoundaryContext) (batch: Set<Guid>) =
        if Set.isEmpty batch then false
        else
            let state = ctx.GetState()
            batch |> Set.exists (fun g ->
                match state.CallStates |> Map.tryFind g with
                | Some Status4.Going -> true
                | _ ->
                    match state.WorkStates |> Map.tryFind g with
                    | Some Status4.Going -> true
                    | _ -> false)

    /// batch 의 모든 unit 이 finish 될 때까지 nextEventTime 단위로 advance.
    /// cascade 로 새 Call/Work 가 Going 진입해도 batch 에 없으니 다음 STEP 으로 자연 분리.
    let private advanceUntilBatchFinish (ctx: StepBoundaryContext) (batch: Set<Guid>) =
        let mutable timeAdvanced = false
        let mutable guard = 0

        while isAnyBatchStillGoing ctx batch && guard < 256 do
            match ctx.NextEventTime() with
            | Some nextEventTime ->
                guard <- guard + 1
                ctx.AdvanceStepRuntime nextEventTime
                timeAdvanced <- true
            | None ->
                guard <- 256

        timeAdvanced

    let runStepUntilBoundary (ctx: StepBoundaryContext) =
        let before = stepSnapshot ctx

        ctx.SetAllFlowStates FlowTag.Drive
        // 1) cascade events (zero-time) 처리 — Ready→Going 전이 등 같은 sim time 진행.
        ctx.AdvanceStepRuntime (ctx.CurrentTimeMs())

        // 2) cascade 후 batch 잡음 (Going Call + leaf-like Going Work).
        let batch = goingBatchGuids ctx

        // 3) batch 의 모든 unit 이 finish 될 때까지 advance.
        let timeAdvanced = advanceUntilBatchFinish ctx batch
        let progressed = timeAdvanced || stepSnapshot ctx <> before

        ctx.SetAllFlowStates FlowTag.Pause
        progressed

    type StepContext = {
        Index: SimIndex
        StateManager: StateManager
        HasStartableWork: unit -> bool
        HasActiveDuration: unit -> bool
        HasGoingCall: unit -> bool
        NextToken: unit -> TokenValue
        SeedToken: Guid -> TokenValue -> unit
        ForceWorkState: Guid -> Status4 -> unit
        RunStepUntilBoundary: unit -> bool
    }

    let canAdvanceStep (ctx: StepContext) selectedSourceGuid autoStartSources =
        StepSemantics.canAdvanceStep
            ctx.Index
            (ctx.StateManager.GetState())
            ctx.StateManager.GetWorkState
            (ctx.HasStartableWork())
            (ctx.HasActiveDuration())
            (ctx.HasGoingCall())
            autoStartSources
            selectedSourceGuid

    let private primeStepSources (ctx: StepContext) selectedSourceGuid autoStartSources =
        let sourceGuids =
            StepSemantics.primableSourceGuids
                ctx.Index
                (ctx.StateManager.GetState())
                ctx.StateManager.GetWorkState
                autoStartSources
                selectedSourceGuid

        for sourceGuid in sourceGuids do
            if ctx.StateManager.GetWorkToken(sourceGuid) |> Option.isNone then
                let token = ctx.NextToken()
                ctx.SeedToken sourceGuid token
            ctx.ForceWorkState sourceGuid Status4.Going

        not sourceGuids.IsEmpty

    /// STEP 시작: Drive flow + Source priming + cascade. batch 반환.
    /// C# 측에서 단계적 advance 진행 시 사용 (각 nextEventTime 까지 wait + AdvanceStepRuntime).
    /// 마지막에 endStep 으로 FlowTag.Pause 복원.
    let beginStepBatch
        (boundaryCtx: StepBoundaryContext)
        (stepCtx: StepContext)
        selectedSourceGuid
        autoStartSources =
        boundaryCtx.SetAllFlowStates FlowTag.Drive

        let hasEngineProgress =
            stepCtx.HasGoingCall()
            || stepCtx.HasStartableWork()
            || stepCtx.HasActiveDuration()
        if not hasEngineProgress then
            primeStepSources stepCtx selectedSourceGuid autoStartSources |> ignore

        // cascade events (zero-time) 처리.
        boundaryCtx.AdvanceStepRuntime (boundaryCtx.CurrentTimeMs())

        goingBatchGuids boundaryCtx |> Set.toArray

    let endStep (boundaryCtx: StepBoundaryContext) =
        boundaryCtx.SetAllFlowStates FlowTag.Pause

    let stepWithSourcePriming (ctx: StepContext) selectedSourceGuid autoStartSources =
        let hasEngineProgress =
            ctx.HasGoingCall()
            || ctx.HasStartableWork()
            || ctx.HasActiveDuration()

        if not hasEngineProgress then
            primeStepSources ctx selectedSourceGuid autoStartSources |> ignore

        if ctx.HasGoingCall() || ctx.HasStartableWork() || ctx.HasActiveDuration() then
            ctx.RunStepUntilBoundary()
        else
            false

    type ReloadContext = {
        RemoveScheduledConditionEvents: unit -> unit
        ClearConnectionTransientState: unit -> unit
        ReloadConnections: unit -> SimIndex.ConnectionSnapshot * SimIndex.ConnectionSnapshot
        InvalidateOrphanedGoingWorks: SimIndex.ConnectionSnapshot -> SimIndex.ConnectionSnapshot -> unit
        InvalidateOrphanedTokenHolders: unit -> unit
        ScheduleConditionEvaluation: unit -> unit
    }

    let reloadConnections (ctx: ReloadContext) =
        ctx.RemoveScheduledConditionEvents()
        ctx.ClearConnectionTransientState()
        let previousSnapshot, currentSnapshot = ctx.ReloadConnections()
        ctx.InvalidateOrphanedGoingWorks previousSnapshot currentSnapshot
        ctx.InvalidateOrphanedTokenHolders()
        ctx.ScheduleConditionEvaluation()
