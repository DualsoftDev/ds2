namespace Ds2.Core.Store

open System
open Ds2.Core

type ImportPlanOperation =
    | LinkSystemToProject of projectId: Guid * systemId: Guid * isActive: bool
    | AddSystem of DsSystem
    | AddFlow of Flow
    | AddWork of Work
    | AddCall of Call
    | AddApiDef of ApiDef
    | AddApiCall of ApiCall
    | AddArrowWork of ArrowBetweenWorks
    | AddArrowCall of ArrowBetweenCalls
    /// Remove (cascade). entityKind = Project/System/Flow/Work/Call/ApiDef. entityId = 대상 GUID.
    /// applyDirect 는 dict 단순 제거만 수행 (cascade 책임은 호출자) — applyTracked 가 CascadeRemove 호출.
    | RemoveEntity of entityKind: EntityKind * entityId: Guid
    /// Rename (System / ApiDef 만 — Flow/Work/Call 은 자식 cascade 복잡도로 phase 후속).
    /// entityKind = System | ApiDef. entityId = 대상 GUID. newName = sanitized 이름.
    | RenameEntity of entityKind: EntityKind * entityId: Guid * newName: string

type ImportPlan = {
    Operations: ImportPlanOperation list
}

[<RequireQualifiedAccess>]
module ImportPlan =

    let empty = { Operations = [] }

    let ofSeq (operations: seq<ImportPlanOperation>) =
        { Operations = operations |> Seq.toList }

    let private applyOperationDirect (store: DsStore) operation =
        match operation with
        | LinkSystemToProject(projectId, systemId, isActive) ->
            let project = store.Projects.[projectId]
            if isActive then
                project.ActiveSystemIds.Add(systemId)
            else
                project.PassiveSystemIds.Add(systemId)
        | AddSystem system ->
            store.DirectWrite(store.Systems, system)
        | AddFlow flow ->
            store.DirectWrite(store.Flows, flow)
        | AddWork work ->
            store.DirectWrite(store.Works, work)
        | AddCall call ->
            store.DirectWrite(store.Calls, call)
        | AddApiDef apiDef ->
            store.DirectWrite(store.ApiDefs, apiDef)
        | AddApiCall apiCall ->
            store.DirectWrite(store.ApiCalls, apiCall)
        | AddArrowWork arrow ->
            store.DirectWrite(store.ArrowWorks, arrow)
        | AddArrowCall arrow ->
            store.DirectWrite(store.ArrowCalls, arrow)
        | RemoveEntity (kind, id) ->
            // Direct path 는 cascade 없이 단순 dict 제거. Mermaid/CSV importer 등 raw build 용도.
            // LLM mutation 은 ImportPlanApply 측 applyOperationTracked 가 cascade 처리.
            match kind with
            | EntityKind.Project -> store.Projects.Remove(id) |> ignore
            | EntityKind.System  -> store.Systems.Remove(id)  |> ignore
            | EntityKind.Flow    -> store.Flows.Remove(id)    |> ignore
            | EntityKind.Work    -> store.Works.Remove(id)    |> ignore
            | EntityKind.Call    -> store.Calls.Remove(id)    |> ignore
            | EntityKind.ApiDef  -> store.ApiDefs.Remove(id)  |> ignore
            | _ -> invalidOp $"RemoveEntity direct: 지원하지 않는 EntityKind ({kind})."
        | RenameEntity (kind, id, newName) ->
            match kind with
            | EntityKind.System ->
                match store.Systems.TryGetValue(id) with
                | true, s -> s.Name <- newName
                | _ -> invalidOp $"RenameEntity direct: System(id={id}) 가 존재하지 않습니다."
            | EntityKind.ApiDef ->
                match store.ApiDefs.TryGetValue(id) with
                | true, d -> d.Name <- newName
                | _ -> invalidOp $"RenameEntity direct: ApiDef(id={id}) 가 존재하지 않습니다."
            | _ ->
                invalidOp $"RenameEntity direct: kind={kind} 는 미지원 (System/ApiDef 만)."

    let internal applyDirect (store: DsStore) (plan: ImportPlan) =
        for operation in plan.Operations do
            applyOperationDirect store operation
