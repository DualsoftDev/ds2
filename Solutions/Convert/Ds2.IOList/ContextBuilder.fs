namespace Ds2.IOList

open System
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery

// =============================================================================
// DS2 Context Builder
// =============================================================================

module ContextBuilder =

    /// Build generation context from ApiCall
    let buildContext (store: DsStore) (apiCall: ApiCall) : Result<GenerationContext, GenerationError> =
        // 1. Get ApiDef
        match apiCall.ApiDefId with
        | None ->
            Error {
                ApiCallId = Some apiCall.Id
                Message = $"ApiCall {apiCall.Id} has no ApiDefId"
                ErrorType = MissingApiDefId apiCall.Id
            }
        | Some apiDefId ->
            match Queries.getApiDef apiDefId store with
            | None ->
                Error {
                    ApiCallId = Some apiCall.Id
                    Message = $"ApiDef {apiDefId} not found in store"
                    ErrorType = ApiDefNotFound apiDefId
                }
            | Some apiDef ->
                // 2. Get System
                match Queries.getSystem apiDef.ParentId store with
                | None ->
                    Error {
                        ApiCallId = Some apiCall.Id
                        Message = $"System {apiDef.ParentId} not found in store"
                        ErrorType = SystemNotFound apiDef.ParentId
                    }
                | Some system ->
                    // 3. Get SystemType
                    match system.GetSimulationProperties() |> Option.bind (fun p -> p.SystemType) with
                    | None ->
                        Error (GenerationError.missingSystemType system.Id)
                    | Some systemType ->
                        // 4. Get OriginFlowId
                        match apiCall.OriginFlowId with
                        | None ->
                            Error (GenerationError.missingOriginFlowId apiCall.Id)
                        | Some flowId ->
                            // 5. Get Flow
                            match Queries.getFlow flowId store with
                            | None ->
                                Error {
                                    ApiCallId = Some apiCall.Id
                                    Message = $"Flow {flowId} not found in store"
                                    ErrorType = FlowNotFound flowId
                                }
                            | Some flow ->
                                // 6. Get parent Call
                                let callOpt =
                                    store.Calls.Values
                                    |> Seq.tryFind (fun c -> c.ApiCalls |> Seq.exists (fun ac -> ac.Id = apiCall.Id))

                                match callOpt with
                                | None ->
                                    Error {
                                        ApiCallId = Some apiCall.Id
                                        Message = $"Parent Call not found for ApiCall {apiCall.Id}"
                                        ErrorType = ParentCallNotFound apiCall.Id
                                    }
                                | Some call ->
                                    // 7. Get Work
                                    let workOpt = Queries.getWork call.ParentId store
                                    let workName = workOpt |> Option.map (fun w -> w.Name) |> Option.defaultValue ""

                                    // 8. Extract data types from ValueSpec
                                    let inputDataType = IecDataType.fromValueSpec apiCall.InputSpec
                                    let outputDataType = IecDataType.fromValueSpec apiCall.OutputSpec

                                    // 9. Build context
                                    Ok {
                                        ApiCallId = apiCall.Id
                                        ApiDefName = apiDef.Name
                                        SystemType = systemType
                                        FlowName = flow.Name
                                        WorkName = workName
                                        CallName = call.Name
                                        DeviceAlias = call.DevicesAlias
                                        HasInputTag = apiCall.InTag.IsSome
                                        HasOutputTag = apiCall.OutTag.IsSome
                                        InputDataType = inputDataType
                                        OutputDataType = outputDataType
                                    }

    /// Get all ApiCalls from store
    let getAllApiCalls (store: DsStore) : ApiCall seq =
        store.ApiCalls.Values

    /// Build all generation contexts
    let buildAllContexts (store: DsStore) : GenerationContext list * GenerationError list =
        let results =
            getAllApiCalls store
            |> Seq.map (buildContext store)
            |> Seq.toList

        let errors = results |> List.choose (function Error e -> Some e | Ok _ -> None)
        let contexts = results |> List.choose (function Ok c -> Some c | Error _ -> None)

        (contexts, errors)
