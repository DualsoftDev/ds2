namespace Ds2.Runtime.Engine.Passive

open System
open System.Collections.Generic
open Ds2.Core

module internal PassiveInferenceWorkCycle =
    let private resolveWorkName (ctx: PassiveWorkContext) workGuid =
        ctx.Index.WorkName
        |> Map.tryFind workGuid
        |> Option.defaultValue (string workGuid)

    let private resolveCanonicalCallGuid (ctx: PassiveWorkContext) callGuid =
        ctx.Index.CallCanonicalGuids
        |> Map.tryFind callGuid
        |> Option.defaultValue callGuid

    let private familyAddressKey dir address = $"{dir}|{address}"

    let private getRequiredPassiveCycleMatchCount (ctx: PassiveWorkContext) =
        if ctx.RuntimeMode = RuntimeMode.Monitoring then 3 else 2

    let private resolvePassiveLearningLogPrefix (ctx: PassiveWorkContext) =
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

    let private tryLogMonitoringGapHint
        (ctx: PassiveWorkContext)
        workGuid
        tailIdx
        headIdx
        gapTicks =
        ctx.AddLog
            PassiveInferenceLogKind.System
            (sprintf
                "[Mon] %s provisional gap head=%d tail=%d ticks=%d"
                (resolveWorkName ctx workGuid)
                headIdx
                tailIdx
                gapTicks)

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

    let private resolveWorkPositiveFamilyToken (ctx: PassiveWorkContext) workGuid address isOut =
        match ctx.WorkPositiveFamilyTokens.TryGetValue(workGuid) with
        | true, tokenMap ->
            let key = familyAddressKey (if isOut then "Out" else "In") address
            match tokenMap.TryGetValue(key) with
            | true, token -> Some token
            | _ -> None
        | _ -> None

    let appendToWorkSequence (wl: WorkLearning) dirVal token observedTick =
        match wl.LearningCurrentKey with
        | Some current when current = dirVal && wl.Sequence.Count > 0 ->
            let items =
                HashSet<string>(
                    wl.Sequence[wl.Sequence.Count - 1].Split([|'|'|], StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.Ordinal)

            items.Add(token) |> ignore
            wl.Sequence[wl.Sequence.Count - 1] <-
                items
                |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                |> String.concat "|"
            wl.GroupEndTicks[wl.GroupEndTicks.Count - 1] <- observedTick
            false
        | _ ->
            wl.Sequence.Add(token)
            wl.GroupKeys.Add(dirVal)
            wl.GroupStartTicks.Add(observedTick)
            wl.GroupEndTicks.Add(observedTick)
            wl.LearningCurrentKey <- Some dirVal
            true

    let private tryUpdateMonitoringGapHint
        (ctx: PassiveWorkContext)
        workGuid
        (wl: WorkLearning) =
        if ctx.RuntimeMode = RuntimeMode.Monitoring && not wl.Synced && wl.GroupStartTicks.Count >= 2 then
            let headIdx = wl.GroupStartTicks.Count - 1
            let tailIdx = headIdx - 1
            let headDir, headValue = wl.GroupKeys[headIdx]
            let gapTicks = wl.GroupStartTicks[headIdx] - wl.GroupEndTicks[tailIdx]

            if gapTicks > wl.LargestGapTicks && headDir = "Out" && headValue = "true" then
                wl.LargestGapTicks <- gapTicks
                wl.ProvisionalHeadGroupIdx <- Some headIdx
                wl.ProvisionalTailGroupIdx <- Some tailIdx
                tryLogMonitoringGapHint ctx workGuid tailIdx headIdx gapTicks

    let private setPositiveFamilyToken
        (tokenMap: Dictionary<string, string>)
        dir
        address
        (ownerOrdinals: seq<int>) =
        let ordinals =
            ownerOrdinals
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toArray

        if ordinals.Length > 0 then
            tokenMap[familyAddressKey dir address] <- sprintf "%s#%s" dir (String.Join(",", ordinals |> Array.map string))

    let computeWorkUniqueAddresses (ctx: PassiveWorkContext) =
        ctx.WorkUniqueAddresses.Clear()

        let workAllAddrs = Dictionary<Guid, HashSet<string>>()
        for mapping in ctx.IoMap.Mappings do
            match ctx.Index.CallWorkGuid |> Map.tryFind mapping.CallGuid with
            | Some workGuid ->
                let set =
                    match workAllAddrs.TryGetValue(workGuid) with
                    | true, existing -> existing
                    | _ ->
                        let created = HashSet<string>(StringComparer.Ordinal)
                        workAllAddrs[workGuid] <- created
                        created

                if not (String.IsNullOrEmpty(mapping.OutAddress)) then
                    set.Add(mapping.OutAddress) |> ignore
                if not (String.IsNullOrEmpty(mapping.InAddress)) then
                    set.Add(mapping.InAddress) |> ignore
            | None -> ()

        for KeyValue(workGuid, addrs) in workAllAddrs do
            let otherAddrs = HashSet<string>(StringComparer.Ordinal)
            for KeyValue(otherWorkGuid, otherSet) in workAllAddrs do
                if otherWorkGuid <> workGuid then
                    otherAddrs.UnionWith(otherSet)

            let unique = HashSet<string>(addrs, StringComparer.Ordinal)
            unique.ExceptWith(otherAddrs)
            ctx.WorkUniqueAddresses[workGuid] <- unique

            let workName = resolveWorkName ctx workGuid
            ctx.AddLog PassiveInferenceLogKind.System (sprintf "[%s 학습] %s 고유 주소 %d개 (전체 %d개 중)" (resolvePassiveLearningLogPrefix ctx) workName unique.Count addrs.Count)
            if unique.Count = 0 then
                ctx.AddLog PassiveInferenceLogKind.Warn (sprintf "[%s 학습] %s 고유 주소 없음 — 사이클 감지 불가 (완전 공유 케이스)" (resolvePassiveLearningLogPrefix ctx) workName)

    let computeWorkPositiveFamilyTokens (ctx: PassiveWorkContext) =
        ctx.WorkPositiveFamilyTokens.Clear()

        for KeyValue(workGuid, uniqueAddresses) in ctx.WorkUniqueAddresses do
            match ctx.Index.WorkCallGuids |> Map.tryFind workGuid with
            | None -> ()
            | Some workCalls ->
                let canonicalOrder = Dictionary<Guid, int>()
                let mutable nextOrdinal = 0
                for callGuid in workCalls do
                    let canonicalCallGuid = resolveCanonicalCallGuid ctx callGuid
                    if not (canonicalOrder.ContainsKey(canonicalCallGuid)) then
                        canonicalOrder[canonicalCallGuid] <- nextOrdinal
                        nextOrdinal <- nextOrdinal + 1

                let tokenMap = Dictionary<string, string>(StringComparer.Ordinal)
                let workMappings =
                    ctx.IoMap.Mappings
                    |> List.filter (fun mapping ->
                        match ctx.Index.CallWorkGuid |> Map.tryFind mapping.CallGuid with
                        | Some wg -> wg = workGuid
                        | None -> false)

                for address in uniqueAddresses do
                    setPositiveFamilyToken
                        tokenMap
                        "Out"
                        address
                        (workMappings
                         |> Seq.filter (fun mapping -> mapping.OutAddress = address)
                         |> Seq.map (fun mapping -> resolveCanonicalCallGuid ctx mapping.CallGuid)
                         |> Seq.filter (fun canonicalGuid -> canonicalOrder.ContainsKey(canonicalGuid))
                         |> Seq.map (fun canonicalGuid -> canonicalOrder[canonicalGuid]))

                    setPositiveFamilyToken
                        tokenMap
                        "In"
                        address
                        (workMappings
                         |> Seq.filter (fun mapping -> mapping.InAddress = address)
                         |> Seq.map (fun mapping -> resolveCanonicalCallGuid ctx mapping.CallGuid)
                         |> Seq.filter (fun canonicalGuid -> canonicalOrder.ContainsKey(canonicalGuid))
                         |> Seq.map (fun canonicalGuid -> canonicalOrder[canonicalGuid]))

                if tokenMap.Count > 0 then
                    ctx.WorkPositiveFamilyTokens[workGuid] <- tokenMap

    let buildPassiveResetTargetsByPred (ctx: PassiveWorkContext) =
        ctx.WorkResetTargetsByPred.Clear()

        for KeyValue(targetGuid, predGuids) in ctx.Index.WorkResetPreds do
            for predGuid in predGuids |> Seq.distinct do
                let targets =
                    match ctx.WorkResetTargetsByPred.TryGetValue(predGuid) with
                    | true, existing -> existing
                    | _ ->
                        let created = ResizeArray<Guid>()
                        ctx.WorkResetTargetsByPred[predGuid] <- created
                        created

                if not (targets.Contains(targetGuid)) then
                    targets.Add(targetGuid)

    let getOrCreateWorkLearning (ctx: PassiveWorkContext) workGuid =
        match ctx.WorkLearning.TryGetValue(workGuid) with
        | true, wl -> wl
        | _ ->
            let wl = WorkLearning()
            ctx.WorkLearning[workGuid] <- wl
            wl

    let observePositiveWorkSignal
        (ctx: PassiveWorkContext)
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        address
        isOut
        observedTick =
        let dirVal = (if isOut then "Out" else "In"), "true"
        for KeyValue(workGuid, uniqueAddresses) in ctx.WorkUniqueAddresses do
            if uniqueAddresses.Contains(address) then
                let token =
                    resolveWorkPositiveFamilyToken ctx workGuid address isOut
                    |> Option.defaultValue address

                let wl = getOrCreateWorkLearning ctx workGuid
                if not wl.Synced then
                    let createdGroup = appendToWorkSequence wl dirVal token observedTick
                    if createdGroup then
                        tryUpdateMonitoringGapHint ctx workGuid wl
                    detectWorkPeriod ctx workGuid wl
                else
                    observeSyncedWorkGroup ctx actions overlay workGuid wl dirVal token
