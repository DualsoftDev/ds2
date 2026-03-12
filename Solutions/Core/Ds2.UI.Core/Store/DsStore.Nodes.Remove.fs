namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core


module internal CascadeRemove =
    let private arrowsFor (dict: System.Collections.Generic.IReadOnlyDictionary<Guid, 'T>) (srcOf: 'T -> Guid) (tgtOf: 'T -> Guid) (nodeIds: Set<Guid>) =
        dict.Values
        |> Seq.filter (fun a -> nodeIds.Contains(srcOf a) || nodeIds.Contains(tgtOf a))
        |> Seq.toList

    let arrowWorksFor (store: DsStore) (workIds: Set<Guid>) =
        arrowsFor store.ArrowWorksReadOnly (fun a -> a.SourceId) (fun a -> a.TargetId) workIds

    let arrowCallsFor (store: DsStore) (callIds: Set<Guid>) =
        arrowsFor store.ArrowCallsReadOnly (fun a -> a.SourceId) (fun a -> a.TargetId) callIds


    let private collectReferencedApiCallIds (store: DsStore) =
        store.Calls.Values
        |> Seq.collect CallConditionQueries.referencedApiCallIds
        |> Set.ofSeq

    let removeOrphanApiCalls (store: DsStore) =
        let referencedIds = collectReferencedApiCallIds store
        let orphanIds =
            store.ApiCalls.Keys
            |> Seq.filter (fun id -> not (referencedIds.Contains id))
            |> Seq.toList
        for orphanId in orphanIds do
            store.TrackRemove(store.ApiCalls, orphanId)

    let removeHwComponents (store: DsStore) (systemIds: Guid list) =
        for sid in systemIds do
            DsQuery.apiDefsOf    sid store |> List.iter (fun d -> store.TrackRemove(store.ApiDefs,      d.Id))
            DsQuery.buttonsOf    sid store |> List.iter (fun b -> store.TrackRemove(store.HwButtons,    b.Id))
            DsQuery.lampsOf      sid store |> List.iter (fun l -> store.TrackRemove(store.HwLamps,      l.Id))
            DsQuery.conditionsOf sid store |> List.iter (fun c -> store.TrackRemove(store.HwConditions, c.Id))
            DsQuery.actionsOf    sid store |> List.iter (fun a -> store.TrackRemove(store.HwActions,    a.Id))

    let removeSystem (store: DsStore) (systemId: Guid) =
        for p in store.Projects.Values do
            let inActive = p.ActiveSystemIds.Contains(systemId)
            let inPassive = p.PassiveSystemIds.Contains(systemId)
            if inActive || inPassive then
                store.TrackMutate(store.Projects, p.Id, fun proj ->
                    if inActive then proj.ActiveSystemIds.Remove(systemId) |> ignore
                    if inPassive then proj.PassiveSystemIds.Remove(systemId) |> ignore)
        store.TrackRemove(store.Systems, systemId)

    let cascadeRemoveCall (store: DsStore) (callId: Guid) =
        arrowCallsFor store (Set.singleton callId)
        |> List.iter (fun a -> store.TrackRemove(store.ArrowCalls, a.Id))
        store.TrackRemove(store.Calls, callId)

    let cascadeRemoveWork (store: DsStore) (workId: Guid) =
        DsQuery.callsOf workId store 
        |> List.iter (fun call -> cascadeRemoveCall store call.Id)
        arrowWorksFor store (Set.singleton workId)
        |> List.iter (fun a -> store.TrackRemove(store.ArrowWorks, a.Id))
        store.TrackRemove(store.Works, workId)

    let cascadeRemoveFlow (store: DsStore) (flowId: Guid) =
        DsQuery.worksOf flowId store 
        |> List.iter (fun work -> cascadeRemoveWork store work.Id)
        store.TrackRemove(store.Flows, flowId)

    let cascadeRemoveSystem (store: DsStore) (systemId: Guid) =
        DsQuery.flowsOf systemId store 
        |> List.iter (fun flow -> cascadeRemoveFlow store flow.Id)
        removeHwComponents store [ systemId ]
        removeSystem store systemId

    let cascadeRemoveProject (store: DsStore) (projectId: Guid) =
        DsQuery.projectSystemsOf projectId store 
        |> List.iter (fun system -> cascadeRemoveSystem store system.Id)
        store.TrackRemove(store.Projects, projectId)

    let batchRemoveEntities (store: DsStore) (selections: (EntityKind * Guid) list) =
        let selIds = selections |> List.map snd |> Set.ofList

        for (ek, id) in selections do
            match ek with
            | EntityKind.Call ->
                // 부모 Work가 함께 선택된 Call은 건너뜀 — Work 캐스케이드가 처리
                match DsQuery.getCall id store with
                | Some call when not (selIds.Contains call.ParentId) ->
                    cascadeRemoveCall store id
                | _ -> ()
            | EntityKind.Work      -> cascadeRemoveWork store id
            | EntityKind.Flow      -> cascadeRemoveFlow store id
            | EntityKind.System    -> cascadeRemoveSystem store id
            | EntityKind.Project   -> cascadeRemoveProject store id
            | EntityKind.ApiDef    -> store.TrackRemove(store.ApiDefs, id)
            | EntityKind.Button    -> store.TrackRemove(store.HwButtons, id)
            | EntityKind.Lamp      -> store.TrackRemove(store.HwLamps, id)
            | EntityKind.Condition -> store.TrackRemove(store.HwConditions, id)
            | EntityKind.Action    -> store.TrackRemove(store.HwActions, id)
            | _ -> ()

        removeOrphanApiCalls store
