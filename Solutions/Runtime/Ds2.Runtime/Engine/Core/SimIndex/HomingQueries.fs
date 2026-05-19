namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Core.Store

module internal SimIndexHomingQueries =

    let private resolveApiDefGuids = SimIndexAlgorithms.resolveApiDefGuids

    let private findOrEmpty = SimIndexAlgorithms.findOrEmpty

    let private rxWorkGuids (index: SimIndex) (callGuid: Guid) =
        resolveApiDefGuids index.Store (findOrEmpty callGuid index.CallApiCallGuids) (fun d -> d.RxGuid)

    let private activeWorkDeviceCalls (index: SimIndex) =
        let allWorkGuids = index.AllWorkGuids |> Set.ofList
        let resolveDeviceSystemId (call: Call) =
            call.ApiCalls
            |> Seq.tryHead
            |> Option.bind (fun apiCall -> apiCall.ApiDefId)
            |> Option.bind (fun defId -> Queries.getApiDef defId index.Store)
            |> Option.map (fun apiDef -> apiDef.ParentId)
        let toDeviceCall (call: Call) : SimIndexAutoHoming.DeviceCall = {
            CallGuid = call.Id
            DeviceSystemId = resolveDeviceSystemId call
            RxWorkGuids =
                call.ApiCalls
                |> Seq.map (fun apiCall -> apiCall.Id)
                |> Seq.toList
                |> fun apiCallGuids -> resolveApiDefGuids index.Store apiCallGuids (fun d -> d.RxGuid)
                |> List.filter allWorkGuids.Contains
        }
        Queries.allProjects index.Store
        |> List.collect (fun project -> Queries.activeSystemsOf project.Id index.Store)
        |> List.collect (fun system -> Queries.flowsOf system.Id index.Store)
        |> List.collect (fun flow -> Queries.worksOf flow.Id index.Store)
        |> List.map (fun activeWork -> Queries.callsOf activeWork.Id index.Store |> List.map toDeviceCall)

    let computeAutoHomingTargets (index: SimIndex) : Set<Guid> =
        SimIndexAutoHoming.computeAutoHomingTargets index.CallStartPreds (activeWorkDeviceCalls index)

    let computeAutoHomingPlan (index: SimIndex) : Set<Guid> * Set<Guid> =
        SimIndexAutoHoming.computeAutoHomingPlan index.CallStartPreds (activeWorkDeviceCalls index)

    let computeAutoHomingCallPlan (index: SimIndex) : Set<Guid> * Set<Guid> =
        SimIndexAutoHoming.computeAutoHomingCallPlan index.CallStartPreds (activeWorkDeviceCalls index)

    let findHomingEntryPoints (index: SimIndex) (readyTargets: Set<Guid>) : Guid list * string list =
        SimIndexAutoHoming.findHomingEntryPoints
            (fun workGuid -> index.WorkName |> Map.tryFind workGuid |> Option.defaultValue (string workGuid))
            index.WorkResetPreds
            index.WorkStartPreds
            (fun workGuid ->
                index.Store.ApiDefs.Values
                |> Seq.exists (fun apiDef -> apiDef.TxGuid = Some workGuid || apiDef.RxGuid = Some workGuid))
            readyTargets

    let findInitialFlagRxWorkGuids (index: SimIndex) : Set<Guid> =
        let isFinishedWorks =
            index.AllWorkGuids
            |> List.filter (fun workGuid ->
                Queries.getWork workGuid index.Store
                |> Option.bind (fun work -> work.GetSimulationProperties())
                |> Option.map (fun simProps -> simProps.IsFinished)
                |> Option.defaultValue false)
        if not isFinishedWorks.IsEmpty then
            isFinishedWorks |> Set.ofList
        else
            let autoTargets = computeAutoHomingTargets index
            if not autoTargets.IsEmpty then
                autoTargets
            else
                let isRetDirection callGuid =
                    rxWorkGuids index callGuid
                    |> List.exists (fun rxGuid ->
                        Queries.getWork rxGuid index.Store
                        |> Option.map (fun work -> work.Name.ToUpperInvariant().Contains("RET"))
                        |> Option.defaultValue false)
                index.AllCallGuids
                |> List.filter isRetDirection
                |> Seq.collect (fun callGuid -> rxWorkGuids index callGuid)
                |> Set.ofSeq

    let hasAnyTokenRole (index: SimIndex) : bool =
        index.WorkTokenRole
        |> Map.exists (fun _ role -> role.HasFlag(TokenRole.Source) || role.HasFlag(TokenRole.Ignore))
