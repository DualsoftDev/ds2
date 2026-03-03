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

    let internal requireEntity (entityType: string) (entityId: Guid) (entityOpt: 'T option) : 'T =
        entityOpt
        |> Option.defaultWith (fun () ->
            invalidOp $"'{entityType}' entity not found. id={entityId}")

    let private requireCallAndCond (callId: Guid) (conditionId: Guid) (store: DsStore) =
        let call = DsQuery.getCall callId store |> requireEntity "Call" callId
        let cond = call.CallConditions |> Seq.find (fun c -> c.Id = conditionId)
        call, cond

    let private runAdd opName addFn store entity toEvent =
        addFn entity store |> requireMutationOk opName
        [ toEvent entity ]

    let private runRemove opName removeFn store id toEvent =
        removeFn id store |> requireMutationOk opName
        [ toEvent id ]

    let private updateExisting entityType query store id update event =
        let entity = query id store |> requireEntity entityType id
        update entity
        [ event ]

    let private updateCall (callId: Guid) (store: DsStore) (update: Call -> unit) =
        let call = DsQuery.getCall callId store |> requireEntity "Call" callId
        update call
        [ CallPropsChanged callId ]

    let private updateCallCondition (callId: Guid) (conditionId: Guid) (store: DsStore) (update: CallCondition -> unit) =
        let _, condition = requireCallAndCond callId conditionId store
        update condition
        [ CallPropsChanged callId ]

    /// 명령 실행 -> 발행할 이벤트 리스트 반환
    let rec execute (cmd: EditorCommand) (store: DsStore) : EditorEvent list =
        match cmd with

        // --- Project ---
        | AddProject project -> runAdd "addProject" Mutation.addProject store project ProjectAdded
        | RemoveProject backup -> runRemove "removeProject" Mutation.removeProject store backup.Id ProjectRemoved

        // --- System ---
        // Mutation.addSystem은 store.Systems에만 추가하므로
        // Project.ActiveSystems/PassiveSystems 에도 추가해야 함 (동기화 처리)
        | AddSystem(system, projectId, isActive) ->
            Mutation.addSystem system store |> requireMutationOk "addSystem"
            let project = DsQuery.getProject projectId store |> requireEntity "Project" projectId
            if isActive then project.ActiveSystemIds.Add(system.Id)
            else project.PassiveSystemIds.Add(system.Id)
            [ SystemAdded system ]

        | RemoveSystem(backup, _projectId, _isActive) ->
            // Mutation.removeSystem removes system reference from project lists.
            runRemove "removeSystem" Mutation.removeSystem store backup.Id SystemRemoved

        // --- Flow ---
        | AddFlow flow -> runAdd "addFlow" Mutation.addFlow store flow FlowAdded
        | RemoveFlow backup -> runRemove "removeFlow" Mutation.removeFlow store backup.Id FlowRemoved

        // --- Work ---
        | AddWork work -> runAdd "addWork" Mutation.addWork store work WorkAdded
        | RemoveWork backup -> runRemove "removeWork" Mutation.removeWork store backup.Id WorkRemoved

        | MoveWork(id, _, newPos) ->
            updateExisting "Work" DsQuery.getWork store id (fun work -> work.Position <- newPos) (WorkMoved(id, newPos))
        | RenameWork(id, _, newName) ->
            updateExisting "Work" DsQuery.getWork store id (fun work -> work.Name <- newName) (EntityRenamed(id, newName))
        | UpdateWorkProps(id, _, newProps) ->
            updateExisting "Work" DsQuery.getWork store id (fun work -> work.Properties <- newProps) (WorkPropsChanged id)

        // --- Call ---
        // Mutation.removeCall 은 store.ApiCalls 에 포함된 ApiCall 들도 제거하므로
        // 따라서 AddCall execute 에서 store.ApiCalls 에 다시 추가해 주어야 함 (동기화 처리)
        | AddCall call ->
            Mutation.addCall call store |> requireMutationOk "addCall"
            for apiCall in call.ApiCalls do
                store.ApiCalls.[apiCall.Id] <- apiCall
            [ CallAdded call ]

        | RemoveCall backup ->
            runRemove "removeCall" Mutation.removeCall store backup.Id CallRemoved

        | MoveCall(id, _, newPos) ->
            updateExisting "Call" DsQuery.getCall store id (fun call -> call.Position <- newPos) (CallMoved(id, newPos))
        | RenameCall(id, _, newName) ->
            updateExisting "Call" DsQuery.getCall store id (fun call -> call.Name <- newName) (EntityRenamed(id, newName))
        | UpdateCallProps(id, _, newProps) ->
            updateExisting "Call" DsQuery.getCall store id (fun call -> call.Properties <- newProps) (CallPropsChanged id)

        // --- CallCondition ---
        | AddCallCondition(callId, condition) ->
            updateCall callId store (fun call -> call.CallConditions.Add(condition))

        | RemoveCallCondition(callId, backup) ->
            updateCall callId store (fun call -> call.CallConditions.RemoveAll(fun c -> c.Id = backup.Id) |> ignore)

        | UpdateCallConditionSettings(callId, conditionId, _, newIsOR, _, newIsRising) ->
            updateCallCondition callId conditionId store (fun condition ->
                condition.IsOR <- newIsOR
                condition.IsRising <- newIsRising)

        | AddApiCallToCondition(callId, conditionId, apiCall) ->
            updateCallCondition callId conditionId store (fun condition -> condition.Conditions.Add(apiCall))

        | RemoveApiCallFromCondition(callId, conditionId, backup) ->
            updateCallCondition callId conditionId store (fun condition ->
                condition.Conditions.RemoveAll(fun ac -> ac.Id = backup.Id) |> ignore)

        | UpdateConditionApiCallOutputSpec(callId, conditionId, apiCallId, _, newSpec) ->
            updateCallCondition callId conditionId store (fun condition ->
                let apiCall = condition.Conditions |> Seq.find (fun x -> x.Id = apiCallId)
                apiCall.OutputSpec <- newSpec)

        // --- Arrow ---
        | AddArrowWork arrow -> runAdd "addArrowWork" Mutation.addArrowWork store arrow ArrowWorkAdded
        | RemoveArrowWork backup -> runRemove "removeArrowWork" Mutation.removeArrowWork store backup.Id ArrowWorkRemoved
        | AddArrowCall arrow -> runAdd "addArrowCall" Mutation.addArrowCall store arrow ArrowCallAdded
        | RemoveArrowCall backup -> runRemove "removeArrowCall" Mutation.removeArrowCall store backup.Id ArrowCallRemoved

        | ReconnectArrowWork(id, _, _, newSourceId, newTargetId) ->
            updateExisting "ArrowWork" DsQuery.getArrowWork store id
                (fun arrow ->
                    arrow.SourceId <- newSourceId
                    arrow.TargetId <- newTargetId)
                StoreRefreshed
        | ReconnectArrowCall(id, _, _, newSourceId, newTargetId) ->
            updateExisting "ArrowCall" DsQuery.getArrowCall store id
                (fun arrow ->
                    arrow.SourceId <- newSourceId
                    arrow.TargetId <- newTargetId)
                StoreRefreshed

        // --- ApiDef ---
        | AddApiDef apiDef -> runAdd "addApiDef" Mutation.addApiDef store apiDef ApiDefAdded
        | RemoveApiDef backup -> runRemove "removeApiDef" Mutation.removeApiDef store backup.Id ApiDefRemoved

        | UpdateApiDefProps(id, _, newProps) ->
            updateExisting "ApiDef" DsQuery.getApiDef store id (fun apiDef -> apiDef.Properties <- newProps) (ApiDefPropsChanged id)

        // --- ApiCall (Call 내의 API 호출 추가/삭제) ---
        | AddApiCallToCall(callId, apiCall) ->
            updateCall callId store (fun call ->
                call.ApiCalls.Add(apiCall)
                store.ApiCalls.[apiCall.Id] <- apiCall)

        | RemoveApiCallFromCall(callId, apiCall) ->
            updateCall callId store (fun call ->
                call.ApiCalls.RemoveAll(fun ac -> ac.Id = apiCall.Id) |> ignore
                store.ApiCalls.Remove(apiCall.Id) |> ignore)

        // 공유된 ApiCall 은 store.ApiCalls 에서 제거하지 않고 Call.ApiCalls 에서만 제거함
        | AddSharedApiCallToCall(callId, apiCallId) ->
            updateCall callId store (fun call ->
                let apiCall = DsQuery.getApiCall apiCallId store |> requireEntity "ApiCall" apiCallId
                call.ApiCalls.Add(apiCall))

        | RemoveSharedApiCallFromCall(callId, apiCallId) ->
            updateCall callId store (fun call -> call.ApiCalls.RemoveAll(fun ac -> ac.Id = apiCallId) |> ignore)

        // --- HW Components ---
        | AddButton button ->
            runAdd "addButton" Mutation.addButton store button (fun b -> HwComponentAdded(EntityTypeNames.Button, b.Id, b.Name))
        | RemoveButton backup ->
            runRemove "removeButton" Mutation.removeButton store backup.Id (fun id -> HwComponentRemoved(EntityTypeNames.Button, id))
        | AddLamp lamp ->
            runAdd "addLamp" Mutation.addLamp store lamp (fun l -> HwComponentAdded(EntityTypeNames.Lamp, l.Id, l.Name))
        | RemoveLamp backup ->
            runRemove "removeLamp" Mutation.removeLamp store backup.Id (fun id -> HwComponentRemoved(EntityTypeNames.Lamp, id))
        | AddHwCondition condition ->
            runAdd "addCondition" Mutation.addCondition store condition (fun c -> HwComponentAdded(EntityTypeNames.Condition, c.Id, c.Name))
        | RemoveHwCondition backup ->
            runRemove "removeCondition" Mutation.removeCondition store backup.Id (fun id -> HwComponentRemoved(EntityTypeNames.Condition, id))
        | AddHwAction action ->
            runAdd "addAction" Mutation.addAction store action (fun a -> HwComponentAdded(EntityTypeNames.Action, a.Id, a.Name))
        | RemoveHwAction backup ->
            runRemove "removeAction" Mutation.removeAction store backup.Id (fun id -> HwComponentRemoved(EntityTypeNames.Action, id))

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
    let rec private invert (cmd: EditorCommand) : EditorCommand =
        match cmd with
        | AddProject p -> RemoveProject p
        | RemoveProject b -> AddProject b
        | AddSystem(system, projectId, isActive) -> RemoveSystem(system, projectId, isActive)
        | RemoveSystem(backup, projectId, isActive) -> AddSystem(backup, projectId, isActive)
        | AddFlow flow -> RemoveFlow flow
        | RemoveFlow backup -> AddFlow backup
        | AddWork work -> RemoveWork work
        | RemoveWork backup -> AddWork backup
        | AddCall call -> RemoveCall call
        | RemoveCall backup -> AddCall backup
        | AddCallCondition(callId, condition) -> RemoveCallCondition(callId, condition)
        | RemoveCallCondition(callId, backup) -> AddCallCondition(callId, backup)
        | AddApiCallToCondition(callId, conditionId, apiCall) -> RemoveApiCallFromCondition(callId, conditionId, apiCall)
        | RemoveApiCallFromCondition(callId, conditionId, backup) -> AddApiCallToCondition(callId, conditionId, backup)
        | UpdateCallConditionSettings(callId, conditionId, oldIsOR, newIsOR, oldIsRising, newIsRising) -> UpdateCallConditionSettings(callId, conditionId, newIsOR, oldIsOR, newIsRising, oldIsRising)
        | UpdateConditionApiCallOutputSpec(callId, conditionId, apiCallId, oldSpec, newSpec) -> UpdateConditionApiCallOutputSpec(callId, conditionId, apiCallId, newSpec, oldSpec)
        | AddArrowWork arrow -> RemoveArrowWork arrow
        | RemoveArrowWork backup -> AddArrowWork backup
        | AddArrowCall arrow -> RemoveArrowCall arrow
        | RemoveArrowCall backup -> AddArrowCall backup
        | AddApiDef apiDef -> RemoveApiDef apiDef
        | RemoveApiDef backup -> AddApiDef backup
        | AddApiCallToCall(callId, apiCall) -> RemoveApiCallFromCall(callId, apiCall)
        | RemoveApiCallFromCall(callId, apiCall) -> AddApiCallToCall(callId, apiCall)
        | AddSharedApiCallToCall(callId, apiCallId) -> RemoveSharedApiCallFromCall(callId, apiCallId)
        | RemoveSharedApiCallFromCall(callId, apiCallId) -> AddSharedApiCallToCall(callId, apiCallId)
        | AddButton button -> RemoveButton button
        | RemoveButton backup -> AddButton backup
        | AddLamp lamp -> RemoveLamp lamp
        | RemoveLamp backup -> AddLamp backup
        | AddHwCondition condition -> RemoveHwCondition condition
        | RemoveHwCondition backup -> AddHwCondition backup
        | AddHwAction action -> RemoveHwAction action
        | RemoveHwAction backup -> AddHwAction backup
        | MoveWork(id, oldPos, newPos) -> MoveWork(id, newPos, oldPos)
        | RenameWork(id, oldName, newName) -> RenameWork(id, newName, oldName)
        | UpdateWorkProps(id, oldProps, newProps) -> UpdateWorkProps(id, newProps, oldProps)
        | MoveCall(id, oldPos, newPos) -> MoveCall(id, newPos, oldPos)
        | RenameCall(id, oldName, newName) -> RenameCall(id, newName, oldName)
        | UpdateCallProps(id, oldProps, newProps) -> UpdateCallProps(id, newProps, oldProps)
        | UpdateApiDefProps(id, oldProps, newProps) -> UpdateApiDefProps(id, newProps, oldProps)
        | ReconnectArrowWork(id, oldSourceId, oldTargetId, newSourceId, newTargetId) -> ReconnectArrowWork(id, newSourceId, newTargetId, oldSourceId, oldTargetId)
        | ReconnectArrowCall(id, oldSourceId, oldTargetId, newSourceId, newTargetId) -> ReconnectArrowCall(id, newSourceId, newTargetId, oldSourceId, oldTargetId)
        | RenameEntity(id, entityType, oldName, newName) -> RenameEntity(id, entityType, newName, oldName)
        | Composite(_, commands) -> Composite("Undo Composite", commands |> List.rev |> List.map invert)

    /// 명령 실행 취소 -> 반대 이벤트를 반환
    /// Add/Remove 계열은 undo(Add X) = execute(Remove X), undo(Remove X) = execute(Add X)
    /// 상태 변경 계열 (Move/Rename/Update/Reconnect): old/new 값을 반전하여 execute 호출
    let undo (cmd: EditorCommand) (store: DsStore) : EditorEvent list =
        execute (invert cmd) store