namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core

// =============================================================================
// PasteResolvers — 붙여넣기 대상 해석 유틸리티 (internal)
// =============================================================================

module internal PasteResolvers =

    let isCopyableEntityKind (entityKind: EntityKind) : bool =
        match entityKind with
        | EntityKind.Flow | EntityKind.Work | EntityKind.Call -> true
        | _ -> false

// =============================================================================
// 내부 헬퍼 — Paste 직접 mutation (ExecFn 콜백 제거)
// =============================================================================

module internal DirectPasteOps =

    type private DevicePasteState = {
        ClonedSystems: Map<string, DsSystem * Map<Guid, Guid>>
    }

    let private initialDevicePasteState = { ClonedSystems = Map.empty }

    type private DeviceFlowCtx = {
        Store: DsStore
        ProjectId: Guid
        TargetFlowName: string
    }

    let private cloneApiCall (sourceApiCall: ApiCall) (mapApiDefId: Guid option -> Guid option) : ApiCall =
        let cloneIOTag (tagOpt: IOTag option) : IOTag option =
            tagOpt |> Option.map (fun t -> IOTag(t.Name, t.Address, t.Description))
        let cloned = ApiCall(sourceApiCall.Name)
        cloned.InTag      <- cloneIOTag sourceApiCall.InTag
        cloned.OutTag     <- cloneIOTag sourceApiCall.OutTag
        cloned.ApiDefId   <- mapApiDefId sourceApiCall.ApiDefId
        cloned.InputSpec  <- sourceApiCall.InputSpec
        cloned.OutputSpec <- sourceApiCall.OutputSpec
        cloned

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

    let private ensureTargetDeviceSystem
        (store: DsStore) (projectId: Guid) (targetFlowName: string) (devAlias: string) (sourceSystemId: Guid)
        (state: DevicePasteState) : DevicePasteState * Map<Guid, Guid> =
        let targetName = $"{targetFlowName}_{devAlias}"
        match Map.tryFind targetName state.ClonedSystems with
        | Some (_, mapping) -> state, mapping
        | None ->
            let existing =
                DsQuery.passiveSystemsOf projectId store
                |> List.tryFind (fun s -> s.Name = targetName)
            let targetSystem, mapping =
                match existing with
                | Some sys ->
                    let targetApiDefs = DsQuery.apiDefsOf sys.Id store
                    let sourceApiDefs = DsQuery.apiDefsOf sourceSystemId store
                    let mapping =
                        sourceApiDefs
                        |> List.choose (fun src ->
                            targetApiDefs
                            |> List.tryFind (fun t -> t.Name = src.Name)
                            |> Option.map (fun t -> src.Id, t.Id))
                        |> Map.ofList
                    sys, mapping
                | None ->
                    let newSystem = DsSystem(targetName)
                    store.TrackAdd(store.Systems, newSystem)
                    store.TrackMutate(store.Projects, projectId, fun p ->
                        p.PassiveSystemIds.Add(newSystem.Id))
                    let newFlow = Flow($"{devAlias}_Flow", newSystem.Id)
                    store.TrackAdd(store.Flows, newFlow)
                    let sourceApiDefs = DsQuery.apiDefsOf sourceSystemId store
                    let mapping =
                        sourceApiDefs
                        |> List.map (fun src ->
                            let cloned = ApiDef(src.Name, newSystem.Id)
                            cloned.Properties.IsPush <- src.Properties.IsPush
                            let work = Work(src.Name, newFlow.Id)
                            store.TrackAdd(store.Works, work)
                            cloned.Properties.TxGuid <- Some work.Id
                            store.TrackAdd(store.ApiDefs, cloned)
                            src.Id, cloned.Id)
                        |> Map.ofList
                    newSystem, mapping
            { ClonedSystems = Map.add targetName (targetSystem, mapping) state.ClonedSystems }, mapping

    let private copyApiCallsWithMapping (store: DsStore) (sourceCall: Call) (targetCallId: Guid) (mapApiDefId: Guid option -> Guid option) =
        for apiCall in sourceCall.ApiCalls do
            let copied = cloneApiCall apiCall mapApiDefId
            store.TrackAdd(store.ApiCalls, copied)
            store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(copied))

    let private shareApiCalls (store: DsStore) (sourceCall: Call) (targetCallId: Guid) =
        for apiCall in sourceCall.ApiCalls do
            store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(apiCall))

    let private tryFindPassiveSystemId (store: DsStore) (apiDefIdOpt: Guid option) : Guid option =
        apiDefIdOpt
        |> Option.bind (fun aid -> DsQuery.getApiDef aid store)
        |> Option.bind (fun d ->
            if DsQuery.allProjects store |> List.exists (fun p -> p.PassiveSystemIds.Contains(d.ParentId))
            then Some d.ParentId else None)

    let private copyApiCallsAcrossFlows
        (store: DsStore) (projectId: Guid) (targetFlowName: string)
        (sourceCall: Call) (targetCallId: Guid) (state: DevicePasteState)
        : DevicePasteState =
        // 각 ApiCall별로 Device System이 다를 수 있으므로 ApiCall 단위로 매핑
        sourceCall.ApiCalls
        |> Seq.fold (fun accState (apiCall: ApiCall) ->
            let sourceSystemIdOpt = tryFindPassiveSystemId store apiCall.ApiDefId
            match sourceSystemIdOpt with
            | None ->
                let copied = cloneApiCall apiCall id
                store.TrackAdd(store.ApiCalls, copied)
                store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(copied))
                accState
            | Some sourceSystemId ->
                let devAlias =
                    // ApiCall 이름에서 devAlias 추출 (Conv_1.ADV → Conv_1)
                    match apiCall.Name.IndexOf('.') with
                    | -1 -> sourceCall.DevicesAlias
                    | idx -> apiCall.Name.[..idx - 1]
                let newState, apiDefMapping = ensureTargetDeviceSystem store projectId targetFlowName devAlias sourceSystemId accState
                let copied = cloneApiCall apiCall (fun srcIdOpt ->
                    srcIdOpt |> Option.bind (fun id -> Map.tryFind id apiDefMapping) |> Option.orElse srcIdOpt)
                store.TrackAdd(store.ApiCalls, copied)
                store.TrackMutate(store.Calls, targetCallId, fun c -> c.ApiCalls.Add(copied))
                newState
        ) state

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
                match DsQuery.getCall src store with
                | Some newCall ->
                    let a = ArrowBetweenCalls(newCall.ParentId, src, tgt, arrow.ArrowType)
                    store.TrackAdd(store.ArrowCalls, a)
                | None -> ()
            | _ -> ()

    let private offsetPosition (baseIndex: int) (index: int) (pos: Xywh option) =
        let offset = 30 * (baseIndex + index + 1)
        pos |> Option.map (fun p -> Xywh(p.X + offset, p.Y + offset, p.W, p.H))

    let private pasteCallToWork
        (store: DsStore) (context: CallCopyContext) (sourceCall: Call) (targetWorkId: Guid)
        (deviceState: DevicePasteState) (deviceFlowCtxOpt: DeviceFlowCtx option) (baseIndex: int) (index: int)
        : Call * DevicePasteState =
        let pastedCall = Call(sourceCall.DevicesAlias, sourceCall.ApiName, targetWorkId)
        pastedCall.Properties <- sourceCall.Properties.DeepCopy()
        pastedCall.Position <- offsetPosition baseIndex index sourceCall.Position
        store.TrackAdd(store.Calls, pastedCall)
        match context with
        | SameWork ->
            shareApiCalls store sourceCall pastedCall.Id
            pastedCall, deviceState
        | DifferentWork ->
            copyApiCallsWithMapping store sourceCall pastedCall.Id id
            pastedCall, deviceState
        | DifferentFlow ->
            match deviceFlowCtxOpt with
            | Some ctx ->
                let newState = copyApiCallsAcrossFlows ctx.Store ctx.ProjectId ctx.TargetFlowName sourceCall pastedCall.Id deviceState
                pastedCall, newState
            | None ->
                copyApiCallsWithMapping store sourceCall pastedCall.Id id
                pastedCall, deviceState

    let private makeDeviceFlowCtx (store: DsStore) (targetFlowId: Guid) : DeviceFlowCtx option =
        match DsQuery.getFlow targetFlowId store with
        | None -> None
        | Some targetFlow ->
            let projectIdOpt =
                DsQuery.getSystem targetFlow.ParentId store
                |> Option.bind (fun s -> EntityHierarchyQueries.findProjectOfSystem store s.Id)
            projectIdOpt |> Option.map (fun pid ->
                { Store = store; ProjectId = pid; TargetFlowName = targetFlow.Name })

    let private pasteWorkToFlow
        (store: DsStore) (sourceWork: Work) (targetFlowId: Guid)
        (deviceState: DevicePasteState) (deviceFlowCtxOpt: DeviceFlowCtx option) (baseIndex: int) (index: int)
        : Work * Map<Guid, Guid> * DevicePasteState =
        let pastedWork = Work(sourceWork.Name, targetFlowId)
        pastedWork.Properties <- sourceWork.Properties.DeepCopy()
        pastedWork.Position <- offsetPosition baseIndex index sourceWork.Position
        store.TrackAdd(store.Works, pastedWork)
        let isDifferentFlow = sourceWork.ParentId <> targetFlowId
        let context = if isDifferentFlow then DifferentFlow else DifferentWork
        let ctxOpt = if isDifferentFlow then deviceFlowCtxOpt else None
        let callMap, finalDeviceState =
            DsQuery.callsOf sourceWork.Id store
            |> List.fold (fun (callMap, devState) sourceCall ->
                let pastedCall, newDevState = pasteCallToWork store context sourceCall pastedWork.Id devState ctxOpt 0 0
                Map.add sourceCall.Id pastedCall.Id callMap, newDevState
            ) (Map.empty, deviceState)
        pastedWork, callMap, finalDeviceState

    let pasteFlowToSystem (store: DsStore) (sourceFlow: Flow) (targetSystemId: Guid) (newNameOpt: string option) : Guid =
        let flowName = newNameOpt |> Option.defaultValue sourceFlow.Name
        let pastedFlow = Flow(flowName, targetSystemId)
        store.TrackAdd(store.Flows, pastedFlow)
        let deviceFlowCtxOpt =
            EntityHierarchyQueries.findProjectOfSystem store targetSystemId
            |> Option.map (fun projectId ->
                { Store = store; ProjectId = projectId; TargetFlowName = pastedFlow.Name })
        let sourceSystemId = sourceFlow.ParentId
        let sourceWorkArrows = DsQuery.arrowWorksOf sourceSystemId store
        let sourceWorks = DsQuery.worksOf sourceFlow.Id store
        let sourceCallArrows =
            sourceWorks |> List.collect (fun w -> DsQuery.arrowCallsOf w.Id store)
        let workMap, callMap, _ =
            sourceWorks
            |> List.fold (fun (wm, cm, ds) sw ->
                let pw, lcm, nds = pasteWorkToFlow store sw pastedFlow.Id ds deviceFlowCtxOpt 0 0
                Map.add sw.Id pw.Id wm, mergeGuidMap cm lcm, nds
            ) (Map.empty, Map.empty, initialDevicePasteState)
        replayWorkArrows store targetSystemId sourceWorkArrows workMap
        replayCallArrows store sourceCallArrows callMap
        pastedFlow.Id

    let pasteWorksToFlowBatch (store: DsStore) (sourceWorks: Work list) (targetFlowId: Guid) (baseIndex: int) : Guid list =
        let selectedWorkIds = sourceWorks |> List.map (fun w -> w.Id) |> Set.ofList
        let selectedCallIds =
            sourceWorks
            |> List.collect (fun w -> DsQuery.callsOf w.Id store)
            |> List.map (fun c -> c.Id) |> Set.ofList
        let sourceSystemIds =
            sourceWorks
            |> List.choose (fun w -> DsQuery.getFlow w.ParentId store |> Option.map (fun f -> f.ParentId))
            |> Set.ofList
        let sourceWorkArrows =
            collectArrowsWithinSet DsQuery.arrowWorksOf (fun a -> a.SourceId) (fun a -> a.TargetId) sourceSystemIds selectedWorkIds store
        let sourceCallArrows =
            collectArrowsWithinSet DsQuery.arrowCallsOf (fun a -> a.SourceId) (fun a -> a.TargetId) selectedWorkIds selectedCallIds store
        let deviceFlowCtxOpt = makeDeviceFlowCtx store targetFlowId
        let workMap, callMap, pastedIdsRev, _, _ =
            sourceWorks
            |> List.fold (fun (wm, cm, ids, ds, idx) sw ->
                let pw, lcm, nds = pasteWorkToFlow store sw targetFlowId ds deviceFlowCtxOpt baseIndex idx
                Map.add sw.Id pw.Id wm, mergeGuidMap cm lcm, pw.Id :: ids, nds, idx + 1
            ) (Map.empty, Map.empty, [], initialDevicePasteState, 0)
        match DsQuery.getFlow targetFlowId store with
        | Some flow -> replayWorkArrows store flow.ParentId sourceWorkArrows workMap
        | None -> ()
        replayCallArrows store sourceCallArrows callMap
        pastedIdsRev |> List.rev

    let pasteCallsToWorkBatch (store: DsStore) (sourceCalls: Call list) (targetWorkId: Guid) (baseIndex: int) : Guid list =
        let selectedCallIds = sourceCalls |> List.map (fun c -> c.Id) |> Set.ofList
        let sourceWorkIds =
            sourceCalls |> List.map (fun c -> c.ParentId) |> Set.ofList
        let sourceCallArrows =
            collectArrowsWithinSet DsQuery.arrowCallsOf (fun a -> a.SourceId) (fun a -> a.TargetId) sourceWorkIds selectedCallIds store
        let targetFlowIdOpt = DsQuery.getWork targetWorkId store |> Option.map (fun w -> w.ParentId)
        let deviceFlowCtxForDiffFlow = targetFlowIdOpt |> Option.bind (makeDeviceFlowCtx store)
        let callMap, pastedIdsRev, _, _ =
            sourceCalls
            |> List.fold (fun (cm, ids, ds, idx) sc ->
                let context =
                    if sc.ParentId = targetWorkId then SameWork
                    else
                        match DsQuery.getWork targetWorkId store, DsQuery.getWork sc.ParentId store with
                        | Some tw, Some sw when tw.ParentId = sw.ParentId -> DifferentWork
                        | _ -> DifferentFlow
                let ctxOpt = match context with DifferentFlow -> deviceFlowCtxForDiffFlow | _ -> None
                let pc, nds = pasteCallToWork store context sc targetWorkId ds ctxOpt baseIndex idx
                Map.add sc.Id pc.Id cm, pc.Id :: ids, nds, idx + 1
            ) (Map.empty, [], initialDevicePasteState, 0)
        replayCallArrows store sourceCallArrows callMap
        pastedIdsRev |> List.rev

    let dispatchPaste
        (store: DsStore) (copiedEntityKind: EntityKind) (copiedIds: Guid list)
        (targetEntityKind: EntityKind) (targetEntityId: Guid) (baseIndex: int) : Guid list =
        match copiedEntityKind with
        | EntityKind.Flow ->
            let sourceFlows = copiedIds |> List.choose (fun id -> DsQuery.getFlow id store)
            if sourceFlows.IsEmpty then []
            else
                sourceFlows |> List.map (fun sf ->
                    let targetSystemId =
                        EntityHierarchyQueries.resolveTarget store EntityKind.System targetEntityKind targetEntityId
                        |> Option.defaultValue sf.ParentId
                    pasteFlowToSystem store sf targetSystemId None)
        | EntityKind.Work ->
            let sourceWorks = copiedIds |> List.choose (fun id -> DsQuery.getWork id store)
            if sourceWorks.IsEmpty then []
            else
                let targetFlowId =
                    EntityHierarchyQueries.resolveTarget store EntityKind.Flow targetEntityKind targetEntityId
                    |> Option.defaultValue sourceWorks.Head.ParentId
                pasteWorksToFlowBatch store sourceWorks targetFlowId baseIndex
        | EntityKind.Call ->
            let sourceCalls = copiedIds |> List.choose (fun id -> DsQuery.getCall id store)
            if sourceCalls.IsEmpty then []
            else
                let targetWorkId =
                    EntityHierarchyQueries.resolveTarget store EntityKind.Work targetEntityKind targetEntityId
                    |> Option.defaultValue sourceCalls.Head.ParentId
                pasteCallsToWorkBatch store sourceCalls targetWorkId baseIndex
        | _ -> []

