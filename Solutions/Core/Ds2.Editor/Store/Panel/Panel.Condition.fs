namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store


/// <summary>
/// CallCondition 트리 wholesale 교체용 DTO — `ReplaceCallConditionTree` 입력.
/// 중첩 그룹 구조 보존 (item leaves + 재귀 children).
/// </summary>
type CallConditionTreeDto = {
    IsOR        : bool
    IsInverted  : bool
    /// 직접 leaf — 기존 store 의 ApiCall id (deep-copy 해서 spec 보존).
    /// Inverter leaf 는 Guid.Empty.
    ApiCallIds  : System.Collections.Generic.IReadOnlyList<Guid>
    /// ApiCallIds 와 평행 — leaf 별 접점 종류.
    ApiCallKinds: System.Collections.Generic.IReadOnlyList<ContactKind>
    /// ApiCall 매핑 실패한 raw 심볼 (시스템 플래그 _ON/_OFF, 외부 비트 등) — 편집기 보존용.
    RawSymbols     : System.Collections.Generic.IReadOnlyList<string>
    /// RawSymbols 와 평행 — 각 심볼의 ContactKind.
    RawSymbolKinds : System.Collections.Generic.IReadOnlyList<ContactKind>
    Children    : System.Collections.Generic.IReadOnlyList<CallConditionTreeDto>
}


