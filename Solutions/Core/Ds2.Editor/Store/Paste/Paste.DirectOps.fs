namespace Ds2.Editor

open System
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery

module internal DirectPasteOps =
    let private mergeGuidMap (baseMap: Map<Guid, Guid>) (additions: Map<Guid, Guid>) =
        Map.fold (fun acc k v -> Map.add k v acc) baseMap additions

    let private collectArrowsWithinSet
        (getArrows: Guid -> DsStore -> 'a list)
        (getSourceId: 'a -> Guid) (getTargetId: 'a -> Guid)
        (parentIds: Set<Guid>) (selectedIds: Set<Guid>) (store: DsStore) : 'a list =
        parentIds
        |> Set.toList
        |> List.collect (fun parentId -> getArrows parentId store)
        |> List.filter (fun a -> selectedIds.Contains(getSourceId a) && selectedIds.Contains(getTargetId a))

    let private replayWorkArrows (store: DsStore) (targetSystemId: Guid)
        (sourceWorkArrows: ArrowBetweenWorks list) (workMap: Map<Guid, Guid>) =
        for arrow in sourceWorkArrows do
            match Map.tryFind arrow.SourceId workMap, Map.tryFind arrow.TargetId workMap with
            | Some src, Some tgt ->
                let a = ArrowBetweenWorks(targetSystemId, src, tgt, arrow.ArrowType)
                store.TrackAdd(store.ArrowWorks, a)
            | _ -> ()

    let private replayCallArrows (store: DsStore)
        (sourceCallArrows: ArrowBetweenCalls list) (callMap: Map<Guid, Guid>) =
        for arrow in sourceCallArrows do
            match Map.tryFind arrow.SourceId callMap, Map.tryFind arrow.TargetId callMap with
            | Some src, Some tgt ->
                match Queries.getCall src store with
                | Some newCall ->
                    let a = ArrowBetweenCalls(newCall.ParentId, src, tgt, arrow.ArrowType)
                    store.TrackAdd(store.ArrowCalls, a)
                | None -> ()
            | _ -> ()

    let private sortByPositionAndName
        (getPosition: 'a -> Xywh option) (getName: 'a -> string) (items: 'a list) =
        items
        |> List.mapi (fun index item -> index, item)
        |> List.sortBy (fun (index, item) ->
            match getPosition item with
            | Some pos -> 0, pos.Y, pos.X, getName item, index
            | None -> 1, 0, 0, getName item, index)
        |> List.map snd

    let private offsetPosition (baseIndex: int) (pos: Xywh option) =
        let offset = 30 * (baseIndex + 1)
        pos |> Option.map (fun p -> Xywh(p.X + offset, p.Y + offset, p.W, p.H))

    let private pasteCallToWork
        (store: DsStore) (context: CallCopyContext) (sourceCall: Call) (targetWorkId: Guid)
        (deviceState: PasteDeviceOps.DevicePasteState) (deviceFlowCtxOpt: PasteDeviceOps.DeviceFlowCtx option) (baseIndex: int)
        : Call * PasteDeviceOps.DevicePasteState =
        let pastedCall = Call(sourceCall.DevicesAlias, sourceCall.ApiName, targetWorkId)
        pastedCall.Properties <- sourceCall.Properties.DeepCopy()
        pastedCall.Position <- offsetPosition baseIndex sourceCall.Position
        store.TrackAdd(store.Calls, pastedCall)
        let newDeviceState =
            PasteDeviceOps.copyApiCallsForPaste store context sourceCall pastedCall.Id deviceState deviceFlowCtxOpt
        pastedCall, newDeviceState

    let private pasteWorkToFlow
        (store: DsStore) (sourceWork: Work) (targetFlowId: Guid)
        (deviceState: PasteDeviceOps.DevicePasteState) (deviceFlowCtxOpt: PasteDeviceOps.DeviceFlowCtx option) (baseIndex: int)
        : Work * Map<Guid, Guid> * PasteDeviceOps.DevicePasteState =
        let targetFlow = Queries.getFlow targetFlowId store |> Option.get
        let existingLocalNames = Queries.worksOf targetFlowId store |> List.map (fun w -> w.LocalName)
        let newLocalName = Queries.nextUniqueName sourceWork.LocalName existingLocalNames
        let pastedWork = Work(targetFlow.Name, newLocalName, targetFlowId)
        pastedWork.Properties <- sourceWork.Properties.DeepCopy()
        pastedWork.TokenRole <- sourceWork.TokenRole
        pastedWork.Position <- offsetPosition baseIndex sourceWork.Position
        store.TrackAdd(store.Works, pastedWork)
        let isDifferentFlow = sourceWork.ParentId <> targetFlowId
        let context = if isDifferentFlow then DifferentFlow else DifferentWork
        let ctxOpt = if isDifferentFlow then deviceFlowCtxOpt else None
        let callMap, finalDeviceState =
            Queries.callsOf sourceWork.Id store
            |> sortByPositionAndName (fun call -> call.Position) (fun call -> call.Name)
            |> List.fold (fun (callMap, devState) sourceCall ->
                let pastedCall, newDevState = pasteCallToWork store context sourceCall pastedWork.Id devState ctxOpt 0
                Map.add sourceCall.Id pastedCall.Id callMap, newDevState
            ) (Map.empty, deviceState)
        pastedWork, callMap, finalDeviceState

    let pasteFlowToSystem (store: DsStore) (sourceFlow: Flow) (targetSystemId: Guid) (newNameOpt: string option) : Guid =
        let baseName = newNameOpt |> Option.defaultValue sourceFlow.Name
        let existingFlowNames = Queries.flowsOf targetSystemId store |> List.map (fun f -> f.Name)
        let flowName = Queries.nextUniqueName baseName existingFlowNames
        let pastedFlow = Flow(flowName, targetSystemId)
        store.TrackAdd(store.Flows, pastedFlow)
        let deviceFlowCtxOpt =
            StoreHierarchyQueries.findProjectOfSystem store targetSystemId
            |> Option.map (fun projectId ->
                let ctx : PasteDeviceOps.DeviceFlowCtx =
                    { Store = store; ProjectId = projectId; TargetFlowName = pastedFlow.Name }
                ctx)
        let sourceSystemId = sourceFlow.ParentId
        let sourceWorkArrows = Queries.arrowWorksOf sourceSystemId store
        let sourceWorks = Queries.worksOf sourceFlow.Id store
        let sourceCallArrows =
            sourceWorks |> List.collect (fun w -> Queries.arrowCallsOf w.Id store)
        let workMap, callMap, _ =
            sourceWorks
            |> sortByPositionAndName (fun work -> work.Position) (fun work -> work.Name)
            |> List.fold (fun (wm, cm, ds) sw ->
                let pw, lcm, nds = pasteWorkToFlow store sw pastedFlow.Id ds deviceFlowCtxOpt 0
                Map.add sw.Id pw.Id wm, mergeGuidMap cm lcm, nds
            ) (Map.empty, Map.empty, PasteDeviceOps.initialDevicePasteState)
        replayWorkArrows store targetSystemId sourceWorkArrows workMap
        replayCallArrows store sourceCallArrows callMap
        pastedFlow.Id

    let pasteWorksToFlowBatch (store: DsStore) (sourceWorks: Work list) (targetFlowId: Guid) (baseIndex: int) : Guid list =
        let selectedWorkIds = sourceWorks |> List.map (fun w -> w.Id) |> Set.ofList
        let selectedCallIds =
            sourceWorks
            |> List.collect (fun w -> Queries.callsOf w.Id store)
            |> List.map (fun c -> c.Id) |> Set.ofList
        let sourceSystemIds =
            sourceWorks
            |> List.choose (fun w -> Queries.getFlow w.ParentId store |> Option.map (fun f -> f.ParentId))
            |> Set.ofList
        let sourceWorkArrows =
            collectArrowsWithinSet Queries.arrowWorksOf (fun a -> a.SourceId) (fun a -> a.TargetId) sourceSystemIds selectedWorkIds store
        let sourceCallArrows =
            collectArrowsWithinSet Queries.arrowCallsOf (fun a -> a.SourceId) (fun a -> a.TargetId) selectedWorkIds selectedCallIds store
        let deviceFlowCtxOpt = PasteDeviceOps.makeDeviceFlowCtx store targetFlowId
        let workMap, callMap, pastedIdsRev, _ =
            sourceWorks
            |> sortByPositionAndName (fun work -> work.Position) (fun work -> work.Name)
            |> List.fold (fun (wm, cm, ids, ds) sw ->
                let pw, lcm, nds = pasteWorkToFlow store sw targetFlowId ds deviceFlowCtxOpt baseIndex
                Map.add sw.Id pw.Id wm, mergeGuidMap cm lcm, pw.Id :: ids, nds
            ) (Map.empty, Map.empty, [], PasteDeviceOps.initialDevicePasteState)
        match Queries.getFlow targetFlowId store with
        | Some flow -> replayWorkArrows store flow.ParentId sourceWorkArrows workMap
        | None -> ()
        replayCallArrows store sourceCallArrows callMap
        pastedIdsRev |> List.rev

    let pasteCallsToWorkBatch (store: DsStore) (sourceCalls: Call list) (targetWorkId: Guid) (baseIndex: int) : Guid list =
        let selectedCallIds = sourceCalls |> List.map (fun c -> c.Id) |> Set.ofList
        let sourceWorkIds =
            sourceCalls |> List.map (fun c -> c.ParentId) |> Set.ofList
        let sourceCallArrows =
            collectArrowsWithinSet Queries.arrowCallsOf (fun a -> a.SourceId) (fun a -> a.TargetId) sourceWorkIds selectedCallIds store
        let targetFlowIdOpt = Queries.getWork targetWorkId store |> Option.map (fun w -> w.ParentId)
        let deviceFlowCtxForDiffFlow = targetFlowIdOpt |> Option.bind (PasteDeviceOps.makeDeviceFlowCtx store)
        let callMap, pastedIdsRev, _ =
            sourceCalls
            |> sortByPositionAndName (fun call -> call.Position) (fun call -> call.Name)
            |> List.fold (fun (cm, ids, ds) sc ->
                let context =
                    if sc.ParentId = targetWorkId then SameWork
                    else
                        match Queries.getWork targetWorkId store, Queries.getWork sc.ParentId store with
                        | Some tw, Some sw when tw.ParentId = sw.ParentId -> DifferentWork
                        | _ -> DifferentFlow
                let ctxOpt = match context with DifferentFlow -> deviceFlowCtxForDiffFlow | _ -> None
                let pc, nds = pasteCallToWork store context sc targetWorkId ds ctxOpt baseIndex
                Map.add sc.Id pc.Id cm, pc.Id :: ids, nds
            ) (Map.empty, [], PasteDeviceOps.initialDevicePasteState)
        replayCallArrows store sourceCallArrows callMap
        pastedIdsRev |> List.rev

    let dispatchPaste
        (store: DsStore) (copiedEntityKind: EntityKind) (copiedIds: Guid list)
        (targetEntityKind: EntityKind) (targetEntityId: Guid) (baseIndex: int) : Guid list =
        match copiedEntityKind with
        | EntityKind.Flow ->
            let sourceFlows = copiedIds |> List.choose (fun id -> Queries.getFlow id store)
            if sourceFlows.IsEmpty then []
            else
                sourceFlows |> List.map (fun sf ->
                    let targetSystemId =
                        StoreHierarchyQueries.resolveTarget store EntityKind.System targetEntityKind targetEntityId
                        |> Option.defaultValue sf.ParentId
                    pasteFlowToSystem store sf targetSystemId None)
        | EntityKind.Work ->
            let sourceWorks = copiedIds |> List.choose (fun id -> Queries.getWork id store)
            if sourceWorks.IsEmpty then []
            else
                let targetFlowId =
                    StoreHierarchyQueries.resolveTarget store EntityKind.Flow targetEntityKind targetEntityId
                    |> Option.defaultValue sourceWorks.Head.ParentId
                pasteWorksToFlowBatch store sourceWorks targetFlowId baseIndex
        | EntityKind.Call ->
            let sourceCalls = copiedIds |> List.choose (fun id -> Queries.getCall id store)
            if sourceCalls.IsEmpty then []
            else
                let targetWorkId =
                    StoreHierarchyQueries.resolveTarget store EntityKind.Work targetEntityKind targetEntityId
                    |> Option.defaultValue sourceCalls.Head.ParentId
                pasteCallsToWorkBatch store sourceCalls targetWorkId baseIndex
        | _ -> []
