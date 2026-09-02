namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store


/// <summary>
/// Condition 트리 wholesale 교체용 DTO — `ReplaceCallConditionTree` / `ReplaceWorkConditionTree` 입력.
/// 중첩 그룹 구조 보존 (item leaves + 재귀 children).
/// </summary>
type ConditionTreeDto = {
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
    Children    : System.Collections.Generic.IReadOnlyList<ConditionTreeDto>
}


// =============================================================================
// 내부 트리 빌더 (Call/Work 공용)
// =============================================================================

module internal ConditionTreeOps =
    /// SkipAction 조건 leaf 의 기본 기대값.
    /// UndefinedValue 로 두면 런타임 checkConditionSpecBase 가 "RxWork 가 Finish 인가" 분기를 타서
    /// 참조 신호의 값을 전혀 보지 않는다 → 어떤 상태에서도 skip 이 발생하지 않는다.
    /// BoolValue(Single false) 로 두어야 "신호 off(디바이스 Work=Ready)" 를 값으로 판정하고,
    /// ContactKind(참조건/부정조건) 선택이 그대로 실행/건너뜀으로 이어진다.
    let internal defaultSkipActionInputSpec = ValueSpec.BoolValue(Single false)

    /// SkipAction 조건에 들어가는 leaf 는 기대값이 비어 있으면 기본값을 채운다.
    /// (원본 ApiCall 은 디바이스 캐스케이드 기본값이라 InputSpec 이 UndefinedValue 다)
    let internal applySkipActionInputSpec (condType: ConditionType option) (apiCall: ApiCall) =
        if condType = Some ConditionType.SkipAction && apiCall.InputSpec = ValueSpec.UndefinedValue then
            apiCall.InputSpec <- defaultSkipActionInputSpec

    let rec build (store: DsStore) (dto: ConditionTreeDto) (typeOpt: ConditionType option) : Condition =
        let cc = Condition(IsOR = dto.IsOR, IsInverted = dto.IsInverted, Type = typeOpt)
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
                cc.ApiCalls.Add(dummy)
            else
                match Queries.getApiCall sid store with
                | Some src ->
                    let copy = src.DeepCopy()
                    copy.Id <- src.Id
                    copy.ContactKind <- kind
                    applySkipActionInputSpec typeOpt copy
                    cc.ApiCalls.Add(copy)
                | None -> ()
        // raw 심볼(_ON/_OFF 등 ApiCall 외 leaf) → dummy ApiCall 로 변환하여 cc.ApiCalls 에 추가.
        // 로드 시 ConditionExprBuilder.leafCond 가 lookup 실패 → ac.Name fallback 으로 자동 처리.
        let rawKinds = dto.RawSymbolKinds
        let rn = dto.RawSymbols.Count
        for i in 0 .. rn - 1 do
            let sym = dto.RawSymbols.[i]
            let kind = if i < rawKinds.Count then rawKinds.[i] else ContactKind.NoContact
            let dummy = ApiCall(sym)
            dummy.ContactKind <- kind
            cc.ApiCalls.Add(dummy)
        for child in dto.Children do
            cc.Children.Add(build store child None)  // children 은 Type 미설정
        cc


// =============================================================================
// Call 쪽 Condition 확장
// =============================================================================

