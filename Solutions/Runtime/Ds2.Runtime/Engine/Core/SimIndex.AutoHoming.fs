namespace Ds2.Runtime.Engine.Core

open System

module internal SimIndexAutoHoming =

    type DeviceCall = {
        CallGuid: Guid
        DeviceSystemId: Guid option
        RxWorkGuids: Guid list
    }

    let private buildAncestorDescendant (callGuids: Guid list) (startSuccessors: Map<Guid, Guid list>) : (Guid -> Guid -> bool option) =
        let indexByGuid = callGuids |> List.mapi (fun i guid -> guid, i) |> Map.ofList
        let count = callGuids.Length

        if count = 0 then
            fun _ _ -> None
        else
            let table = Array2D.create<bool> count count false
            let visited = System.Collections.Generic.HashSet<Guid>()

            let rec traverse (callGuid: Guid) ancestors =
                let callIndex = indexByGuid.[callGuid]
                for ancestor in ancestors do
                    table.[indexByGuid.[ancestor], callIndex] <- true

                if visited.Contains callGuid then
                    for ancestor in ancestors do
                        let ancestorIndex = indexByGuid.[ancestor]
                        for descendantIndex = 0 to count - 1 do
                            if table.[callIndex, descendantIndex] then
                                table.[ancestorIndex, descendantIndex] <- true
                else
                    visited.Add(callGuid) |> ignore
                    let successors = startSuccessors |> Map.tryFind callGuid |> Option.defaultValue []
                    for successor in successors do
                        traverse successor (callGuid :: ancestors)

            let hasIncoming = startSuccessors |> Map.toList |> List.collect snd |> Set.ofList
            for initGuid in callGuids |> List.filter (fun guid -> not (hasIncoming.Contains guid)) do
                traverse initGuid []

            fun leftGuid rightGuid ->
                match Map.tryFind leftGuid indexByGuid, Map.tryFind rightGuid indexByGuid with
                | Some leftIndex, Some rightIndex ->
                    if table.[leftIndex, rightIndex] then Some true
                    elif table.[rightIndex, leftIndex] then Some false
                    else None
                | _ -> None

    let private computeCallInitialType (ancestorOf: Guid -> Guid -> bool option) callGuid (partnerGuids: Guid list) =
        if partnerGuids.IsEmpty then None
        else
            let relations = partnerGuids |> List.choose (ancestorOf callGuid)
            if relations.IsEmpty then None
            elif relations |> List.forall id then Some false
            elif relations |> List.forall not then Some true
            else None

    let private buildLocalCallSuccessors callStartPreds callGuids =
        callGuids
        |> List.fold (fun (acc: Map<Guid, Guid list>) callGuid ->
            let preds = callStartPreds |> Map.tryFind callGuid |> Option.defaultValue []
            preds
            |> List.filter (fun predGuid -> callGuids |> List.contains predGuid)
            |> List.fold (fun innerAcc predGuid ->
                let existing = innerAcc |> Map.tryFind predGuid |> Option.defaultValue []
                innerAcc |> Map.add predGuid (callGuid :: existing)) acc) Map.empty

    let private collectCallsByDevice (calls: DeviceCall list) =
        calls
        |> List.choose (fun call -> call.DeviceSystemId |> Option.map (fun systemId -> systemId, call))
        |> List.groupBy fst
        |> List.map (fun (_, pairs) -> pairs |> List.map snd)
        |> List.filter (fun group -> group.Length > 1)

    let private forEachDeviceCallDecision callStartPreds (calls: DeviceCall list) visit =
        if calls.Length >= 2 then
            let callGuids = calls |> List.map (fun call -> call.CallGuid)
            let ancestorOf = buildAncestorDescendant callGuids (buildLocalCallSuccessors callStartPreds callGuids)

            for deviceCalls in collectCallsByDevice calls do
                if deviceCalls.Length >= 2 then
                    for call in deviceCalls do
                        let partnerGuids =
                            deviceCalls
                            |> List.filter (fun peer -> peer.CallGuid <> call.CallGuid)
                            |> List.map (fun peer -> peer.CallGuid)
                        match computeCallInitialType ancestorOf call.CallGuid partnerGuids with
                        | Some isOn -> visit call isOn
                        | None -> ()

    let computeAutoHomingTargets callStartPreds (activeWorkCalls: DeviceCall list list) =
        let votes = System.Collections.Generic.Dictionary<Guid, ResizeArray<bool>>()
        let addVote workGuid isOn =
            match votes.TryGetValue(workGuid) with
            | true, list -> list.Add(isOn)
            | false, _ -> votes.[workGuid] <- ResizeArray([ isOn ])

        for calls in activeWorkCalls do
            forEachDeviceCallDecision callStartPreds calls (fun call isOn ->
                call.RxWorkGuids |> List.iter (fun rxWorkGuid -> addVote rxWorkGuid isOn))

        votes
        |> Seq.choose (fun kv ->
            let allVotes = kv.Value
            if allVotes.Count > 0 && allVotes |> Seq.forall id then Some kv.Key else None)
        |> Set.ofSeq

    let computeAutoHomingPlan callStartPreds (activeWorkCalls: DeviceCall list list) =
        let onVotes = System.Collections.Generic.Dictionary<Guid, ResizeArray<bool>>()
        let offVotes = System.Collections.Generic.Dictionary<Guid, ResizeArray<bool>>()
        let addVote workGuid isOn =
            let target = if isOn then onVotes else offVotes
            match target.TryGetValue(workGuid) with
            | true, list -> list.Add(true)
            | false, _ -> target.[workGuid] <- ResizeArray([ true ])

        for calls in activeWorkCalls do
            forEachDeviceCallDecision callStartPreds calls (fun call isOn ->
                call.RxWorkGuids |> List.iter (fun rxWorkGuid -> addVote rxWorkGuid isOn))

        let onKeys = onVotes |> Seq.choose (fun kv -> if kv.Value.Count > 0 then Some kv.Key else None) |> Set.ofSeq
        let offKeys = offVotes |> Seq.choose (fun kv -> if kv.Value.Count > 0 then Some kv.Key else None) |> Set.ofSeq
        let conflictingKeys = Set.intersect onKeys offKeys
        Set.difference onKeys conflictingKeys, Set.difference offKeys conflictingKeys

    let computeAutoHomingCallPlan callStartPreds (activeWorkCalls: DeviceCall list list) =
        let mutable onCalls = Set.empty<Guid>
        let mutable offCalls = Set.empty<Guid>

        for calls in activeWorkCalls do
            forEachDeviceCallDecision callStartPreds calls (fun call isOn ->
                if isOn then onCalls <- onCalls.Add(call.CallGuid)
                else offCalls <- offCalls.Add(call.CallGuid))

        onCalls, offCalls

    let findHomingEntryPoints
        workNameOf
        (workResetPreds: Map<Guid, Guid list>)
        (workStartPreds: Map<Guid, Guid list>)
        hasApiDef
        (readyTargets: Set<Guid>) =
        let findOrEmpty key map = map |> Map.tryFind key |> Option.defaultValue []
        let mutable entryWorkGuids = []
        let mutable warnings = []

        for readyWorkGuid in readyTargets do
            let resetPreds = findOrEmpty readyWorkGuid workResetPreds
            if resetPreds.IsEmpty then
                warnings <- $"Device Work '{workNameOf readyWorkGuid}'에 Reset predecessor가 없습니다. 수동 IsFinished 설정이 필요합니다." :: warnings
            else
                for entryGuid in resetPreds do
                    let rec findApiDefWork currentGuid visited =
                        if visited |> Set.contains currentGuid then ()
                        else
                            let nextVisited = visited |> Set.add currentGuid
                            if hasApiDef currentGuid then
                                entryWorkGuids <- currentGuid :: entryWorkGuids
                            else
                                let startPreds = findOrEmpty currentGuid workStartPreds
                                if startPreds.IsEmpty then
                                    warnings <- $"Work '{workNameOf currentGuid}'에서 ApiDef 연결 Work를 찾을 수 없습니다." :: warnings
                                else
                                    for predGuid in startPreds do
                                        findApiDefWork predGuid nextVisited
                    findApiDefWork entryGuid Set.empty

        entryWorkGuids |> List.distinct, warnings |> List.distinct
