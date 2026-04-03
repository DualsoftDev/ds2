namespace Ds2.Store

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Store.DsQuery

module internal ImportPlanDeviceOps =

    type private DeviceBatchState = {
        PendingSystems: Map<string, DsSystem>
        PendingFlows: Map<string, Flow>
        PendingWorks: Map<string * Guid, Work>
        PendingApiDefs: Map<string * Guid, ApiDef>
        NewSystemIds: Set<Guid>
        PendingWorkOrderRev: Map<string, Work list>
        PlannedArrowPairs: Set<Guid * Guid>
    }

    let private initialState = {
        PendingSystems = Map.empty
        PendingFlows = Map.empty
        PendingWorks = Map.empty
        PendingApiDefs = Map.empty
        NewSystemIds = Set.empty
        PendingWorkOrderRev = Map.empty
        PlannedArrowPairs = Set.empty
    }

    let hasCreatableApiName (callName: string) =
        let parts = callName.Split([| '.' |], 2)
        let apiName = if parts.Length > 1 then parts.[1] else ""
        not (String.IsNullOrEmpty apiName)

    let private queueOperation operation (operations: ResizeArray<ImportPlanOperation>) =
        operations.Add(operation)

    let private ensureSystem
        (store: DsStore)
        (projectId: Guid)
        (flowName: string)
        (devAlias: string)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        let systemName = $"{flowName}_{devAlias}"
        match Map.tryFind systemName state.PendingSystems with
        | Some system -> system, state
        | None ->
            match Queries.passiveSystemsOf projectId store |> List.tryFind (fun s -> s.Name = systemName) with
            | Some existing ->
                match Queries.flowsOf existing.Id store with
                | flow :: _ ->
                    let existingWorks = Queries.worksOf flow.Id store
                    let existingWorkOrder =
                        Map.tryFind devAlias state.PendingWorkOrderRev
                        |> Option.defaultValue []
                        |> List.append existingWorks
                    let existingPendingWorks =
                        existingWorks
                        |> List.fold (fun acc work -> Map.add (work.Name, existing.Id) work acc) state.PendingWorks
                    existing,
                    { state with
                        PendingSystems = Map.add systemName existing state.PendingSystems
                        PendingFlows = Map.add devAlias flow state.PendingFlows
                        NewSystemIds = Set.add existing.Id state.NewSystemIds
                        PendingWorkOrderRev = Map.add devAlias existingWorkOrder state.PendingWorkOrderRev
                        PendingWorks = existingPendingWorks }
                | [] ->
                    let flow = Flow($"{devAlias}_Flow", existing.Id)
                    queueOperation (AddFlow flow) operations
                    existing,
                    { state with
                        PendingSystems = Map.add systemName existing state.PendingSystems
                        PendingFlows = Map.add devAlias flow state.PendingFlows
                        NewSystemIds = Set.add existing.Id state.NewSystemIds }
            | None ->
                let system = DsSystem(systemName)
                let flow = Flow($"{devAlias}_Flow", system.Id)
                queueOperation (AddSystem system) operations
                queueOperation (LinkSystemToProject(projectId, system.Id, false)) operations
                queueOperation (AddFlow flow) operations
                system,
                { state with
                    PendingSystems = Map.add systemName system state.PendingSystems
                    PendingFlows = Map.add devAlias flow state.PendingFlows
                    NewSystemIds = Set.add system.Id state.NewSystemIds }

    let private ensurePendingWork
        (devAlias: string)
        (apiName: string)
        (systemId: Guid)
        (store: DsStore)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        let key = (apiName, systemId)
        if not (Set.contains systemId state.NewSystemIds) || Map.containsKey key state.PendingWorks then
            state
        else
            let flow = Map.find devAlias state.PendingFlows
            let work =
                Queries.worksOf flow.Id store
                |> List.tryFind (fun existing -> existing.Name = apiName)
                |> Option.defaultWith (fun () ->
                    let created = Work(flow.Name, apiName, flow.Id)
                    created.Properties.Duration <- Some (TimeSpan.FromMilliseconds 500.)
                    queueOperation (AddWork created) operations
                    created)
            let current = Map.tryFind devAlias state.PendingWorkOrderRev |> Option.defaultValue []
            { state with
                PendingWorks = Map.add key work state.PendingWorks
                PendingWorkOrderRev = Map.add devAlias (work :: current) state.PendingWorkOrderRev }

    let private ensureApiDef
        (store: DsStore)
        (system: DsSystem)
        (apiName: string)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        let key = (apiName, system.Id)
        match Map.tryFind key state.PendingApiDefs with
        | Some apiDef -> apiDef, state
        | None ->
            match Queries.apiDefsOf system.Id store |> List.tryFind (fun existing -> existing.Name = apiName) with
            | Some existing ->
                existing, { state with PendingApiDefs = Map.add key existing state.PendingApiDefs }
            | None ->
                let apiDef = ApiDef(apiName, system.Id)
                match Map.tryFind key state.PendingWorks with
                | Some work ->
                    apiDef.Properties.IsPush <- false
                    apiDef.Properties.TxGuid <- Some work.Id
                    apiDef.Properties.RxGuid <- Some work.Id
                | None -> ()
                queueOperation (AddApiDef apiDef) operations
                apiDef, { state with PendingApiDefs = Map.add key apiDef state.PendingApiDefs }

    let private createAndRegisterApiCall
        (call: Call)
        (name: string)
        (apiDefId: Guid)
        (operations: ResizeArray<ImportPlanOperation>) =
        let apiCall = ApiCall(name)
        apiCall.ApiDefId <- Some apiDefId
        call.ApiCalls.Add(apiCall)
        queueOperation (AddApiCall apiCall) operations

    let private buildWorkArrows
        (store: DsStore)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        state.PendingWorkOrderRev
        |> Map.fold (fun currentState devAlias workOrderRev ->
            match Map.tryFind devAlias currentState.PendingFlows with
            | None -> currentState
            | Some flow ->
                let systemId = flow.ParentId
                let existingArrows = Queries.arrowWorksOf systemId store
                let nextPairs =
                    workOrderRev
                    |> List.rev
                    |> List.pairwise
                    |> List.fold (fun pairs (src, dst) ->
                        let pair = (src.Id, dst.Id)
                        let alreadyExists =
                            Set.contains pair pairs
                            || existingArrows |> List.exists (fun arrow ->
                                arrow.ArrowType = ArrowType.ResetReset
                                && ((arrow.SourceId = src.Id && arrow.TargetId = dst.Id)
                                    || (arrow.SourceId = dst.Id && arrow.TargetId = src.Id)))
                        if alreadyExists then
                            pairs
                        else
                            let arrow = ArrowBetweenWorks(systemId, src.Id, dst.Id, ArrowType.ResetReset)
                            queueOperation (AddArrowWork arrow) operations
                            Set.add pair pairs
                    ) currentState.PlannedArrowPairs
                { currentState with PlannedArrowPairs = nextPairs }) state
        |> ignore

    let linkCallsToDevices
        (store: DsStore)
        (projectId: Guid)
        (flowName: string)
        (calls: (Call * string) list)
        (operations: ResizeArray<ImportPlanOperation>) =
        if not calls.IsEmpty then
            let finalState =
                calls
                |> List.fold (fun state (call, callName) ->
                    let apiName = call.ApiName
                    if String.IsNullOrEmpty apiName then
                        state
                    else
                        let devAlias = call.DevicesAlias
                        let system, withSystem = ensureSystem store projectId flowName devAlias operations state
                        let withWork = ensurePendingWork devAlias apiName system.Id store operations withSystem
                        let apiDef, withApiDef = ensureApiDef store system apiName operations withWork
                        createAndRegisterApiCall call callName apiDef.Id operations
                        withApiDef
                ) initialState

            buildWorkArrows store operations finalState
