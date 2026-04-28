namespace Ds2.Runtime.Engine.Passive

open System
open System.Collections.Generic
open System.Diagnostics
open Ds2.Core
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO

type PassiveInferenceSession(index: SimIndex, ioMap: SignalIOMap, runtimeMode: RuntimeMode) =
    let pendingLogs = ResizeArray<PassiveInferenceLog>()
    let workLearning = Dictionary<Guid, WorkLearning>()
    let workUniqueAddresses = Dictionary<Guid, HashSet<string>>()
    let workPositiveFamilyTokens = Dictionary<Guid, Dictionary<string, string>>()
    let workResetTargetsByPred = Dictionary<Guid, ResizeArray<Guid>>()
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

    let matchesPassiveSpec valueSpec currentValue =
        match valueSpec with
        | UndefinedValue -> String.Equals(currentValue, "true", StringComparison.OrdinalIgnoreCase)
        | _ -> ValueSpec.evaluate valueSpec currentValue

    let tryGetApiCallSpec apiCallGuid isOut =
        Ds2.Core.Store.Queries.getApiCall apiCallGuid index.Store
        |> Option.map (fun apiCall -> if isOut then apiCall.OutputSpec else apiCall.InputSpec)

    let observePassiveCallSignal
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        (mapping: SignalMapping)
        address
        value
        isOut =
        let callGuid = mapping.CallGuid
        let highMap = if isOut then callOutHighAddresses else callInHighAddresses
        let highSet = getOrAddSignalSet highMap callGuid
        let matchesSpec =
            tryGetApiCallSpec mapping.ApiCallGuid isOut
            |> Option.map (fun valueSpec -> matchesPassiveSpec valueSpec value)
            |> Option.defaultValue (String.Equals(value, "true", StringComparison.OrdinalIgnoreCase))

        if matchesSpec then
            highSet.Add(address) |> ignore
        else
            highSet.Remove(address) |> ignore

        if not matchesSpec then
            ()
        elif isOut then
            if overlay.GetCallState(callGuid) <> Status4.Going then
                PassiveInferenceWorkCycle.enqueueCallState actions overlay callGuid Status4.Going
        else
            if overlay.GetCallState(callGuid) <> Status4.Finish then
                PassiveInferenceWorkCycle.enqueueCallState actions overlay callGuid Status4.Finish

    let observePassiveSignalDirectionInternal
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        address
        value
        isOut
        (mappings: seq<SignalMapping>) =
        let isOn = value = "true"
        let observedTick = Stopwatch.GetTimestamp()
        let mappingArray = mappings |> Seq.toArray

        mappingArray
        |> Seq.iter (fun mapping -> observePassiveCallSignal actions overlay mapping address value isOut)

        // e2b6d21 방식 복귀: VP/Monitoring 모두 observePositiveWorkSignal 로 cycle 학습 진행 →
        // applyWorkStateForExpectedGroup 가 cycle boundary 기반 정확한 Work 상태 trigger.
        // 9abc013 가 도입한 VP 전용 단순 집계(syncVirtualPlantWorkFromCalls) 는 cycle 끝과 다음 cycle
        // 첫 자식 Going 사이의 ms 공백에 Finish→Going 깜빡임을 유발해 폐기.
        if isOn then
            PassiveInferenceWorkCycle.observePositiveWorkSignal workContext actions overlay address isOut observedTick

    do
        PassiveInferenceWorkCycle.computeWorkUniqueAddresses workContext
        PassiveInferenceWorkCycle.computeWorkPositiveFamilyTokens workContext
        PassiveInferenceWorkCycle.buildPassiveResetTargetsByPred workContext

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
