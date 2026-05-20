namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store

module SimulationProjection =

    type SimulationEntry = {
        Id: Guid
        Name: string
        Kind: EntityKind
        SystemName: string
        ParentWorkId: Nullable<Guid>
    }

    type GanttVisualTiming = {
        BaseDurationMs: Nullable<double>
        VirtualAppendMs: int
        OutputAppendMs: int
    }

    let private isCanonicalWork (index: SimIndex) workGuid =
        index.WorkCanonicalGuids
        |> Map.tryFind workGuid
        |> Option.map ((=) workGuid)
        |> Option.defaultValue true

    let private isCanonicalCall (index: SimIndex) callGuid =
        index.CallCanonicalGuids
        |> Map.tryFind callGuid
        |> Option.map ((=) callGuid)
        |> Option.defaultValue true

    let indexedEntries (index: SimIndex) : SimulationEntry[] =
        [|
            for workGuid in index.AllWorkGuids do
                if isCanonicalWork index workGuid then
                    match Map.tryFind workGuid index.WorkName, Map.tryFind workGuid index.WorkSystemName with
                    | Some workName, Some systemName when index.ActiveSystemNames.Contains systemName ->
                        yield {
                            Id = workGuid
                            Name = workName
                            Kind = EntityKind.Work
                            SystemName = systemName
                            ParentWorkId = Nullable()
                        }

                        match Map.tryFind workGuid index.WorkCallGuids with
                        | Some callGuids ->
                            for callGuid in callGuids do
                                if isCanonicalCall index callGuid then
                                    match Queries.getCall callGuid index.Store with
                                    | Some call ->
                                        yield {
                                            Id = callGuid
                                            Name = call.Name
                                            Kind = EntityKind.Call
                                            SystemName = systemName
                                            ParentWorkId = Nullable(workGuid)
                                        }
                                    | None -> ()
                        | None -> ()
                    | _ -> ()
        |]

    let private maxOrZero values =
        values
        |> Seq.fold max 0

    let private virtualAppendMs sensingType =
        match sensingType with
        | SensingType.Virtual (Some (Append n)) -> n
        | _ -> 0

    let private outputAppendMs actionType =
        match actionType with
        | ActionType.Real (SignalMode.Level, Some (Append n)) -> n
        | _ -> 0

    let private workDurationMs (index: SimIndex) workGuid =
        index.WorkDuration
        |> Map.tryFind workGuid

    let private nullableMax (values: seq<float>) =
        let values = values |> Seq.toArray
        if values.Length = 0 then Nullable()
        else Nullable(values |> Array.max)

    let private apiDefsOfCall (index: SimIndex) callGuid =
        SimIndex.findOrEmpty callGuid index.CallApiCallGuids
        |> List.choose (fun apiCallGuid ->
            match index.Store.ApiCalls.TryGetValue(apiCallGuid) with
            | true, apiCall ->
                apiCall.ApiDefId
                |> Option.bind (fun apiDefGuid ->
                    match index.Store.ApiDefs.TryGetValue(apiDefGuid) with
                    | true, apiDef -> Some apiDef
                    | _ -> None)
            | _ -> None)

    let private workVirtualAppendMs (index: SimIndex) workGuid =
        let boundAppend =
            index.Store.ApiDefs.Values
            |> Seq.choose (fun apiDef ->
                let isCompletionWork =
                    apiDef.RxGuid = Some workGuid
                    || (apiDef.RxGuid.IsNone && apiDef.TxGuid = Some workGuid)

                if isCompletionWork then
                    let ms = virtualAppendMs apiDef.SensingType
                    if ms > 0 then Some ms else None
                else
                    None)

        let unboundAppend =
            SimIndex.findOrEmpty workGuid index.WorkCallGuids
            |> Seq.collect (apiDefsOfCall index)
            |> Seq.choose (fun apiDef ->
                if apiDef.TxGuid.IsNone && apiDef.RxGuid.IsNone then
                    let ms = virtualAppendMs apiDef.SensingType
                    if ms > 0 then Some ms else None
                else
                    None)

        Seq.append boundAppend unboundAppend |> maxOrZero

    let private callBaseDurationMs (index: SimIndex) callGuid (parentWorkId: Nullable<Guid>) =
        apiDefsOfCall index callGuid
        |> Seq.choose (fun apiDef ->
            match apiDef.SensingType with
            | SensingType.Virtual _ ->
                match apiDef.RxGuid |> Option.orElse apiDef.TxGuid with
                | Some completionWorkGuid -> workDurationMs index completionWorkGuid
                | None when parentWorkId.HasValue -> workDurationMs index parentWorkId.Value
                | None -> None
            | _ -> None)
        |> nullableMax

    let private callVirtualAppendMs (index: SimIndex) callGuid =
        apiDefsOfCall index callGuid
        |> Seq.map (fun apiDef -> virtualAppendMs apiDef.SensingType)
        |> maxOrZero

    let private callOutputAppendMs (index: SimIndex) callGuid =
        apiDefsOfCall index callGuid
        |> Seq.map (fun apiDef -> outputAppendMs apiDef.ActionType)
        |> maxOrZero

    let ganttVisualTiming (index: SimIndex) entryId kind (parentWorkId: Nullable<Guid>) : GanttVisualTiming =
        match kind with
        | EntityKind.Work ->
            {
                BaseDurationMs =
                    workDurationMs index entryId
                    |> Option.map Nullable
                    |> Option.defaultValue (Nullable())
                VirtualAppendMs = workVirtualAppendMs index entryId
                OutputAppendMs = 0
            }
        | EntityKind.Call ->
            {
                BaseDurationMs = callBaseDurationMs index entryId parentWorkId
                VirtualAppendMs = callVirtualAppendMs index entryId
                OutputAppendMs = callOutputAppendMs index entryId
            }
        | _ ->
            {
                BaseDurationMs = Nullable()
                VirtualAppendMs = 0
                OutputAppendMs = 0
            }
