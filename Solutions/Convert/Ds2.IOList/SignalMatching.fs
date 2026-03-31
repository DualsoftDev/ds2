namespace Ds2.IOList

open System
open System.Linq
open Ds2.Store
open Ds2.Core
open Microsoft.FSharp.Core

// =============================================================================
// Signal Matching - Match generated signals with DS2 store entities
// =============================================================================

module SignalMatching =

    /// IO batch row with IDs
    type IoBatchRow = {
        CallId: Guid
        ApiCallId: Guid
        FlowName: string
        DeviceName: string
        ApiName: string
        InAddress: string
        InSymbol: string
        OutAddress: string
        OutSymbol: string
        OutDataType: string
        InDataType: string
    }

    /// Unmatched signal information
    type UnmatchedSignal = {
        FlowName: string
        DeviceName: string
        ApiName: string
        OutSymbol: string
        OutAddress: string
        InSymbol: string
        InAddress: string
        FailureReason: string
    }

    /// Signal matching result
    type MatchingResult = {
        MatchedRows: IoBatchRow list
        UnmatchedSignals: UnmatchedSignal list
    }

    /// Find Call by name in the store
    let private findCallByName (store: DsStore) (flowName: string) (workName: string) (callName: string) : Call option =
        let flows = DsQuery.allFlows store

        flows
        |> List.tryPick (fun flow ->
            if flow.Name.Equals(flowName, StringComparison.OrdinalIgnoreCase) then
                let works = DsQuery.worksOf flow.Id store
                works
                |> List.tryPick (fun work ->
                    if work.Name.Equals(workName, StringComparison.OrdinalIgnoreCase) then
                        let calls = DsQuery.callsOf work.Id store
                        calls
                        |> List.tryFind (fun call ->
                            call.Name.Equals(callName, StringComparison.OrdinalIgnoreCase))
                    else
                        None)
            else
                None)

    /// Find ApiCall by ApiDef name within a Call
    let private findApiCallByApiDefName (store: DsStore) (call: Call) (apiDefName: string) : ApiCall option =
        call.ApiCalls
        |> Seq.tryFind (fun apiCall ->
            match apiCall.ApiDefId with
            | Some apiDefId ->
                match DsQuery.getApiDef apiDefId store with
                | Some apiDef ->
                    apiDef.Name.Equals(apiDefName, StringComparison.OrdinalIgnoreCase)
                | None -> false
            | None -> false)

    /// Get device name from ApiCall
    let private getDeviceNameFromApiCall (store: DsStore) (apiCall: ApiCall) : string option =
        match apiCall.ApiDefId with
        | Some apiDefId ->
            match DsQuery.getApiDef apiDefId store with
            | Some apiDef ->
                match store.Systems.TryGetValue(apiDef.ParentId) with
                | (true, system) -> Some system.Name
                | _ -> None
            | None -> None
        | None -> None

    /// Convert SignalRecord list to IoBatchRow list with matching
    let convertSignalsToRows (store: DsStore) (signals: SignalRecord list) : MatchingResult =
        // Group signals by Flow/Work/Call/Device
        let grouped =
            signals
            |> List.groupBy (fun s -> (s.FlowName, s.WorkName, s.CallName, s.DeviceName))

        let matchedRows = ResizeArray<IoBatchRow>()
        let unmatchedSignals = ResizeArray<UnmatchedSignal>()

        for ((flowName, workName, callName, deviceName), signalGroup) in grouped do
            // Find input and output signals
            let inputSignal =
                signalGroup
                |> List.tryFind (fun s -> s.IoType.StartsWith("I", StringComparison.OrdinalIgnoreCase))

            let outputSignal =
                signalGroup
                |> List.tryFind (fun s -> s.IoType.StartsWith("Q", StringComparison.OrdinalIgnoreCase))

            // Try to match with store
            match findCallByName store flowName workName callName with
            | Some call ->
                match findApiCallByApiDefName store call deviceName with
                | Some apiCall ->
                    let deviceNameResolved =
                        match getDeviceNameFromApiCall store apiCall with
                        | Some name -> name
                        | None -> "UNKNOWN"

                    matchedRows.Add({
                        CallId = call.Id
                        ApiCallId = apiCall.Id
                        FlowName = flowName
                        DeviceName = deviceNameResolved
                        ApiName = deviceName
                        InAddress = inputSignal |> Option.map (fun s -> s.Address) |> Option.defaultValue ""
                        InSymbol = inputSignal |> Option.map (fun s -> s.VarName) |> Option.defaultValue ""
                        OutAddress = outputSignal |> Option.map (fun s -> s.Address) |> Option.defaultValue ""
                        OutSymbol = outputSignal |> Option.map (fun s -> s.VarName) |> Option.defaultValue ""
                        OutDataType = "BOOL"
                        InDataType = "BOOL"
                    })
                | None ->
                    unmatchedSignals.Add({
                        FlowName = flowName
                        DeviceName = "UNKNOWN"
                        ApiName = deviceName
                        OutSymbol = outputSignal |> Option.map (fun s -> s.VarName) |> Option.defaultValue ""
                        OutAddress = outputSignal |> Option.map (fun s -> s.Address) |> Option.defaultValue ""
                        InSymbol = inputSignal |> Option.map (fun s -> s.VarName) |> Option.defaultValue ""
                        InAddress = inputSignal |> Option.map (fun s -> s.Address) |> Option.defaultValue ""
                        FailureReason = "ApiCall(Device)을 찾을 수 없음"
                    })
            | None ->
                unmatchedSignals.Add({
                    FlowName = flowName
                    DeviceName = "UNKNOWN"
                    ApiName = deviceName
                    OutSymbol = outputSignal |> Option.map (fun s -> s.VarName) |> Option.defaultValue ""
                    OutAddress = outputSignal |> Option.map (fun s -> s.Address) |> Option.defaultValue ""
                    InSymbol = inputSignal |> Option.map (fun s -> s.VarName) |> Option.defaultValue ""
                    InAddress = inputSignal |> Option.map (fun s -> s.Address) |> Option.defaultValue ""
                    FailureReason = "Call을 찾을 수 없음"
                })

        {
            MatchedRows = matchedRows |> Seq.toList
            UnmatchedSignals = unmatchedSignals |> Seq.toList
        }

    // Note: applyIoTagsToStore should be done in Promaker using Ds2.Editor.UpdateApiCallIoTags
    // since it requires Ds2.Editor dependency which we don't want in Ds2.IOList

