namespace Ds2.Runtime.Engine.Passive

open System
open System.Collections.Generic
open Ds2.Core

module internal PassiveInferenceWorkCycle =
    let enqueueWorkState = PassiveInferenceWorkCycleState.enqueueWorkState
    let enqueueCallState = PassiveInferenceWorkCycleState.enqueueCallState

    let private resolveWorkPositiveFamilyToken (ctx: PassiveWorkContext) workGuid address isOut =
        match ctx.WorkPositiveFamilyTokens.TryGetValue(workGuid) with
        | true, tokenMap ->
            let key = PassiveInferenceWorkCycleAlignment.familyAddressKey (if isOut then "Out" else "In") address
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
                PassiveInferenceWorkCycleAlignment.tryLogMonitoringGapHint ctx workGuid tailIdx headIdx gapTicks

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
            tokenMap[PassiveInferenceWorkCycleAlignment.familyAddressKey dir address] <- sprintf "%s#%s" dir (String.Join(",", ordinals |> Array.map string))

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

            let workName = PassiveInferenceWorkCycleAlignment.resolveWorkName ctx workGuid
            ctx.AddLog PassiveInferenceLogKind.System (sprintf "[%s 학습] %s 고유 주소 %d개 (전체 %d개 중)" (PassiveInferenceWorkCycleAlignment.resolvePassiveLearningLogPrefix ctx) workName unique.Count addrs.Count)
            if unique.Count = 0 then
                ctx.AddLog PassiveInferenceLogKind.Warn (sprintf "[%s 학습] %s 고유 주소 없음 -> 사이클 감지 불가 (완전 공유 케이스)" (PassiveInferenceWorkCycleAlignment.resolvePassiveLearningLogPrefix ctx) workName)

    let computeWorkPositiveFamilyTokens (ctx: PassiveWorkContext) =
        ctx.WorkPositiveFamilyTokens.Clear()

        for KeyValue(workGuid, uniqueAddresses) in ctx.WorkUniqueAddresses do
            match ctx.Index.WorkCallGuids |> Map.tryFind workGuid with
            | None -> ()
            | Some workCalls ->
                let canonicalOrder = Dictionary<Guid, int>()
                let mutable nextOrdinal = 0
                for callGuid in workCalls do
                    let canonicalCallGuid = PassiveInferenceWorkCycleAlignment.resolveCanonicalCallGuid ctx callGuid
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
                         |> Seq.map (fun mapping -> PassiveInferenceWorkCycleAlignment.resolveCanonicalCallGuid ctx mapping.CallGuid)
                         |> Seq.filter (fun canonicalGuid -> canonicalOrder.ContainsKey(canonicalGuid))
                         |> Seq.map (fun canonicalGuid -> canonicalOrder[canonicalGuid]))

                    setPositiveFamilyToken
                        tokenMap
                        "In"
                        address
                        (workMappings
                         |> Seq.filter (fun mapping -> mapping.InAddress = address)
                         |> Seq.map (fun mapping -> PassiveInferenceWorkCycleAlignment.resolveCanonicalCallGuid ctx mapping.CallGuid)
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
                    PassiveInferenceWorkCycleAlignment.detectWorkPeriod ctx workGuid wl
                else
                    PassiveInferenceWorkCycleState.observeSyncedWorkGroup ctx actions overlay workGuid wl dirVal token