[<Extension>]
type DsStorePanelConditionExtensions =
    [<Extension>]
    static member GetCallConditionsForPanel(store: DsStore, callId: Guid) : CallConditionPanelItem list =
        let resolvedId = Queries.resolveOriginalCallId callId store
        let rec toPanel (cond: CallCondition) : CallConditionPanelItem =
            let items = cond.Conditions |> Seq.map (DirectPanelOps.toConditionApiCallItem store) |> Seq.toList
            let children = cond.Children |> Seq.map toPanel |> Seq.toList
            CallConditionPanelItem(
                cond.Id,
                (cond.Type |> Option.defaultValue CallConditionType.AutoAux),
                cond.IsOR, cond.IsInverted, items, children)
        DirectPanelOps.withCallOrEmpty store resolvedId (fun call ->
            call.CallConditions |> Seq.map toPanel |> Seq.toList)

    [<Extension>]
    static member AddCallCondition(store: DsStore, callId: Guid, condType: CallConditionType) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, condType={condType}")
        StoreLog.requireCall(store, callId) |> ignore
        let cond = CallCondition(Type = Some condType)
        DirectPanelOps.mutateCallProps store callId "조건 추가" (fun c -> c.CallConditions.Add(cond))

    /// 트리 통째 교체용 DTO. 재귀 — children 포함.
    [<Extension>]
    static member ReplaceCallConditionTree(store: DsStore, callId: Guid, condType: CallConditionType,
                                            tree: CallConditionTreeDto) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, condType={condType}")
        StoreLog.requireCall(store, callId) |> ignore
        DirectPanelOps.mutateCallProps store callId "조건 트리 교체" (fun c ->
            // 기존 condType conditions 제거.
            let toRemove = c.CallConditions |> Seq.filter (fun cc -> cc.Type = Some condType) |> Seq.toList
            for cc in toRemove do c.CallConditions.Remove(cc) |> ignore
            // 트리 → CallCondition 재귀 빌드.
            let rec build (dto: CallConditionTreeDto) (typeOpt: CallConditionType option) =
                let cc = CallCondition(IsOR = dto.IsOR, IsInverted = dto.IsInverted, Type = typeOpt)
                // items: 기존 store 의 ApiCall 을 deep copy 해서 spec 보존. ContactKind 적용.
                let kinds = dto.ApiCallKinds
                let n = dto.ApiCallIds.Count
                for i in 0 .. n - 1 do
                    let sid = dto.ApiCallIds.[i]
                    let kind = if i < kinds.Count then kinds.[i] else ContactKind.NoContact
                    if kind = ContactKind.Inverter then
                        // placeholder leaf — 실 ApiCall 무관.
                        let dummy = ApiCall("__inverter__")
                        dummy.ContactKind <- ContactKind.Inverter
                        cc.Conditions.Add(dummy)
                    else
                        match Queries.getApiCall sid store with
                        | Some src ->
                            let copy = src.DeepCopy()
                            copy.Id <- src.Id
                            copy.ContactKind <- kind
                            cc.Conditions.Add(copy)
                        | None -> ()
                // raw 심볼(_ON/_OFF 등 ApiCall 외 leaf) → dummy ApiCall 로 변환하여 cc.Conditions 에 추가.
                // Core 변경 없이 보존 — Inverter dummy 와 동일 패턴 (ApiCall(name) + ContactKind 설정).
                // 로드 시 ConditionExprBuilder.leafCond 가 lookup 실패 → ac.Name fallback 으로 자동 처리.
                let rawKinds = dto.RawSymbolKinds
                let rn = dto.RawSymbols.Count
                for i in 0 .. rn - 1 do
                    let sym = dto.RawSymbols.[i]
                    let kind = if i < rawKinds.Count then rawKinds.[i] else ContactKind.NoContact
                    let dummy = ApiCall(sym)
                    dummy.ContactKind <- kind
                    cc.Conditions.Add(dummy)
                for child in dto.Children do
                    cc.Children.Add(build child None)  // children 은 Type 미설정
                cc
            c.CallConditions.Add(build tree (Some condType)))

    /// 단일 트랜잭션: 조건 생성 + ApiCall 추가 (드래그&드롭용)
    [<Extension>]
    static member AddConditionWithApiCalls(store: DsStore, callId: Guid, condType: CallConditionType, sourceApiCallIds: Guid seq) : Guid =
        Queries.requireNonReferenceCall callId store
        let sources = sourceApiCallIds |> Seq.choose (fun id -> Queries.getApiCall id store) |> Seq.toList
        StoreLog.debug($"callId={callId}, condType={condType}, count={sources.Length}")
        StoreLog.requireCall(store, callId) |> ignore
        let cond = CallCondition(Type = Some condType)
        DirectPanelOps.mutateCallProps store callId "조건 추가 + ApiCall" (fun c ->
            c.CallConditions.Add(cond)
            for src in sources do
                let copy = src.DeepCopy()
                copy.Id <- src.Id
                cond.Conditions.Add(copy))
        cond.Id

    [<Extension>]
    static member RemoveCallCondition(store: DsStore, callId: Guid, conditionId: Guid) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, conditionId={conditionId}")
        StoreLog.requireCallCondition(store, callId, conditionId) |> ignore
        let rec removeRec (list: ResizeArray<CallCondition>) =
            if list.RemoveAll(fun cc -> cc.Id = conditionId) = 0 then
                list |> Seq.iter (fun cc -> removeRec cc.Children)
        DirectPanelOps.mutateCallProps store callId "조건 삭제" (fun c -> removeRec c.CallConditions)

    /// 기존 조건 안에 하위 조건 추가
    [<Extension>]
    static member AddChildCondition(store: DsStore, callId: Guid, parentCondId: Guid, isOR: bool) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, parentCondId={parentCondId}, isOR={isOR}")
        StoreLog.requireCallCondition(store, callId, parentCondId) |> ignore
        let child = CallCondition(IsOR = isOR)
        DirectPanelOps.mutateCallProps store callId "하위 조건 추가" (fun c ->
            let parent = DirectPanelOps.requireCondition callId c parentCondId
            parent.Children.Add(child))

    [<Extension>]
    static member UpdateCallConditionSettings(store: DsStore, callId: Guid, condId: Guid, isOR: bool) : bool =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, condId={condId}, isOR={isOR}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        if cond.IsOR <> isOR then
            DirectPanelOps.mutateCallProps store callId "조건 설정 변경" (fun _ ->
                cond.IsOR <- isOR)
            true
        else false

    [<Extension>]
    static member AddApiCallsToConditionBatch(store: DsStore, callId: Guid, condId: Guid, sourceApiCallIds: Guid seq) : int =
        Queries.requireNonReferenceCall callId store
        let sources = sourceApiCallIds |> Seq.choose (fun id -> Queries.getApiCall id store) |> Seq.toList
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
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        StoreLog.requireApiCallInCondition(cond, apiCallId) |> ignore
        DirectPanelOps.mutateCallProps store callId "조건에서 ApiCall 제거" (fun c ->
            let targetCond = DirectPanelOps.requireCondition callId c condId
            targetCond.Conditions.RemoveAll(fun ac -> ac.Id = apiCallId) |> ignore)

    [<Extension>]
    static member UpdateConditionApiCallInputSpec(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid, inTypeIndex: int, inText: string) : bool =
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        let ac = StoreLog.requireApiCallInCondition(cond, apiCallId)
        let newSpec = PropertyPanelValueSpec.parseFromPanel inTypeIndex inText
        if ac.InputSpec <> newSpec then
            DirectPanelOps.mutateCallProps store callId "조건 InputSpec 변경" (fun c ->
                let targetCond = DirectPanelOps.requireCondition callId c condId
                let targetApiCall = DirectPanelOps.requireApiCallInCondition callId condId targetCond apiCallId
                targetApiCall.InputSpec <- newSpec)
            true
        else false

    [<Extension>]
    static member UpdateConditionApiCallOutputSpec(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid, outTypeIndex: int, outText: string) : bool =
        Queries.requireNonReferenceCall callId store
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

    [<Extension>]
    static member UpdateConditionApiCallSkipInputSensor(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid, skipInputSensor: bool) : bool =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}, skipInputSensor={skipInputSensor}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        let ac = StoreLog.requireApiCallInCondition(cond, apiCallId)
        if ac.SkipInputSensor <> skipInputSensor then
            DirectPanelOps.mutateCallProps store callId "조건 SkipInputSensor 변경" (fun c ->
                let targetCond = DirectPanelOps.requireCondition callId c condId
                let targetApiCall = DirectPanelOps.requireApiCallInCondition callId condId targetCond apiCallId
                targetApiCall.SkipInputSensor <- skipInputSensor)
            true
        else false
