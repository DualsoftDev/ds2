namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store


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
        if not (DsQuery.isFlowNameUniqueInSystem systemId name None store) then
            invalidOp $"같은 System 내에 이미 '{name}' Flow가 존재합니다."
        let flow = Flow(name, systemId)
        store.WithTransaction($"Flow 추가 \"{name}\"", fun () ->
            store.TrackAdd(store.Flows, flow))
        store.EmitAndHistory(FlowAdded flow)
        flow.Id

    [<Extension>]
    static member AddWork(store: DsStore, name: string, flowId: Guid) : Guid =
        StoreLog.debug($"name={name}, flowId={flowId}")
        let flow = StoreLog.requireFlow(store, flowId)
        if not (DsQuery.isLocalNameUniqueInFlow flowId name None store) then
            invalidOp $"같은 Flow 내에 이미 '{name}' Work가 존재합니다."
        let work = Work(flow.Name, name, flowId)
        store.WithTransaction($"Work 추가 \"{work.Name}\"", fun () ->
            store.TrackAdd(store.Works, work))
        store.EmitAndHistory(WorkAdded work)
        work.Id

    [<Extension>]
    static member AddReferenceWork(store: DsStore, originalWorkId: Guid) : Guid =
        StoreLog.debug($"originalWorkId={originalWorkId}")
        let original = StoreLog.requireWork(store, originalWorkId)
        let refWork = Work(original.FlowPrefix, original.LocalName, original.ParentId)
        refWork.ReferenceOf <- Some originalWorkId
        refWork.Position <-
            original.Position
            |> Option.map (fun pos -> Xywh(pos.X + 40, pos.Y + 40, pos.W, pos.H))
        store.WithTransaction("레퍼런스 Work 생성", fun () ->
            store.TrackAdd(store.Works, refWork))
        store.EmitAndHistory(WorkAdded refWork)
        refWork.Id

    // ─── Add (배치/디바이스) ──────────────────────────────────────────
    [<Extension>]
    static member AddCallsWithDevice(store: DsStore, projectId: Guid, workId: Guid, callNames: string seq, createDeviceSystem: bool, systemType: string option) =
        let names = callNames |> Seq.toList
        StoreLog.debug($"projectId={projectId}, workId={workId}, count={names.Length}, createDevice={createDeviceSystem}")
        StoreLog.requireWork(store, workId) |> ignore
        if createDeviceSystem && (names |> List.exists DirectDeviceOps.hasCreatableApiName) then
            StoreLog.requireProject(store, projectId) |> ignore
        store.WithTransaction("Add Calls", fun () ->
            DirectDeviceOps.addCallsWithDevice store projectId workId names createDeviceSystem systemType)
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
         workId: Guid, callNames: string seq, createDeviceSystem: bool, systemType: string option) : unit =
        match StoreHierarchyQueries.tryFindProjectIdForEntity store entityKind entityId with
        | Some projectId ->
            DsStoreNodesExtensions.AddCallsWithDevice(store, projectId, workId, callNames, createDeviceSystem, systemType)
        | None -> invalidOp $"Project not found for entity {entityKind}/{entityId}"

    [<Extension>]
    static member AddCallWithMultipleDevicesResolved
        (store: DsStore, entityKind: EntityKind, entityId: Guid,
         workId: Guid, callDevicesAlias: string, apiName: string, deviceAliases: string seq, systemType: string option) : Guid =
        match StoreHierarchyQueries.tryFindProjectIdForEntity store entityKind entityId with
        | Some projectId ->
            let aliases = deviceAliases |> Seq.toList
            let resultId =
                store.WithTransaction("Add Call (ApiCall 복제)", fun () ->
                    DirectDeviceOps.addCallWithMultipleDevices store projectId workId callDevicesAlias apiName aliases systemType)
            store.EmitRefreshAndHistory()
            resultId
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
        // Call은 DevicesAlias만 변경, Work는 LocalName만 변경
        let resolvedName =
            match entityKind with
            | EntityKind.Call ->
                match newName.IndexOf('.') with
                | -1  -> newName
                | idx -> newName[..idx - 1]
            | EntityKind.Work ->
                // "FlowPrefix.LocalName" 형식이 올 수 있으므로 LocalName 부분만 추출
                match newName.IndexOf('.') with
                | -1  -> newName
                | idx -> newName[idx + 1..]
            | _ -> newName
        let oldName, isChanged =
            match entityKind with
            | EntityKind.Call ->
                match store.Calls.TryGetValue(id) with
                | true, c  -> Some c.DevicesAlias, c.DevicesAlias <> resolvedName
                | false, _ -> None, false
            | EntityKind.Work ->
                match store.Works.TryGetValue(id) with
                | true, w  -> Some w.LocalName, w.LocalName <> resolvedName
                | false, _ -> None, false
            | _ ->
                let n = DsQuery.tryGetName store entityKind id
                n, n |> Option.map (fun o -> o <> resolvedName) |> Option.defaultValue false
        match oldName with
        | Some _ when isChanged ->
            StoreLog.debug($"id={id}, kind={entityKind}, newName={resolvedName}")
            // Work: Flow 내 LocalName 중복 검사
            match entityKind with
            | EntityKind.Work ->
                let work = store.Works.[id]
                if not (DsQuery.isLocalNameUniqueInFlow work.ParentId resolvedName (Some id) store) then
                    invalidOp $"같은 Flow 내에 이미 '{resolvedName}' Work가 존재합니다."
            | EntityKind.Flow ->
                let flow = store.Flows.[id]
                if not (DsQuery.isFlowNameUniqueInSystem flow.ParentId resolvedName (Some id) store) then
                    invalidOp $"같은 System 내에 이미 '{resolvedName}' Flow가 존재합니다."
            | _ -> ()
            store.WithTransaction($"이름 변경 → \"{resolvedName}\"", fun () ->
                match entityKind with
                | EntityKind.Project   -> store.TrackMutate(store.Projects, id, fun e -> e.Name <- resolvedName)
                | EntityKind.System    -> store.TrackMutate(store.Systems, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Flow      ->
                    store.TrackMutate(store.Flows, id, fun e -> e.Name <- resolvedName)
                    // Cascade: 자식 Work들의 FlowPrefix 갱신
                    for work in DsQuery.worksOf id store do
                        store.TrackMutate(store.Works, work.Id, fun w -> w.FlowPrefix <- resolvedName)
                | EntityKind.Work      ->
                    store.TrackMutate(store.Works, id, fun e -> e.LocalName <- resolvedName)
                | EntityKind.Call      -> store.TrackMutate(store.Calls, id, fun e -> e.DevicesAlias <- resolvedName)
                | EntityKind.ApiDef    -> store.TrackMutate(store.ApiDefs, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Button    -> store.TrackMutate(store.HwButtons, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Lamp      -> store.TrackMutate(store.HwLamps, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Condition -> store.TrackMutate(store.HwConditions, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Action    -> store.TrackMutate(store.HwActions, id, fun e -> e.Name <- resolvedName)
                | _                    -> failwithf "Unknown entity kind: %A" entityKind)
            // Call/Work은 표시명 조합이 다름
            let displayName =
                match entityKind with
                | EntityKind.Call -> store.Calls.[id].Name
                | EntityKind.Work -> store.Works.[id].Name
                | _ -> resolvedName
            store.EmitAndHistory(EntityRenamed(id, displayName))
        | Some _ -> () // 이름 변경 없음
        | None ->
            StoreLog.warn($"Entity not found. kind={entityKind}, id={id}")
            invalidOp $"Entity not found. kind={entityKind}, id={id}"

    // ─── AutoLayout ────────────────────────────────────────────────────

    /// 노드들이 모두 같은 좌표에 몰려있으면 자동 배치 적용 (Mermaid 임포트 등)
    [<Extension>]
    static member AutoLayoutIfNeeded(store: DsStore, kind: TabKind, rootId: Guid) : bool =
        let content = EditorCanvasProjection.canvasContentForTab store kind rootId
        if CanvasLayout.needsAutoLayout content then
            let requests = CanvasLayout.computeLayout content
            DsStoreNodesExtensions.MoveEntities(store, requests) |> ignore
            true
        else false
