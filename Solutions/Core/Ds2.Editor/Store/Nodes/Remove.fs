namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery


module internal CascadeRemove =
    let private arrowsFor (dict: System.Collections.Generic.IReadOnlyDictionary<Guid, 'T>) (srcOf: 'T -> Guid) (tgtOf: 'T -> Guid) (nodeIds: Set<Guid>) =
        dict.Values
        |> Seq.filter (fun a -> nodeIds.Contains(srcOf a) || nodeIds.Contains(tgtOf a))
        |> Seq.toList

    let arrowWorksFor (store: DsStore) (workIds: Set<Guid>) =
        arrowsFor store.ArrowWorksReadOnly (fun a -> a.SourceId) (fun a -> a.TargetId) workIds

    let arrowCallsFor (store: DsStore) (callIds: Set<Guid>) =
        arrowsFor store.ArrowCallsReadOnly (fun a -> a.SourceId) (fun a -> a.TargetId) callIds


    let removeOrphanApiCalls (store: DsStore) =
        let referencedIds =
            store.Calls.Values
            |> Seq.collect CallConditionQueries.referencedApiCallIds
            |> Set.ofSeq
        let orphanIds =
            store.ApiCalls.Keys
            |> Seq.filter (fun id -> not (referencedIds.Contains id))
            |> Seq.toList
        for orphanId in orphanIds do
            store.TrackRemove(store.ApiCalls, orphanId)

    let removeHwComponents (store: DsStore) (systemIds: Guid list) =
        for sid in systemIds do
            Queries.apiDefsOf    sid store |> List.iter (fun d -> store.TrackRemove(store.ApiDefs,      d.Id))

    let removeSystem (store: DsStore) (systemId: Guid) =
        for p in store.Projects.Values do
            let inActive = p.ActiveSystemIds.Contains(systemId)
            let inPassive = p.PassiveSystemIds.Contains(systemId)
            if inActive || inPassive then
                store.TrackMutate(store.Projects, p.Id, fun proj ->
                    if inActive then proj.ActiveSystemIds.Remove(systemId) |> ignore
                    if inPassive then proj.PassiveSystemIds.Remove(systemId) |> ignore)
        store.TrackRemove(store.Systems, systemId)

    let rec cascadeRemoveCall (store: DsStore) (callId: Guid) =
        // 원본 Call 삭제 시 → 이 Call을 참조하는 모든 reference Call도 삭제
        store.CallsReadOnly.Values
        |> Seq.filter (fun c -> c.ReferenceOf = Some callId)
        |> Seq.toList
        |> List.iter (fun refC -> cascadeRemoveCall store refC.Id)
        arrowCallsFor store (Set.singleton callId)
        |> List.iter (fun a -> store.TrackRemove(store.ArrowCalls, a.Id))
        store.TrackRemove(store.Calls, callId)

    let rec cascadeRemoveWork (store: DsStore) (workId: Guid) =
        // 원본 Work 삭제 시 → 이 Work를 참조하는 모든 reference Work도 삭제
        store.WorksReadOnly.Values
        |> Seq.filter (fun w -> w.ReferenceOf = Some workId)
        |> Seq.toList
        |> List.iter (fun refW -> cascadeRemoveWork store refW.Id)
        Queries.callsOf workId store
        |> List.iter (fun call -> cascadeRemoveCall store call.Id)
        arrowWorksFor store (Set.singleton workId)
        |> List.iter (fun a -> store.TrackRemove(store.ArrowWorks, a.Id))
        store.TrackRemove(store.Works, workId)

    let cascadeRemoveFlow (store: DsStore) (flowId: Guid) =
        Queries.worksOf flowId store 
        |> List.iter (fun work -> cascadeRemoveWork store work.Id)
        store.TrackRemove(store.Flows, flowId)

    let cascadeRemoveSystem (store: DsStore) (systemId: Guid) =
        Queries.flowsOf systemId store 
        |> List.iter (fun flow -> cascadeRemoveFlow store flow.Id)
        removeHwComponents store [ systemId ]
        removeSystem store systemId

    let cascadeRemoveProject (store: DsStore) (projectId: Guid) =
        Queries.projectSystemsOf projectId store 
        |> List.iter (fun system -> cascadeRemoveSystem store system.Id)
        store.TrackRemove(store.Projects, projectId)

    let batchRemoveEntities (store: DsStore) (selections: (EntityKind * Guid) list) =
        let selIds = selections |> List.map snd |> Set.ofList

        for (ek, id) in selections do
            match ek with
            | EntityKind.Call ->
                // 부모 Work가 함께 선택된 Call은 건너뜀 — Work 캐스케이드가 처리
                match Queries.getCall id store with
                | Some call when not (selIds.Contains call.ParentId) ->
                    cascadeRemoveCall store id
                | _ -> ()
            | EntityKind.Work      -> cascadeRemoveWork store id
            | EntityKind.Flow      -> cascadeRemoveFlow store id
            | EntityKind.System    -> cascadeRemoveSystem store id
            | EntityKind.Project   -> cascadeRemoveProject store id
            | EntityKind.ApiDef    -> store.TrackRemove(store.ApiDefs, id)
            | _ -> ()

        removeOrphanApiCalls store
