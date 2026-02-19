namespace Ds2.UI.Core

open System
open Ds2.Core

// =============================================================================
// Mutation 모듈 - 쓰기 API (CRUD의 Create, Update, Delete)
// =============================================================================

/// <summary>
/// 도메인 스토어 변경 모듈
///
/// <para>모든 엔티티 생성, 수정, 삭제 기능을 제공합니다.</para>
/// <para>모든 작업은 Result&lt;unit, string&gt;를 반환하여 에러 처리가 가능합니다.</para>
///
/// <example>
/// <code>
/// let store = DsStore.empty()
/// let project = Project("MyProject")
///
/// match Mutation.addProject project store with
/// | Ok () -> printfn "Success"
/// | Error msg -> printfn "Error: %s" msg
/// </code>
/// </example>
/// </summary>
module Mutation =

    // ─────────────────────────────────────────────────────────────────────────
    // Project CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Project 추가</summary>
    let addProject (project: Project) (store: DsStore) : Result<unit, string> =
        if store.Projects.ContainsKey(project.Id) then
            Error $"Project with ID {project.Id} already exists"
        else
            store.Projects.[project.Id] <- project
            Ok ()

    /// <summary>Project 업데이트</summary>
    let updateProject (project: Project) (store: DsStore) : Result<unit, string> =
        if not (store.Projects.ContainsKey(project.Id)) then
            Error $"Project with ID {project.Id} not found"
        else
            store.Projects.[project.Id] <- project
            Ok ()

    /// <summary>Project 삭제</summary>
    let removeProject (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.Projects.ContainsKey(id)) then
            Error $"Project with ID {id} not found"
        else
            store.Projects.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // DsSystem CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>DsSystem 추가</summary>
    let addSystem (system: DsSystem) (store: DsStore) : Result<unit, string> =
        if store.Systems.ContainsKey(system.Id) then
            Error $"DsSystem with ID {system.Id} already exists"
        else
            store.Systems.[system.Id] <- system
            Ok ()

    /// <summary>DsSystem 업데이트</summary>
    let updateSystem (system: DsSystem) (store: DsStore) : Result<unit, string> =
        if not (store.Systems.ContainsKey(system.Id)) then
            Error $"DsSystem with ID {system.Id} not found"
        else
            store.Systems.[system.Id] <- system
            Ok ()

    /// <summary>
    /// DsSystem 삭제 (Orphan 방지)
    /// <para>삭제 시 모든 Project의 ActiveSystems/PassiveSystems 컬렉션에서 제거</para>
    /// </summary>
    let removeSystem (id: Guid) (store: DsStore) : Result<unit, string> =
        match store.Systems.TryGetValue(id) with
        | false, _ -> Error $"DsSystem with ID {id} not found"
        | true, _system ->
            // Project.ActiveSystemIds/PassiveSystemIds에서 제거
            store.Projects.Values
            |> Seq.iter (fun p ->
                p.ActiveSystemIds.Remove(id) |> ignore
                p.PassiveSystemIds.Remove(id) |> ignore)

            // 스토어에서 제거
            store.Systems.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // Flow CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Flow 추가</summary>
    let addFlow (flow: Flow) (store: DsStore) : Result<unit, string> =
        if store.Flows.ContainsKey(flow.Id) then
            Error $"Flow with ID {flow.Id} already exists"
        else
            store.Flows.[flow.Id] <- flow
            Ok ()

    /// <summary>Flow 업데이트</summary>
    let updateFlow (flow: Flow) (store: DsStore) : Result<unit, string> =
        if not (store.Flows.ContainsKey(flow.Id)) then
            Error $"Flow with ID {flow.Id} not found"
        else
            store.Flows.[flow.Id] <- flow
            Ok ()

    /// <summary>Flow 삭제</summary>
    let removeFlow (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.Flows.ContainsKey(id)) then
            Error $"Flow with ID {id} not found"
        else
            store.Flows.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // Work CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Work 추가</summary>
    let addWork (work: Work) (store: DsStore) : Result<unit, string> =
        if store.Works.ContainsKey(work.Id) then
            Error $"Work with ID {work.Id} already exists"
        else
            store.Works.[work.Id] <- work
            Ok ()

    /// <summary>Work 업데이트</summary>
    let updateWork (work: Work) (store: DsStore) : Result<unit, string> =
        if not (store.Works.ContainsKey(work.Id)) then
            Error $"Work with ID {work.Id} not found"
        else
            store.Works.[work.Id] <- work
            Ok ()

    /// <summary>Work 삭제</summary>
    let removeWork (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.Works.ContainsKey(id)) then
            Error $"Work with ID {id} not found"
        else
            store.Works.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // Call CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Call 추가</summary>
    let addCall (call: Call) (store: DsStore) : Result<unit, string> =
        if store.Calls.ContainsKey(call.Id) then
            Error $"Call with ID {call.Id} already exists"
        else
            store.Calls.[call.Id] <- call
            Ok ()

    /// <summary>Call 업데이트</summary>
    let updateCall (call: Call) (store: DsStore) : Result<unit, string> =
        if not (store.Calls.ContainsKey(call.Id)) then
            Error $"Call with ID {call.Id} not found"
        else
            store.Calls.[call.Id] <- call
            Ok ()

    /// <summary>Call 삭제</summary>
    let removeCall (id: Guid) (store: DsStore) : Result<unit, string> =
        match store.Calls.TryGetValue(id) with
        | false, _ -> Error $"Call with ID {id} not found"
        | true, call ->
            // 다른 Call이 공유 중인 ApiCall은 store에서 제거하지 않음 (공유 참조 보호)
            let sharedIds =
                store.Calls.Values
                |> Seq.filter (fun c -> c.Id <> id)
                |> Seq.collect (fun c -> c.ApiCalls |> Seq.map (fun (ac, _) -> ac.Id))
                |> Set.ofSeq
            call.ApiCalls |> Seq.iter (fun ((ac: ApiCall), _) ->
                if not (sharedIds.Contains ac.Id) then
                    store.ApiCalls.Remove(ac.Id) |> ignore)

            // 스토어에서 제거
            store.Calls.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // ApiDef CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ApiDef 추가</summary>
    let addApiDef (apiDef: ApiDef) (store: DsStore) : Result<unit, string> =
        if store.ApiDefs.ContainsKey(apiDef.Id) then
            Error $"ApiDef with ID {apiDef.Id} already exists"
        else
            store.ApiDefs.[apiDef.Id] <- apiDef
            Ok ()

    /// <summary>ApiDef 업데이트</summary>
    let updateApiDef (apiDef: ApiDef) (store: DsStore) : Result<unit, string> =
        if not (store.ApiDefs.ContainsKey(apiDef.Id)) then
            Error $"ApiDef with ID {apiDef.Id} not found"
        else
            store.ApiDefs.[apiDef.Id] <- apiDef
            Ok ()

    /// <summary>ApiDef 삭제</summary>
    let removeApiDef (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.ApiDefs.ContainsKey(id)) then
            Error $"ApiDef with ID {id} not found"
        else
            store.ApiDefs.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // ApiCall CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ApiCall 추가</summary>
    let addApiCall (apiCall: ApiCall) (store: DsStore) : Result<unit, string> =
        if store.ApiCalls.ContainsKey(apiCall.Id) then
            Error $"ApiCall with ID {apiCall.Id} already exists"
        else
            store.ApiCalls.[apiCall.Id] <- apiCall
            Ok ()

    /// <summary>ApiCall 업데이트</summary>
    let updateApiCall (apiCall: ApiCall) (store: DsStore) : Result<unit, string> =
        if not (store.ApiCalls.ContainsKey(apiCall.Id)) then
            Error $"ApiCall with ID {apiCall.Id} not found"
        else
            store.ApiCalls.[apiCall.Id] <- apiCall
            Ok ()

    /// <summary>
    /// ApiCall 삭제 (Orphan 방지)
    /// <para>삭제 시 모든 Call.ApiCalls 컬렉션에서 제거</para>
    /// </summary>
    let removeApiCall (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.ApiCalls.ContainsKey(id)) then
            Error $"ApiCall with ID {id} not found"
        else
            // Call.ApiCalls에서 제거
            store.Calls.Values
            |> Seq.iter (fun call ->
                let idx = call.ApiCalls.FindIndex(fun ((ac: ApiCall), _) -> ac.Id = id)
                if idx >= 0 then
                    call.ApiCalls.RemoveAt(idx))

            // 스토어에서 제거
            store.ApiCalls.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // ArrowBetweenWorks CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ArrowBetweenWorks 추가</summary>
    let addArrowWork (arrow: ArrowBetweenWorks) (store: DsStore) : Result<unit, string> =
        if store.ArrowWorks.ContainsKey(arrow.Id) then
            Error $"ArrowBetweenWorks with ID {arrow.Id} already exists"
        else
            store.ArrowWorks.[arrow.Id] <- arrow
            Ok ()

    /// <summary>ArrowBetweenWorks 업데이트</summary>
    let updateArrowWork (arrow: ArrowBetweenWorks) (store: DsStore) : Result<unit, string> =
        if not (store.ArrowWorks.ContainsKey(arrow.Id)) then
            Error $"ArrowBetweenWorks with ID {arrow.Id} not found"
        else
            store.ArrowWorks.[arrow.Id] <- arrow
            Ok ()

    /// <summary>ArrowBetweenWorks 삭제</summary>
    let removeArrowWork (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.ArrowWorks.ContainsKey(id)) then
            Error $"ArrowBetweenWorks with ID {id} not found"
        else
            store.ArrowWorks.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // ArrowBetweenCalls CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ArrowBetweenCalls 추가</summary>
    let addArrowCall (arrow: ArrowBetweenCalls) (store: DsStore) : Result<unit, string> =
        if store.ArrowCalls.ContainsKey(arrow.Id) then
            Error $"ArrowBetweenCalls with ID {arrow.Id} already exists"
        else
            store.ArrowCalls.[arrow.Id] <- arrow
            Ok ()

    /// <summary>ArrowBetweenCalls 업데이트</summary>
    let updateArrowCall (arrow: ArrowBetweenCalls) (store: DsStore) : Result<unit, string> =
        if not (store.ArrowCalls.ContainsKey(arrow.Id)) then
            Error $"ArrowBetweenCalls with ID {arrow.Id} not found"
        else
            store.ArrowCalls.[arrow.Id] <- arrow
            Ok ()

    /// <summary>ArrowBetweenCalls 삭제</summary>
    let removeArrowCall (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.ArrowCalls.ContainsKey(id)) then
            Error $"ArrowBetweenCalls with ID {id} not found"
        else
            store.ArrowCalls.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // HwButton CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwButton 추가</summary>
    let addButton (button: HwButton) (store: DsStore) : Result<unit, string> =
        if store.HwButtons.ContainsKey(button.Id) then
            Error $"HwButton with ID {button.Id} already exists"
        else
            store.HwButtons.[button.Id] <- button
            Ok ()

    /// <summary>HwButton 업데이트</summary>
    let updateButton (button: HwButton) (store: DsStore) : Result<unit, string> =
        if not (store.HwButtons.ContainsKey(button.Id)) then
            Error $"HwButton with ID {button.Id} not found"
        else
            store.HwButtons.[button.Id] <- button
            Ok ()

    /// <summary>HwButton 삭제</summary>
    let removeButton (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.HwButtons.ContainsKey(id)) then
            Error $"HwButton with ID {id} not found"
        else
            store.HwButtons.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // HwLamp CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwLamp 추가</summary>
    let addLamp (lamp: HwLamp) (store: DsStore) : Result<unit, string> =
        if store.HwLamps.ContainsKey(lamp.Id) then
            Error $"HwLamp with ID {lamp.Id} already exists"
        else
            store.HwLamps.[lamp.Id] <- lamp
            Ok ()

    /// <summary>HwLamp 업데이트</summary>
    let updateLamp (lamp: HwLamp) (store: DsStore) : Result<unit, string> =
        if not (store.HwLamps.ContainsKey(lamp.Id)) then
            Error $"HwLamp with ID {lamp.Id} not found"
        else
            store.HwLamps.[lamp.Id] <- lamp
            Ok ()

    /// <summary>HwLamp 삭제</summary>
    let removeLamp (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.HwLamps.ContainsKey(id)) then
            Error $"HwLamp with ID {id} not found"
        else
            store.HwLamps.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // HwCondition CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwCondition 추가</summary>
    let addCondition (condition: HwCondition) (store: DsStore) : Result<unit, string> =
        if store.HwConditions.ContainsKey(condition.Id) then
            Error $"HwCondition with ID {condition.Id} already exists"
        else
            store.HwConditions.[condition.Id] <- condition
            Ok ()

    /// <summary>HwCondition 업데이트</summary>
    let updateCondition (condition: HwCondition) (store: DsStore) : Result<unit, string> =
        if not (store.HwConditions.ContainsKey(condition.Id)) then
            Error $"HwCondition with ID {condition.Id} not found"
        else
            store.HwConditions.[condition.Id] <- condition
            Ok ()

    /// <summary>HwCondition 삭제</summary>
    let removeCondition (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.HwConditions.ContainsKey(id)) then
            Error $"HwCondition with ID {id} not found"
        else
            store.HwConditions.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // HwAction CRUD
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>HwAction 추가</summary>
    let addAction (action: HwAction) (store: DsStore) : Result<unit, string> =
        if store.HwActions.ContainsKey(action.Id) then
            Error $"HwAction with ID {action.Id} already exists"
        else
            store.HwActions.[action.Id] <- action
            Ok ()

    /// <summary>HwAction 업데이트</summary>
    let updateAction (action: HwAction) (store: DsStore) : Result<unit, string> =
        if not (store.HwActions.ContainsKey(action.Id)) then
            Error $"HwAction with ID {action.Id} not found"
        else
            store.HwActions.[action.Id] <- action
            Ok ()

    /// <summary>HwAction 삭제</summary>
    let removeAction (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.HwActions.ContainsKey(id)) then
            Error $"HwAction with ID {id} not found"
        else
            store.HwActions.Remove(id) |> ignore
            Ok ()