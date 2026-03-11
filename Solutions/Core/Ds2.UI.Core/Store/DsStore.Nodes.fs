namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core


// =============================================================================
// 내부 헬퍼 — 캐스케이드 삭제 + 디바이스 생성
// =============================================================================

module internal CascadeRemove =
    let private arrowsFor (dict: System.Collections.Generic.IReadOnlyDictionary<Guid, 'T>) (srcOf: 'T -> Guid) (tgtOf: 'T -> Guid) (nodeIds: Set<Guid>) =
        dict.Values
        |> Seq.filter (fun a -> nodeIds.Contains(srcOf a) || nodeIds.Contains(tgtOf a))
        |> Seq.toList

    let arrowWorksFor (store: DsStore) (workIds: Set<Guid>) =
        arrowsFor store.ArrowWorksReadOnly (fun a -> a.SourceId) (fun a -> a.TargetId) workIds

    let arrowCallsFor (store: DsStore) (callIds: Set<Guid>) =
        arrowsFor store.ArrowCallsReadOnly (fun a -> a.SourceId) (fun a -> a.TargetId) callIds


    let private collectReferencedApiCallIds (store: DsStore) =
        store.Calls.Values
        |> Seq.collect CallConditionQueries.referencedApiCallIds
        |> Set.ofSeq

    let removeOrphanApiCalls (store: DsStore) =
        let referencedIds = collectReferencedApiCallIds store
        let orphanIds =
            store.ApiCalls.Keys
            |> Seq.filter (fun id -> not (referencedIds.Contains id))
            |> Seq.toList
        for orphanId in orphanIds do
            store.TrackRemove(store.ApiCalls, orphanId)

    let removeHwComponents (store: DsStore) (systemIds: Guid list) =
        for sid in systemIds do
            DsQuery.apiDefsOf    sid store |> List.iter (fun d -> store.TrackRemove(store.ApiDefs,      d.Id))
            DsQuery.buttonsOf    sid store |> List.iter (fun b -> store.TrackRemove(store.HwButtons,    b.Id))
            DsQuery.lampsOf      sid store |> List.iter (fun l -> store.TrackRemove(store.HwLamps,      l.Id))
            DsQuery.conditionsOf sid store |> List.iter (fun c -> store.TrackRemove(store.HwConditions, c.Id))
            DsQuery.actionsOf    sid store |> List.iter (fun a -> store.TrackRemove(store.HwActions,    a.Id))

    let removeSystem (store: DsStore) (systemId: Guid) =
        for p in store.Projects.Values do
            let inActive = p.ActiveSystemIds.Contains(systemId)
            let inPassive = p.PassiveSystemIds.Contains(systemId)
            if inActive || inPassive then
                store.TrackMutate(store.Projects, p.Id, fun proj ->
                    if inActive then proj.ActiveSystemIds.Remove(systemId) |> ignore
                    if inPassive then proj.PassiveSystemIds.Remove(systemId) |> ignore)
        store.TrackRemove(store.Systems, systemId)

    let cascadeRemoveCall (store: DsStore) (callId: Guid) =
        arrowCallsFor store (Set.singleton callId)
        |> List.iter (fun a -> store.TrackRemove(store.ArrowCalls, a.Id))
        store.TrackRemove(store.Calls, callId)

    let cascadeRemoveWork (store: DsStore) (workId: Guid) =
        DsQuery.callsOf workId store 
        |> List.iter (fun call -> cascadeRemoveCall store call.Id)
        arrowWorksFor store (Set.singleton workId)
        |> List.iter (fun a -> store.TrackRemove(store.ArrowWorks, a.Id))
        store.TrackRemove(store.Works, workId)

    let cascadeRemoveFlow (store: DsStore) (flowId: Guid) =
        DsQuery.worksOf flowId store 
        |> List.iter (fun work -> cascadeRemoveWork store work.Id)
        store.TrackRemove(store.Flows, flowId)

    let cascadeRemoveSystem (store: DsStore) (systemId: Guid) =
        DsQuery.flowsOf systemId store 
        |> List.iter (fun flow -> cascadeRemoveFlow store flow.Id)
        removeHwComponents store [ systemId ]
        removeSystem store systemId

    let cascadeRemoveProject (store: DsStore) (projectId: Guid) =
        DsQuery.projectSystemsOf projectId store 
        |> List.iter (fun system -> cascadeRemoveSystem store system.Id)
        store.TrackRemove(store.Projects, projectId)

    let batchRemoveEntities (store: DsStore) (selections: (EntityKind * Guid) list) =
        let selIds = selections |> List.map snd |> Set.ofList

        for (ek, id) in selections do
            match ek with
            | EntityKind.Call ->
                // 부모 Work가 함께 선택된 Call은 건너뜀 — Work 캐스케이드가 처리
                match DsQuery.getCall id store with
                | Some call when not (selIds.Contains call.ParentId) ->
                    cascadeRemoveCall store id
                | _ -> ()
            | EntityKind.Work      -> cascadeRemoveWork store id
            | EntityKind.Flow      -> cascadeRemoveFlow store id
            | EntityKind.System    -> cascadeRemoveSystem store id
            | EntityKind.Project   -> cascadeRemoveProject store id
            | EntityKind.ApiDef    -> store.TrackRemove(store.ApiDefs, id)
            | EntityKind.Button    -> store.TrackRemove(store.HwButtons, id)
            | EntityKind.Lamp      -> store.TrackRemove(store.HwLamps, id)
            | EntityKind.Condition -> store.TrackRemove(store.HwConditions, id)
            | EntityKind.Action    -> store.TrackRemove(store.HwActions, id)
            | _ -> ()

        removeOrphanApiCalls store

module internal DirectDeviceOps =
    type private DeviceBatchState = {
        PendingSystems: Map<string, DsSystem>
        PendingFlows: Map<string, Flow>
        PendingWorks: Map<string * Guid, Work>
        PendingApiDefs: Map<string * Guid, ApiDef>
        NewSystemIds: Set<Guid>
        PendingWorkOrderRev: Map<string, Work list>
    }

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

    let private shouldCreate (create: bool) (apiName: string) =
        create && not (String.IsNullOrEmpty apiName)

    let private ensureSystem (store: DsStore) (projectId: Guid) (flowName: string) (devAlias: string) (state: DeviceBatchState) =
        let systemName = $"{flowName}_{devAlias}"
        match Map.tryFind systemName state.PendingSystems with
        | Some system -> system, state
        | None ->
            match DsQuery.passiveSystemsOf projectId store |> List.tryFind (fun s -> s.Name = systemName) with
            | Some existing ->
                existing, { state with PendingSystems = Map.add systemName existing state.PendingSystems }
            | None ->
                let system = DsSystem(systemName)
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
            let work = Work(apiName, flow.Id)
            store.TrackAdd(store.Works, work)
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
                    apiDef.Properties.IsPush <- true
                    apiDef.Properties.TxGuid <- Some work.Id
                | None -> ()
                store.TrackAdd(store.ApiDefs, apiDef)
                apiDef, { state with PendingApiDefs = Map.add key apiDef state.PendingApiDefs }

    let private createAndRegisterApiCall (store: DsStore) (call: Call) (name: string) (apiDefId: Guid) =
        let apiCall = ApiCall(name)
        apiCall.ApiDefId <- Some apiDefId
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
                workOrderRev
                |> List.rev
                |> List.pairwise
                |> List.iter (fun (src, dst) ->
                    let arrow = ArrowBetweenWorks(systemId, src.Id, dst.Id, ArrowType.ResetReset)
                    store.TrackAdd(store.ArrowWorks, arrow)))

    let addCallsWithDevice (store: DsStore) (projectId: Guid) (workId: Guid) (callNames: string list) (createDeviceSystem: bool) =
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

                    if not (shouldCreate createDeviceSystem apiName) then state
                    else
                        let system, withSystem = ensureSystem store projectId flowName devAlias state
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