[<Extension>]
type DsStorePanelConditionExtensions =
    [<Extension>]
    static member GetCallConditionsForPanel(store: DsStore, callId: Guid) : ConditionPanelItem list =
        let resolvedId = Queries.resolveOriginalCallId callId store
        let rec toPanel (cond: Condition) : ConditionPanelItem =
            let items = cond.ApiCalls |> Seq.map (DirectPanelOps.toConditionApiCallItem store) |> Seq.toList
            let children = cond.Children |> Seq.map toPanel |> Seq.toList
            ConditionPanelItem(
                cond.Id,
                (cond.Type |> Option.defaultValue ConditionType.AutoAux),
                cond.IsOR, cond.IsInverted, items, children)
        DirectPanelOps.withCallOrEmpty store resolvedId (fun call ->
            call.Conditions |> Seq.map toPanel |> Seq.toList)

    [<Extension>]
    static member AddCallCondition(store: DsStore, callId: Guid, condType: ConditionType) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, condType={condType}")
        StoreLog.requireCall(store, callId) |> ignore
        let cond = Condition(Type = Some condType)
        DirectPanelOps.mutateCallProps store callId "Call 조건 추가" (fun c -> c.Conditions.Add(cond))

    /// 트리 통째 교체용 DTO. 재귀 — children 포함.
    [<Extension>]
    static member ReplaceCallConditionTree(store: DsStore, callId: Guid, condType: ConditionType,
                                            tree: ConditionTreeDto) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, condType={condType}")
        StoreLog.requireCall(store, callId) |> ignore
        DirectPanelOps.mutateCallProps store callId "Call 조건 트리 교체" (fun c ->
            // 기존 condType conditions 제거.
            let toRemove = c.Conditions |> Seq.filter (fun cc -> cc.Type = Some condType) |> Seq.toList
            for cc in toRemove do c.Conditions.Remove(cc) |> ignore
            c.Conditions.Add(ConditionTreeOps.build store tree (Some condType)))

    /// 단일 트랜잭션: 조건 생성 + ApiCall 추가 (드래그&드롭용)
    [<Extension>]
    static member AddConditionWithApiCalls(store: DsStore, callId: Guid, condType: ConditionType, sourceApiCallIds: Guid seq) : Guid =
        Queries.requireNonReferenceCall callId store
        let sources = sourceApiCallIds |> Seq.choose (fun id -> Queries.getApiCall id store) |> Seq.toList
        StoreLog.debug($"callId={callId}, condType={condType}, count={sources.Length}")
        StoreLog.requireCall(store, callId) |> ignore
        let cond = Condition(Type = Some condType)
        DirectPanelOps.mutateCallProps store callId "Call 조건 추가 + ApiCall" (fun c ->
            c.Conditions.Add(cond)
            for src in sources do
                let copy = src.DeepCopy()
                copy.Id <- src.Id
                ConditionTreeOps.applySkipActionInputSpec (Some condType) copy
                cond.ApiCalls.Add(copy))
        cond.Id

    [<Extension>]
    static member RemoveCallCondition(store: DsStore, callId: Guid, conditionId: Guid) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, conditionId={conditionId}")
        StoreLog.requireCallCondition(store, callId, conditionId) |> ignore
        let rec removeRec (list: ResizeArray<Condition>) =
            if list.RemoveAll(fun cc -> cc.Id = conditionId) = 0 then
                list |> Seq.iter (fun cc -> removeRec cc.Children)
        DirectPanelOps.mutateCallProps store callId "Call 조건 삭제" (fun c -> removeRec c.Conditions)

    /// 기존 조건 안에 하위 조건 추가
    [<Extension>]
    static member AddChildCondition(store: DsStore, callId: Guid, parentCondId: Guid, isOR: bool) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, parentCondId={parentCondId}, isOR={isOR}")
        StoreLog.requireCallCondition(store, callId, parentCondId) |> ignore
        let child = Condition(IsOR = isOR)
        DirectPanelOps.mutateCallProps store callId "Call 하위 조건 추가" (fun c ->
            let parent = DirectPanelOps.requireConditionInCall callId c parentCondId
            parent.Children.Add(child))

    [<Extension>]
    static member UpdateCallConditionSettings(store: DsStore, callId: Guid, condId: Guid, isOR: bool) : bool =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, condId={condId}, isOR={isOR}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        if cond.IsOR <> isOR then
            DirectPanelOps.mutateCallProps store callId "Call 조건 설정 변경" (fun _ ->
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
            DirectPanelOps.mutateCallProps store callId "Call 조건에 ApiCall 추가" (fun c ->
                let cond = DirectPanelOps.requireConditionInCall callId c condId
                for src in sources do
                    let copy = src.DeepCopy()
                    copy.Id <- src.Id
                    ConditionTreeOps.applySkipActionInputSpec cond.Type copy
                    cond.ApiCalls.Add(copy))
            sources.Length

    [<Extension>]
    static member RemoveApiCallFromCondition(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        StoreLog.requireApiCallInCondition(cond, apiCallId) |> ignore
        DirectPanelOps.mutateCallProps store callId "Call 조건에서 ApiCall 제거" (fun c ->
            let targetCond = DirectPanelOps.requireConditionInCall callId c condId
            targetCond.ApiCalls.RemoveAll(fun ac -> ac.Id = apiCallId) |> ignore)

    [<Extension>]
    static member UpdateConditionApiCallInputSpec(store: DsStore, callId: Guid, condId: Guid, apiCallId: Guid, inTypeIndex: int, inText: string) : bool =
        StoreLog.debug($"callId={callId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireCallCondition(store, callId, condId)
        let ac = StoreLog.requireApiCallInCondition(cond, apiCallId)
        let newSpec = PropertyPanelValueSpec.parseFromPanel inTypeIndex inText
        if ac.InputSpec <> newSpec then
            DirectPanelOps.mutateCallProps store callId "Call 조건 InputSpec 변경" (fun c ->
                let targetCond = DirectPanelOps.requireConditionInCall callId c condId
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
            DirectPanelOps.mutateCallProps store callId "Call 조건 OutputSpec 변경" (fun c ->
                let targetCond = DirectPanelOps.requireConditionInCall callId c condId
                let targetApiCall = DirectPanelOps.requireApiCallInCondition callId condId targetCond apiCallId
                targetApiCall.OutputSpec <- newSpec)
            true
        else false

    // v10: UpdateConditionApiCallSkipInputSensor 제거 — SkipInputSensor 가 ApiDef.SensingType=Virtual 로 흡수됨.

    /// SkipAction Drag&Drop picker (A접/B접) 결과를 반영하는 후처리 setter.
    /// apiCallIds 에 포함된 ApiCall 만 ContactKind 를 일괄 설정한다 (방금 추가된 leaf 만 타겟).
    [<Extension>]
    static member SetConditionApiCallsContactKind
        (store: DsStore, callId: Guid, condId: Guid, apiCallIds: Guid seq, kind: ContactKind) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, condId={condId}, kind={kind}")
        StoreLog.requireCallCondition(store, callId, condId) |> ignore
        let idSet = apiCallIds |> Set.ofSeq
        if idSet.IsEmpty then () else
        DirectPanelOps.mutateCallProps store callId "Call 조건 ApiCall ContactKind 설정" (fun c ->
            let targetCond = DirectPanelOps.requireConditionInCall callId c condId
            for ac in targetCond.ApiCalls do
                if idSet.Contains ac.Id then
                    ac.ContactKind <- kind)


// =============================================================================
// Work 쪽 Condition 확장 (SkipAction 만 의미 — 그러나 API 형태는 Call 과 대칭)
// =============================================================================

[<Extension>]
type DsStorePanelWorkConditionExtensions =
    [<Extension>]
    static member GetWorkConditionsForPanel(store: DsStore, workId: Guid) : ConditionPanelItem list =
        let resolvedId = Queries.resolveOriginalWorkId workId store
        let rec toPanel (cond: Condition) : ConditionPanelItem =
            let items = cond.ApiCalls |> Seq.map (DirectPanelOps.toConditionApiCallItem store) |> Seq.toList
            let children = cond.Children |> Seq.map toPanel |> Seq.toList
            ConditionPanelItem(
                cond.Id,
                (cond.Type |> Option.defaultValue ConditionType.SkipAction),
                cond.IsOR, cond.IsInverted, items, children)
        DirectPanelOps.withWorkOrEmpty store resolvedId (fun work ->
            work.Conditions |> Seq.map toPanel |> Seq.toList)

    [<Extension>]
    static member AddWorkCondition(store: DsStore, workId: Guid, condType: ConditionType) =
        StoreLog.debug($"workId={workId}, condType={condType}")
        StoreLog.requireWork(store, workId) |> ignore
        let cond = Condition(Type = Some condType)
        DirectPanelOps.mutateWorkProps store workId "Work 조건 추가" (fun w -> w.Conditions.Add(cond))

    [<Extension>]
    static member ReplaceWorkConditionTree(store: DsStore, workId: Guid, condType: ConditionType,
                                            tree: ConditionTreeDto) =
        StoreLog.debug($"workId={workId}, condType={condType}")
        StoreLog.requireWork(store, workId) |> ignore
        DirectPanelOps.mutateWorkProps store workId "Work 조건 트리 교체" (fun w ->
            let toRemove = w.Conditions |> Seq.filter (fun cc -> cc.Type = Some condType) |> Seq.toList
            for cc in toRemove do w.Conditions.Remove(cc) |> ignore
            w.Conditions.Add(ConditionTreeOps.build store tree (Some condType)))

    [<Extension>]
    static member AddWorkConditionWithApiCalls(store: DsStore, workId: Guid, condType: ConditionType, sourceApiCallIds: Guid seq) : Guid =
        let sources = sourceApiCallIds |> Seq.choose (fun id -> Queries.getApiCall id store) |> Seq.toList
        StoreLog.debug($"workId={workId}, condType={condType}, count={sources.Length}")
        StoreLog.requireWork(store, workId) |> ignore
        let cond = Condition(Type = Some condType)
        DirectPanelOps.mutateWorkProps store workId "Work 조건 추가 + ApiCall" (fun w ->
            w.Conditions.Add(cond)
            for src in sources do
                let copy = src.DeepCopy()
                copy.Id <- src.Id
                ConditionTreeOps.applySkipActionInputSpec (Some condType) copy
                cond.ApiCalls.Add(copy))
        cond.Id

    [<Extension>]
    static member RemoveWorkCondition(store: DsStore, workId: Guid, conditionId: Guid) =
        StoreLog.debug($"workId={workId}, conditionId={conditionId}")
        StoreLog.requireWorkCondition(store, workId, conditionId) |> ignore
        let rec removeRec (list: ResizeArray<Condition>) =
            if list.RemoveAll(fun cc -> cc.Id = conditionId) = 0 then
                list |> Seq.iter (fun cc -> removeRec cc.Children)
        DirectPanelOps.mutateWorkProps store workId "Work 조건 삭제" (fun w -> removeRec w.Conditions)

    [<Extension>]
    static member AddWorkChildCondition(store: DsStore, workId: Guid, parentCondId: Guid, isOR: bool) =
        StoreLog.debug($"workId={workId}, parentCondId={parentCondId}, isOR={isOR}")
        StoreLog.requireWorkCondition(store, workId, parentCondId) |> ignore
        let child = Condition(IsOR = isOR)
        DirectPanelOps.mutateWorkProps store workId "Work 하위 조건 추가" (fun w ->
            let parent = DirectPanelOps.requireConditionInWork workId w parentCondId
            parent.Children.Add(child))

    [<Extension>]
    static member UpdateWorkConditionSettings(store: DsStore, workId: Guid, condId: Guid, isOR: bool) : bool =
        StoreLog.debug($"workId={workId}, condId={condId}, isOR={isOR}")
        let cond = StoreLog.requireWorkCondition(store, workId, condId)
        if cond.IsOR <> isOR then
            DirectPanelOps.mutateWorkProps store workId "Work 조건 설정 변경" (fun _ ->
                cond.IsOR <- isOR)
            true
        else false

    [<Extension>]
    static member AddApiCallsToWorkConditionBatch(store: DsStore, workId: Guid, condId: Guid, sourceApiCallIds: Guid seq) : int =
        let sources = sourceApiCallIds |> Seq.choose (fun id -> Queries.getApiCall id store) |> Seq.toList
        if sources.IsEmpty then 0
        else
            StoreLog.debug($"workId={workId}, condId={condId}, count={sources.Length}")
            StoreLog.requireWorkCondition(store, workId, condId) |> ignore
            DirectPanelOps.mutateWorkProps store workId "Work 조건에 ApiCall 추가" (fun w ->
                let cond = DirectPanelOps.requireConditionInWork workId w condId
                for src in sources do
                    let copy = src.DeepCopy()
                    copy.Id <- src.Id
                    ConditionTreeOps.applySkipActionInputSpec cond.Type copy
                    cond.ApiCalls.Add(copy))
            sources.Length

    [<Extension>]
    static member RemoveApiCallFromWorkCondition(store: DsStore, workId: Guid, condId: Guid, apiCallId: Guid) =
        StoreLog.debug($"workId={workId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireWorkCondition(store, workId, condId)
        StoreLog.requireApiCallInCondition(cond, apiCallId) |> ignore
        DirectPanelOps.mutateWorkProps store workId "Work 조건에서 ApiCall 제거" (fun w ->
            let targetCond = DirectPanelOps.requireConditionInWork workId w condId
            targetCond.ApiCalls.RemoveAll(fun ac -> ac.Id = apiCallId) |> ignore)

    [<Extension>]
    static member UpdateWorkConditionApiCallInputSpec(store: DsStore, workId: Guid, condId: Guid, apiCallId: Guid, inTypeIndex: int, inText: string) : bool =
        StoreLog.debug($"workId={workId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireWorkCondition(store, workId, condId)
        let ac = StoreLog.requireApiCallInCondition(cond, apiCallId)
        let newSpec = PropertyPanelValueSpec.parseFromPanel inTypeIndex inText
        if ac.InputSpec <> newSpec then
            DirectPanelOps.mutateWorkProps store workId "Work 조건 InputSpec 변경" (fun w ->
                let targetCond = DirectPanelOps.requireConditionInWork workId w condId
                let targetApiCall = DirectPanelOps.requireApiCallInCondition workId condId targetCond apiCallId
                targetApiCall.InputSpec <- newSpec)
            true
        else false

    [<Extension>]
    static member UpdateWorkConditionApiCallOutputSpec(store: DsStore, workId: Guid, condId: Guid, apiCallId: Guid, outTypeIndex: int, outText: string) : bool =
        StoreLog.debug($"workId={workId}, condId={condId}, apiCallId={apiCallId}")
        let cond = StoreLog.requireWorkCondition(store, workId, condId)
        let ac = StoreLog.requireApiCallInCondition(cond, apiCallId)
        let newSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        if ac.OutputSpec <> newSpec then
            DirectPanelOps.mutateWorkProps store workId "Work 조건 OutputSpec 변경" (fun w ->
                let targetCond = DirectPanelOps.requireConditionInWork workId w condId
                let targetApiCall = DirectPanelOps.requireApiCallInCondition workId condId targetCond apiCallId
                targetApiCall.OutputSpec <- newSpec)
            true
        else false

    /// SkipAction Drag&Drop picker (A접/B접) 결과 후처리 setter (Work 측).
    /// apiCallIds 에 포함된 ApiCall 만 ContactKind 를 일괄 설정한다 (방금 추가된 leaf 만 타겟).
    [<Extension>]
    static member SetWorkConditionApiCallsContactKind
        (store: DsStore, workId: Guid, condId: Guid, apiCallIds: Guid seq, kind: ContactKind) =
        StoreLog.debug($"workId={workId}, condId={condId}, kind={kind}")
        StoreLog.requireWorkCondition(store, workId, condId) |> ignore
        let idSet = apiCallIds |> Set.ofSeq
        if idSet.IsEmpty then () else
        DirectPanelOps.mutateWorkProps store workId "Work 조건 ApiCall ContactKind 설정" (fun w ->
            let targetCond = DirectPanelOps.requireConditionInWork workId w condId
            for ac in targetCond.ApiCalls do
                if idSet.Contains ac.Id then
                    ac.ContactKind <- kind)
