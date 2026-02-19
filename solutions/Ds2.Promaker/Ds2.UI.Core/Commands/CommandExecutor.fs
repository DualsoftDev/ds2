namespace Ds2.UI.Core

open System
open Ds2.Core

// =============================================================================
// CommandExecutor 의 execute/undo 핵심 구현 (STRUCTURE.md 5, 5.1)
// =============================================================================

module CommandExecutor =

    let private requireMutationOk (opName: string) (result: Result<unit, string>) =
        match result with
        | Ok () -> ()
        | Error message -> invalidOp $"Mutation '{opName}' failed: {message}"
    /// 명령 실행 -> 발행할 이벤트 리스트 반환
    let rec execute (cmd: EditorCommand) (store: DsStore) : EditorEvent list =
        match cmd with

        // --- Project ---
        | AddProject project ->
            Mutation.addProject project store |> requireMutationOk "addProject"
            [ ProjectAdded project ]

        | RemoveProject backup ->
            Mutation.removeProject backup.Id store |> requireMutationOk "removeProject"
            [ ProjectRemoved backup.Id ]

        // --- System ---
        // Mutation.addSystem은 store.Systems에만 추가하므로
        // Project.ActiveSystems/PassiveSystems 에도 추가해야 함 (동기화 처리)
        | AddSystem(system, projectId, isActive) ->
            Mutation.addSystem system store |> requireMutationOk "addSystem"
            let project = DsQuery.getProject projectId store |> Option.get
            if isActive then project.ActiveSystemIds.Add(system.Id)
            else project.PassiveSystemIds.Add(system.Id)
            [ SystemAdded system ]

        | RemoveSystem(backup, _projectId, _isActive) ->
            // Mutation.removeSystem 은 Project 하위의 참조도 함께 제거함
            Mutation.removeSystem backup.Id store |> requireMutationOk "removeSystem"
            [ SystemRemoved backup.Id ]

        // --- Flow ---
        | AddFlow flow ->
            Mutation.addFlow flow store |> requireMutationOk "addFlow"
            [ FlowAdded flow ]

        | RemoveFlow backup ->
            Mutation.removeFlow backup.Id store |> requireMutationOk "removeFlow"
            [ FlowRemoved backup.Id ]

        // --- Work ---
        | AddWork work ->
            Mutation.addWork work store |> requireMutationOk "addWork"
            [ WorkAdded work ]

        | RemoveWork backup ->
            Mutation.removeWork backup.Id store |> requireMutationOk "removeWork"
            [ WorkRemoved backup.Id ]

        | MoveWork(id, _, newPos) ->
            let work = DsQuery.getWork id store |> Option.get
            work.Position <- newPos
            [ WorkMoved(id, newPos) ]

        | RenameWork(id, _, newName) ->
            let work = DsQuery.getWork id store |> Option.get
            work.Name <- newName
            [ EntityRenamed(id, newName) ]

        | UpdateWorkProps(id, _, newProps) ->
            let work = DsQuery.getWork id store |> Option.get
            work.Properties <- newProps
            [ WorkPropsChanged id ]

        // --- Call ---
        // Mutation.removeCall 은 store.ApiCalls 에 포함된 ApiCall 들도 제거하므로
        // 따라서 AddCall execute 에서 store.ApiCalls 에 다시 추가해 주어야 함 (동기화 처리)
        | AddCall call ->
            Mutation.addCall call store |> requireMutationOk "addCall"
            for (apiCall, _) in call.ApiCalls do
                store.ApiCalls.[apiCall.Id] <- apiCall
            [ CallAdded call ]

        | RemoveCall backup ->
            Mutation.removeCall backup.Id store |> requireMutationOk "removeCall"
            [ CallRemoved backup.Id ]

        | MoveCall(id, _, newPos) ->
            let call = DsQuery.getCall id store |> Option.get
            call.Position <- newPos
            [ CallMoved(id, newPos) ]

        | RenameCall(id, _, newName) ->
            let call = DsQuery.getCall id store |> Option.get
            call.Name <- newName
            [ EntityRenamed(id, newName) ]

        | UpdateCallProps(id, _, newProps) ->
            let call = DsQuery.getCall id store |> Option.get
            call.Properties <- newProps
            [ CallPropsChanged id ]

        // --- Arrow ---
        | AddArrowWork arrow ->
            Mutation.addArrowWork arrow store |> requireMutationOk "addArrowWork"
            [ ArrowWorkAdded arrow ]

        | RemoveArrowWork backup ->
            Mutation.removeArrowWork backup.Id store |> requireMutationOk "removeArrowWork"
            [ ArrowWorkRemoved backup.Id ]

        | AddArrowCall arrow ->
            Mutation.addArrowCall arrow store |> requireMutationOk "addArrowCall"
            [ ArrowCallAdded arrow ]

        | RemoveArrowCall backup ->
            Mutation.removeArrowCall backup.Id store |> requireMutationOk "removeArrowCall"
            [ ArrowCallRemoved backup.Id ]

        | ReconnectArrowWork(id, _, _, newSourceId, newTargetId) ->
            let arrow = DsQuery.getArrowWork id store |> Option.get
            arrow.SourceId <- newSourceId
            arrow.TargetId <- newTargetId
            [ StoreRefreshed ]

        | ReconnectArrowCall(id, _, _, newSourceId, newTargetId) ->
            let arrow = DsQuery.getArrowCall id store |> Option.get
            arrow.SourceId <- newSourceId
            arrow.TargetId <- newTargetId
            [ StoreRefreshed ]

        // --- ApiDef ---
        | AddApiDef apiDef ->
            Mutation.addApiDef apiDef store |> requireMutationOk "addApiDef"
            [ ApiDefAdded apiDef ]

        | RemoveApiDef backup ->
            Mutation.removeApiDef backup.Id store |> requireMutationOk "removeApiDef"
            [ ApiDefRemoved backup.Id ]

        | UpdateApiDefProps(id, _, newProps) ->
            let apiDef = DsQuery.getApiDef id store |> Option.get
            apiDef.Properties <- newProps
            [ ApiDefPropsChanged id ]

        // --- ApiCall (Call 내의 API 호출 추가/삭제) ---
        | AddApiCallToCall(callId, apiCall, valueSpec) ->
            let call = DsQuery.getCall callId store |> Option.get
            call.ApiCalls.Add((apiCall, valueSpec))
            store.ApiCalls.[apiCall.Id] <- apiCall
            [ CallPropsChanged callId ]

        | RemoveApiCallFromCall(callId, apiCall, _valueSpec) ->
            let call = DsQuery.getCall callId store |> Option.get
            call.ApiCalls.RemoveAll(fun (ac, _) -> ac.Id = apiCall.Id) |> ignore
            store.ApiCalls.Remove(apiCall.Id) |> ignore
            [ CallPropsChanged callId ]

        // 공유된 ApiCall 은 store.ApiCalls 에서 제거하지 않고 Call.ApiCalls 에서만 제거함
        | AddSharedApiCallToCall(callId, apiCallId, valueSpec) ->
            let call = DsQuery.getCall callId store |> Option.get
            match DsQuery.getApiCall apiCallId store with
            | Some apiCall -> call.ApiCalls.Add((apiCall, valueSpec))
            | None -> ()
            [ CallPropsChanged callId ]

        | RemoveSharedApiCallFromCall(callId, apiCallId, _valueSpec) ->
            let call = DsQuery.getCall callId store |> Option.get
            call.ApiCalls.RemoveAll(fun (ac, _) -> ac.Id = apiCallId) |> ignore
            [ CallPropsChanged callId ]

        // --- HW Components ---
        | AddButton button ->
            Mutation.addButton button store |> requireMutationOk "addButton"
            [ HwComponentAdded("Button", button.Id, button.Name) ]

        | RemoveButton backup ->
            Mutation.removeButton backup.Id store |> requireMutationOk "removeButton"
            [ HwComponentRemoved("Button", backup.Id) ]

        | AddLamp lamp ->
            Mutation.addLamp lamp store |> requireMutationOk "addLamp"
            [ HwComponentAdded("Lamp", lamp.Id, lamp.Name) ]

        | RemoveLamp backup ->
            Mutation.removeLamp backup.Id store |> requireMutationOk "removeLamp"
            [ HwComponentRemoved("Lamp", backup.Id) ]

        | AddHwCondition condition ->
            Mutation.addCondition condition store |> requireMutationOk "addCondition"
            [ HwComponentAdded("Condition", condition.Id, condition.Name) ]

        | RemoveHwCondition backup ->
            Mutation.removeCondition backup.Id store |> requireMutationOk "removeCondition"
            [ HwComponentRemoved("Condition", backup.Id) ]

        | AddHwAction action ->
            Mutation.addAction action store |> requireMutationOk "addAction"
            [ HwComponentAdded("Action", action.Id, action.Name) ]

        | RemoveHwAction backup ->
            Mutation.removeAction backup.Id store |> requireMutationOk "removeAction"
            [ HwComponentRemoved("Action", backup.Id) ]

        // --- 엔티티 공통 ---
        | RenameEntity(id, entityType, _, newName) ->
            EntityNameAccess.setName store entityType id newName
            [ EntityRenamed(id, newName) ]

        // --- Composite ---
        | Composite(_, commands) ->
            commands |> List.collect (fun c -> execute c store)

    /// 명령 실행 취소 -> 역방향 이벤트를 반환
    /// Add/Remove 계열은 undo(Add X) = execute(Remove X), undo(Remove X) = execute(Add X)
    /// 상태 변경 계열 (Move/Rename/Update/Reconnect): old/new 값을 반전하여 execute 호출
    let rec undo (cmd: EditorCommand) (store: DsStore) : EditorEvent list =
        match cmd with
        // --- 추가/삭제 (Add/Remove) ---
        | AddProject p                           -> execute (RemoveProject p) store
        | RemoveProject b                        -> execute (AddProject b) store
        | AddSystem(s, pid, ia)                  -> execute (RemoveSystem(s, pid, ia)) store
        | RemoveSystem(b, pid, ia)               -> execute (AddSystem(b, pid, ia)) store
        | AddFlow f                              -> execute (RemoveFlow f) store
        | RemoveFlow b                           -> execute (AddFlow b) store
        | AddWork w                              -> execute (RemoveWork w) store
        | RemoveWork b                           -> execute (AddWork b) store
        | AddCall c                              -> execute (RemoveCall c) store
        | RemoveCall b                           -> execute (AddCall b) store
        | AddArrowWork a                         -> execute (RemoveArrowWork a) store
        | RemoveArrowWork b                      -> execute (AddArrowWork b) store
        | AddArrowCall a                         -> execute (RemoveArrowCall a) store
        | RemoveArrowCall b                      -> execute (AddArrowCall b) store
        | AddApiDef d                            -> execute (RemoveApiDef d) store
        | RemoveApiDef b                         -> execute (AddApiDef b) store
        | AddApiCallToCall(c, ac, vs)            -> execute (RemoveApiCallFromCall(c, ac, vs)) store
        | RemoveApiCallFromCall(c, ac, vs)       -> execute (AddApiCallToCall(c, ac, vs)) store
        | AddSharedApiCallToCall(c, id, vs)      -> execute (RemoveSharedApiCallFromCall(c, id, vs)) store
        | RemoveSharedApiCallFromCall(c, id, vs) -> execute (AddSharedApiCallToCall(c, id, vs)) store
        | AddButton b                            -> execute (RemoveButton b) store
        | RemoveButton b                         -> execute (AddButton b) store
        | AddLamp l                              -> execute (RemoveLamp l) store
        | RemoveLamp b                           -> execute (AddLamp b) store
        | AddHwCondition c                       -> execute (RemoveHwCondition c) store
        | RemoveHwCondition b                    -> execute (AddHwCondition b) store
        | AddHwAction a                          -> execute (RemoveHwAction a) store
        | RemoveHwAction b                       -> execute (AddHwAction b) store
        // --- 상태 변경 (Move/Rename/Update/Reconnect): old/new 반전 ---
        | MoveWork(id, oldPos, newPos)                   -> execute (MoveWork(id, newPos, oldPos)) store
        | RenameWork(id, oldName, newName)               -> execute (RenameWork(id, newName, oldName)) store
        | UpdateWorkProps(id, oldProps, newProps)        -> execute (UpdateWorkProps(id, newProps, oldProps)) store
        | MoveCall(id, oldPos, newPos)                   -> execute (MoveCall(id, newPos, oldPos)) store
        | RenameCall(id, oldName, newName)               -> execute (RenameCall(id, newName, oldName)) store
        | UpdateCallProps(id, oldProps, newProps)        -> execute (UpdateCallProps(id, newProps, oldProps)) store
        | UpdateApiDefProps(id, oldProps, newProps)      -> execute (UpdateApiDefProps(id, newProps, oldProps)) store
        | ReconnectArrowWork(id, os, ot, ns, nt)         -> execute (ReconnectArrowWork(id, ns, nt, os, ot)) store
        | ReconnectArrowCall(id, os, ot, ns, nt)         -> execute (ReconnectArrowCall(id, ns, nt, os, ot)) store
        | RenameEntity(id, t, oldName, newName)          -> execute (RenameEntity(id, t, newName, oldName)) store
        // --- Composite: 역순으로 undo ---
        | Composite(_, commands) ->
            commands |> List.rev |> List.collect (fun c -> undo c store)