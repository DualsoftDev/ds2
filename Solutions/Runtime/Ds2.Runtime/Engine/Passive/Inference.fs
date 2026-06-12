namespace Ds2.Runtime.Engine.Passive

open System
open System.Collections.Generic
open System.Diagnostics
open Ds2.Core
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO

type PassiveInferenceSession(index: SimIndex, ioMap: SignalIOMap, runtimeMode: RuntimeMode, baselineFirstObservation: bool) =
    let pendingLogs = ResizeArray<PassiveInferenceLog>()
    let workLearning = Dictionary<Guid, WorkLearning>()
    let workUniqueAddresses = Dictionary<Guid, HashSet<string>>()
    let workPositiveFamilyTokens = Dictionary<Guid, Dictionary<string, string>>()
    let workResetTargetsByPred = Dictionary<Guid, ResizeArray<Guid>>()
    let callOutHighAddresses = Dictionary<Guid, HashSet<string>>()
    let callInHighAddresses = Dictionary<Guid, HashSet<string>>()
    // 308a72ab 복원: Call 별 전체 기대 주소(모든 ApiCall OutTag/InTag). high 가 이를 전부 덮을(IsSupersetOf)
    // 때만 Going/Finish — "모든 Out On=Going / 모든 In On=Finish".
    let callOutExpectedAddresses = Dictionary<Guid, HashSet<string>>()
    let callInExpectedAddresses = Dictionary<Guid, HashSet<string>>()
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

    // 308a72ab HasAllObservedSignals: 그 Call 의 expected 주소가 전부 high 에 포함되는가(IsSupersetOf).
    let hasAllObserved
        (expectedMap: Dictionary<Guid, HashSet<string>>)
        (highMap: Dictionary<Guid, HashSet<string>>)
        callGuid =
        match expectedMap.TryGetValue(callGuid) with
        | true, expected when expected.Count > 0 ->
            match highMap.TryGetValue(callGuid) with
            | true, high -> high.IsSupersetOf(expected)
            | _ -> false
        | _ -> false

    let matchesPassiveSpec valueSpec currentValue =
        match valueSpec with
        | UndefinedValue -> String.Equals(currentValue, "true", StringComparison.OrdinalIgnoreCase)
        | _ -> ValueSpec.evaluate valueSpec currentValue

    let tryGetApiCallSpec apiCallGuid isOut =
        Ds2.Core.Store.Queries.getApiCall apiCallGuid index.Store
        |> Option.map (fun apiCall -> if isOut then apiCall.OutputSpec else apiCall.InputSpec)

    let tryEnqueueCallFinishFromObservedInputs
        (actions: ResizeArray<PassiveInferenceAction>)
        (overlay: StateOverlay)
        callGuid =
        if hasAllObserved callInExpectedAddresses callInHighAddresses callGuid
           && overlay.GetCallState(callGuid) = Status4.Going then
            PassiveInferenceWorkCycle.enqueueCallState actions overlay callGuid Status4.Finish

    /// 공유 Out 주소 오귀속 방지 — "차례" 게이트.
    /// 같은 Out 주소를 여러 Call 이 단계별로 호출하는 모델(예: %Q2001 을 4-디바이스 동시 Call
    /// Tester.RET{4주소}와 단독 Call Tester1.RET{1주소}가 공유)에서, 동시 묶음 차례의 rising 에
    /// 단독 Call 도 자기 기대(1주소)가 충족돼 사이클마다 가짜 Going/Finish 1세트를 만들었다.
    /// 사이클 학습(Synced)의 기대 그룹이 곧 "현재 차례"다 — 이 Call 의 기대 Out 토큰 집합이
    /// 기대 그룹의 *진부분집합*이면 상위 묶음의 차례이므로 Going 을 양보한다.
    /// (자기 차례엔 기대 그룹 = 자기 집합이라 정상 Going. 미동기화/기대 불일치/토큰 미해석은
    /// 판단 보류 → 기존 동작 — 보수적.)
    let yieldsToExpectedGroupTurn (callGuid: Guid) =
        match index.CallWorkGuid |> Map.tryFind callGuid with
        | None -> false
        | Some workGuid ->
            match workLearning.TryGetValue(workGuid) with
            | true, wl when wl.Synced ->
                match wl.DetectedPeriod with
                | Some period when period > 0 ->
                    // 이 rising 이 속할 그룹의 기대 idx.
                    //   Out 그룹 진행 중(LiveCurrentKey=Out) → 현재 기대 그대로.
                    //   다른 키 그룹 진행 중(In 등) → 이 신호가 새 그룹: 경계에서 ++ 예정이라 +1.
                    //   None(Synced 직후 첫 신호) → NextExpectedGroupIdx 가 이미 "다음 올 그룹"
                    //   으로 세팅돼 있음(detectWorkPeriod) — 그대로.
                    let expectedIdx =
                        match wl.LiveCurrentKey with
                        | Some key when key <> ("Out", "true") -> (wl.NextExpectedGroupIdx + 1) % period
                        | _ -> wl.NextExpectedGroupIdx
                    if expectedIdx < 0 || expectedIdx >= wl.CycleSequence.Count then false
                    else
                        match workPositiveFamilyTokens.TryGetValue(workGuid),
                              callOutExpectedAddresses.TryGetValue(callGuid) with
                        | (true, tokenMap), (true, myAddrs) when myAddrs.Count > 0 ->
                            let myTokens = HashSet<string>(StringComparer.Ordinal)
                            let mutable allResolved = true
                            for addr in myAddrs do
                                match tokenMap.TryGetValue(PassiveInferenceWorkCycleAlignment.familyAddressKey "Out" addr) with
                                | true, token -> myTokens.Add(token) |> ignore
                                | _ -> allResolved <- false
                            if not allResolved || myTokens.Count = 0 then false
                            else
                                let expectedTokens =
                                    HashSet<string>(
                                        wl.CycleSequence[expectedIdx].Split([| '|' |], StringSplitOptions.RemoveEmptyEntries),
                                        StringComparer.Ordinal)
                                expectedTokens.Count > myTokens.Count
                                && expectedTokens.IsSupersetOf(myTokens)
                        | _ -> false
                | _ -> false
            | _ -> false

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

        // 308a72ab: HasAllObservedSignals(IsSupersetOf) — 전체 ApiCall IO On/Off 기준.
        //   Call 의 모든 Out high → Going, 모든 In high → Finish. rising(matchesSpec) 일 때만 평가해
        //   falling 으로 인한 재진입을 막는다(Finish 후 In off 는 SensorOpen 일 뿐 상태 유지).
        if not matchesSpec then
            ()
        elif isOut then
            if hasAllObserved callOutExpectedAddresses callOutHighAddresses callGuid
               && overlay.GetCallState(callGuid) <> Status4.Going
               && not (yieldsToExpectedGroupTurn callGuid) then
                if overlay.GetCallState(callGuid) <> Status4.Ready then
                    PassiveInferenceWorkCycle.enqueueCallState actions overlay callGuid Status4.Ready
                PassiveInferenceWorkCycle.enqueueCallState actions overlay callGuid Status4.Going
                tryEnqueueCallFinishFromObservedInputs actions overlay callGuid
        else
            tryEnqueueCallFinishFromObservedInputs actions overlay callGuid

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
        // 308a72ab BuildPassiveCallAddressSets: Call 별 전체 ApiCall Out/In 주소를 expected 로 고정.
        for kvp in ioMap.CallToMappings do
            let outSet = HashSet<string>(StringComparer.Ordinal)
            let inSet = HashSet<string>(StringComparer.Ordinal)
            for m in kvp.Value do
                if not (String.IsNullOrWhiteSpace m.OutAddress) then outSet.Add(m.OutAddress) |> ignore
                if not (String.IsNullOrWhiteSpace m.InAddress) then inSet.Add(m.InAddress) |> ignore
            callOutExpectedAddresses[kvp.Key] <- outSet
            callInExpectedAddresses[kvp.Key] <- inSet

        PassiveInferenceWorkCycle.computeWorkUniqueAddresses workContext
        PassiveInferenceWorkCycle.computeWorkPositiveFamilyTokens workContext
        PassiveInferenceWorkCycle.buildPassiveResetTargetsByPred workContext

    let tryGetWorkGuidFromCall callGuid =
        index.CallWorkGuid |> Map.tryFind callGuid

    let getRelatedWorkGuids address =
        seq {
            for mapping in ioMap.GetByOutAddress(address) do
                match tryGetWorkGuidFromCall mapping.CallGuid with
                | Some workGuid -> yield workGuid
                | None -> ()
            for mapping in ioMap.GetByInAddress(address) do
                match tryGetWorkGuidFromCall mapping.CallGuid with
                | Some workGuid -> yield workGuid
                | None -> ()
        }
        |> Seq.distinct
        |> Seq.toArray

    new(index: SimIndex, ioMap: SignalIOMap, runtimeMode: RuntimeMode) =
        PassiveInferenceSession(index, ioMap, runtimeMode, false)

    member _.DrainLogs() =
        let logs = pendingLogs.ToArray()
        pendingLogs.Clear()
        logs

    member _.IsAbnormalReadyForAddress(address: string) =
        let workGuids = getRelatedWorkGuids address
        workGuids.Length > 0
        && (workGuids
            |> Array.exists (fun workGuid ->
                match workLearning.TryGetValue(workGuid) with
                | true, learning -> learning.Synced
                | _ -> false))

    member _.Observe(
        address: string,
        value: string,
        getWorkState: Func<Guid, Status4>,
        getCallState: Func<Guid, Status4>
    ) =
        match lastObservedValue.TryGetValue(address) with
        | true, previous when previous = value -> Array.empty
        | _ ->
            let outMappings = ioMap.GetByOutAddress(address)
            let inMappings = ioMap.GetByInAddress(address)
            if List.isEmpty outMappings && List.isEmpty inMappings then
                lastObservedValue[address] <- value
                Array.empty
            else
                let isFirstObservation = not (lastObservedValue.ContainsKey(address))
                let matchesAnySpec =
                    let matchesMapping isOut (mapping: SignalMapping) =
                        tryGetApiCallSpec mapping.ApiCallGuid isOut
                        |> Option.map (fun valueSpec -> matchesPassiveSpec valueSpec value)
                        |> Option.defaultValue (String.Equals(value, "true", StringComparison.OrdinalIgnoreCase))

                    (outMappings |> List.exists (matchesMapping true))
                    || (inMappings |> List.exists (matchesMapping false))

                lastObservedValue[address] <- value

                if baselineFirstObservation && isFirstObservation && not matchesAnySpec then
                    Array.empty
                else
                    let actions = ResizeArray<PassiveInferenceAction>()
                    let overlay = StateOverlay(getWorkState, getCallState)
                    if not (List.isEmpty outMappings) then
                        observePassiveSignalDirectionInternal actions overlay address value true outMappings
                    if not (List.isEmpty inMappings) then
                        observePassiveSignalDirectionInternal actions overlay address value false inMappings
                    actions.ToArray()

    member _.Baseline(address: string, value: string) =
        if not (String.IsNullOrWhiteSpace(address)) then
            lastObservedValue[address] <- value

    /// 통신 blackout(PLC 단절/신호 두절) — 진행 중 관측만 무효화한다.
    /// 단절 구간은 edge 순서를 신뢰할 수 없어, stale 한 high 집합/직전값으로 재개 신호를
    /// 가짜 전이로 추론하는 것을 막는다. 사이클 학습(workLearning)은 보존 — 재개 시
    /// 기존 Synced 패턴으로 재합류한다. 직전값(lastObservedValue)도 비워 재개 첫 신호가
    /// 항상 관측되게 한다(같은 값 재수신 무시 가드 우회).
    member _.InvalidateObservations() =
        for kv in callOutHighAddresses do kv.Value.Clear()
        for kv in callInHighAddresses do kv.Value.Clear()
        lastObservedValue.Clear()

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
