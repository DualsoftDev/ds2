namespace Ds2.Runtime.Engine.Passive

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO

type PassiveInferenceSession(index: SimIndex, ioMap: SignalIOMap, runtimeMode: RuntimeMode) =
    let pendingLogs = ResizeArray<PassiveInferenceLog>()
    let workLearning = Dictionary<Guid, WorkLearning>()
    let workUniqueAddresses = Dictionary<Guid, HashSet<string>>()
    let workPositiveFamilyTokens = Dictionary<Guid, Dictionary<string, string>>()
    let workResetTargetsByPred = Dictionary<Guid, ResizeArray<Guid>>()
    let callOutAddresses = Dictionary<Guid, HashSet<string>>()
    let callInAddresses = Dictionary<Guid, HashSet<string>>()
    let callOutHighAddresses = Dictionary<Guid, HashSet<string>>()
    let callInHighAddresses = Dictionary<Guid, HashSet<string>>()
    let lastObservedValue = Dictionary<string, string>(StringComparer.Ordinal)

    let addLog kind message =
        pendingLogs.Add({ Kind = kind; Message = message })

    let workContext = {
        Index = index
        IoMap = ioMap
        RuntimeMode = runtimeMode
        AddLog = addLog
        WorkLearning = workLearning
        WorkUniqueAddresses = workUniqueAddresses
        WorkPositiveFamilyTokens = workPositiveFamilyTokens
        WorkResetTargetsByPred = workResetTargetsByPred
        CallOutHighAddresses = callOutHighAddresses
        CallInHighAddresses = callInHighAddresses
    }

    let getOrAddSignalSet (map: Dictionary<Guid, HashSet<string>>) key =
        match map.TryGetValue(key) with
        | true, set -> set
        | _ ->
            let set = HashSet<string>(StringComparer.Ordinal)
            map[key] <- set
            set

    let hasAllObservedSignals
        (expectedMap: Dictionary<Guid, HashSet<string>>)
        (highMap: Dictionary<Guid, HashSet<string>>)
        key =
        match expectedMap.TryGetValue(key), highMap.TryGetValue(key) with
        | (true, expected), (true, high) when expected.Count > 0 -> high.IsSupersetOf(expected)
        | _ -> false

    let buildPassiveCallAddressSets () =
        callOutAddresses.Clear()
        callInAddresses.Clear()

        for callGuid in index.AllCallGuids do
            match index.Store.Calls.TryGetValue(callGuid) with
            | true, call ->
                let outSet = HashSet<string>(StringComparer.Ordinal)
                let inSet = HashSet<string>(StringComparer.Ordinal)
                for apiCall in call.ApiCalls do
                    let outAddress = apiCall.OutTag |> Option.map (fun tag -> tag.Address)
                    let inAddress = apiCall.InTag |> Option.map (fun tag -> tag.Address)
                    match outAddress with
                    | Some address when not (String.IsNullOrWhiteSpace(address)) ->
                        outSet.Add(address) |> ignore
                    | _ -> ()
                    match inAddress with
                    | Some address when not (String.IsNullOrWhiteSpace(address)) ->
                        inSet.Add(address) |> ignore
                    | _ -> ()

                callOutAddresses[callGuid] <- outSet
                callInAddresses[callGuid] <- inSet
            | _ -> ()

    let observePassiveCallSignal
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        callGuid
        address
        isOut
        isOn =
        let highMap = if isOut then callOutHighAddresses else callInHighAddresses
        let highSet = getOrAddSignalSet highMap callGuid

        if isOn then
            highSet.Add(address) |> ignore
        else
            highSet.Remove(address) |> ignore

        if hasAllObservedSignals callInAddresses callInHighAddresses callGuid then
            if overlay.GetCallState(callGuid) <> Status4.Finish then
                PassiveInferenceWorkCycle.enqueueCallState actions overlay callGuid Status4.Finish
        elif isOut && isOn && hasAllObservedSignals callOutAddresses callOutHighAddresses callGuid then
            if overlay.GetCallState(callGuid) <> Status4.Going then
                PassiveInferenceWorkCycle.enqueueCallState actions overlay callGuid Status4.Going

    let observePassiveSignalDirectionInternal
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        address
        value
        isOut
        (mappings: seq<SignalMapping>) =
        let isOn = value = "true"

        mappings
        |> Seq.map (fun mapping -> mapping.CallGuid)
        |> Seq.distinct
        |> Seq.iter (fun callGuid -> observePassiveCallSignal actions overlay callGuid address isOut isOn)

        if isOn then
            PassiveInferenceWorkCycle.observePositiveWorkSignal workContext actions overlay address isOut

    do
        PassiveInferenceWorkCycle.computeWorkUniqueAddresses workContext
        PassiveInferenceWorkCycle.computeWorkPositiveFamilyTokens workContext
        PassiveInferenceWorkCycle.buildPassiveResetTargetsByPred workContext
        buildPassiveCallAddressSets ()

    member _.DrainLogs() =
        let logs = pendingLogs.ToArray()
        pendingLogs.Clear()
        logs

    member _.Observe(
        address: string,
        value: string,
        getWorkState: Func<Guid, Status4>,
        getCallState: Func<Guid, Status4>
    ) =
        match lastObservedValue.TryGetValue(address) with
        | true, previous when previous = value -> Array.empty
        | _ ->
            lastObservedValue[address] <- value

            let outMappings = ioMap.GetByOutAddress(address)
            let inMappings = ioMap.GetByInAddress(address)
            if List.isEmpty outMappings && List.isEmpty inMappings then
                Array.empty
            else
                let actions = ResizeArray<PassiveInferenceAction>()
                let overlay = StateOverlay(getWorkState, getCallState)
                if not (List.isEmpty outMappings) then
                    observePassiveSignalDirectionInternal actions overlay address value true outMappings
                if not (List.isEmpty inMappings) then
                    observePassiveSignalDirectionInternal actions overlay address value false inMappings
                actions.ToArray()

    member _.ObserveDirection(
        address: string,
        value: string,
        isOut: bool,
        mappings: seq<SignalMapping>,
        getWorkState: Func<Guid, Status4>,
        getCallState: Func<Guid, Status4>
    ) =
        let actions = ResizeArray<PassiveInferenceAction>()
        let overlay = StateOverlay(getWorkState, getCallState)
        observePassiveSignalDirectionInternal actions overlay address value isOut mappings
        actions.ToArray()
