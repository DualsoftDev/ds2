namespace Ds2.Core.Kpi

open System.Collections.Generic
open Ds2.Core
open Ds2.Core.StandardSubmodels

/// Ensure 결과 상태.
type EnsureState =
    /// 새로 추가됨 (append 발생)
    | Added
    /// 이미 존재 (skip)
    | Existed
    /// 충돌 감지 (동일 SemanticId 다른 IdShort — 경고 후 skip)
    | Conflict
    /// 사용자가 tombstone 표시함 — auto-regenerate skip (Provenance §C)
    | Suppressed

/// AID append — OPC UA 인터랙션 추가.
/// AID 가 None 이면 새로 만들고 project 에 설정 (호출자 책임).
/// 반환: 실제 append 여부.
[<RequireQualifiedAccess>]
module KpiAidAppender =

    /// OPC UA endpoint 를 확보 · 없으면 기본값 opc.tcp://localhost:48400.
    let private ensureOpcUaBinding (aid: AssetInterfacesDescription) : AidBinding =
        let existing =
            aid.Interfaces
            |> Seq.tryFind (fun b -> match b with OpcUa _ -> true | _ -> false)
        match existing with
        | Some b -> b
        | None ->
            let ep = { EndpointMetadata.empty with Base = "opc.tcp://localhost:48400" }
            let binding = OpcUa (ep, [], [])
            aid.Interfaces.Add(binding)
            binding

    /// OpcUa binding 을 갱신 (F# immutable list 라 재구성 필요).
    let private replaceOpcUaBinding
            (aid: AssetInterfacesDescription)
            (newBinding: AidBinding) : unit =
        let idx =
            aid.Interfaces
            |> Seq.tryFindIndex (fun b -> match b with OpcUa _ -> true | _ -> false)
        match idx with
        | Some i -> aid.Interfaces.[i] <- newBinding
        | None -> aid.Interfaces.Add(newBinding)

    let private mkInteraction (target: KpiTarget) : OpcUaInteraction =
        {
            IdShort    = target.IdShort
            SemanticId = SemanticId target.Metric.SemanticId
            ValueType  = target.Metric.DataType
            Unit       = if System.String.IsNullOrEmpty target.Metric.Unit then None
                         else Some target.Metric.Unit
            Href       = sprintf "nsu=urn:ds:kpi;s=%s" target.SignalId.Value
            SignalId   = target.SignalId
        }

    /// Batch ensure: 기존 인터랙션의 IdShort/SemanticId 인덱스를 1회만 구축하여
    /// 각 target 을 O(1) 로 분류. 신규 인터랙션들은 최종 한 번의 list append 로 부착.
    /// 단발 ensure 를 n 회 호출할 때의 O(n²) 를 O(n) 으로 완화.
    let ensureMany (aid: AssetInterfacesDescription) (targets: KpiTarget seq) : EnsureState list =
        let binding = ensureOpcUaBinding aid
        match binding with
        | OpcUa (ep, interactions, events) ->
            let existingIdShort = Dictionary<string, string>() // IdShort → SemanticId
            for i in interactions do
                existingIdShort.[i.IdShort] <- i.SemanticId.Value
            let addedIdShort = HashSet<string>()
            let toAppend = ResizeArray<OpcUaInteraction>()
            let states = ResizeArray<EnsureState>()
            for t in targets do
                if aid.SuppressedAutoIdShorts.Contains t.IdShort then
                    states.Add(Suppressed)
                else
                    match existingIdShort.TryGetValue t.IdShort with
                    | true, sem when sem = t.Metric.SemanticId ->
                        aid.AutoOriginIdShorts.Add(t.IdShort) |> ignore
                        states.Add(Existed)
                    | true, _ ->
                        states.Add(Conflict)
                    | false, _ ->
                        if addedIdShort.Add(t.IdShort) then
                            toAppend.Add(mkInteraction t)
                            aid.AutoOriginIdShorts.Add(t.IdShort) |> ignore
                            states.Add(Added)
                        else
                            // 같은 배치 안에서 IdShort 중복 — 무해하지만 Existed 로 표기.
                            states.Add(Existed)
            if toAppend.Count > 0 then
                let newInteractions = interactions @ (List.ofSeq toAppend)
                replaceOpcUaBinding aid (OpcUa (ep, newInteractions, events))
            List.ofSeq states
        | _ ->
            targets |> Seq.map (fun _ -> Conflict) |> List.ofSeq

    /// 단발 append — ensureMany 로 위임.
    let ensure (aid: AssetInterfacesDescription) (target: KpiTarget) : EnsureState =
        ensureMany aid [ target ] |> List.head


