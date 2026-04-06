namespace Ds2.Editor

open System
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery

module internal PasteDeviceOps =

    type DevicePasteState = {
        ClonedSystems: Map<string, DsSystem * Map<Guid, Guid>>
    }

    let initialDevicePasteState = { ClonedSystems = Map.empty }

    type DeviceFlowCtx = {
        Store: DsStore
        ProjectId: Guid
        TargetFlowName: string
    }

    let private cloneApiCall (sourceApiCall: ApiCall) (mapApiDefId: Guid option -> Guid option) : ApiCall =
        let cloneIOTag (tagOpt: IOTag option) : IOTag option =
            tagOpt |> Option.map (fun t -> IOTag(t.Name, t.Address, t.Description))
        let cloned = ApiCall(sourceApiCall.Name)
        cloned.InTag <- cloneIOTag sourceApiCall.InTag
        cloned.OutTag <- cloneIOTag sourceApiCall.OutTag
        cloned.ApiDefId <- mapApiDefId sourceApiCall.ApiDefId
        cloned.InputSpec <- sourceApiCall.InputSpec
        cloned.OutputSpec <- sourceApiCall.OutputSpec
        cloned

    let private ensureTargetDeviceSystem
        (store: DsStore) (projectId: Guid) (targetFlowName: string) (devAlias: string) (sourceSystemId: Guid)
        (state: DevicePasteState) : DevicePasteState * Map<Guid, Guid> =
        let targetName = $"{targetFlowName}_{devAlias}"
        match Map.tryFind targetName state.ClonedSystems with
        | Some (_, mapping) -> state, mapping
        | None ->
            let existing =
                Queries.passiveSystemsOf projectId store
                |> List.tryFind (fun s -> s.Name = targetName)
            let targetSystem, mapping =
                match existing with
                | Some sys ->
                    let targetApiDefs = Queries.apiDefsOf sys.Id store
                    let sourceApiDefs = Queries.apiDefsOf sourceSystemId store
                    let mapping =
                        sourceApiDefs
                        |> List.choose (fun src ->
                            targetApiDefs
                            |> List.tryFind (fun t -> t.Name = src.Name)
                            |> Option.map (fun t -> src.Id, t.Id))
                        |> Map.ofList
                    sys, mapping
                | None ->
                    let newSystem = DsSystem(targetName)

                    // 원본 System의 SystemType 복사
                    match Queries.getSystem sourceSystemId store with
                    | Some sourceSystem ->
                        sourceSystem.GetSimulationProperties()
                        |> Option.bind (fun p -> p.SystemType)
                        |> Option.iter (fun sysType ->
                            match newSystem.GetSimulationProperties() with
                            | Some props -> props.SystemType <- Some sysType
                            | None ->
                                let props = SimulationSystemProperties()
                                props.SystemType <- Some sysType
                                newSystem.SetSimulationProperties(props))
                    | None -> ()

                    store.TrackAdd(store.Systems, newSystem)
                    store.TrackMutate(store.Projects, projectId, fun p ->
                        p.PassiveSystemIds.Add(newSystem.Id))
                    let newFlow = Flow($"{devAlias}_Flow", newSystem.Id)
                    store.TrackAdd(store.Flows, newFlow)
                    let sourceApiDefs = Queries.apiDefsOf sourceSystemId store

                    // Work 생성 및 수집
                    let createdWorks = ResizeArray<Work>()
                    let mapping =
                        sourceApiDefs
                        |> List.map (fun src ->
                            let cloned = ApiDef(src.Name, newSystem.Id)
                            cloned.IsPush <- src.IsPush
                            let work = Work(newFlow.Name, src.Name, newFlow.Id)
                            // 원본 ApiDef의 TxGuid Work에서 SimulationProperties(Duration 등) 복사
                            src.TxGuid
                            |> Option.bind (fun srcWorkId -> Queries.getWork srcWorkId store)
                            |> Option.iter (fun srcWork -> srcWork.GetSimulationProperties() |> Option.iter (fun p -> work.SetSimulationProperties(p.DeepCopy())))
                            store.TrackAdd(store.Works, work)
                            createdWorks.Add(work)
                            cloned.TxGuid <- Some work.Id
                            cloned.RxGuid <- Some work.Id
                            store.TrackAdd(store.ApiDefs, cloned)
                            src.Id, cloned.Id)
                        |> Map.ofList

                    // 생성된 Work들 사이에 상호 리셋 Arrow 생성 (공통 함수 사용)
                    let workList = createdWorks |> Seq.toList
                    DirectDeviceOps.createMutualResetArrows store newSystem.Id workList

                    newSystem, mapping
            { ClonedSystems = Map.add targetName (targetSystem, mapping) state.ClonedSystems }, mapping

    let private copyApiCallsWithMapping (store: DsStore) (sourceCall: Call) (targetCallId: Guid) (mapApiDefId: Guid option -> Guid option) =
        for apiCall in sourceCall.ApiCalls do
            let copied = cloneApiCall apiCall mapApiDefId
            store.TrackAdd(store.ApiCalls, copied)
            store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(copied))

    let private shareApiCalls (store: DsStore) (sourceCall: Call) (targetCallId: Guid) =
        for apiCall in sourceCall.ApiCalls do
            store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(apiCall))

    let private tryFindPassiveSystemId (store: DsStore) (apiDefIdOpt: Guid option) : Guid option =
        apiDefIdOpt
        |> Option.bind (fun aid -> Queries.getApiDef aid store)
        |> Option.bind (fun d ->
            if Queries.allProjects store |> List.exists (fun p -> p.PassiveSystemIds.Contains(d.ParentId))
            then Some d.ParentId else None)

    let private copyApiCallsAcrossFlows
        (store: DsStore) (projectId: Guid) (targetFlowName: string)
        (sourceCall: Call) (targetCallId: Guid) (state: DevicePasteState)
        : DevicePasteState =
        sourceCall.ApiCalls
        |> Seq.fold (fun accState (apiCall: ApiCall) ->
            let sourceSystemIdOpt = tryFindPassiveSystemId store apiCall.ApiDefId
            match sourceSystemIdOpt with
            | None ->
                let copied = cloneApiCall apiCall id
                store.TrackAdd(store.ApiCalls, copied)
                store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(copied))
                accState
            | Some sourceSystemId ->
                let devAlias =
                    match apiCall.Name.IndexOf('.') with
                    | -1 -> sourceCall.DevicesAlias
                    | idx -> apiCall.Name.[..idx - 1]
                let newState, apiDefMapping = ensureTargetDeviceSystem store projectId targetFlowName devAlias sourceSystemId accState
                let copied = cloneApiCall apiCall (fun srcIdOpt ->
                    srcIdOpt |> Option.bind (fun id -> Map.tryFind id apiDefMapping) |> Option.orElse srcIdOpt)
                store.TrackAdd(store.ApiCalls, copied)
                store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(copied))
                newState
        ) state

    let copyApiCallsForPaste
        (store: DsStore)
        (context: CallCopyContext)
        (sourceCall: Call)
        (targetCallId: Guid)
        (deviceState: DevicePasteState)
        (deviceFlowCtxOpt: DeviceFlowCtx option) =
        match context with
        | SameWork ->
            shareApiCalls store sourceCall targetCallId
            deviceState
        | DifferentWork ->
            copyApiCallsWithMapping store sourceCall targetCallId id
            deviceState
        | DifferentFlow ->
            match deviceFlowCtxOpt with
            | Some ctx -> copyApiCallsAcrossFlows ctx.Store ctx.ProjectId ctx.TargetFlowName sourceCall targetCallId deviceState
            | None ->
                copyApiCallsWithMapping store sourceCall targetCallId id
                deviceState

    let makeDeviceFlowCtx (store: DsStore) (targetFlowId: Guid) : DeviceFlowCtx option =
        match Queries.getFlow targetFlowId store with
        | None -> None
        | Some targetFlow ->
            let projectIdOpt =
                Queries.getSystem targetFlow.ParentId store
                |> Option.bind (fun s -> StoreHierarchyQueries.findProjectOfSystem store s.Id)
            projectIdOpt |> Option.map (fun pid ->
                { Store = store; ProjectId = pid; TargetFlowName = targetFlow.Name })
