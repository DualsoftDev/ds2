namespace Ds2.UI.Core

open System
open System.Runtime.CompilerServices
open Ds2.Core

[<Extension>]
type DsStorePanelConditionExtensions =
    [<Extension>]
    static member GetCallConditionsForPanel(store: DsStore, callId: Guid) : CallConditionPanelItem list =
        let rec toPanel (cond: CallCondition) : CallConditionPanelItem =
            let items = cond.Conditions |> Seq.map (DirectPanelOps.toConditionApiCallItem store) |> Seq.toList
            let children = cond.Children |> Seq.map toPanel |> Seq.toList
            CallConditionPanelItem(
                cond.Id,
                (cond.Type |> Option.defaultValue CallConditionType.Auto),
                cond.IsOR, cond.IsRising, items, children)
        DirectPanelOps.withCallOrEmpty store callId (fun call ->
            call.CallConditions |> Seq.map toPanel |> Seq.toList)

    [<Extension>]
    static member AddCallCondition(store: DsStore, callId: Guid, condType: CallConditionType) =
        StoreLog.debug($"callId={callId}, condType={condType}")
        StoreLog.requireCall(store, callId) |> ignore
        let cond = CallCondition(Type = Some condType)
        DirectPanelOps.mutateCallProps store callId "조건 추가" (fun c -> c.CallConditions.Add(cond))

    [<Extension>]
    static member RemoveCallCondition(store: DsStore, callId: Guid, conditionId: Guid) =
        StoreLog.debug($"callId={callId}, conditionId={conditionId}")
        StoreLog.requireCallCondition(store, callId, conditionId) |> ignore
        let rec removeRec (list: ResizeArray<CallCondition>) =
            if list.RemoveAll(fun cc -> cc.Id = conditionId) = 0 then
                list |> Seq.iter (fun cc -> removeRec cc.Children)
        DirectPanelOps.mutateCallProps store callId "조건 삭제" (fun c -> removeRec c.CallConditions)

    /// 기존 조건 안에 하위 조건 추가
    [<Extension>]
    static member AddChildCondition(store: DsStore, callId: Guid, parentCondId: Guid, isOR: bool) =
        StoreLog.debug($"callId={callId}, parentCondId={parentCondId}, isOR={isOR}")
        StoreLog.requireCallCondition(store, callId, parentCondId) |> ignore
        let child = CallCondition(IsOR = isOR)
        DirectPanelOps.mutateCallProps store callId "하위 조건 추가" (fun c ->
            let parent = DirectPanelOps.requireCondition callId c parentCondId
            parent.Children.Add(child))

    [<Extension>]
    static member UpdateCallConditionSettings(store: DsStore, callId: Guid, condId: Guid, isOR: bool, isRising: bool) : bool =
        StoreLog.debug($"callId={callId}, condId={condId}, isOR={isOR}, isRising={isRising}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        if cond.IsOR <> isOR || cond.IsRising <> isRising then
            DirectPanelOps.mutateCallProps store callId "조건 설정 변경" (fun _ ->
                cond.IsOR <- isOR
                cond.IsRising <- isRising)
            true
        else false

    [<Extension>]
    static member AddApiCallsToConditionBatch(store: DsStore, callId: Guid, condId: Guid, sourceApiCallIds: Guid seq) : int =
        let sources = sourceApiCallIds |> Seq.choose (fun id -> DsQuery.getApiCall id store) |> Seq.toList
        if sources.IsEmpty then 0
        else
            StoreLog.debug($"callId={callId}, condId={condId}, count={sources.Length}")
            StoreLog.requireCallCondition(store, callId, condId) |> ignore
            DirectPanelOps.mutateCallProps store callId "조건에 ApiCall 추가" (fun c ->
                let cond = DirectPanelOps.requireCondition callId c condId
                for src in sources do
                    let copy = src.DeepCopy()
                    copy.Id <- src.Id
                    cond.Conditions.Add(copy))
            sources.Length

    [<Extension>]
    static member RemoveApiCallFromCondition(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid) =
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        StoreLog.requireApiCallInCondition(cond, apiCallId) |> ignore
        DirectPanelOps.mutateCallProps store callId "조건에서 ApiCall 제거" (fun c ->
            let targetCond = DirectPanelOps.requireCondition callId c condId
            targetCond.Conditions.RemoveAll(fun ac -> ac.Id = apiCallId) |> ignore)

    [<Extension>]
    static member UpdateConditionApiCallOutputSpec(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid, outTypeIndex: int, outText: string) : bool =
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        let ac = StoreLog.requireApiCallInCondition(cond, apiCallId)
        let newSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        if ac.OutputSpec <> newSpec then
            DirectPanelOps.mutateCallProps store callId "조건 OutputSpec 변경" (fun c ->
                let targetCond = DirectPanelOps.requireCondition callId c condId
                let targetApiCall = DirectPanelOps.requireApiCallInCondition callId condId targetCond apiCallId
                targetApiCall.OutputSpec <- newSpec)
            true
        else false
