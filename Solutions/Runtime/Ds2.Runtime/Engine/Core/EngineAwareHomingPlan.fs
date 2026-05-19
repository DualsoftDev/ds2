namespace Ds2.Runtime.Engine.Core

open System
open System.Collections.Generic
open Ds2.Runtime.IO
open Ds2.Runtime.Model

/// <summary>
/// Engine-aware push-button homing 결정 로직.
/// engine 의 정적 home 메타(<c>SimIndex.computeAutoHomingPlan</c> + <c>WorkResetPreds</c>)와
/// 현재 PLC IN 값을 비교해 어긋난 Work 의 home OUT 후보를 추출하고,
/// 각 후보 Call 의 ComAux 조건으로 안전 게이트.
///
/// PLC IO write 는 호출자(C#) 가 담당. 이 모듈은 순수 결정.
/// </summary>
module EngineAwareHomingPlan =

    /// 어긋난 Work 의 home OUT 후보 1건. CallGuid 의 ComAux 조건으로 게이트됨.
    type HomingCandidate = {
        OutAddress: string
        CallGuid: Guid
        WorkName: string
        Reason: string
    }

    type HomingPlan = {
        /// 모든 추출 후보 (ComAux 게이트 전).
        Candidates: HomingCandidate[]
        /// ComAux 통과 후보.
        Passed: HomingCandidate[]
        /// ComAux 차단된 CallGuid (dedup, 순서 유지).
        BlockedCallGuids: Guid[]
        /// 실 fire 할 OUT 주소 (대소문자 무시 dedup, 순서 유지).
        OutsToFire: string[]
        /// 목표 Ready 인데 WorkResetPreds 가 비어있어 reset 불가한 work.
        WorksMissingResetPreds: (Guid * string)[]
    }

    /// buildPlan 에 필요한 SimIndex 의 부분 — 테스트 용이성을 위해 분리.
    type Context = {
        AllWorkGuids: Guid list
        WorkName: Map<Guid, string>
        WorkResetPreds: Map<Guid, Guid list>
        CallComAuxConditions: Map<Guid, ConditionExpression>
    }

    let fromIndex (index: SimIndex) : Context = {
        AllWorkGuids = index.AllWorkGuids
        WorkName = index.WorkName
        WorkResetPreds = index.WorkResetPreds
        CallComAuxConditions = index.CallComAuxConditions
    }

    let private nonEmpty (s: string) = not (String.IsNullOrWhiteSpace s)

    let private callsForOutAddress (iom: SignalIOMap) (outAddress: string) : Guid seq =
        match iom.OutAddressToMappings |> Map.tryFind outAddress with
        | Some mappings -> mappings |> Seq.map (fun m -> m.CallGuid)
        | None -> Seq.empty

    let private workInAddrs (iom: SignalIOMap) (workGuid: Guid) : string[] =
        match iom.RxWorkToInAddresses |> Map.tryFind workGuid with
        | Some addrs -> addrs |> List.filter nonEmpty |> List.distinct |> Array.ofList
        | None -> Array.empty

    let private workOutAddrs (iom: SignalIOMap) (workGuid: Guid) : string[] =
        match iom.TxWorkToOutAddresses |> Map.tryFind workGuid with
        | Some addrs -> addrs |> List.filter nonEmpty |> List.distinct |> Array.ofList
        | None -> Array.empty

    let private resolveWorkName (ctx: Context) (workGuid: Guid) : string =
        match ctx.WorkName |> Map.tryFind workGuid with
        | Some n -> n
        | None -> workGuid.ToString()

    let private anyInOn (inValues: IReadOnlyDictionary<string, bool>) (addrs: string[]) : bool =
        addrs
        |> Array.exists (fun a ->
            match inValues.TryGetValue(a) with
            | true, v -> v
            | _ -> false)

    /// 단일 Work 에 대한 후보 추출.
    /// Result.Error = (workGuid, workName) — 목표 Ready 인데 reset partner 없음.
    let private extractForWork
            (ctx: Context) (iom: SignalIOMap)
            (finishTargets: Set<Guid>) (readyTargets: Set<Guid>)
            (inValues: IReadOnlyDictionary<string, bool>)
            (workGuid: Guid)
            : Result<HomingCandidate[], Guid * string> =
        let outAddrs = workOutAddrs iom workGuid
        let inAddrs = workInAddrs iom workGuid
        if outAddrs.Length = 0 && inAddrs.Length = 0 then
            Ok [||]
        else
            let workName = resolveWorkName ctx workGuid
            let inIsOn = anyInOn inValues inAddrs

            if finishTargets.Contains workGuid && not inIsOn then
                let reason = "FIRE 목표=Finish, 현재 IN=false"
                let cands =
                    outAddrs
                    |> Seq.collect (fun oa ->
                        callsForOutAddress iom oa
                        |> Seq.map (fun cg ->
                            { OutAddress = oa
                              CallGuid = cg
                              WorkName = workName
                              Reason = reason }))
                    |> Array.ofSeq
                Ok cands
            elif readyTargets.Contains workGuid && inIsOn then
                match ctx.WorkResetPreds |> Map.tryFind workGuid with
                | Some partners when not (List.isEmpty partners) ->
                    let reason = "RESET via partner 목표=Ready, 현재 IN=true"
                    let cands =
                        partners
                        |> Seq.collect (fun partnerGuid ->
                            workOutAddrs iom partnerGuid
                            |> Seq.collect (fun po ->
                                callsForOutAddress iom po
                                |> Seq.map (fun cg ->
                                    { OutAddress = po
                                      CallGuid = cg
                                      WorkName = workName
                                      Reason = reason })))
                        |> Array.ofSeq
                    Ok cands
                | _ ->
                    Error(workGuid, workName)
            else
                Ok [||]

    let private comAuxBlocks (ctx: Context) (state: SimState) (callGuid: Guid) : bool =
        match ctx.CallComAuxConditions |> Map.tryFind callGuid with
        | Some expr -> not (WorkConditionChecker.evaluateConditionExpression state expr)
        | None -> false

    /// 후보 추출 + ComAux 게이트 + OUT dedup. finishTargets/readyTargets 외부 주입.
    let buildPlanWithTargets
            (ctx: Context)
            (iom: SignalIOMap)
            (state: SimState)
            (inValues: IReadOnlyDictionary<string, bool>)
            (finishTargets: Set<Guid>)
            (readyTargets: Set<Guid>)
            : HomingPlan =
        let allCandidates = ResizeArray<HomingCandidate>()
        let worksMissing = ResizeArray<Guid * string>()

        for workGuid in ctx.AllWorkGuids do
            match extractForWork ctx iom finishTargets readyTargets inValues workGuid with
            | Ok cands -> allCandidates.AddRange(cands)
            | Error w -> worksMissing.Add(w)

        let passed = ResizeArray<HomingCandidate>()
        let blockedSet = HashSet<Guid>()
        let blocked = ResizeArray<Guid>()

        for c in allCandidates do
            if comAuxBlocks ctx state c.CallGuid then
                if blockedSet.Add c.CallGuid then
                    blocked.Add c.CallGuid
            else
                passed.Add c

        let outsToFire =
            let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
            let result = ResizeArray<string>()
            for c in passed do
                if seen.Add c.OutAddress then
                    result.Add c.OutAddress
            result.ToArray()

        {
            Candidates = allCandidates.ToArray()
            Passed = passed.ToArray()
            BlockedCallGuids = blocked.ToArray()
            OutsToFire = outsToFire
            WorksMissingResetPreds = worksMissing.ToArray()
        }

    /// SimIndex 로부터 자동 plan 결정 + buildPlanWithTargets 호출.
    let buildPlan
            (index: SimIndex)
            (iom: SignalIOMap)
            (state: SimState)
            (inValues: IReadOnlyDictionary<string, bool>)
            : HomingPlan =
        let finishTargets, readyTargets = SimIndex.computeAutoHomingPlan index
        buildPlanWithTargets (fromIndex index) iom state inValues finishTargets readyTargets
