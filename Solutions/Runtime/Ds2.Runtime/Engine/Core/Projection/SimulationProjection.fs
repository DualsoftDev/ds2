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
