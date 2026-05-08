namespace Ds2.Core.Store

open System
open System.Collections.Generic
open Ds2.Core

module internal ImportPlanDeviceOps =

    type internal WiringMode =
        | Chain
        | AllPairs
        | NoneMode

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
        (systemNameHint: string option)
        (systemType: string option)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        let systemName = systemNameHint |> Option.defaultWith (fun () -> $"{flowName}_{devAlias}")
        match Map.tryFind systemName state.PendingSystems with
        | Some system -> system, systemName, state
        | None ->
            // DeviceAlias fallback: systemNameHint가 없을 때만 (hint가 있으면 systemName이 고유 키)
            match (if systemNameHint.IsNone then Map.tryFind devAlias state.PendingSystems else None) with
            | Some system -> system, systemName, state
            | None ->
            let passiveSystems = Queries.passiveSystemsOf projectId store
            let passiveMatch =
                if systemNameHint.IsNone then
                    passiveSystems |> List.tryFind (fun s -> s.Name = devAlias)
                    |> Option.orElseWith (fun () -> passiveSystems |> List.tryFind (fun s -> s.Name = systemName))
                else
                    passiveSystems |> List.tryFind (fun s -> s.Name = systemName)
            match passiveMatch with
            | Some existing ->
                match Queries.flowsOf existing.Id store with
                | flow :: _ ->
                    let existingWorks = Queries.worksOf flow.Id store
                    let existingWorkOrder =
                        Map.tryFind systemName state.PendingWorkOrderRev
                        |> Option.defaultValue []
                        |> List.append existingWorks
                    let existingPendingWorks =
                        existingWorks
                        |> List.fold (fun acc work -> Map.add (work.Name, existing.Id) work acc) state.PendingWorks
                    existing, systemName,
                    { state with
                        PendingSystems = Map.add systemName existing state.PendingSystems
                        PendingFlows = Map.add systemName flow state.PendingFlows
                        NewSystemIds = Set.add existing.Id state.NewSystemIds
                        PendingWorkOrderRev = Map.add systemName existingWorkOrder state.PendingWorkOrderRev
                        PendingWorks = existingPendingWorks }
                | [] ->
                    let flow = Flow($"{devAlias}_Flow", existing.Id)
                    queueOperation (AddFlow flow) operations
                    existing, systemName,
                    { state with
                        PendingSystems = Map.add systemName existing state.PendingSystems
                        PendingFlows = Map.add systemName flow state.PendingFlows
                        NewSystemIds = Set.add existing.Id state.NewSystemIds }
            | None ->
                let system = DsSystem(systemName)
                system.SystemType <- systemType
                let flow = Flow($"{devAlias}_Flow", system.Id)
                queueOperation (AddSystem system) operations
                queueOperation (LinkSystemToProject(projectId, system.Id, false)) operations
                queueOperation (AddFlow flow) operations
                system, systemName,
                { state with
                    PendingSystems = Map.add systemName system state.PendingSystems
                    PendingFlows = Map.add systemName flow state.PendingFlows
                    NewSystemIds = Set.add system.Id state.NewSystemIds }

    let private ensurePendingWork
        (deviceKey: string)
        (apiName: string)
        (systemId: Guid)
        (workDuration: TimeSpan option)
        (store: DsStore)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        let key = (apiName, systemId)
        if not (Set.contains systemId state.NewSystemIds) || Map.containsKey key state.PendingWorks then
            state
        else
            let flow = Map.find deviceKey state.PendingFlows
            let work =
                Queries.worksOf flow.Id store
                |> List.tryFind (fun existing -> existing.LocalName = apiName)
                |> Option.defaultWith (fun () ->
                    let created = Work(flow.Name, apiName, flow.Id)
                    created.Duration <- workDuration
                    queueOperation (AddWork created) operations
                    created)
            let current = Map.tryFind deviceKey state.PendingWorkOrderRev |> Option.defaultValue []
            { state with
                PendingWorks = Map.add key work state.PendingWorks
                PendingWorkOrderRev = Map.add deviceKey (work :: current) state.PendingWorkOrderRev }

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
                // PendingWorks에서 매칭되는 Work가 있으면 연결 설정
                match Map.tryFind key state.PendingWorks with
                | Some work ->
                    // Work가 있으면 Normal 타입으로 설정 (Push 아님)
                    apiDef.ApiDefActionType <- ApiDefActionType.Normal
                    apiDef.TxGuid <- Some work.Id
                    apiDef.RxGuid <- Some work.Id
                | None ->
                    // Work가 없으면 기본값 유지 (Normal)
                    ()
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

    let private buildWorkArrowsBy
        (pairsOf: Work list -> (Work * Work) list)
        (store: DsStore)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        state.PendingWorkOrderRev
        |> Map.fold (fun currentState deviceKey workOrderRev ->
            match Map.tryFind deviceKey currentState.PendingFlows with
            | None -> currentState
            | Some flow ->
                let systemId = flow.ParentId
                let existingArrows = Queries.arrowWorksOf systemId store
                let pairs = workOrderRev |> List.rev |> pairsOf
                let nextPairs =
                    pairs
                    |> List.fold (fun acc (src, dst) ->
                        let pair = (src.Id, dst.Id)
                        let alreadyExists =
                            Set.contains pair acc
                            || existingArrows |> List.exists (fun arrow ->
                                arrow.ArrowType = ArrowType.ResetReset
                                && ((arrow.SourceId = src.Id && arrow.TargetId = dst.Id)
                                    || (arrow.SourceId = dst.Id && arrow.TargetId = src.Id)))
                        if alreadyExists then
                            acc
                        else
                            let arrow = ArrowBetweenWorks(systemId, src.Id, dst.Id, ArrowType.ResetReset)
                            queueOperation (AddArrowWork arrow) operations
                            Set.add pair acc
                    ) currentState.PlannedArrowPairs
                { currentState with PlannedArrowPairs = nextPairs }) state
        |> ignore

    let private buildWorkArrows store operations state =
        buildWorkArrowsBy List.pairwise store operations state

    let private buildWorkArrowsAllPairs store operations state =
        let allPairs (ws: Work list) =
            [ for i in 0 .. ws.Length - 1 do
                for j in i + 1 .. ws.Length - 1 do
                    yield ws.[i], ws.[j] ]
        buildWorkArrowsBy allPairs store operations state

    let private linkCallsToDevicesWithState
        (store: DsStore)
        (projectId: Guid)
        (flowName: string)
        (calls: (Call * string * string option) list)
        (operations: ResizeArray<ImportPlanOperation>)
        (state: DeviceBatchState) =
        if calls.IsEmpty then state
        else
            calls
            |> List.fold (fun st (call, callName, sysHint) ->
                let apiName = call.ApiName
                if String.IsNullOrEmpty apiName then
                    st
                else
                    let devAlias = call.DevicesAlias
                    let system, deviceKey, withSystem = ensureSystem store projectId flowName devAlias sysHint None operations st
                    let workDurationDefault = Some (TimeSpan.FromMilliseconds 500.)
                    let withWork = ensurePendingWork deviceKey apiName system.Id workDurationDefault store operations withSystem
                    let apiDef, withApiDef = ensureApiDef store system apiName operations withWork
                    createAndRegisterApiCall call callName apiDef.Id operations
                    withApiDef
            ) state

    /// LLM helper 진입점 — PassiveSystem + Flow + Work×N + ApiDef×N (+ optional ResetReset Arrow) cascade 1회 발행.
    /// 반환 = (PassiveSystem.Id, (apiName * ApiDef.Id) list). caller (LlmAgent) 가 batch ref table 다중 등록 source 로 사용.
    /// helper 는 *신규* device 생성 책임만 짐 — 동명 PassiveSystem 이 store 에 이미 존재하면 invalidOp.
    /// 기존 device 재사용 시나리오는 LLM 이 사전에 find_by_name/list_systems 로 조회 후 primitive add_call 사용.
    let internal buildPassiveDeviceCascade
        (store: DsStore)
        (projectId: Guid)
        (operations: ResizeArray<ImportPlanOperation>)
        (name: string)
        (deviceType: string)
        (apiNames: string list)
        (workDuration: TimeSpan option)
        (wiringMode: WiringMode)
        : Guid * (string * Guid) list =
        let existing =
            Queries.passiveSystemsOf projectId store
            |> List.tryFind (fun s -> s.Name = name)
        match existing with
        | Some _ ->
            invalidOp $"PassiveSystem '{name}' 이 이미 존재합니다 — find_by_name/list_systems 로 사전 조회 후 primitive add_call 로 기존 ApiDef.Id 참조 권장 (helper 는 신규 device 생성 책임)"
        | None -> ()
        let system, deviceKey, stateWithSystem =
            ensureSystem store projectId name name (Some name) (Some deviceType) operations initialState
        let stateWithWorks =
            apiNames
            |> List.fold (fun st apiName ->
                ensurePendingWork deviceKey apiName system.Id workDuration store operations st
            ) stateWithSystem
        let apiDefIds, stateWithApiDefs =
            apiNames
            |> List.fold (fun (acc, st) apiName ->
                let apiDef, st' = ensureApiDef store system apiName operations st
                ((apiName, apiDef.Id) :: acc, st')
            ) ([], stateWithWorks)
        let apiDefIdsOrdered = List.rev apiDefIds
        match wiringMode with
        | Chain -> buildWorkArrows store operations stateWithApiDefs
        | AllPairs -> buildWorkArrowsAllPairs store operations stateWithApiDefs
        | NoneMode -> ()
        system.Id, apiDefIdsOrdered

    let linkCallsToDevices
        (store: DsStore)
        (projectId: Guid)
        (flowName: string)
        (calls: (Call * string) list)
        (operations: ResizeArray<ImportPlanOperation>) =
        if not calls.IsEmpty then
            let withHint = calls |> List.map (fun (c, n) -> c, n, None)
            let finalState = linkCallsToDevicesWithState store projectId flowName withHint operations initialState
            buildWorkArrows store operations finalState

    /// 여러 Flow의 Call을 state 공유하며 처리. systemNameHint가 있으면 System 이름으로 사용.
    let linkCallsToDevicesMultiFlow
        (store: DsStore)
        (projectId: Guid)
        (callsByFlow: (string * (Call * string * string option) list) list)
        (operations: ResizeArray<ImportPlanOperation>) =
        let finalState =
            callsByFlow
            |> List.fold (fun st (flowName, calls) ->
                linkCallsToDevicesWithState store projectId flowName calls operations st
            ) initialState
        buildWorkArrows store operations finalState
