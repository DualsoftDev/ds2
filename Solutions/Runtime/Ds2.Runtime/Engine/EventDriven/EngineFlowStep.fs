namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core

module internal EngineFlowStep =

    type FlowContext = {
        Index: SimIndex
        StateManager: StateManager
        DurationTracker: DurationTracker
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
        GetState: unit -> SimState
        CurrentTimeMs: unit -> int64
        SetAllFlowStates: FlowTag -> unit
        AdvanceStepRuntime: int64 -> unit
        NextEventTime: unit -> int64 option
    }

    let private stepSnapshot (ctx: StepBoundaryContext) =
        let state = ctx.GetState()
        state.WorkStates, state.CallStates, state.WorkTokens, state.CompletedTokens, ctx.CurrentTimeMs()

    let private advanceUntilStepBoundary (ctx: StepBoundaryContext) before =
        let mutable progressed = false
        let mutable guard = 0

        while not progressed && guard < 256 do
            match ctx.NextEventTime() with
            | Some nextEventTime ->
                guard <- guard + 1
                ctx.AdvanceStepRuntime nextEventTime
                progressed <- stepSnapshot ctx <> before
            | None ->
                guard <- 256

        progressed

    let runStepUntilBoundary (ctx: StepBoundaryContext) =
        let before = stepSnapshot ctx

        ctx.SetAllFlowStates FlowTag.Drive
        ctx.AdvanceStepRuntime (ctx.CurrentTimeMs())

        let progressed =
            if stepSnapshot ctx <> before then true
            else advanceUntilStepBoundary ctx before

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

    let stepWithSourcePriming (ctx: StepContext) selectedSourceGuid autoStartSources =
        if ctx.HasGoingCall() then
            false
        else
            let hasEngineProgress = ctx.HasStartableWork() || ctx.HasActiveDuration()
            if not hasEngineProgress then
                primeStepSources ctx selectedSourceGuid autoStartSources |> ignore

            if ctx.HasStartableWork() || ctx.HasActiveDuration() then
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
