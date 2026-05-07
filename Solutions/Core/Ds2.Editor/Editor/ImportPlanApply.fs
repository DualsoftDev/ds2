namespace Ds2.Editor

open Ds2.Core.Store
open System.Runtime.CompilerServices

[<RequireQualifiedAccess>]
module ImportPlanApply =

    let private applyOperationTracked (store: DsStore) operation =
        match operation with
        | LinkSystemToProject(projectId, systemId, isActive) ->
            store.TrackMutate(store.Projects, projectId, fun project ->
                if isActive then
                    project.ActiveSystemIds.Add(systemId)
                else
                    project.PassiveSystemIds.Add(systemId))
        | AddProject project ->
            store.TrackAdd(store.Projects, project)
        | AddSystem system ->
            store.TrackAdd(store.Systems, system)
        | AddFlow flow ->
            store.TrackAdd(store.Flows, flow)
        | AddWork work ->
            store.TrackAdd(store.Works, work)
        | AddCall call ->
            store.TrackAdd(store.Calls, call)
        | AddApiDef apiDef ->
            store.TrackAdd(store.ApiDefs, apiDef)
        | AddApiCall apiCall ->
            store.TrackAdd(store.ApiCalls, apiCall)
        | AddArrowWork arrow ->
            store.TrackAdd(store.ArrowWorks, arrow)
        | AddArrowCall arrow ->
            store.TrackAdd(store.ArrowCalls, arrow)
        | RemoveEntity (kind, id) ->
            // CascadeRemove 가 history 추적 (TrackRemove / TrackMutate) + 자식 cascade 모두 처리.
            // batchRemoveEntities 는 selections list 안에서 부모-자식 중복 제거 + orphan ApiCall 정리까지 포함.
            CascadeRemove.batchRemoveEntities store [ (kind, id) ]
        | RenameEntity (kind, id, newName) ->
            match kind with
            | EntityKind.System ->
                store.TrackMutate(store.Systems, id, fun s -> s.Name <- newName)
            | EntityKind.ApiDef ->
                store.TrackMutate(store.ApiDefs, id, fun d -> d.Name <- newName)
            | _ ->
                invalidOp $"RenameEntity: kind={kind} 는 phase 2 미지원 (System/ApiDef 만)."

    let applyWithUndo (store: DsStore) (label: string) (plan: ImportPlan) =
        store.WithTransaction(label, fun () ->
            for operation in plan.Operations do
                applyOperationTracked store operation)
        store.EmitRefreshAndHistory()

[<Extension>]
type DsStoreImportPlanExtensions =

    [<Extension>]
    static member ApplyImportPlan(store: DsStore, label: string, plan: ImportPlan) =
        ImportPlanApply.applyWithUndo store label plan