// =============================================================================
// DsStore Paste 확장
// =============================================================================

[<Extension>]
type DsStorePasteExtensions =

    [<Extension>]
    static member PasteFlowWithRename
        (store: DsStore, sourceFlowId: Guid, targetSystemId: Guid, newFlowName: string) : Guid option =
        match DsQuery.getFlow sourceFlowId store with
        | None -> None
        | Some sourceFlow ->
            StoreLog.debug($"PasteFlowWithRename: {sourceFlow.Name} → {newFlowName}, targetSystem={targetSystemId}")
            let mutable pastedId = Guid.Empty
            store.WithTransaction($"Paste Flow '{newFlowName}'", fun () ->
                pastedId <- DirectPasteOps.pasteFlowToSystem store sourceFlow targetSystemId (Some newFlowName))
            if pastedId <> Guid.Empty then
                store.EmitRefreshAndHistory()
                Some pastedId
            else None

    [<Extension>]
    static member PasteEntities
        (store: DsStore, copiedEntityKind: EntityKind, copiedEntityIds: seq<Guid>,
         targetEntityKind: EntityKind, targetEntityId: Guid, pasteIndex: int) : Guid list =
        let ids = copiedEntityIds |> Seq.distinct |> Seq.toList
        if not (PasteResolvers.isCopyableEntityKind copiedEntityKind) || ids.IsEmpty then []
        else
            StoreLog.debug($"kind={copiedEntityKind}, count={ids.Length}, targetKind={targetEntityKind}, targetId={targetEntityId}")
            let mutable pastedIds = []
            store.WithTransaction($"Paste {copiedEntityKind}s", fun () ->
                pastedIds <- DirectPasteOps.dispatchPaste store copiedEntityKind ids targetEntityKind targetEntityId pasteIndex)
            if not pastedIds.IsEmpty then store.EmitRefreshAndHistory()
            pastedIds

    [<Extension>]
    static member ValidateCopySelection(store: DsStore, keys: seq<SelectionKey>) : CopyValidationResult =
        let filtered =
            keys
            |> Seq.filter (fun k -> PasteResolvers.isCopyableEntityKind k.EntityKind)
            |> Seq.distinctBy (fun k -> k.Id, k.EntityKind)
            |> Seq.toList
        if filtered.IsEmpty then CopyValidationResult.NothingToCopy
        else
            let kinds = filtered |> List.map (fun k -> k.EntityKind) |> List.distinct
            if kinds.Length > 1 then CopyValidationResult.MixedTypes
            elif filtered.Length > 1 then
                let parents =
                    filtered
                    |> List.map (fun k -> EntityHierarchyQueries.parentIdOf store k.EntityKind k.Id)
                    |> List.distinct
                if parents.Length > 1 then CopyValidationResult.MixedParents
                else CopyValidationResult.Ok filtered
            else CopyValidationResult.Ok filtered

