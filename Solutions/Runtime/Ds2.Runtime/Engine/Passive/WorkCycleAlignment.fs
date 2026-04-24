namespace Ds2.Runtime.Engine.Passive

open System
open System.Collections.Generic
open Ds2.Core

module internal PassiveInferenceWorkCycleAlignment =
    let resolveWorkName (ctx: PassiveWorkContext) workGuid =
        ctx.Index.WorkName
        |> Map.tryFind workGuid
        |> Option.defaultValue (string workGuid)

    let resolveCanonicalCallGuid (ctx: PassiveWorkContext) callGuid =
        ctx.Index.CallCanonicalGuids
        |> Map.tryFind callGuid
        |> Option.defaultValue callGuid

    let familyAddressKey dir address = $"{dir}|{address}"

    let getRequiredPassiveCycleMatchCount (ctx: PassiveWorkContext) =
        if ctx.RuntimeMode = RuntimeMode.Monitoring then 3 else 2

    let resolvePassiveLearningLogPrefix (ctx: PassiveWorkContext) =
        match ctx.RuntimeMode with
        | RuntimeMode.Monitoring -> "Mon"
        | RuntimeMode.VirtualPlant -> "VP"
        | _ -> "Passive"

    let private rotateResizeArray (items: ResizeArray<'T>) startIdx =
        if items.Count = 0 || startIdx <= 0 then
            ResizeArray<'T>(items)
        else
            ResizeArray<'T>(Seq.append (items |> Seq.skip startIdx) (items |> Seq.take startIdx))

    let private rotateCycleToCanonicalStart (wl: WorkLearning) period =
        match wl.WorkGoingStartGroupIdx with
        | Some rotationOffset when period > 0 && rotationOffset > 0 ->
            let rotatedSeq = rotateResizeArray wl.CycleSequence rotationOffset
            let rotatedKeys = rotateResizeArray wl.CycleGroupKeys rotationOffset
            wl.CycleSequence.Clear()
            wl.CycleGroupKeys.Clear()
            rotatedSeq |> Seq.iter wl.CycleSequence.Add
            rotatedKeys |> Seq.iter wl.CycleGroupKeys.Add
            wl.NextExpectedGroupIdx <- (wl.NextExpectedGroupIdx - rotationOffset + period) % period
            wl.WorkFinishGroupIdx <-
                wl.WorkFinishGroupIdx
                |> Option.map (fun finishIdx -> (finishIdx - rotationOffset + period) % period)
            wl.WorkGoingStartGroupIdx <- Some 0
        | _ -> ()

    let private tryGetRelativeProvisionalHeadIdx start period requiredMatches (wl: WorkLearning) =
        match wl.ProvisionalHeadGroupIdx with
        | Some headIdx when period > 0 && headIdx >= start && headIdx < start + (period * requiredMatches) ->
            Some ((headIdx - start) % period)
        | _ -> None

    let private tryGetGapBeforeGroupIdx start period repeatIdx relIdx (wl: WorkLearning) =
        let absoluteIdx = start + (repeatIdx * period) + relIdx
        if absoluteIdx > start && absoluteIdx < wl.GroupStartTicks.Count && absoluteIdx - 1 < wl.GroupEndTicks.Count then
            Some (wl.GroupStartTicks[absoluteIdx] - wl.GroupEndTicks[absoluteIdx - 1])
        else
            None

    let private tryGetRotatedFinishInfo start period headRelIdx (wl: WorkLearning) =
        let mutable finishRotIdx = -1

        for rotIdx in 0 .. period - 1 do
            let dir, value = wl.GroupKeys[start + ((headRelIdx + rotIdx) % period)]
            if dir = "In" && value = "true" then
                finishRotIdx <- rotIdx

        if finishRotIdx < 0 then
            None
        else
            let mutable outAfterFinish = 0
            for rotIdx in finishRotIdx + 1 .. period - 1 do
                let dir, value = wl.GroupKeys[start + ((headRelIdx + rotIdx) % period)]
                if dir = "Out" && value = "true" then
                    outAfterFinish <- outAfterFinish + 1

            let finishOriginalIdx = (headRelIdx + finishRotIdx) % period
            Some (finishOriginalIdx, finishRotIdx, outAfterFinish)

    let private tryResolveMonitoringGapScoredCycleShape
        (ctx: PassiveWorkContext)
        workGuid
        start
        period
        requiredMatches
        (wl: WorkLearning)
        fallbackStartIdx
        fallbackFinishIdx =
        if period <= 0 then
            fallbackStartIdx, fallbackFinishIdx
        else
            let provisionalRelIdx = tryGetRelativeProvisionalHeadIdx start period requiredMatches wl
            let mutable bestHeadIdx = fallbackStartIdx
            let mutable bestFinishIdx = fallbackFinishIdx
            let mutable bestFinishRotIdx =
                if fallbackFinishIdx >= 0 then
                    (fallbackFinishIdx - fallbackStartIdx + period) % period
                else
                    -1
            let mutable bestOutAfterFinish = Int32.MaxValue
            let mutable bestGap = Int64.MinValue
            let mutable bestProvisional = false
            let mutable foundCandidate = false

            for relIdx in 0 .. period - 1 do
                let dir, value = wl.GroupKeys[start + relIdx]
                if dir = "Out" && value = "true" then
                    match tryGetRotatedFinishInfo start period relIdx wl with
                    | Some (finishOriginalIdx, finishRotIdx, outAfterFinish) ->
                        let gapSamples =
                            [|
                                for repeatIdx in 0 .. requiredMatches - 1 do
                                    match tryGetGapBeforeGroupIdx start period repeatIdx relIdx wl with
                                    | Some gap -> yield gap
                                    | None -> ()
                            |]

                        let avgGap =
                            if gapSamples.Length = 0 then 0L
                            else gapSamples |> Array.averageBy float |> int64

                        let matchesProvisional =
                            match provisionalRelIdx with
                            | Some idx -> idx = relIdx
                            | None -> false

                        if
                            not foundCandidate
                            || outAfterFinish < bestOutAfterFinish
                            || (outAfterFinish = bestOutAfterFinish && finishRotIdx > bestFinishRotIdx)
                            || (outAfterFinish = bestOutAfterFinish && finishRotIdx = bestFinishRotIdx && avgGap > bestGap)
                            || (outAfterFinish = bestOutAfterFinish && finishRotIdx = bestFinishRotIdx && avgGap = bestGap && matchesProvisional && not bestProvisional)
                            || (outAfterFinish = bestOutAfterFinish && finishRotIdx = bestFinishRotIdx && avgGap = bestGap && matchesProvisional = bestProvisional && relIdx = fallbackStartIdx && bestHeadIdx <> fallbackStartIdx)
                        then
                            foundCandidate <- true
                            bestHeadIdx <- relIdx
                            bestFinishIdx <- finishOriginalIdx
                            bestFinishRotIdx <- finishRotIdx
                            bestOutAfterFinish <- outAfterFinish
                            bestGap <- avgGap
                            bestProvisional <- matchesProvisional
                    | None -> ()

            if foundCandidate then
                ctx.AddLog
                    PassiveInferenceLogKind.System
                    (sprintf
                        "[Mon] %s gap-scored head=%d finish=%d fallback=%d avgGap=%d outAfterFinish=%d provisional=%s"
                        (resolveWorkName ctx workGuid)
                        bestHeadIdx
                        bestFinishRotIdx
                        fallbackStartIdx
                        bestGap
                        bestOutAfterFinish
                        (if bestProvisional then "yes" else "no"))
                bestHeadIdx, bestFinishIdx
            else
                fallbackStartIdx, fallbackFinishIdx

    let tryLogMonitoringGapHint
        (ctx: PassiveWorkContext)
        workGuid
        tailIdx
        headIdx
        (gapTicks: int64) =
        ctx.AddLog
            PassiveInferenceLogKind.System
            (sprintf
                "[Mon] %s provisional gap head=%d tail=%d ticks=%d"
                (resolveWorkName ctx workGuid)
                headIdx
                tailIdx
                gapTicks)

    let detectWorkPeriod (ctx: PassiveWorkContext) workGuid (wl: WorkLearning) =
        if not wl.Synced then
            let completedCount = wl.Sequence.Count
            if completedCount >= 2 then
                let requiredMatches = getRequiredPassiveCycleMatchCount ctx
                let mutable fixedCycle = false
                let mutable period = 1
                while not fixedCycle && period <= completedCount / requiredMatches do
                    let mutable start = 0
                    while not fixedCycle && start + (period * requiredMatches) <= completedCount do
                        let mutable matched = true
                        let mutable repeatIdx = 1
                        while matched && repeatIdx < requiredMatches do
                            let mutable i = 0
                            while matched && i < period do
                                if wl.Sequence[start + i] <> wl.Sequence[start + (repeatIdx * period) + i] then
                                    matched <- false
                                i <- i + 1
                            repeatIdx <- repeatIdx + 1

                        if matched then
                            wl.DetectedPeriod <- Some period
                            wl.CycleSequence.Clear()
                            wl.CycleGroupKeys.Clear()
                            wl.Sequence |> Seq.skip start |> Seq.take period |> Seq.iter wl.CycleSequence.Add
                            wl.GroupKeys |> Seq.skip start |> Seq.take period |> Seq.iter wl.CycleGroupKeys.Add

                            let mutable workGoingStartIdx = -1
                            let mutable i = 0
                            while i < wl.CycleGroupKeys.Count do
                                let dir, value = wl.CycleGroupKeys[i]
                                if workGoingStartIdx < 0 && dir = "Out" && value = "true" then
                                    workGoingStartIdx <- i
                                i <- i + 1

                            let mutable workFinishIdx = -1
                            let mutable finishSearch = wl.CycleGroupKeys.Count - 1
                            while finishSearch >= 0 && workFinishIdx < 0 do
                                let dir, value = wl.CycleGroupKeys[finishSearch]
                                if dir = "In" && value = "true" then
                                    workFinishIdx <- finishSearch
                                finishSearch <- finishSearch - 1

                            let workGoingStartIdx, workFinishIdx =
                                if ctx.RuntimeMode = RuntimeMode.Monitoring then
                                    tryResolveMonitoringGapScoredCycleShape ctx workGuid start period requiredMatches wl workGoingStartIdx workFinishIdx
                                else
                                    workGoingStartIdx, workFinishIdx

                            wl.WorkFinishGroupIdx <- if workFinishIdx >= 0 then Some workFinishIdx else None
                            wl.WorkGoingStartGroupIdx <- if workGoingStartIdx >= 0 then Some workGoingStartIdx else None
                            wl.Synced <- true
                            wl.NextExpectedGroupIdx <- (completedCount - start) % period
                            rotateCycleToCanonicalStart wl period
                            wl.LiveCurrentKey <- None
                            wl.LiveCurrentTokens.Clear()
                            wl.HasObservedSyncedGoing <- false
                            wl.ProvisionalHeadGroupIdx <- None
                            wl.ProvisionalTailGroupIdx <- None
                            wl.LargestGapTicks <- 0L

                            let seqStr = wl.CycleSequence |> String.concat " | "
                            ctx.AddLog
                                PassiveInferenceLogKind.System
                                (sprintf
                                    "[%s] %s cycle fixed groups=%d, matches=%d, GoingStart=%s, Finish=%s / Seq[0..%d]=%s"
                                    (resolvePassiveLearningLogPrefix ctx)
                                    (resolveWorkName ctx workGuid)
                                    period
                                    requiredMatches
                                    (wl.WorkGoingStartGroupIdx |> Option.map string |> Option.defaultValue "none")
                                    (wl.WorkFinishGroupIdx |> Option.map string |> Option.defaultValue "none")
                                    (period - 1)
                                    seqStr)
                            fixedCycle <- true

                        start <- start + 1
                    period <- period + 1
