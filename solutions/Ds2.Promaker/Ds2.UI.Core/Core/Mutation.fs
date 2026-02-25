namespace Ds2.UI.Core

open System
open Ds2.Core

// =============================================================================
// Mutation 모듈 - 쓰기 API (CRUD의 Create, Update, Delete)
// =============================================================================

module Mutation =

    // ─────────────────────────────────────────────────────────────────────────
    // 내부 헬퍼 — dictionary CRUD 보일러플레이트 제거
    // ─────────────────────────────────────────────────────────────────────────

    let private addEntity (dict: System.Collections.Generic.Dictionary<Guid, 'T>) (id: Guid) (entity: 'T) (typeName: string) : Result<unit, string> =
        if dict.ContainsKey(id) then Error $"{typeName} with ID {id} already exists"
        else dict.[id] <- entity; Ok ()

    let private updateEntity (dict: System.Collections.Generic.Dictionary<Guid, 'T>) (id: Guid) (entity: 'T) (typeName: string) : Result<unit, string> =
        if not (dict.ContainsKey(id)) then Error $"{typeName} with ID {id} not found"
        else dict.[id] <- entity; Ok ()

    let private removeEntity (dict: System.Collections.Generic.Dictionary<Guid, 'T>) (id: Guid) (typeName: string) : Result<unit, string> =
        if not (dict.ContainsKey(id)) then Error $"{typeName} with ID {id} not found"
        else dict.Remove(id) |> ignore; Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // Project CRUD
    // ─────────────────────────────────────────────────────────────────────────

    let addProject (project: Project) (store: DsStore) =
        addEntity store.Projects project.Id project "Project"

    let updateProject (project: Project) (store: DsStore) =
        updateEntity store.Projects project.Id project "Project"

    let removeProject (id: Guid) (store: DsStore) =
        removeEntity store.Projects id "Project"

    // ─────────────────────────────────────────────────────────────────────────
    // DsSystem CRUD
    // ─────────────────────────────────────────────────────────────────────────

    let addSystem (system: DsSystem) (store: DsStore) =
        addEntity store.Systems system.Id system "DsSystem"

    let updateSystem (system: DsSystem) (store: DsStore) =
        updateEntity store.Systems system.Id system "DsSystem"

    /// DsSystem 삭제 — Project.ActiveSystemIds/PassiveSystemIds에서도 제거
    let removeSystem (id: Guid) (store: DsStore) : Result<unit, string> =
        match store.Systems.TryGetValue(id) with
        | false, _ -> Error $"DsSystem with ID {id} not found"
        | true, _ ->
            store.Projects.Values
            |> Seq.iter (fun p ->
                p.ActiveSystemIds.Remove(id) |> ignore
                p.PassiveSystemIds.Remove(id) |> ignore)
            store.Systems.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // Flow CRUD
    // ─────────────────────────────────────────────────────────────────────────

    let addFlow (flow: Flow) (store: DsStore) =
        addEntity store.Flows flow.Id flow "Flow"

    let updateFlow (flow: Flow) (store: DsStore) =
        updateEntity store.Flows flow.Id flow "Flow"

    let removeFlow (id: Guid) (store: DsStore) =
        removeEntity store.Flows id "Flow"

    // ─────────────────────────────────────────────────────────────────────────
    // Work CRUD
    // ─────────────────────────────────────────────────────────────────────────

    let addWork (work: Work) (store: DsStore) =
        addEntity store.Works work.Id work "Work"

    let updateWork (work: Work) (store: DsStore) =
        updateEntity store.Works work.Id work "Work"

    let removeWork (id: Guid) (store: DsStore) =
        removeEntity store.Works id "Work"

    // ─────────────────────────────────────────────────────────────────────────
    // Call CRUD
    // ─────────────────────────────────────────────────────────────────────────

    let addCall (call: Call) (store: DsStore) =
        addEntity store.Calls call.Id call "Call"

    let updateCall (call: Call) (store: DsStore) =
        updateEntity store.Calls call.Id call "Call"

    /// Call 삭제 — 다른 Call이 공유 중인 ApiCall은 store에서 제거하지 않음
    let removeCall (id: Guid) (store: DsStore) : Result<unit, string> =
        match store.Calls.TryGetValue(id) with
        | false, _ -> Error $"Call with ID {id} not found"
        | true, call ->
            let sharedIds =
                store.Calls.Values
                |> Seq.filter (fun c -> c.Id <> id)
                |> Seq.collect (fun c -> c.ApiCalls |> Seq.map (fun ac -> ac.Id))
                |> Set.ofSeq
            call.ApiCalls |> Seq.iter (fun (ac: ApiCall) ->
                if not (sharedIds.Contains ac.Id) then
                    store.ApiCalls.Remove(ac.Id) |> ignore)
            store.Calls.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // ApiDef CRUD
    // ─────────────────────────────────────────────────────────────────────────

    let addApiDef (apiDef: ApiDef) (store: DsStore) =
        addEntity store.ApiDefs apiDef.Id apiDef "ApiDef"

    let updateApiDef (apiDef: ApiDef) (store: DsStore) =
        updateEntity store.ApiDefs apiDef.Id apiDef "ApiDef"

    let removeApiDef (id: Guid) (store: DsStore) =
        removeEntity store.ApiDefs id "ApiDef"

    // ─────────────────────────────────────────────────────────────────────────
    // ApiCall CRUD
    // ─────────────────────────────────────────────────────────────────────────

    let addApiCall (apiCall: ApiCall) (store: DsStore) =
        addEntity store.ApiCalls apiCall.Id apiCall "ApiCall"

    let updateApiCall (apiCall: ApiCall) (store: DsStore) =
        updateEntity store.ApiCalls apiCall.Id apiCall "ApiCall"

    /// ApiCall 삭제 — Call.ApiCalls 컬렉션에서도 제거
    let removeApiCall (id: Guid) (store: DsStore) : Result<unit, string> =
        if not (store.ApiCalls.ContainsKey(id)) then
            Error $"ApiCall with ID {id} not found"
        else
            store.Calls.Values
            |> Seq.iter (fun call ->
                let idx = call.ApiCalls.FindIndex(fun (ac: ApiCall) -> ac.Id = id)
                if idx >= 0 then
                    call.ApiCalls.RemoveAt(idx))
            store.ApiCalls.Remove(id) |> ignore
            Ok ()

    // ─────────────────────────────────────────────────────────────────────────
    // ArrowBetweenWorks CRUD
    // ─────────────────────────────────────────────────────────────────────────

    let addArrowWork (arrow: ArrowBetweenWorks) (store: DsStore) =
        addEntity store.ArrowWorks arrow.Id arrow "ArrowBetweenWorks"

    let updateArrowWork (arrow: ArrowBetweenWorks) (store: DsStore) =
        updateEntity store.ArrowWorks arrow.Id arrow "ArrowBetweenWorks"

    let removeArrowWork (id: Guid) (store: DsStore) =
        removeEntity store.ArrowWorks id "ArrowBetweenWorks"

    // ─────────────────────────────────────────────────────────────────────────
    // ArrowBetweenCalls CRUD
    // ─────────────────────────────────────────────────────────────────────────

    let addArrowCall (arrow: ArrowBetweenCalls) (store: DsStore) =
        addEntity store.ArrowCalls arrow.Id arrow "ArrowBetweenCalls"

    let updateArrowCall (arrow: ArrowBetweenCalls) (store: DsStore) =
        updateEntity store.ArrowCalls arrow.Id arrow "ArrowBetweenCalls"

    let removeArrowCall (id: Guid) (store: DsStore) =
        removeEntity store.ArrowCalls id "ArrowBetweenCalls"

    // ─────────────────────────────────────────────────────────────────────────
    // Hardware CRUD
    // ─────────────────────────────────────────────────────────────────────────

    let addButton (button: HwButton) (store: DsStore) =
        addEntity store.HwButtons button.Id button "HwButton"

    let updateButton (button: HwButton) (store: DsStore) =
        updateEntity store.HwButtons button.Id button "HwButton"

    let removeButton (id: Guid) (store: DsStore) =
        removeEntity store.HwButtons id "HwButton"

    let addLamp (lamp: HwLamp) (store: DsStore) =
        addEntity store.HwLamps lamp.Id lamp "HwLamp"

    let updateLamp (lamp: HwLamp) (store: DsStore) =
        updateEntity store.HwLamps lamp.Id lamp "HwLamp"

    let removeLamp (id: Guid) (store: DsStore) =
        removeEntity store.HwLamps id "HwLamp"

    let addCondition (condition: HwCondition) (store: DsStore) =
        addEntity store.HwConditions condition.Id condition "HwCondition"

    let updateCondition (condition: HwCondition) (store: DsStore) =
        updateEntity store.HwConditions condition.Id condition "HwCondition"

    let removeCondition (id: Guid) (store: DsStore) =
        removeEntity store.HwConditions id "HwCondition"

    let addAction (action: HwAction) (store: DsStore) =
        addEntity store.HwActions action.Id action "HwAction"

    let updateAction (action: HwAction) (store: DsStore) =
        updateEntity store.HwActions action.Id action "HwAction"

    let removeAction (id: Guid) (store: DsStore) =
        removeEntity store.HwActions id "HwAction"
