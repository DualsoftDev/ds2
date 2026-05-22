namespace Ds2.Core.Store

open System
open Ds2.Core

type ImportPlanOperation =
    | LinkSystemToProject of projectId: Guid * systemId: Guid * isActive: bool
    | AddProject of Project
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

    /// 위반 entity 는 skip + log — Mermaid/CSV 의 *부분 깨진 import* 가 *전체 rollback* 보다 *valid 한 부분만 import*
    /// 가 사용자 의도에 부합. AASX populateStore 의 graceful import 와 동일 패턴.
    /// (Ds2.Core 는 log4net 의존 없음 — eprintfn 으로 stderr 송출)
    let private skipWithLog (op: string) (reason: string) =
        eprintfn $"[WARN] ImportPlan.applyDirect — {op} skipped: {reason}"

    let private applyOperationDirect (store: DsStore) operation =
        match operation with
        | LinkSystemToProject(projectId, systemId, isActive) ->
            // LinkSystemToProject 시점에 *project ↔ system* 연결되므로 *system 이름 중복* 재검증.
            match store.Projects.TryGetValue(projectId), store.Systems.TryGetValue(systemId) with
            | (true, _), (true, system) ->
                match ImportValidator.validateSystem store (Some projectId) system with
                | Some reason ->
                    skipWithLog $"LinkSystemToProject(sys='{system.Name}')" reason
                | None ->
                    let project = store.Projects.[projectId]
                    if isActive then project.ActiveSystemIds.Add(systemId)
                    else project.PassiveSystemIds.Add(systemId)
            | _ ->
                skipWithLog "LinkSystemToProject" $"projectId={projectId} 또는 systemId={systemId} 없음"
        | AddProject project ->
            match ImportValidator.validateProject store project with
            | Some reason -> skipWithLog $"AddProject('{project.Name}')" reason
            | None -> store.DirectWrite(store.Projects, project)
        | AddSystem system ->
            // AddSystem 시점엔 *어느 project 인지 아직 모름* — 빈 이름만 검사 (중복 검사는 LinkSystemToProject 단계).
            match ImportValidator.validateSystem store None system with
            | Some reason -> skipWithLog $"AddSystem('{system.Name}')" reason
            | None -> store.DirectWrite(store.Systems, system)
        | AddFlow flow ->
            match ImportValidator.validateFlow store flow with
            | Some reason -> skipWithLog $"AddFlow('{flow.Name}')" reason
            | None -> store.DirectWrite(store.Flows, flow)
        | AddWork work ->
            match ImportValidator.validateWork store work with
            | Some reason -> skipWithLog $"AddWork('{work.LocalName}')" reason
            | None -> store.DirectWrite(store.Works, work)
        | AddCall call ->
            match ImportValidator.validateCall store call with
            | Some reason -> skipWithLog $"AddCall('{call.Name}')" reason
            | None -> store.DirectWrite(store.Calls, call)
        | AddApiDef apiDef ->
            match ImportValidator.validateApiDef store apiDef with
            | Some reason -> skipWithLog $"AddApiDef('{apiDef.Name}')" reason
            | None -> store.DirectWrite(store.ApiDefs, apiDef)
        | AddApiCall apiCall ->
            store.DirectWrite(store.ApiCalls, apiCall)
        | AddArrowWork arrow ->
            match ImportValidator.validateArrowWorks arrow with
            | Some reason -> skipWithLog $"AddArrowWork({arrow.SourceId}→{arrow.TargetId})" reason
            | None -> store.DirectWrite(store.ArrowWorks, arrow)
        | AddArrowCall arrow ->
            match ImportValidator.validateArrowCalls arrow with
            | Some reason -> skipWithLog $"AddArrowCall({arrow.SourceId}→{arrow.TargetId})" reason
            | None -> store.DirectWrite(store.ArrowCalls, arrow)
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
