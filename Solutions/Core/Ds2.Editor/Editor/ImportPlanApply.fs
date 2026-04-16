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
        | AddSystem system ->
            store.TrackAdd(store.Systems, system)
        | AddFlow flow ->
            store.TrackAdd(store.Flows, flow)
            match store.Systems.TryGetValue(flow.ParentId) with
            | true, _ -> store.TrackMutate(store.Systems, flow.ParentId, fun s -> s.FlowIds.Add(flow.Id))
            | _ -> ()
        | AddWork work ->
            store.TrackAdd(store.Works, work)
            match store.Flows.TryGetValue(work.ParentId) with
            | true, _ -> store.TrackMutate(store.Flows, work.ParentId, fun f -> f.WorkIds.Add(work.Id))
            | _ -> ()
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