// =============================================================================
// C# Interop API
// =============================================================================

/// C# friendly IO batch row
[<CLIMutable>]
type CsIoBatchRow = {
    CallId: Guid
    ApiCallId: Guid
    FlowName: string
    DeviceName: string
    ApiName: string
    InAddress: string
    InSymbol: string
    OutAddress: string
    OutSymbol: string
    OutDataType: string
    InDataType: string
}

/// C# friendly unmatched signal
[<CLIMutable>]
type CsUnmatchedSignal = {
    FlowName: string
    DeviceName: string
    ApiName: string
    OutSymbol: string
    OutAddress: string
    InSymbol: string
    InAddress: string
    FailureReason: string
}

/// C# friendly matching result
[<CLIMutable>]
type CsMatchingResult = {
    MatchedRows: CsIoBatchRow array
    UnmatchedSignals: CsUnmatchedSignal array
}

/// C# friendly API for signal matching
type SignalMatchingApi() =

    /// Convert signals to rows with matching
    static member ConvertSignalsToRows(store: DsStore, result: GenerationResult) : CsMatchingResult =
        let signals = result.IoSignals |> List.ofSeq
        let matchingResult = SignalMatching.convertSignalsToRows store signals

        {
            MatchedRows =
                matchingResult.MatchedRows
                |> List.map (fun r -> {
                    CallId = r.CallId
                    ApiCallId = r.ApiCallId
                    FlowName = r.FlowName
                    DeviceName = r.DeviceName
                    ApiName = r.ApiName
                    InAddress = r.InAddress
                    InSymbol = r.InSymbol
                    OutAddress = r.OutAddress
                    OutSymbol = r.OutSymbol
                    OutDataType = r.OutDataType
                    InDataType = r.InDataType
                })
                |> List.toArray

            UnmatchedSignals =
                matchingResult.UnmatchedSignals
                |> List.map (fun u -> {
                    FlowName = u.FlowName
                    DeviceName = u.DeviceName
                    ApiName = u.ApiName
                    OutSymbol = u.OutSymbol
                    OutAddress = u.OutAddress
                    InSymbol = u.InSymbol
                    InAddress = u.InAddress
                    FailureReason = u.FailureReason
                })
                |> List.toArray
        }

    // Note: ApplyIoTagsToStore should be done in Promaker using Ds2.Editor.UpdateApiCallIoTags
    // See TagWizardDialog.SignalGeneration.cs for reference implementation
