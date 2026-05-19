namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store

module GraphWarningProjection =

    type DurationWarning = {
        WorkGuid: Guid
        SystemName: string
        WorkName: string
        ConfiguredMs: int
        CriticalPathMs: int
    }

    type RaceConditionWarning = {
        WorkGuid: Guid
        WorkName: string
        LeftCallGuid: Guid
        LeftCallName: string
        RightCallGuid: Guid
        RightCallName: string
    }

    type TokenSpecWarning = {
        WorkGuid: Guid
        WorkName: string
    }

    let private callName (index: SimIndex) callGuid =
        match Queries.getCall callGuid index.Store with
        | Some call -> call.Name
        | None -> "?"

    let private pairKey (leftGuid: Guid) (rightGuid: Guid) =
        [ leftGuid.ToString(); rightGuid.ToString() ]
        |> List.sort
        |> String.concat ","

    let private apiDefReferencesWork store originalWorkGuid apiDefId =
        Queries.getApiDef apiDefId store
        |> Option.exists (fun apiDef ->
            apiDef.TxGuid = Some originalWorkGuid
            || apiDef.RxGuid = Some originalWorkGuid)

    let private callReferencesWork store originalWorkGuid (call: Call) =
        call.ApiCalls
        |> Seq.exists (fun apiCall ->
            apiCall.ApiDefId
            |> Option.exists (apiDefReferencesWork store originalWorkGuid))

    let findRaceConditionWarnings (index: SimIndex) : RaceConditionWarning list =
        let mutable reported = Set.empty<string>

        index.CallRaceExclusions
        |> Map.toList
        |> List.collect (fun (callGuid, exclusions) ->
            exclusions
            |> Set.toList
            |> List.choose (fun excludedGuid ->
                let key = pairKey callGuid excludedGuid
                if Set.contains key reported then
                    None
                else
                    reported <- Set.add key reported
                    let workGuid = index.CallWorkGuid |> Map.tryFind callGuid |> Option.defaultValue Guid.Empty
                    let workName = index.WorkName |> Map.tryFind workGuid |> Option.defaultValue "?"
                    Some {
                        WorkGuid = workGuid
                        WorkName = workName
                        LeftCallGuid = callGuid
                        LeftCallName = callName index callGuid
                        RightCallGuid = excludedGuid
                        RightCallName = callName index excludedGuid
                    }))

    let findDurationLessThanCriticalPathWarnings (index: SimIndex) : DurationWarning list =
        index.AllWorkGuids
        |> List.choose (fun workGuid ->
            match Queries.getWork workGuid index.Store, Queries.tryGetDeviceDurationMs workGuid index.Store with
            | Some work, Some criticalPathMs ->
                let configuredMs =
                    work.Duration
                    |> Option.map (fun duration -> int duration.TotalMilliseconds)
                    |> Option.defaultValue 0

                if configuredMs > 0 && configuredMs < criticalPathMs then
                    Some {
                        WorkGuid = workGuid
                        SystemName = index.WorkSystemName |> Map.tryFind workGuid |> Option.defaultValue ""
                        WorkName = index.WorkName |> Map.tryFind workGuid |> Option.defaultValue ""
                        ConfiguredMs = configuredMs
                        CriticalPathMs = criticalPathMs
                    }
                else
                    None
            | _ -> None)

    let findTokenSourcesWithoutSpecs (index: SimIndex) : TokenSpecWarning list =
        let specWorkIds =
            Queries.getTokenSpecs index.Store
            |> List.choose (fun spec -> spec.WorkId)
            |> List.map (fun workId -> Queries.resolveOriginalWorkId workId index.Store)
            |> Set.ofList

        index.TokenSourceGuids
        |> List.filter (fun workGuid -> not (Set.contains workGuid specWorkIds))
        |> List.choose (fun workGuid ->
            index.WorkName
            |> Map.tryFind workGuid
            |> Option.map (fun workName -> {
                WorkGuid = workGuid
                WorkName = workName
            }))

    let expandWarningTarget (store: DsStore) (warningGuid: Guid) : Guid[] =
        if store.Works.ContainsKey(warningGuid) then
            let originalWorkGuid = Queries.resolveOriginalWorkId warningGuid store
            let workTargets = Queries.referenceGroupOf originalWorkGuid store |> Set.ofList

            let referencedOriginalCallGuids =
                store.CallsReadOnly.Values
                |> Seq.filter (callReferencesWork store originalWorkGuid)
                |> Seq.map (fun call -> Queries.resolveOriginalCallId call.Id store)
                |> Set.ofSeq

            let callTargets =
                store.CallsReadOnly.Values
                |> Seq.filter (fun call ->
                    referencedOriginalCallGuids
                    |> Set.contains (Queries.resolveOriginalCallId call.Id store))
                |> Seq.map (fun call -> call.Id)
                |> Set.ofSeq

            Set.union workTargets callTargets |> Set.toArray
        elif store.Calls.ContainsKey(warningGuid) then
            Queries.callReferenceGroupOf warningGuid store |> Set.ofList |> Set.toArray
        else
            [| warningGuid |]

    let expandWarningTargets (store: DsStore) (warningGuids: seq<Guid>) : Guid[] =
        warningGuids
        |> Seq.collect (expandWarningTarget store)
        |> Set.ofSeq
        |> Set.toArray

    let warningGuidsForTarget (store: DsStore) (warningGuids: seq<Guid>) (targetGuid: Guid) : Guid[] =
        warningGuids
        |> Seq.filter (fun warningGuid ->
            expandWarningTarget store warningGuid
            |> Array.contains targetGuid)
        |> Seq.toArray
