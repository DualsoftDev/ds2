namespace Ds2.Editor

open System
open Ds2.Core
open Ds2.Core.Store

/// Flow 간 Call 복사/이동 시 device system 처리 모드.
/// - CloneSystem: {newFlow}_{devAlias} 새 system + ApiDef 복제 + ApiCall.ApiDefId 재매핑 (기본).
/// - RenameSourceSystem: 원본 device system 의 Name 을 {newFlow}_{devAlias} 로 rename. ApiDefId 그대로 재사용.
/// - KeepReferences: device system 자체는 손대지 않음. ApiCall.ApiDefId 도 원본 그대로 (Call 만 옮김).
[<RequireQualifiedAccess>]
type CrossFlowDeviceMode =
    | CloneSystem
    | RenameSourceSystem
    | KeepReferences

/// RenameSourceSystem 모드의 충돌 사유.
type RenameDeviceConflict =
    /// 원본 system 의 ApiDef 가 다른 Flow Call 한테도 참조 중. Rename 시 다른 참조가 깨짐.
    | SharedWithOtherCalls of otherCallIds: Guid list
    /// 대상 이름 {newFlow}_{devAlias} 가 이미 다른 system 으로 존재.
    | NameTaken of existingSystemId: Guid


module internal PasteDeviceOps =

    type DevicePasteState = {
        ClonedSystems: Map<string, DsSystem * Map<Guid, Guid>>
    }

    let initialDevicePasteState = { ClonedSystems = Map.empty }

    type DeviceFlowCtx = {
        Store: DsStore
        ProjectId: Guid
        TargetFlowId: Guid
        TargetFlowName: string
        Mode: CrossFlowDeviceMode
    }

    let private cloneApiCall
        (sourceApiCall: ApiCall)
        (mapApiDefId: Guid option -> Guid option)
        (targetOriginFlowId: Guid option) : ApiCall =
        let cloneIOTag (tagOpt: IOTag option) : IOTag option =
            tagOpt |> Option.map (fun t -> IOTag(t.Name, t.Address, t.Description))
        let cloned = ApiCall(sourceApiCall.Name)
        cloned.InTag <- cloneIOTag sourceApiCall.InTag
        cloned.OutTag <- cloneIOTag sourceApiCall.OutTag
        cloned.ApiDefId <- mapApiDefId sourceApiCall.ApiDefId
        cloned.InputSpec <- sourceApiCall.InputSpec
        cloned.OutputSpec <- sourceApiCall.OutputSpec
        cloned.OriginFlowId <-
            match targetOriginFlowId with
            | Some _ -> targetOriginFlowId
            | None -> sourceApiCall.OriginFlowId
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
                        sourceSystem.SystemType
                        |> Option.iter (fun sysType ->
                            newSystem.SystemType <- Some sysType)
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
                            cloned.ActionType <- src.ActionType
                            cloned.SensingType <- src.SensingType
                            let work = Work(newFlow.Name, src.Name, newFlow.Id)
                            // 원본 ApiDef의 TxGuid Work에서 SimulationProperties와 Duration 복사
                            src.TxGuid
                            |> Option.bind (fun srcWorkId -> Queries.getWork srcWorkId store)
                            |> Option.iter (fun srcWork ->
                                srcWork.GetSimulationProperties() |> Option.iter (fun p -> work.SetSimulationProperties(p.DeepCopy()))
                                work.Duration <- srcWork.Duration)
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

    let private copyApiCallsWithMapping
        (store: DsStore)
        (sourceCall: Call)
        (targetCallId: Guid)
        (mapApiDefId: Guid option -> Guid option)
        (targetOriginFlowId: Guid option) =
        for apiCall in sourceCall.ApiCalls do
            let copied = cloneApiCall apiCall mapApiDefId targetOriginFlowId
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
        (store: DsStore) (projectId: Guid) (targetFlowId: Guid) (targetFlowName: string)
        (sourceCall: Call) (targetCallId: Guid) (state: DevicePasteState)
        : DevicePasteState =
        sourceCall.ApiCalls
        |> Seq.fold (fun accState (apiCall: ApiCall) ->
            let sourceSystemIdOpt = tryFindPassiveSystemId store apiCall.ApiDefId
            match sourceSystemIdOpt with
            | None ->
                let copied = cloneApiCall apiCall id (Some targetFlowId)
                store.TrackAdd(store.ApiCalls, copied)
                store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(copied))
                accState
            | Some sourceSystemId ->
                let devAlias =
                    Queries.splitApiCallName apiCall.Name
                    |> Option.map fst
                    |> Option.defaultValue sourceCall.DevicesAlias
                let newState, apiDefMapping = ensureTargetDeviceSystem store projectId targetFlowName devAlias sourceSystemId accState
                let copied =
                    cloneApiCall
                        apiCall
                        (fun srcIdOpt ->
                            srcIdOpt |> Option.bind (fun id -> Map.tryFind id apiDefMapping) |> Option.orElse srcIdOpt)
                        (Some targetFlowId)
                store.TrackAdd(store.ApiCalls, copied)
                store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(copied))
                newState
        ) state

    /// RenameSourceSystem 모드: source device system 의 Name 을 {targetFlowName}_{devAlias} 로 변경하고
    /// ApiDefId 는 *원본 그대로 재사용*. 같은 paste 안에서 동일 device 가 중복 등장하면 첫 호출만 rename.
    let private renameAndReuseDevice
        (store: DsStore) (targetFlowName: string)
        (sourceCall: Call) (targetCallId: Guid) (state: DevicePasteState) (targetFlowId: Guid)
        : DevicePasteState =
        sourceCall.ApiCalls
        |> Seq.fold (fun accState (apiCall: ApiCall) ->
            let sourceSystemIdOpt = tryFindPassiveSystemId store apiCall.ApiDefId
            match sourceSystemIdOpt with
            | None ->
                let copied = cloneApiCall apiCall id (Some targetFlowId)
                store.TrackAdd(store.ApiCalls, copied)
                store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(copied))
                accState
            | Some sourceSystemId ->
                let devAlias =
                    Queries.splitApiCallName apiCall.Name
                    |> Option.map fst
                    |> Option.defaultValue sourceCall.DevicesAlias
                let renameKey = $"{targetFlowName}_{devAlias}"
                let newState =
                    if Map.containsKey renameKey accState.ClonedSystems then accState
                    else
                        store.TrackMutate(store.Systems, sourceSystemId, fun s -> s.Name <- renameKey)
                        match Queries.getSystem sourceSystemId store with
                        | Some sys ->
                            { ClonedSystems = Map.add renameKey (sys, Map.empty) accState.ClonedSystems }
                        | None -> accState
                let copied = cloneApiCall apiCall id (Some targetFlowId)
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
            copyApiCallsWithMapping store sourceCall targetCallId id None
            deviceState
        | DifferentFlow ->
            match deviceFlowCtxOpt with
            | Some ctx ->
                match ctx.Mode with
                | CrossFlowDeviceMode.CloneSystem ->
                    copyApiCallsAcrossFlows ctx.Store ctx.ProjectId ctx.TargetFlowId ctx.TargetFlowName sourceCall targetCallId deviceState
                | CrossFlowDeviceMode.RenameSourceSystem ->
                    renameAndReuseDevice ctx.Store ctx.TargetFlowName sourceCall targetCallId deviceState ctx.TargetFlowId
                | CrossFlowDeviceMode.KeepReferences ->
                    copyApiCallsWithMapping store sourceCall targetCallId id (Some ctx.TargetFlowId)
                    deviceState
            | None ->
                copyApiCallsWithMapping store sourceCall targetCallId id None
                deviceState

    let makeDeviceFlowCtx (store: DsStore) (targetFlowId: Guid) (mode: CrossFlowDeviceMode) : DeviceFlowCtx option =
        match Queries.getFlow targetFlowId store with
        | None -> None
        | Some targetFlow ->
            let projectIdOpt =
                Queries.getSystem targetFlow.ParentId store
                |> Option.bind (fun s -> StoreHierarchyQueries.findProjectOfSystem store s.Id)
            projectIdOpt |> Option.map (fun pid ->
                { Store = store
                  ProjectId = pid
                  TargetFlowId = targetFlowId
                  TargetFlowName = targetFlow.Name
                  Mode = mode })

    /// Rename 모드 사전 검증: 충돌 사유들을 수집해 반환. 빈 리스트면 OK.
    /// - 같은 device system 이 다른 Flow Call 한테 공유되면 SharedWithOtherCalls.
    /// - {targetFlowName}_{devAlias} 가 이미 다른 system 으로 존재하면 NameTaken.
    let collectRenameConflicts
        (store: DsStore)
        (sourceCallIds: Guid list)
        (targetFlowName: string)
        (projectId: Guid)
        : (string * RenameDeviceConflict) list =
        // 1) source Call 들이 참조하는 source device system 집합 + 그 system 별 devAlias.
        let sourceCallSet = Set.ofList sourceCallIds
        let deviceUsages =
            sourceCallIds
            |> List.choose (Queries.getCall >> fun f -> f store)
            |> List.collect (fun sc ->
                sc.ApiCalls
                |> Seq.choose (fun ac ->
                    tryFindPassiveSystemId store ac.ApiDefId
                    |> Option.map (fun sysId ->
                        let devAlias =
                            Queries.splitApiCallName ac.Name
                            |> Option.map fst
                            |> Option.defaultValue sc.DevicesAlias
                        sysId, devAlias))
                |> Seq.toList)
            |> List.distinct
        let conflicts = ResizeArray<string * RenameDeviceConflict>()
        for sysId, devAlias in deviceUsages do
            let consumerCalls = Queries.findCallsReferencingPassiveSystem sysId store
            let otherCallIds =
                consumerCalls
                |> List.map (fun c -> c.Id)
                |> List.filter (fun id -> not (sourceCallSet.Contains id))
            if not (List.isEmpty otherCallIds) then
                conflicts.Add(devAlias, SharedWithOtherCalls otherCallIds)
            let targetName = $"{targetFlowName}_{devAlias}"
            match Queries.passiveSystemsOf projectId store |> List.tryFind (fun s -> s.Name = targetName && s.Id <> sysId) with
            | Some existing -> conflicts.Add(devAlias, NameTaken existing.Id)
            | None -> ()
        conflicts |> Seq.toList