/// OperationalData append — Item 추가.
[<RequireQualifiedAccess>]
module KpiOperationalDataAppender =

    let private mkItem (target: KpiTarget) : OperationalDataItem =
        let item = OperationalDataItem()
        item.IdShort   <- target.IdShort
        item.SemanticId <- KpiIdentifiers.opDataItemSemanticId target.SignalId
        item.ValueType <- target.Metric.DataType
        item.Unit      <- if System.String.IsNullOrEmpty target.Metric.Unit then None
                          else Some target.Metric.Unit
        item

    /// Batch — Items ResizeArray 를 1회 스캔해서 IdShort→SemanticId dict 를 만든 뒤
    /// 각 target 을 O(1) 로 분류. Add 는 ResizeArray.Add 로 amortized O(1).
    let ensureMany (od: OperationalData) (targets: KpiTarget seq) : EnsureState list =
        let existing = Dictionary<string, string>()
        for i in od.Items do
            existing.[i.IdShort] <- i.SemanticId.Value
        let addedIdShort = HashSet<string>()
        let states = ResizeArray<EnsureState>()
        for t in targets do
            if od.SuppressedAutoIdShorts.Contains t.IdShort then
                states.Add(Suppressed)
            else
                let semId = (KpiIdentifiers.opDataItemSemanticId t.SignalId).Value
                match existing.TryGetValue t.IdShort with
                | true, sem when sem = semId ->
                    od.AutoOriginIdShorts.Add(t.IdShort) |> ignore
                    states.Add(Existed)
                | true, _ ->
                    states.Add(Conflict)
                | false, _ ->
                    if addedIdShort.Add(t.IdShort) then
                        od.Items.Add(mkItem t)
                        od.AutoOriginIdShorts.Add(t.IdShort) |> ignore
                        states.Add(Added)
                    else
                        states.Add(Existed)
        List.ofSeq states

    /// 단발 append — ensureMany 로 위임.
    let ensure (od: OperationalData) (target: KpiTarget) : EnsureState =
        ensureMany od [ target ] |> List.head


/// AIMC append — Mapping 추가 (AID interaction ↔ OperationalData item).
[<RequireQualifiedAccess>]
module KpiAimcAppender =

    /// Batch — Mappings ResizeArray 를 1회 스캔해서 (source,sink) HashSet 을 만든 뒤
    /// O(1) 존재검사. 기존은 Seq.exists 매회 = O(n) → 총 O(n²).
    let ensureMany (aimc: AssetInterfacesMappingConfiguration) (targets: KpiTarget seq) : EnsureState list =
        let existingKeys = HashSet<string>()
        for m in aimc.Mappings do
            existingKeys.Add(m.SourceAidPath + "->" + m.SinkAasElementPath) |> ignore
        let states = ResizeArray<EnsureState>()
        for t in targets do
            let source = KpiIdentifiers.aidSourcePath t.IdShort
            let sink   = KpiIdentifiers.opDataSinkPath t.IdShort
            let mappingIdShort = KpiIdentifiers.aimcMappingIdShort source sink
            let key = source + "->" + sink
            if aimc.SuppressedAutoIdShorts.Contains mappingIdShort then
                states.Add(Suppressed)
            elif existingKeys.Contains key then
                aimc.AutoOriginIdShorts.Add(mappingIdShort) |> ignore
                states.Add(Existed)
            else
                aimc.Mappings.Add({ SourceAidPath = source; SinkAasElementPath = sink; Transform = Identity })
                existingKeys.Add(key) |> ignore
                aimc.AutoOriginIdShorts.Add(mappingIdShort) |> ignore
                states.Add(Added)
        List.ofSeq states

    /// 단발 append — ensureMany 로 위임.
    let ensure (aimc: AssetInterfacesMappingConfiguration) (target: KpiTarget) : EnsureState =
        ensureMany aimc [ target ] |> List.head
