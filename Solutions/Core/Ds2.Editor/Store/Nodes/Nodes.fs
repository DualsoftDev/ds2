namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store



[<Extension>]
type DsStoreNodesExtensions =

    // ─── Invariant 공통 헬퍼 ─────────────────────────────────────────
    /// 빈/공백 이름 거부 — 모든 Add* / Rename* 진입점이 공유.
    /// UI / AASX / Mermaid / LlmAgent 어느 진입점이든 통과해야 store 에 들어옴.
    static member private RequireNonEmptyName(kind: string, name: string) =
        if System.String.IsNullOrWhiteSpace name then
            invalidOp $"{kind} 이름은 비어있거나 공백만일 수 없습니다."

    /// DevicesAlias / ApiName 의 점(.) 금지 — Call.Name = "{DevicesAlias}.{ApiName}" parseCallName
    /// split 이 1번만 동작하므로 어느 한쪽에 점이 들어가면 parse 가 ambiguous (의도 안 한 prefix/suffix).
    static member private RequireNoDot(kind: string, value: string) =
        if not (isNull value) && value.Contains '.' then
            invalidOp $"{kind} 에는 점(.)을 사용할 수 없습니다: '{value}'"

    // ─── Add ─────────────────────────────────────────────────────────
    [<Extension>]
    static member AddProject(store: DsStore, name: string) : Guid =
        StoreLog.debug($"name={name}")
        DsStoreNodesExtensions.RequireNonEmptyName("Project", name)
        if not (Queries.isProjectNameUnique name None store) then
            invalidOp $"이미 '{name}' Project가 존재합니다."
        let project = Project(name)
        store.WithTransaction($"프로젝트 추가 \"{name}\"", fun () ->
            store.TrackAdd(store.Projects, project))
        store.EmitAndHistory(ProjectAdded project)
        project.Id

    [<Extension>]
    static member AddSystem(store: DsStore, name: string, projectId: Guid, isActive: bool) : Guid =
        StoreLog.debug($"name={name}, projectId={projectId}, isActive={isActive}")
        DsStoreNodesExtensions.RequireNonEmptyName("System", name)
        StoreLog.requireProject(store, projectId) |> ignore
        if not (Queries.isSystemNameUniqueInProject projectId name None store) then
            invalidOp $"같은 Project 내에 이미 '{name}' System이 존재합니다."
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
        DsStoreNodesExtensions.RequireNonEmptyName("Flow", name)
        StoreLog.requireSystem(store, systemId) |> ignore
        if not (Queries.isFlowNameUniqueInSystem systemId name None store) then
            invalidOp $"같은 System 내에 이미 '{name}' Flow가 존재합니다."
        let flow = Flow(name, systemId)
        store.WithTransaction($"Flow 추가 \"{name}\"", fun () ->
            store.TrackAdd(store.Flows, flow))
        store.EmitAndHistory(FlowAdded flow)
        flow.Id

    [<Extension>]
    static member AddWork(store: DsStore, name: string, flowId: Guid) : Guid =
        StoreLog.debug($"name={name}, flowId={flowId}")
        DsStoreNodesExtensions.RequireNonEmptyName("Work", name)
        let flow = StoreLog.requireFlow(store, flowId)
        if not (Queries.isLocalNameUniqueInFlow flowId name None store) then
            invalidOp $"같은 Flow 내에 이미 '{name}' Work가 존재합니다."
        let work = Work(flow.Name, name, flowId)
        store.WithTransaction($"Work 추가 \"{work.Name}\"", fun () ->
            store.TrackAdd(store.Works, work))
        store.EmitAndHistory(WorkAdded work)
        work.Id

    [<Extension>]
    static member AddReferenceWork(store: DsStore, originalWorkId: Guid) : Guid =
        let resolvedId = Queries.resolveOriginalWorkId originalWorkId store
        StoreLog.debug($"originalWorkId={originalWorkId}, resolvedId={resolvedId}")
        let original = StoreLog.requireWork(store, resolvedId)
        let refWork = Work(original.FlowPrefix, original.LocalName, original.ParentId)
        refWork.ReferenceOf <- Some resolvedId
        refWork.Position <-
            original.Position
            |> Option.map (fun pos -> Xywh(pos.X + 40, pos.Y + 40, pos.W, pos.H))
        store.WithTransaction("레퍼런스 Work 생성", fun () ->
            store.TrackAdd(store.Works, refWork))
        store.EmitAndHistory(WorkAdded refWork)
        refWork.Id

    [<Extension>]
    static member AddReferenceCall(store: DsStore, originalCallId: Guid) : Guid =
        let resolvedId = Queries.resolveOriginalCallId originalCallId store
        StoreLog.debug($"originalCallId={originalCallId}, resolvedId={resolvedId}")
        let original = StoreLog.requireCall(store, resolvedId)
        let refCall = Call(original.DevicesAlias, original.ApiName, original.ParentId)
        refCall.ReferenceOf <- Some resolvedId
        refCall.Position <-
            original.Position
            |> Option.map (fun pos -> Xywh(pos.X + 40, pos.Y + 40, pos.W, pos.H))
        store.WithTransaction("레퍼런스 Call 생성", fun () ->
            store.TrackAdd(store.Calls, refCall))
        store.EmitRefreshAndHistory()
        refCall.Id

    /// Tree 에서 *대상 Work* 에 동일 이름 Call 추가 시도 시, 원본 Call 의 Reference 를
    /// *대상 Work* 에 생성. 원본 Work 와 다른 Work 에 Reference 둘 수 있도록 parentId 명시.
    [<Extension>]
    static member AddReferenceCallToWork(store: DsStore, originalCallId: Guid, targetWorkId: Guid) : Guid =
        let resolvedId = Queries.resolveOriginalCallId originalCallId store
        StoreLog.debug($"originalCallId={originalCallId}, resolvedId={resolvedId}, targetWorkId={targetWorkId}")
        let original = StoreLog.requireCall(store, resolvedId)
        StoreLog.requireWork(store, targetWorkId) |> ignore
        let refCall = Call(original.DevicesAlias, original.ApiName, targetWorkId)
        refCall.ReferenceOf <- Some resolvedId
        // 위치는 원본에서 살짝 offset (같은 Work 안일 경우 겹침 회피, 다른 Work 면 그대로)
        refCall.Position <-
            original.Position
            |> Option.map (fun pos -> Xywh(pos.X + 40, pos.Y + 40, pos.W, pos.H))
        store.WithTransaction("레퍼런스 Call 생성", fun () ->
            store.TrackAdd(store.Calls, refCall))
        store.EmitRefreshAndHistory()
        refCall.Id

    // ─── Add (배치/디바이스) ──────────────────────────────────────────
    /// 같은 Work 안에 동일 callName 이 *원본 Call* 로 존재하면 reject.
    /// 호출자는 이름 중복 발견 시 `AddReferenceCallToWork` 로 Reference Call 만들어야 함
    /// (Promaker UI 의 duplicateMap → 자동 Reference 흐름).
    static member private RequireUniqueCallNames(store: DsStore, workId: Guid, callNames: string list) =
        for callName in callNames do
            DsStoreNodesExtensions.RequireNonEmptyName("Call", callName)
            // Call.Name 은 "{DevicesAlias}.{ApiName}" 형식 — 점 정확히 1개여야 함.
            let dotCount = callName |> Seq.filter (fun c -> c = '.') |> Seq.length
            if dotCount <> 1 then
                invalidOp $"Call 이름은 'DevicesAlias.ApiName' 형식이어야 합니다 (점 정확히 1개): '{callName}'"
            if not (Queries.isCallNameUniqueInWork workId callName None store) then
                invalidOp $"같은 Work 내에 이미 '{callName}' Call이 존재합니다. Reference Call 로 만들거나 다른 이름 사용."

    /// 같은 Project 내에서 동일 DevicesAlias 가 다른 SystemType 으로 이미 등록돼 있으면 reject.
    /// Promaker UI `Create.cs:findConflictingDeviceSystemType` 호출이 같은 검사를 했지만
    /// AASX/Mermaid/LlmAgent 진입은 우회. Core 진입 시점에서 일괄 차단.
    static member private RequireNoSystemTypeConflict
        (store: DsStore, projectId: Guid, devAliases: string seq, systemType: string option) =
        match systemType with
        | None -> ()
        | Some _ ->
            for devAlias in devAliases do
                match Queries.findConflictingDeviceSystemType projectId devAlias systemType store with
                | Some (existing, requested) ->
                    invalidOp $"DevicesAlias '{devAlias}' 는 이미 SystemType '{existing}' 로 등록 — 새 SystemType '{requested}' 와 충돌."
                | None -> ()

    [<Extension>]
    static member AddCallsWithDevice(store: DsStore, projectId: Guid, workId: Guid, callNames: string seq, createDeviceSystem: bool, systemType: string option) =
        let names = callNames |> Seq.toList
        StoreLog.debug($"projectId={projectId}, workId={workId}, count={names.Length}, createDevice={createDeviceSystem}")
        StoreLog.requireWork(store, workId) |> ignore
        DsStoreNodesExtensions.RequireUniqueCallNames(store, workId, names)
        if createDeviceSystem && (names |> List.exists DirectDeviceOps.hasCreatableApiName) then
            StoreLog.requireProject(store, projectId) |> ignore
            let devAliases = names |> List.choose (fun n ->
                let parts = n.Split([| '.' |], 2)
                if parts.Length >= 1 && not (System.String.IsNullOrEmpty parts.[0]) then Some parts.[0] else None)
            DsStoreNodesExtensions.RequireNoSystemTypeConflict(store, projectId, devAliases, systemType)
        store.WithTransaction("Add Calls", fun () ->
            DirectDeviceOps.addCallsWithDevice store projectId workId names createDeviceSystem systemType)
        store.EmitRefreshAndHistory()

    /// Symbol Import Wizard 등 import 경로 전용 — Call/ApiCall 생성과 동시에 OutTag/InTag 채움.
    /// entries = (callName, outTag, inTag) — outTag/inTag 가 null 이면 None 으로 저장.
    /// 1 transaction 으로 묶여 Undo 1회 단위 + EmitRefresh 1회.
    /// 반환값: 실제 태그가 적용된 Call 개수 (callName 으로 work 내 Call lookup 후 ApiCall.[0] 채움).
    [<Extension>]
    static member AddCallsWithDeviceAndTags(
            store: DsStore, projectId: Guid, workId: Guid,
            entries: System.Collections.Generic.IReadOnlyList<struct (string * IOTag * IOTag)>,
            createDeviceSystem: bool, systemType: string option) : int =
        if isNull (box entries) || entries.Count = 0 then 0
        else
            let entryList = [ for e in entries -> let struct (n, o, i) = e in (n, o, i) ]
            let names = entryList |> List.map (fun (n, _, _) -> n)
            StoreLog.debug($"AddCallsWithDeviceAndTags projectId={projectId}, workId={workId}, count={names.Length}")
            StoreLog.requireWork(store, workId) |> ignore
            DsStoreNodesExtensions.RequireUniqueCallNames(store, workId, names)
            if createDeviceSystem && (names |> List.exists DirectDeviceOps.hasCreatableApiName) then
                StoreLog.requireProject(store, projectId) |> ignore
                let devAliases = names |> List.choose (fun n ->
                    let parts = n.Split([| '.' |], 2)
                    if parts.Length >= 1 && not (System.String.IsNullOrEmpty parts.[0]) then Some parts.[0] else None)
                DsStoreNodesExtensions.RequireNoSystemTypeConflict(store, projectId, devAliases, systemType)
            let mutable applied = 0
            store.WithTransaction("Wizard Apply Calls + Tags", fun () ->
                DirectDeviceOps.addCallsWithDevice store projectId workId names createDeviceSystem systemType
                // workId 의 Calls 중 entries 와 매칭되는 Call 의 ApiCall.[0] 에 tag 채움.
                // 같은 이름 다중 추가는 마지막 매칭 1개만 — Wizard plan 의 distinct 보장 가정.
                let workCalls =
                    store.Calls.Values
                    |> Seq.filter (fun c -> c.ParentId = workId)
                    |> Seq.toList
                for (callName, outTag, inTag) in entryList do
                    let matched = workCalls |> List.filter (fun c -> c.Name = callName) |> List.tryLast
                    match matched with
                    | Some call when call.ApiCalls.Count > 0 ->
                        let apiCall = call.ApiCalls.[0]
                        apiCall.OutTag <- Option.ofObj outTag
                        apiCall.InTag <- Option.ofObj inTag
                        applied <- applied + 1
                    | _ -> ())
            store.EmitRefreshAndHistory()
            applied

    [<Extension>]
    static member AddCallWithLinkedApiDefs(store: DsStore, workId: Guid, devicesAlias: string, apiName: string, apiDefIds: Guid seq) : Guid =
        StoreLog.debug($"workId={workId}, devicesAlias={devicesAlias}, apiName={apiName}")
        DsStoreNodesExtensions.RequireNonEmptyName("Call.DevicesAlias", devicesAlias)
        DsStoreNodesExtensions.RequireNonEmptyName("Call.ApiName", apiName)
        DsStoreNodesExtensions.RequireNoDot("Call.DevicesAlias", devicesAlias)
        DsStoreNodesExtensions.RequireNoDot("Call.ApiName", apiName)
        let fullName = $"{devicesAlias}.{apiName}"
        if not (Queries.isCallNameUniqueInWork workId fullName None store) then
            invalidOp $"같은 Work 내에 이미 '{fullName}' Call이 존재합니다. Reference Call 로 만들거나 다른 이름 사용."
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
         workId: Guid, callDevicesAlias: string, apiName: string, deviceAliases: string seq,
         createDeviceSystem: bool, systemType: string option) : Guid =
        match StoreHierarchyQueries.tryFindProjectIdForEntity store entityKind entityId with
        | Some projectId ->
            DsStoreNodesExtensions.RequireNonEmptyName("Call.DevicesAlias", callDevicesAlias)
            DsStoreNodesExtensions.RequireNoDot("Call.DevicesAlias", callDevicesAlias)
            // apiName 빈 케이스 = ApiCall 복제 가드 (createDeviceSystem 가드와 함께 Call 만 만들기) — 빈 이름 OK.
            // apiName 있으면 같은 Work 안 중복 검사.
            if not (System.String.IsNullOrWhiteSpace apiName) then
                DsStoreNodesExtensions.RequireNoDot("Call.ApiName", apiName)
                let fullName = $"{callDevicesAlias}.{apiName}"
                if not (Queries.isCallNameUniqueInWork workId fullName None store) then
                    invalidOp $"같은 Work 내에 이미 '{fullName}' Call이 존재합니다. Reference Call 로 만들거나 다른 이름 사용."
            let aliases = deviceAliases |> Seq.toList
            // 각 device alias 의 SystemType 충돌 검사.
            if createDeviceSystem then
                DsStoreNodesExtensions.RequireNoSystemTypeConflict(store, projectId, aliases, systemType)
            let resultId =
                store.WithTransaction("Add Call (ApiCall 복제)", fun () ->
                    DirectDeviceOps.addCallWithMultipleDevices store projectId workId callDevicesAlias apiName aliases createDeviceSystem systemType)
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
                match Queries.getWork r.Id store with
                | Some work when work.Position <> newPos -> Some(r.Id, true, newPos)
                | Some _ -> None
                | None ->
                    match Queries.getCall r.Id store with
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
            // 트리/캔버스 visual tree 재구축을 피하기 위해 가벼운 이벤트 발행.
            // C# 측에서 인접 화살표 path만 재계산한다.
            let movedIds = moves |> List.map (fun (id, _, _) -> id)
            // 같은 트랜잭션을 undo/redo 할 때도 동일한 가벼운 경로를 타도록 힌트 부착.
            // 미부착 시 applyTransaction 이 StoreRefreshed 를 emit 해 RebuildAll → 50+ 노드 ContentPresenter 재생성.
            store.SetLastUndoLightEvent(EntitiesMoved movedIds)
            store.EmitEntitiesMovedAndHistory(movedIds)
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
        if entityKind = EntityKind.Call then
            Queries.requireNonReferenceCall id store
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
                let n = Queries.tryGetName store entityKind id
                n, n |> Option.map (fun o -> o <> resolvedName) |> Option.defaultValue false
        match oldName with
        | Some _ when isChanged ->
            StoreLog.debug($"id={id}, kind={entityKind}, newName={resolvedName}")
            // Work: Flow 내 LocalName 중복 검사
            match entityKind with
            | EntityKind.Work ->
                let work = store.Works.[id]
                if not (Queries.isLocalNameUniqueInFlow work.ParentId resolvedName (Some id) store) then
                    invalidOp $"같은 Flow 내에 이미 '{resolvedName}' Work가 존재합니다."
            | EntityKind.Flow ->
                let flow = store.Flows.[id]
                if not (Queries.isFlowNameUniqueInSystem flow.ParentId resolvedName (Some id) store) then
                    invalidOp $"같은 System 내에 이미 '{resolvedName}' Flow가 존재합니다."
            | EntityKind.Call ->
                let call = store.Calls.[id]
                let fullName = $"{resolvedName}.{call.ApiName}"
                if not (Queries.isCallNameUniqueInWork call.ParentId fullName (Some id) store) then
                    invalidOp $"같은 Work 내에 이미 '{fullName}' Call이 존재합니다."
            | _ -> ()
            store.WithTransaction($"이름 변경 → \"{resolvedName}\"", fun () ->
                match entityKind with
                | EntityKind.Project   -> store.TrackMutate(store.Projects, id, fun e -> e.Name <- resolvedName)
                | EntityKind.System    -> store.TrackMutate(store.Systems, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Flow      ->
                    store.TrackMutate(store.Flows, id, fun e -> e.Name <- resolvedName)
                    // Cascade: 자식 Work들의 FlowPrefix 갱신
                    for work in Queries.worksOf id store do
                        store.TrackMutate(store.Works, work.Id, fun w -> w.FlowPrefix <- resolvedName)
                | EntityKind.Work      ->
                    store.TrackMutate(store.Works, id, fun e -> e.LocalName <- resolvedName)
                | EntityKind.Call      -> store.TrackMutate(store.Calls, id, fun e -> e.DevicesAlias <- resolvedName)
                | EntityKind.ApiDef    -> store.TrackMutate(store.ApiDefs, id, fun e -> e.Name <- resolvedName)
                | _                    -> failwithf "Unknown entity kind: %A" entityKind)
            // Call/Work은 표시명 조합이 다름
            let displayName =
                match entityKind with
                | EntityKind.Call -> store.Calls.[id].Name
                | EntityKind.Work -> store.Works.[id].Name
                | _ -> resolvedName
            // treeName: 트리에서 표시할 이름 (Work만 LocalName, 나머지는 displayName과 동일)
            let treeName =
                match entityKind with
                | EntityKind.Work -> resolvedName
                | _ -> displayName
            store.EmitAndHistory(EntityRenamed(id, displayName, treeName))
        | Some _ -> () // 이름 변경 없음
        | None ->
            StoreLog.warn($"Entity not found. kind={entityKind}, id={id}")
            invalidOp $"Entity not found. kind={entityKind}, id={id}"

    /// 여러 Flow 의 IsDisabled 를 한 transaction(=1 undo step)으로 토글한다. 실제로 바뀐 개수를 반환.
    /// disabled=true → 비활성화(숨김), false → 복원. 이미 같은 상태인 Flow 는 건너뛴다.
    [<Extension>]
    static member SetFlowsDisabled(store: DsStore, flowIds: seq<Guid>, disabled: bool) : int =
        let targets =
            flowIds
            |> Seq.distinct
            |> Seq.choose (fun id -> Queries.getFlow id store)
            |> Seq.filter (fun f -> f.IsDisabled <> disabled)
            |> Seq.toList
        if targets.IsEmpty then 0
        else
            let verb = if disabled then "비활성화" else "복원"
            store.WithTransaction($"Flow {targets.Length}개 {verb}", fun () ->
                for f in targets do
                    store.TrackMutate(store.Flows, f.Id, fun x -> x.IsDisabled <- disabled))
            store.EmitRefreshAndHistory()
            targets.Length

    /// 여러 Work 를 target Flow 로 이동한다(한 transaction = 1 undo step). 이동한 개수를 반환.
    /// 이름 중복(target Flow 내 동일 LocalName)·no-op(이미 그 Flow)은 제외한다.
    /// 이동 시 그 Work 가 끝점인 Work 화살표는 제거한다(Call 이동과 동일 정책 — cross-flow 연결 끊김).
    [<Extension>]
    static member MoveWorksToFlow(store: DsStore, workIds: seq<Guid>, targetFlowId: Guid) : int =
        match Queries.getFlow targetFlowId store with
        | None -> 0
        | Some targetFlow ->
            let movables =
                workIds
                |> Seq.distinct
                |> Seq.choose (fun id -> Queries.getWork id store)
                |> Seq.filter (fun w ->
                    w.ParentId <> targetFlowId
                    && Queries.isLocalNameUniqueInFlow targetFlowId w.LocalName (Some w.Id) store)
                |> Seq.toList
            if movables.IsEmpty then 0
            else
                store.WithTransaction($"Move {movables.Length} Work(s) to Flow", fun () ->
                    for w in movables do
                        let arrowIds =
                            store.ArrowWorksReadOnly.Values
                            |> Seq.filter (fun a -> a.SourceId = w.Id || a.TargetId = w.Id)
                            |> Seq.map (fun a -> a.Id)
                            |> Seq.toList
                        for aid in arrowIds do
                            store.TrackRemove(store.ArrowWorks, aid)
                        store.TrackMutate(store.Works, w.Id, fun x ->
                            x.ParentId <- targetFlowId
                            x.FlowPrefix <- targetFlow.Name))
                store.EmitRefreshAndHistory()
                movables.Length

    /// 여러 Flow 를 다른 System 으로 통째로 이동한다(한 transaction = 1 undo step). 이동한 개수를 반환.
    /// Work/Call/ApiCall 은 id 그대로 딸려가고(재생성 아님 — 복사와 다름), 화살표는:
    /// - Flow 내부 Work 끼리의 화살표 → 대상 System 으로 재부모화 (ArrowBetweenWorks.ParentId = systemId 불변식)
    /// - 소스 System 의 타 Flow Work 와 걸친 화살표 → 제거 (Work 이동과 동일 정책 — cross-flow 연결 끊김)
    /// 대상 System 에 같은 이름의 Flow 가 있으면 유니크 이름으로 자동 개명하고 Work.FlowPrefix 를 cascade.
    [<Extension>]
    static member MoveFlowsToSystem(store: DsStore, flowIds: seq<Guid>, targetSystemId: Guid) : int =
        match Queries.getSystem targetSystemId store with
        | None -> 0
        | Some _ ->
            let movables =
                flowIds
                |> Seq.distinct
                |> Seq.choose (fun id -> Queries.getFlow id store)
                |> Seq.filter (fun f -> f.ParentId <> targetSystemId)
                |> Seq.toList
            if movables.IsEmpty then 0
            else
                store.WithTransaction($"Move {movables.Length} Flow(s) to System", fun () ->
                    for flow in movables do
                        let workIds =
                            Queries.worksOf flow.Id store |> List.map (fun w -> w.Id) |> Set.ofList
                        let sourceArrows = Queries.arrowWorksOf flow.ParentId store
                        for arrow in sourceArrows do
                            let srcIn = workIds.Contains arrow.SourceId
                            let tgtIn = workIds.Contains arrow.TargetId
                            if srcIn && tgtIn then
                                store.TrackMutate(store.ArrowWorks, arrow.Id, fun a ->
                                    a.ParentId <- targetSystemId)
                            elif srcIn || tgtIn then
                                store.TrackRemove(store.ArrowWorks, arrow.Id)
                        let existingNames =
                            Queries.flowsOf targetSystemId store |> List.map (fun f -> f.Name)
                        // oldName 캡처 필수 — TrackMutate 는 같은 인스턴스를 뮤테이트하므로
                        // 뮤테이트 후 flow.Name 과 비교하면 개명 여부를 판별할 수 없다.
                        let oldName = flow.Name
                        let newName = Queries.nextUniqueName oldName existingNames
                        store.TrackMutate(store.Flows, flow.Id, fun f ->
                            f.ParentId <- targetSystemId
                            f.Name <- newName)
                        if newName <> oldName then
                            for wid in workIds do
                                store.TrackMutate(store.Works, wid, fun w -> w.FlowPrefix <- newName))
                store.EmitRefreshAndHistory()
                movables.Length

    // ─── AutoLayout ────────────────────────────────────────────────────

    /// 노드들이 모두 같은 좌표에 몰려있으면 자동 배치 적용 (Mermaid 임포트, 탭 최초 오픈 등).
    /// 사용자 동작이 아닌 초기화 과정이므로 Undo 스택에 "Move Selected Nodes" 같은 항목을
    /// 남기지 않는다 — 위치만 직접 변경하고 가벼운 EntitiesMoved 이벤트를 발행한다.
    /// (이전에는 StoreRefreshed 였는데, 새 탭 오픈 시마다 큐잉되는 RebuildAll 이 ~2s hitch 의 주범이었음.
    ///  변한 건 노드 좌표뿐이고 OpenTab 직후 RefreshCanvasForActiveTab 가 새 탭을 채우므로 heavy 갱신 불필요.)
    [<Extension>]
    static member AutoLayoutIfNeeded(store: DsStore, kind: TabKind, rootId: Guid) : bool =
        let content = EditorCanvasProjection.canvasContentForTab store kind rootId
        if CanvasLayout.needsAutoLayout content then
            let mutable applied = 0
            let movedIds = ResizeArray<Guid>()
            for r in CanvasLayout.computeLayout content do
                let newPos = Some r.Position
                match Queries.getWork r.Id store with
                | Some work when work.Position <> newPos ->
                    work.Position <- newPos
                    applied <- applied + 1
                    movedIds.Add(r.Id)
                | Some _ -> ()
                | None ->
                    match Queries.getCall r.Id store with
                    | Some call when call.Position <> newPos ->
                        call.Position <- newPos
                        applied <- applied + 1
                        movedIds.Add(r.Id)
                    | _ -> ()
            if applied > 0 then
                store.EmitEvent(EntitiesMoved (List.ofSeq movedIds))
            applied > 0
        else false
