namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store


module internal DirectDeviceOps =
    type private DeviceBatchState = {
        PendingSystems     : Map<string, DsSystem>
        PendingFlows        : Map<string, Flow>
        PendingWorks       : Map<string * Guid, Work>
        PendingApiDefs     : Map<string * Guid, ApiDef>
        NewSystemIds       : Set<Guid>
        PendingWorkOrderRev: Map<string, Work list>
    }

    /// 순서대로 나열된 Work들 사이에 상호 리셋 Arrow 생성 (공통 헬퍼)
    let createMutualResetArrows (store: DsStore) (systemId: Guid) (works: Work list) =
        if works.Length > 1 then
            let existingArrows = DsQuery.arrowWorksOf systemId store

            // 마지막 Work에 IsFinished 자동 설정
            match List.tryLast works with
            | Some lastWork when not lastWork.Properties.IsFinished ->
                store.TrackMutate(store.Works, lastWork.Id, fun w -> w.Properties.IsFinished <- true)
            | _ -> ()

            // Work 쌍마다 상호 리셋 Arrow 생성
            works
            |> List.pairwise
            |> List.iter (fun (src, dst) ->
                let alreadyExists =
                    existingArrows |> List.exists (fun a ->
                        a.ArrowType = ArrowType.ResetReset &&
                        ((a.SourceId = src.Id && a.TargetId = dst.Id) ||
                         (a.SourceId = dst.Id && a.TargetId = src.Id)))
                if not alreadyExists then
                    let arrow = ArrowBetweenWorks(systemId, src.Id, dst.Id, ArrowType.ResetReset)
                    store.TrackAdd(store.ArrowWorks, arrow))

    let private initialState = {
        PendingSystems      = Map.empty
        PendingFlows        = Map.empty
        PendingWorks        = Map.empty
        PendingApiDefs      = Map.empty
        NewSystemIds        = Set.empty
        PendingWorkOrderRev = Map.empty
    }

    let private parseCallName (callName: string) =
        let parts = callName.Split([| '.' |], 2)
        parts.[0], (if parts.Length > 1 then parts.[1] else "")

    let hasCreatableApiName (callName: string) =
        let _, apiName = parseCallName callName
        not (String.IsNullOrEmpty apiName)

    let private ensureSystem (store: DsStore) (projectId: Guid) (flowName: string) (devAlias: string) (systemType: string option) (state: DeviceBatchState) =
        let systemName = $"{flowName}_{devAlias}"
        match Map.tryFind systemName state.PendingSystems with
        | Some system -> system, state
        | None ->
            match DsQuery.passiveSystemsOf projectId store |> List.tryFind (fun s -> s.Name = systemName) with
            | Some existing ->
                // 기존 System에 SystemType 설정 (없으면)
                match systemType with
                | Some sysType when Option.isNone existing.Properties.SystemType ->
                    store.TrackMutate(store.Systems, existing.Id, fun s ->
                        s.Properties.SystemType <- Some sysType)
                | _ -> ()

                match DsQuery.flowsOf existing.Id store with
                | flow :: _ ->
                    let existingWorks = DsQuery.worksOf flow.Id store
                    let existingWorkOrder =
                        Map.tryFind devAlias state.PendingWorkOrderRev |> Option.defaultValue []
                        |> List.append existingWorks
                    let existingPendingWorks =
                        existingWorks
                        |> List.fold (fun acc w -> Map.add (w.Name, existing.Id) w acc)
                            state.PendingWorks
                    existing, {
                        state with
                            PendingSystems = Map.add systemName existing state.PendingSystems
                            PendingFlows = Map.add devAlias flow state.PendingFlows
                            NewSystemIds = Set.add existing.Id state.NewSystemIds
                            PendingWorkOrderRev = Map.add devAlias existingWorkOrder state.PendingWorkOrderRev
                            PendingWorks = existingPendingWorks
                    }
                | [] ->
                    // Flow 없는 기존 System — Flow를 새로 생성
                    let flow = Flow($"{devAlias}_Flow", existing.Id)
                    store.TrackAdd(store.Flows, flow)
                    existing, {
                        state with
                            PendingSystems = Map.add systemName existing state.PendingSystems
                            PendingFlows = Map.add devAlias flow state.PendingFlows
                            NewSystemIds = Set.add existing.Id state.NewSystemIds
                    }
            | None ->
                let system = DsSystem(systemName)
                // 새 System에 SystemType 설정
                systemType |> Option.iter (fun sysType -> system.Properties.SystemType <- Some sysType)
                let flow = Flow($"{devAlias}_Flow", system.Id)
                store.TrackAdd(store.Systems, system)
                store.TrackMutate(store.Projects, projectId, fun p ->
                    p.PassiveSystemIds.Add(system.Id))
                store.TrackAdd(store.Flows, flow)
                let next = {
                    state with
                        PendingSystems = Map.add systemName system state.PendingSystems
                        PendingFlows = Map.add devAlias flow state.PendingFlows
                        NewSystemIds = Set.add system.Id state.NewSystemIds
                }
                system, next

    let private ensurePendingWork (devAlias: string) (apiName: string) (systemId: Guid) (store: DsStore) (state: DeviceBatchState) =
        let key = (apiName, systemId)
        if not (Set.contains systemId state.NewSystemIds) || Map.containsKey key state.PendingWorks then state
        else
            let flow = Map.find devAlias state.PendingFlows
            // 기존 시스템에 이미 같은 이름의 Work가 있으면 재사용, 없으면 생성
            let work =
                DsQuery.worksOf flow.Id store
                |> List.tryFind (fun w -> w.Name = apiName)
                |> Option.defaultWith (fun () ->
                    let w = Work(flow.Name, apiName, flow.Id)
                    store.TrackAdd(store.Works, w)
                    w)
            let current = Map.tryFind devAlias state.PendingWorkOrderRev |> Option.defaultValue []
            { state with
                PendingWorks = Map.add key work state.PendingWorks
                PendingWorkOrderRev = Map.add devAlias (work :: current) state.PendingWorkOrderRev }

    let private ensureApiDef (store: DsStore) (system: DsSystem) (apiName: string) (state: DeviceBatchState) =
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
                    apiDef.Properties.IsPush <- false
                    apiDef.Properties.TxGuid <- Some work.Id
                    apiDef.Properties.RxGuid <- Some work.Id
                | None -> ()
                store.TrackAdd(store.ApiDefs, apiDef)
                apiDef, { state with PendingApiDefs = Map.add key apiDef state.PendingApiDefs }

    let private createAndRegisterApiCall (store: DsStore) (call: Call) (name: string) (apiDefId: Guid) =
        let apiCall = ApiCall(name)
        apiCall.ApiDefId <- Some apiDefId

        // Set OriginFlowId by traversing: Call → Work → Flow
        apiCall.OriginFlowId <-
            DsQuery.getWork call.ParentId store
            |> Option.map (fun work -> work.ParentId)

        store.TrackAdd(store.ApiCalls, apiCall)
        store.TrackMutate(store.Calls, call.Id, fun c -> c.ApiCalls.Add(apiCall))

    let private buildWorkArrows (store: DsStore) (state: DeviceBatchState) =
        state.PendingWorkOrderRev
        |> Map.toList
        |> List.iter (fun (devAlias, workOrderRev) ->
            match Map.tryFind devAlias state.PendingFlows with
            | None -> ()
            | Some flow ->
                let systemId = flow.ParentId
                // workOrderRev는 역순이므로 정순으로 변환 후 공통 함수 호출
                let worksInOrder = List.rev workOrderRev
                createMutualResetArrows store systemId worksInOrder)

    let addCallsWithDevice (store: DsStore) (projectId: Guid) (workId: Guid) (callNames: string list) (createDeviceSystem: bool) (systemType: string option) =
        if callNames.IsEmpty then ()
        else
            let flowName =
                DsQuery.getWork workId store
                |> Option.bind (fun w -> DsQuery.getFlow w.ParentId store)
                |> Option.map (fun f -> f.Name)
                |> Option.defaultValue ""

            let finalState =
                callNames
                |> List.fold (fun state callName ->
                    let devAlias, apiName = parseCallName callName
                    let call = Call(devAlias, apiName, workId)
                    store.TrackAdd(store.Calls, call)

                    if not (createDeviceSystem && not (String.IsNullOrEmpty apiName)) then state
                    else
                        let system, withSystem = ensureSystem store projectId flowName devAlias systemType state
                        let withWork = ensurePendingWork devAlias apiName system.Id store withSystem
                        let apiDef, withApiDef = ensureApiDef store system apiName withWork
                        createAndRegisterApiCall store call callName apiDef.Id
                        withApiDef
                ) initialState

            buildWorkArrows store finalState

    /// 이미 생성된 Call 목록에 대해 Device System + ApiDef + ApiCall 연결.
    /// WithTransaction 내부에서 호출해야 함.
    let linkCallsToDevices (store: DsStore) (projectId: Guid) (flowName: string) (calls: (Call * string) list) (systemType: string option) =
        if calls.IsEmpty then ()
        else
            let finalState =
                calls
                |> List.fold (fun state (call, callName) ->
                    let apiName = call.ApiName
                    if String.IsNullOrEmpty apiName then state
                    else
                        let devAlias = call.DevicesAlias
                        let system, withSystem = ensureSystem store projectId flowName devAlias systemType state
                        let withWork = ensurePendingWork devAlias apiName system.Id store withSystem
                        let apiDef, withApiDef = ensureApiDef store system apiName withWork
                        createAndRegisterApiCall store call callName apiDef.Id
                        withApiDef
                ) initialState
            buildWorkArrows store finalState

    let addCallWithLinkedApiDefs (store: DsStore) (workId: Guid) (devicesAlias: string) (apiName: string) (apiDefIds: Guid seq) : Guid =
        let call = Call(devicesAlias, apiName, workId)
        store.TrackAdd(store.Calls, call)
        apiDefIds
        |> Seq.choose (fun id -> DsQuery.getApiDef id store)
        |> Seq.iter (fun apiDef ->
            createAndRegisterApiCall store call $"{devicesAlias}.{apiDef.Name}" apiDef.Id)
        call.Id

    /// ApiCall 복제 모드: 1개 Call + N개 Device System/ApiDef/ApiCall 생성.
    /// deviceAliases = ["Conv_1"; "Conv_2"; ...], apiName = "ADV"
    /// → Call(Conv, ADV) 안에 ApiCall(Conv_1.ADV), ApiCall(Conv_2.ADV) 각각 별도 Device System에 연결.
    let addCallWithMultipleDevices
        (store: DsStore) (projectId: Guid) (workId: Guid)
        (callDevicesAlias: string) (apiName: string) (deviceAliases: string list) (systemType: string option) : Guid =
        let call = Call(callDevicesAlias, apiName, workId)
        store.TrackAdd(store.Calls, call)

        let flowName =
            DsQuery.getWork workId store
            |> Option.bind (fun w -> DsQuery.getFlow w.ParentId store)
            |> Option.map (fun f -> f.Name)
            |> Option.defaultValue ""

        let finalState =
            deviceAliases
            |> List.fold (fun state devAlias ->
                let system, withSystem = ensureSystem store projectId flowName devAlias systemType state
                let withWork = ensurePendingWork devAlias apiName system.Id store withSystem
                let apiDef, withApiDef = ensureApiDef store system apiName withWork
                createAndRegisterApiCall store call $"{devAlias}.{apiName}" apiDef.Id
                withApiDef
            ) initialState

        buildWorkArrows store finalState
        call.Id