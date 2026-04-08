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

    let internal applyDirect (store: DsStore) (plan: ImportPlan) =
        for operation in plan.Operations do
            applyOperationDirect store operation
