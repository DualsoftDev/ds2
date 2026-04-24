namespace Ds2.Runtime.Engine.Passive

open System
open System.Collections.Generic
open Ds2.Core

module internal PassiveInferenceWorkCycleState =
    let private isCircularInclusive value startIdx endIdx period =
        if period <= 0 then
            false
        elif startIdx <= endIdx then
            value >= startIdx && value <= endIdx
        else
            value >= startIdx || value <= endIdx

    let private shouldHoldFinishForGroup (wl: WorkLearning) groupIdx period =
        match wl.WorkFinishGroupIdx with
        | None -> false
        | Some finishIdx ->
            match wl.WorkGoingStartGroupIdx with
            | None -> groupIdx >= finishIdx
            | Some goingStartIdx ->
                let finishTailEnd = (goingStartIdx - 1 + period) % period
                isCircularInclusive groupIdx finishIdx finishTailEnd period

    let enqueueWorkState
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        workGuid
        state =
        if overlay.GetWorkState(workGuid) <> state then
            actions.Add({
                TargetKind = PassiveInferenceTarget.Work
                TargetGuid = workGuid
                State = state
            })
            overlay.SetWorkState(workGuid, state)

    let enqueueCallState
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        callGuid
        state =
        if overlay.GetCallState(callGuid) <> state then
            actions.Add({
                TargetKind = PassiveInferenceTarget.Call
                TargetGuid = callGuid
                State = state
            })
            overlay.SetCallState(callGuid, state)

    let rec private applyPassiveResetTargets
        (ctx: PassiveWorkContext)
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        predWorkGuid =
        match ctx.WorkResetTargetsByPred.TryGetValue(predWorkGuid) with
        | true, targets ->
            for targetWorkGuid in targets do
                if targetWorkGuid <> predWorkGuid && overlay.GetWorkState(targetWorkGuid) = Status4.Finish then
                    enqueueWorkState actions overlay targetWorkGuid Status4.Ready
                    match ctx.Index.WorkCallGuids |> Map.tryFind targetWorkGuid with
                    | Some workCalls ->
                        for callGuid in workCalls do
                            match ctx.CallOutHighAddresses.TryGetValue(callGuid) with
                            | true, outHigh -> outHigh.Clear()
                            | _ -> ()

                            match ctx.CallInHighAddresses.TryGetValue(callGuid) with
                            | true, inHigh -> inHigh.Clear()
                            | _ -> ()

                            if overlay.GetCallState(callGuid) <> Status4.Ready then
                                enqueueCallState actions overlay callGuid Status4.Ready
                    | None -> ()
        | _ -> ()

    let private applyWorkStateForExpectedGroup
        (ctx: PassiveWorkContext)
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        workGuid
        (wl: WorkLearning) =
        match wl.DetectedPeriod with
        | Some period when period > 0 ->
            let groupIdx = wl.NextExpectedGroupIdx
            if groupIdx >= 0 && groupIdx < period then
                let currentState = overlay.GetWorkState(workGuid)
                let nextState =
                    if shouldHoldFinishForGroup wl groupIdx period then Status4.Finish
                    else Status4.Going

                let shouldSuppressMonitoringFinish =
                    ctx.RuntimeMode = RuntimeMode.Monitoring
                    && nextState = Status4.Finish
                    && not wl.HasObservedSyncedGoing
                    && currentState <> Status4.Going
                    && currentState <> Status4.Finish

                if not shouldSuppressMonitoringFinish && currentState <> nextState then
                    enqueueWorkState actions overlay workGuid nextState
                    if nextState = Status4.Going then
                        wl.HasObservedSyncedGoing <- true
                        applyPassiveResetTargets ctx actions overlay workGuid
        | _ -> ()

    let private finalizeObservedWorkGroup (wl: WorkLearning) =
        match wl.DetectedPeriod with
        | Some period when period > 0 && wl.LiveCurrentTokens.Count > 0 ->
            let actual =
                wl.LiveCurrentTokens
                |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                |> String.concat "|"

            if wl.NextExpectedGroupIdx < period && wl.CycleSequence[wl.NextExpectedGroupIdx] = actual then
                wl.NextExpectedGroupIdx <- (wl.NextExpectedGroupIdx + 1) % period
            else
                let mutable matchedIdx = -1
                let mutable idx = 0
                while idx < period && matchedIdx < 0 do
                    if wl.CycleSequence[idx] = actual then
                        matchedIdx <- idx
                    idx <- idx + 1

                wl.NextExpectedGroupIdx <-
                    if matchedIdx >= 0 then (matchedIdx + 1) % period
                    else 0
        | _ -> ()

    let observeSyncedWorkGroup
        (ctx: PassiveWorkContext)
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        workGuid
        (wl: WorkLearning)
        dirVal
        token =
        if wl.LiveCurrentKey = Some dirVal then
            wl.LiveCurrentTokens.Add(token) |> ignore
        else
            if wl.LiveCurrentKey.IsSome then
                finalizeObservedWorkGroup wl

            wl.LiveCurrentKey <- Some dirVal
            wl.LiveCurrentTokens.Clear()
            wl.LiveCurrentTokens.Add(token) |> ignore
            applyWorkStateForExpectedGroup ctx actions overlay workGuid wl
