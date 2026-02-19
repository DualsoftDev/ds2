module Ds2.UI.Core.DeviceOps

open System
open Ds2.Core

type private DeviceBatchState = {
    CommandsRev: EditorCommand list
    PendingSystems: Map<string, DsSystem>
    PendingFlows: Map<string, Flow>
    PendingWorks: Map<string * Guid, Work>
    PendingApiDefs: Map<string * Guid, ApiDef>
    NewSystemIds: Set<Guid>
    PendingWorkOrderRev: Map<string, Work list>
}

let private initialState = {
    CommandsRev = []
    PendingSystems = Map.empty
    PendingFlows = Map.empty
    PendingWorks = Map.empty
    PendingApiDefs = Map.empty
    NewSystemIds = Set.empty
    PendingWorkOrderRev = Map.empty
}

let private addCommand (cmd: EditorCommand) (state: DeviceBatchState) =
    { state with CommandsRev = cmd :: state.CommandsRev }

let private parseCallName (callName: string) =
    let parts = callName.Split([| '.' |], 2)
    let devAlias = parts.[0]
    let apiName = if parts.Length > 1 then parts.[1] else ""
    devAlias, apiName

let private shouldCreateDeviceSystem (createDeviceSystem: bool) (projectId: Guid) (apiName: string) =
    createDeviceSystem
    && projectId <> Guid.Empty
    && not (String.IsNullOrEmpty apiName)

let private tryFindExistingSystem (store: DsStore) (projectId: Guid) (devAlias: string) =
    DsQuery.passiveSystemsOf projectId store
    |> List.tryFind (fun s -> s.Name = devAlias)

let private ensureSystem
    (store: DsStore)
    (projectId: Guid)
    (devAlias: string)
    (state: DeviceBatchState)
    : DsSystem * DeviceBatchState =
    match Map.tryFind devAlias state.PendingSystems with
    | Some system -> system, state
    | None ->
        match tryFindExistingSystem store projectId devAlias with
        | Some existing ->
            existing, { state with PendingSystems = Map.add devAlias existing state.PendingSystems }
        | None ->
            let system = DsSystem(devAlias)
            let flow = Flow($"{devAlias}_Flow", system.Id)
            let nextState =
                state
                |> addCommand (AddSystem(system, projectId, false))
                |> addCommand (AddFlow flow)
            let nextState = {
                nextState with
                    PendingSystems = Map.add devAlias system nextState.PendingSystems
                    PendingFlows = Map.add devAlias flow nextState.PendingFlows
                    NewSystemIds = Set.add system.Id nextState.NewSystemIds
            }
            system, nextState

let private appendWorkOrder (devAlias: string) (work: Work) (state: DeviceBatchState) =
    let current = Map.tryFind devAlias state.PendingWorkOrderRev |> Option.defaultValue []
    { state with PendingWorkOrderRev = Map.add devAlias (work :: current) state.PendingWorkOrderRev }

let private ensurePendingWork
    (devAlias: string)
    (apiName: string)
    (systemId: Guid)
    (state: DeviceBatchState)
    : DeviceBatchState =
    let key = (apiName, systemId)
    if not (Set.contains systemId state.NewSystemIds) || Map.containsKey key state.PendingWorks then
        state
    else
        let flow = Map.find devAlias state.PendingFlows
        let work = Work(apiName, flow.Id)
        let nextState =
            state
            |> addCommand (AddWork work)
            |> appendWorkOrder devAlias work
        { nextState with PendingWorks = Map.add key work nextState.PendingWorks }

let private ensureApiDef
    (store: DsStore)
    (system: DsSystem)
    (apiName: string)
    (state: DeviceBatchState)
    : ApiDef * DeviceBatchState =
    let key = (apiName, system.Id)
    match Map.tryFind key state.PendingApiDefs with
    | Some apiDef -> apiDef, state
    | None ->
        match DsQuery.apiDefsOf system.Id store |> List.tryFind (fun d -> d.Name = apiName) with
        | Some existing ->
            existing, { state with PendingApiDefs = Map.add key existing state.PendingApiDefs }
        | None ->
            let apiDef = ApiDef(apiName, system.Id)
            match Map.tryFind key state.PendingWorks with
            | Some work ->
                apiDef.Properties.IsPush <- true
                apiDef.Properties.TxGuid <- Some work.Id
            | None -> ()

            let nextState = state |> addCommand (AddApiDef apiDef)
            apiDef, { nextState with PendingApiDefs = Map.add key apiDef nextState.PendingApiDefs }

let private buildArrowCommands (state: DeviceBatchState) : EditorCommand list =
    state.PendingWorkOrderRev
    |> Map.toList
    |> List.collect (fun (devAlias, workOrderRev) ->
        match Map.tryFind devAlias state.PendingFlows with
        | None -> []
        | Some flow ->
            workOrderRev
            |> List.rev
            |> List.pairwise
            |> List.map (fun (src, dst) ->
                AddArrowWork(ArrowBetweenWorks(flow.Id, src.Id, dst.Id, ArrowType.ResetReset))))

let buildAddCallsWithDeviceCmds
    (store: DsStore)
    (projectId: Guid)
    (workId: Guid)
    (callNames: string list)
    (createDeviceSystem: bool)
    : EditorCommand list =
    if callNames.IsEmpty then
        []
    else
        let finalState =
            callNames
            |> List.fold (fun state callName ->
                let devAlias, apiName = parseCallName callName
                let call = Call(devAlias, apiName, workId)
                let withCall = state |> addCommand (AddCall call)

                if not (shouldCreateDeviceSystem createDeviceSystem projectId apiName) then
                    withCall
                else
                    let system, withSystem = ensureSystem store projectId devAlias withCall
                    let withWork = ensurePendingWork devAlias apiName system.Id withSystem
                    let apiDef, withApiDef = ensureApiDef store system apiName withWork

                    let apiCall = ApiCall(callName)
                    apiCall.ApiDefId <- Some apiDef.Id
                    withApiDef |> addCommand (AddApiCallToCall(call.Id, apiCall, ValueSpec.undefined))
            ) initialState

        List.rev finalState.CommandsRev @ buildArrowCommands finalState
