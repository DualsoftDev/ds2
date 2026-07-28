namespace Ds2.Core.StandardSubmodels

open System
open System.Collections.Generic
open Ds2.Core

/// Operational Data — DualSoft in-house submodel (no IDTA standard yet).
///
/// This submodel is the **projection sink** for AIMC (§04-D). Only assets that
/// require a live current-value surface inside AAS carry this; the primary
/// data path remains AID → adapter → OPC UA → SQLite.
[<AutoOpen>]
module OperationalDataTypes =

    /// One projected current-value item.
    type OperationalDataItem() =
        member val IdShort = "" with get, set
        member val SemanticId : SemanticId = SemanticId "" with get, set
        member val ValueType : XsdType = XsDouble with get, set
        member val Unit : string option = None with get, set
        /// Boxed current value. Type is asset-specific; use `ValueType` to interpret.
        member val CurrentValue : obj option = None with get, set
        member val LastUpdated : DateTimeOffset option = None with get, set

    /// AAS Submodel "OperationalData" — DualSoft in-house extension.
    ///
    /// SemanticId 는 Ds2.Aasx.AasxSemantics.OperationalDataSubmodelSemanticId
    /// (`{CdBaseUrl}/sm/OperationalData/1/0`) 와 반드시 일치해야 export/import 왕복이 성립.
    /// Ds2.Core 는 Ds2.Aasx 를 참조할 수 없으므로 문자열을 하드코딩하고 unit test 로 동치성 보장.
    type OperationalData() =
        member val IdShort = "OperationalData" with get, set
        member val SemanticId : SemanticId =
            SemanticId "https://dualsoftdev.github.io/aas-semantics/sm/OperationalData/1/0"
            with get, set
        member val Items = ResizeArray<OperationalDataItem>() with get, set

        /// Provenance §C — Item IdShort 중 KpiWalker 가 auto-generate 한 것들.
        member val AutoOriginIdShorts = HashSet<string>() with get, set

        /// Provenance §C — 사용자가 삭제한 auto-generated Item IdShort (tombstones).
        member val SuppressedAutoIdShorts = HashSet<string>() with get, set

        static member Empty () = OperationalData()

/// SequenceSubmodels → OperationalData 자동 생성 헬퍼.
///
/// 흐름:
///   1. LoggingSystemProperties.SignalPolicies → Items 기본 생성 (IdShort = SignalId)
///   2. AssetInterfacesDescription.Interfaces → ValueType + Unit 보강 (enrichFromAid)
///
/// 두 단계를 따로 또는 조합해서 사용:
///   let od = OperationalDataBuilder.fromSignalPolicies policies
///   OperationalDataBuilder.enrichFromAid od aid
[<RequireQualifiedAccess>]
module OperationalDataBuilder =

    open AssetInterfacesDescriptionTypes

    let private aidTypeMap (aid: AssetInterfacesDescription)
        : Map<string, struct (XsdType * string option)> =
        let mutable m = Map.empty
        for binding in aid.Interfaces do
            let add (sid: SignalId) vt u =
                m <- m |> Map.add sid.Value (struct (vt, u))
            match binding with
            | OpcUa(_, interactions, _) ->
                for i in interactions do add i.SignalId i.ValueType i.Unit
            | Modbus(_, interactions) ->
                for i in interactions do add i.SignalId i.ValueType i.Unit
            | Mqtt(_, interactions) ->
                for i in interactions do add i.SignalId i.ValueType i.Unit
            | Http(_, interactions) ->
                for i in interactions do add i.SignalId i.ValueType i.Unit
        m

    let private mkItem (signalId: SignalId) : OperationalDataItem =
        let item = OperationalDataItem()
        item.IdShort  <- signalId.Value
        item.SemanticId <- SemanticId $"urn:dualsoft:signal:{signalId.Value}"
        item.ValueType  <- XsDouble
        item

    /// SignalPolicies → OperationalData (새 인스턴스 생성).
    let fromSignalPolicies (policies: SignalPolicy seq) : OperationalData =
        let od = OperationalData()
        for p in policies do
            od.Items.Add(mkItem p.SignalId)
        od

    /// 기존 OperationalData 에 없는 SignalId 만 추가 (멱등).
    let appendFromSignalPolicies
        (od: OperationalData)
        (policies: SignalPolicy seq) : unit =
        let existing = od.Items |> Seq.map (fun i -> i.IdShort) |> Set.ofSeq
        for p in policies do
            if not (existing.Contains p.SignalId.Value) then
                od.Items.Add(mkItem p.SignalId)

    /// AID 인터랙션에서 ValueType + Unit 보강 (이미 생성된 Items 를 제자리 갱신).
    /// IdShort = SignalId.Value 로 매칭.
    let enrichFromAid (od: OperationalData) (aid: AssetInterfacesDescription) : unit =
        let m = aidTypeMap aid
        for item in od.Items do
            match m |> Map.tryFind item.IdShort with
            | Some (struct (vt, u)) ->
                item.ValueType <- vt
                item.Unit      <- u
            | None -> ()