// =============================================================================
// DsStore 노드 확장 — CRUD + 이동 + 삭제 + 이름변경
// =============================================================================

[<Extension>]
type DsStoreNodesExtensions =

    // ─── Add ─────────────────────────────────────────────────────────
    [<Extension>]
    static member AddProject(store: DsStore, name: string) : Guid =
        StoreLog.debug($"name={name}")
        let project = Project(name)
        store.WithTransaction($"프로젝트 추가 \"{name}\"", fun () ->
            store.TrackAdd(store.Projects, project))
        store.EmitAndHistory(ProjectAdded project)
        project.Id

    [<Extension>]
    static member AddSystem(store: DsStore, name: string, projectId: Guid, isActive: bool) : Guid =
        StoreLog.debug($"name={name}, projectId={projectId}, isActive={isActive}")
        StoreLog.requireProject(store, projectId) |> ignore
        let system = DsSystem(name)
        store.WithTransaction($"시스템 추가 \"{name}\"", fun () ->
            store.TrackAdd(store.Systems, system)
            store.TrackMutate(store.Projects, projectId, fun p ->
                if isActive then p.ActiveSystemIds.Add(system.Id)
                else p.PassiveSystemIds.Add(system.Id)))
        store.EmitAndHistory(SystemAdded system)
        system.Id

    [<Extension>]
    static member AddFlow(store: DsStore, name: string, systemId: Guid) : Guid =
        StoreLog.debug($"name={name}, systemId={systemId}")
        StoreLog.requireSystem(store, systemId) |> ignore
        let flow = Flow(name, systemId)
        store.WithTransaction($"Flow 추가 \"{name}\"", fun () ->
            store.TrackAdd(store.Flows, flow))
        store.EmitAndHistory(FlowAdded flow)
        flow.Id

    [<Extension>]
    static member AddWork(store: DsStore, name: string, flowId: Guid) : Guid =
        StoreLog.debug($"name={name}, flowId={flowId}")
        StoreLog.requireFlow(store, flowId) |> ignore
        let work = Work(name, flowId)
        store.WithTransaction($"Work 추가 \"{name}\"", fun () ->
            store.TrackAdd(store.Works, work))
        store.EmitAndHistory(WorkAdded work)
        work.Id

    // ─── Add (배치/디바이스) ──────────────────────────────────────────
    [<Extension>]
    static member AddCallsWithDevice(store: DsStore, projectId: Guid, workId: Guid, callNames: string seq, createDeviceSystem: bool) =
        let names = callNames |> Seq.toList
        StoreLog.debug($"projectId={projectId}, workId={workId}, count={names.Length}, createDevice={createDeviceSystem}")
        StoreLog.requireWork(store, workId) |> ignore
        if createDeviceSystem && (names |> List.exists DirectDeviceOps.hasCreatableApiName) then
            StoreLog.requireProject(store, projectId) |> ignore
        store.WithTransaction("Add Calls", fun () ->
            DirectDeviceOps.addCallsWithDevice store projectId workId names createDeviceSystem)
        store.EmitRefreshAndHistory()

    [<Extension>]
    static member AddCallWithLinkedApiDefs(store: DsStore, workId: Guid, devicesAlias: string, apiName: string, apiDefIds: Guid seq) : Guid =
        StoreLog.debug($"workId={workId}, devicesAlias={devicesAlias}, apiName={apiName}")
        let resultId =
            store.WithTransaction("Add Call", fun () ->
                DirectDeviceOps.addCallWithLinkedApiDefs store workId devicesAlias apiName apiDefIds)
        store.EmitRefreshAndHistory()
        resultId

    // ─── Add (Resolved — 쿼리+생성 통합) ─────────────────────────────
    [<Extension>]
    static member AddSystemResolved
        (store: DsStore, name: string, isActive: bool,
         selectedEntityKind: Nullable<EntityKind>, selectedEntityId: Nullable<Guid>,
         activeTabKind: Nullable<TabKind>, activeTabRootId: Nullable<Guid>) : unit =
        match AddTargetQueries.tryResolveAddSystemTarget store
                (Option.ofNullable selectedEntityKind) (Option.ofNullable selectedEntityId)
                (Option.ofNullable activeTabKind) (Option.ofNullable activeTabRootId) with
        | Some projectId -> DsStoreNodesExtensions.AddSystem(store, name, projectId, isActive) |> ignore
        | None -> ()

    [<Extension>]
    static member AddFlowResolved
        (store: DsStore, name: string,
         selectedEntityKind: Nullable<EntityKind>, selectedEntityId: Nullable<Guid>,
         activeTabKind: Nullable<TabKind>, activeTabRootId: Nullable<Guid>) : unit =
        match AddTargetQueries.tryResolveAddFlowTarget store
                (Option.ofNullable selectedEntityKind) (Option.ofNullable selectedEntityId)
                (Option.ofNullable activeTabKind) (Option.ofNullable activeTabRootId) with
        | Some systemId -> DsStoreNodesExtensions.AddFlow(store, name, systemId) |> ignore
        | None -> ()

    [<Extension>]
    static member AddCallsWithDeviceResolved
        (store: DsStore, entityKind: EntityKind, entityId: Guid,
         workId: Guid, callNames: string seq, createDeviceSystem: bool) : unit =
        match EntityHierarchyQueries.tryFindProjectIdForEntity store entityKind entityId with
        | Some projectId ->
            DsStoreNodesExtensions.AddCallsWithDevice(store, projectId, workId, callNames, createDeviceSystem)
        | None -> invalidOp $"Project not found for entity {entityKind}/{entityId}"

    // ─── Move ────────────────────────────────────────────────────────
    [<Extension>]
    static member MoveEntities(store: DsStore, requests: seq<MoveEntityRequest>) : int =
        let moves =
            requests
            |> Seq.distinctBy (fun r -> r.Id)
            |> Seq.choose (fun r ->
                let newPos = Some r.Position
                match DsQuery.getWork r.Id store with
                | Some work when work.Position <> newPos -> Some(r.Id, true, newPos)
                | Some _ -> None
                | None ->
                    match DsQuery.getCall r.Id store with
                    | Some call when call.Position <> newPos -> Some(r.Id, false, newPos)
                    | _ -> None)
            |> Seq.toList
        if moves.IsEmpty then 0
        else
            StoreLog.debug($"count={moves.Length}")
            store.WithTransaction("Move Selected Nodes", fun () ->
                for (id, isWork, newPos) in moves do
                    if isWork then
                        store.TrackMutate(store.Works, id, fun work -> work.Position <- newPos)
                    else
                        store.TrackMutate(store.Calls, id, fun call -> call.Position <- newPos))
            store.EmitRefreshAndHistory()
            moves.Length

    // ─── Remove ──────────────────────────────────────────────────────
    [<Extension>]
    static member RemoveEntities(store: DsStore, selections: seq<EntityKind * Guid>) =
        let selList = selections |> Seq.distinctBy snd |> Seq.toList
        if not selList.IsEmpty then
            let types = selList |> List.map (fun (k, _) -> k.ToString()) |> List.distinct |> String.concat ","
            StoreLog.debug($"count={selList.Length}, types=[{types}]")
            store.WithTransaction("Delete Entities", fun () ->
                CascadeRemove.batchRemoveEntities store selList)
            store.EmitRefreshAndHistory()

    // ─── Rename ──────────────────────────────────────────────────────
    [<Extension>]
    static member RenameEntity(store: DsStore, id: Guid, entityKind: EntityKind, newName: string) =
        // Call은 DevicesAlias만 변경 — UI에서 전체 이름(Alias.ApiName) 또는 alias만 올 수 있음
        let resolvedName =
            match entityKind with
            | EntityKind.Call ->
                match newName.IndexOf('.') with
                | -1  -> newName
                | idx -> newName[..idx - 1]
            | _ -> newName
        let oldName, isChanged =
            match entityKind with
            | EntityKind.Call ->
                match store.Calls.TryGetValue(id) with
                | true, c  -> Some c.DevicesAlias, c.DevicesAlias <> resolvedName
                | false, _ -> None, false
            | _ ->
                let n = DsQuery.tryGetName store entityKind id
                n, n |> Option.map (fun o -> o <> resolvedName) |> Option.defaultValue false
        match oldName with
        | Some _ when isChanged ->
            StoreLog.debug($"id={id}, kind={entityKind}, newName={resolvedName}")
            store.WithTransaction($"이름 변경 → \"{resolvedName}\"", fun () ->
                match entityKind with
                | EntityKind.Project   -> store.TrackMutate(store.Projects, id, fun e -> e.Name <- resolvedName)
                | EntityKind.System    -> store.TrackMutate(store.Systems, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Flow      -> store.TrackMutate(store.Flows, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Work      -> store.TrackMutate(store.Works, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Call      -> store.TrackMutate(store.Calls, id, fun e -> e.DevicesAlias <- resolvedName)
                | EntityKind.ApiDef    -> store.TrackMutate(store.ApiDefs, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Button    -> store.TrackMutate(store.HwButtons, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Lamp      -> store.TrackMutate(store.HwLamps, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Condition -> store.TrackMutate(store.HwConditions, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Action    -> store.TrackMutate(store.HwActions, id, fun e -> e.Name <- resolvedName)
                | _                    -> failwithf "Unknown entity kind: %A" entityKind)
            // Call은 DevicesAlias만 변경 → 실제 표시명(DevicesAlias.ApiName) 조회
            let displayName =
                match entityKind with
                | EntityKind.Call -> store.Calls.[id].Name
                | _ -> resolvedName
            store.EmitAndHistory(EntityRenamed(id, displayName))
        | Some _ -> () // 이름 변경 없음
        | None ->
            StoreLog.warn($"Entity not found. kind={entityKind}, id={id}")
            invalidOp $"Entity not found. kind={entityKind}, id={id}"
