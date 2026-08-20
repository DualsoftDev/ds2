namespace Ds2.Aasx

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Core.Kpi
open Ds2.Core.StandardSubmodels
open Ds2.Aasx.AasxSemantics

/// 사내 표준 서브모델 (AID / AIMC / OperationalData / SignalPolicy) 의 AAS Submodel 변환 (Export).
///
/// IDTA 공식 템플릿이 확보되기 전에는 **hand-written 방식**으로 SM 을 생성.
/// 향후 템플릿이 도입되면 `TemplateLoader.tryLoadSubmodel` + `TemplateScaffold` 로 교체.
module AasxExportStandardSubmodels =

    open AasxExportCore

    // -------------------------------------------------------------------------
    // 공통 헬퍼
    // -------------------------------------------------------------------------

    /// XsdType (F# 도메인) → AAS DataTypeDefXsd 매핑.
    let private xsdToAas (t: XsdType) : DataTypeDefXsd =
        match t with
        | XsDouble       -> DataTypeDefXsd.Double
        | XsFloat        -> DataTypeDefXsd.Float
        | XsInt          -> DataTypeDefXsd.Int
        | XsLong         -> DataTypeDefXsd.Long
        | XsUnsignedInt  -> DataTypeDefXsd.UnsignedInt
        | XsUnsignedLong -> DataTypeDefXsd.UnsignedLong
        | XsBoolean      -> DataTypeDefXsd.Boolean
        | XsString       -> DataTypeDefXsd.String
        | XsDateTime     -> DataTypeDefXsd.DateTime
        | XsByteString   -> DataTypeDefXsd.HexBinary

    /// 파일 안에서만 쓰는 typed Property 헬퍼 (Core 의 mkTypedProp 은 internal).
    let private mkPropOfType (idShort: string) (dt: DataTypeDefXsd) (value: string) : ISubmodelElement =
        let p = Property(valueType = dt)
        p.IdShort <- idShort
        p.Value <- (if isNull value then "" else value)
        p :> ISubmodelElement

    let private mkPropOfXsdType idShort (t: XsdType) value =
        mkPropOfType idShort (xsdToAas t) value

    let private mkPropOpt idShort (v: string option) : ISubmodelElement option =
        v |> Option.filter (String.IsNullOrEmpty >> not) |> Option.map (mkProp idShort)

    let private mkBoolPropOpt idShort (v: bool option) : ISubmodelElement option =
        v |> Option.map (mkBoolProp idShort)

    let private mkIntPropOpt idShort (v: int option) : ISubmodelElement option =
        v |> Option.map (mkIntProp idShort)

    let private mkByteProp idShort (v: byte) =
        mkPropOfType idShort DataTypeDefXsd.UnsignedByte (string v)

    let private mkDoublePropOpt idShort (v: float option) : ISubmodelElement option =
        v |> Option.map (mkDoubleProp idShort)

    // -------------------------------------------------------------------------
    // Provenance §C — Qualifier(dualsoft:origin) + Submodel Extension(dualsoft:auto-suppressed)
    // -------------------------------------------------------------------------

    let private mkOriginQualifier (origin: string) : IQualifier =
        let q = Qualifier(``type`` = ProvenanceOriginQualifierType, valueType = DataTypeDefXsd.String)
        q.Value <- origin
        q :> IQualifier

    /// SME 에 Origin=Auto Qualifier 부착. 이미 있으면 no-op.
    let private tagAuto (elem: ISubmodelElement) : ISubmodelElement =
        let qualifiable = elem :> IQualifiable
        if qualifiable.Qualifiers = null then
            qualifiable.Qualifiers <- ResizeArray<IQualifier>()
        let hasOrigin =
            qualifiable.Qualifiers
            |> Seq.exists (fun q -> q.Type = ProvenanceOriginQualifierType)
        if not hasOrigin then
            qualifiable.Qualifiers.Add(mkOriginQualifier ProvenanceOriginAuto)
        elem

    /// idShort 가 auto-origin set 에 있으면 Qualifier(Auto) 부착, 아니면 그대로.
    let private tagIfAuto (autos: System.Collections.Generic.HashSet<string>) (elem: ISubmodelElement) : ISubmodelElement =
        if autos.Contains elem.IdShort then tagAuto elem else elem

    /// tombstones (사용자가 삭제한 auto IdShort 집합) 을 Submodel Extension 으로 부착.
    /// 값은 알파벳 정렬 · 세미콜론 구분 (결정론).
    let private attachSuppressedExtension (sm: Submodel) (suppressed: System.Collections.Generic.HashSet<string>) : unit =
        if suppressed.Count > 0 then
            let ordered = suppressed |> Seq.sortWith (fun a b -> System.String.CompareOrdinal(a, b)) |> String.concat ";"
            let ext = Extension(name = ProvenanceSuppressedExtensionName)
            ext.ValueType <- System.Nullable DataTypeDefXsd.String
            ext.Value <- ordered
            if isNull sm.Extensions then sm.Extensions <- ResizeArray<IExtension>()
            sm.Extensions.Add(ext :> IExtension)

    // -------------------------------------------------------------------------
    // AID · AssetInterfacesDescription (IDTA 02017 v1.1)
    // -------------------------------------------------------------------------

    let private signalIdProp (signalId: SignalId) =
        mkProp "signalId" signalId.Value
        |> withSemId (Some SignalIdExtensionSemanticId)

    let private opcUaInteractionSmc (i: OpcUaInteraction) : ISubmodelElement =
        let mutable value = [ mkPropOfType "type" DataTypeDefXsd.String (string (xsdToAas i.ValueType)) ]
        match i.Unit with Some u -> value <- value @ [ mkProp "unit" u ] | None -> ()
        value <- value @ [ mkProp "href" i.Href; signalIdProp i.SignalId ]
        let smc = mkSmc i.IdShort value
        smc |> withSemId (Some i.SemanticId.Value)

    let private modbusInteractionSmc (i: ModbusInteraction) : ISubmodelElement =
        let modbusFn =
            match i.Function with
            | ReadHoldingRegisters   -> "readHoldingRegisters"
            | ReadInputRegisters     -> "readInputRegisters"
            | ReadCoils              -> "readCoils"
            | ReadDiscreteInputs     -> "readDiscreteInputs"
            | WriteSingleRegister    -> "writeSingleRegister"
            | WriteMultipleRegisters -> "writeMultipleRegisters"
        let mutable value = [ mkPropOfType "type" DataTypeDefXsd.String (string (xsdToAas i.ValueType)) ]
        match i.Unit with Some u -> value <- value @ [ mkProp "unit" u ] | None -> ()
        value <- value @ [
            mkProp "href" i.Href
            mkProp "function" modbusFn
            mkBoolProp "mostSignificantWord" i.MostSignificantWord
            mkDoubleProp "scale" i.Scale
            mkDoubleProp "offset" i.Offset
            signalIdProp i.SignalId
        ]
        mkSmc i.IdShort value |> withSemId (Some i.SemanticId.Value)

    let private mqttInteractionSmc (i: MqttInteraction) : ISubmodelElement =
        let cp = match i.ControlPacket with Subscribe -> "subscribe" | Publish -> "publish"
        let mutable value = [ mkPropOfType "type" DataTypeDefXsd.String (string (xsdToAas i.ValueType)) ]
        match i.Unit with Some u -> value <- value @ [ mkProp "unit" u ] | None -> ()
        value <- value @ [
            mkProp "href" i.Href
            mkProp "controlPacket" cp
            mkIntProp "qos" i.Qos
            mkProp "contentType" i.ContentType
            mkProp "payloadPath" i.PayloadPath
            signalIdProp i.SignalId
        ]
        mkSmc i.IdShort value |> withSemId (Some i.SemanticId.Value)

    let private httpInteractionSmc (i: HttpInteraction) : ISubmodelElement =
        let httpMethod =
            match i.Method with
            | Get -> "GET" | Post -> "POST" | Put -> "PUT" | Delete -> "DELETE"
        let mutable value = [ mkPropOfType "type" DataTypeDefXsd.String (string (xsdToAas i.ValueType)) ]
        match i.Unit with Some u -> value <- value @ [ mkProp "unit" u ] | None -> ()
        value <- value @ [
            mkProp "href" i.Href
            mkProp "method" httpMethod
            mkProp "contentType" i.ContentType
            mkProp "payloadPath" i.PayloadPath
        ]
        match i.PollIntervalMs with
        | Some ms -> value <- value @ [ mkIntProp "pollIntervalMs" ms ]
        | None -> ()
        value <- value @ [ signalIdProp i.SignalId ]
        mkSmc i.IdShort value |> withSemId (Some i.SemanticId.Value)

    let private xgtInteractionSmc (i: OpcUaInteraction) : ISubmodelElement =
        let mutable value = [ mkPropOfType "type" DataTypeDefXsd.String (string (xsdToAas i.ValueType)) ]
        match i.Unit with Some u -> value <- value @ [ mkProp "unit" u ] | None -> ()
        value <- value @ [ mkProp "href" i.Href; signalIdProp i.SignalId ]
        mkSmc i.IdShort value |> withSemId (Some i.SemanticId.Value)

    let private autoIdEventSmc (e: AutoIdEventBinding) : ISubmodelElement =
        let value = [
            mkProp "eventType" e.EventType.Value
            mkProp "href" e.SourceNodeHref
            mkProp "payloadPath" e.PayloadPath
            signalIdProp e.SignalId
        ]
        mkSmc e.IdShort value |> withSemId (Some e.SemanticId.Value)

    let private endpointMetadataSmc (ep: EndpointMetadata) : ISubmodelElement =
        let mutable elems : ISubmodelElement list = [ mkProp "base" ep.Base ]
        match ep.SystemId with
        | Some systemId -> elems <- elems @ [ mkProp "systemRef" (string systemId) ]
        | None -> ()
        match ep.Security with
        | Some s when not (String.IsNullOrEmpty s) -> elems <- elems @ [ mkProp "security" s ]
        | _ -> ()
        match ep.UnitId with
        | Some u -> elems <- elems @ [ mkByteProp "unitId" u ]
        | None -> ()
        match ep.AuthReferenceVault with
        | Some v ->
            let refElem = mkProp "authReferenceVault" v
                          |> withSemId (Some VaultReferenceExtensionSemanticId)
            elems <- elems @ [ refElem ]
        | None -> ()
        mkSmc "EndpointMetadata" elems

    let private xgtEndpointMetadataSmc (ep: XgtEndpointMetadata) : ISubmodelElement =
        let cpuModel = match ep.CpuModel with Xgi -> "XGI" | Xgk -> "XGK" | Xgb -> "XGB"
        let transport = match ep.Transport with XgtTcp -> "tcp" | XgtUdp -> "udp"
        let mutable elems : ISubmodelElement list = [
            mkProp "base" ep.Base
            mkProp "cpuModel" cpuModel
            mkBoolProp "localEthernet" ep.LocalEthernet
            mkByteProp "networkNumber" ep.NetworkNumber
            mkByteProp "stationNumber" ep.StationNumber
            mkProp "transport" transport
            mkIntProp "timeoutMs" ep.TimeoutMs
            mkIntProp "scanIntervalMs" ep.ScanIntervalMs
        ]
        match ep.SystemId with
        | Some systemId -> elems <- elems @ [ mkProp "systemRef" (string systemId) ]
        | None -> ()
        match ep.AuthReferenceVault with
        | Some value ->
            elems <- elems @ [ mkProp "authReferenceVault" value |> withSemId (Some VaultReferenceExtensionSemanticId) ]
        | None -> ()
        mkSmc "EndpointMetadata" elems

    let private bindingSmc (autos: System.Collections.Generic.HashSet<string>) (binding: AidBinding) : ISubmodelElement =
        let tag = tagIfAuto autos
        match binding with
        | OpcUa (ep, interactions, events) ->
            let epSmc = endpointMetadataSmc ep
            let interSmc = mkSmc "InteractionMetadata" (interactions |> List.map (opcUaInteractionSmc >> tag))
            let elems =
                if events.IsEmpty then
                    [ epSmc; interSmc ]
                else
                    [ epSmc; interSmc; mkSmc "Events" (events |> List.map (autoIdEventSmc >> tag)) ]
            mkSmc "InterfaceOPCUA" elems
        | Modbus (ep, interactions) ->
            let epSmc = endpointMetadataSmc ep
            let interSmc = mkSmc "InteractionMetadata" (interactions |> List.map (modbusInteractionSmc >> tag))
            mkSmc "InterfaceMODBUS" [ epSmc; interSmc ]
        | Mqtt (ep, interactions) ->
            let epSmc = endpointMetadataSmc ep
            let interSmc = mkSmc "InteractionMetadata" (interactions |> List.map (mqttInteractionSmc >> tag))
            mkSmc "InterfaceMQTT" [ epSmc; interSmc ]
        | Http (ep, interactions) ->
            let epSmc = endpointMetadataSmc ep
            let interSmc = mkSmc "InteractionMetadata" (interactions |> List.map (httpInteractionSmc >> tag))
            mkSmc "InterfaceHTTP" [ epSmc; interSmc ]
        | Xgt (ep, interactions) ->
            let epSmc = xgtEndpointMetadataSmc ep
            let interSmc = mkSmc "InteractionMetadata" (interactions |> List.map (xgtInteractionSmc >> tag))
            mkSmc "InterfaceXGT" [ epSmc; interSmc ]
            |> withSemId (Some XgtInterfaceSemanticId)

    /// AAS Submodel "AssetInterfacesDescription" 생성 (IDTA 02017 v1.1).
    let aidToSubmodel (aid: AssetInterfacesDescription) (assetId: string) : Submodel =
        let sm = Submodel($"urn:dualsoft:aid:{assetId}")
        sm.IdShort <- aid.IdShort
        sm.SemanticId <- mkSemanticRef AidSubmodelSemanticId
        let bindings = aid.Interfaces |> Seq.map (bindingSmc aid.AutoOriginIdShorts) |> List.ofSeq
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>(bindings)
        attachSuppressedExtension sm aid.SuppressedAutoIdShorts
        sm

    // -------------------------------------------------------------------------
    // AIMC · AssetInterfacesMappingConfiguration (IDTA 02027 v2.0)
    // -------------------------------------------------------------------------

    let private mappingSmc (m: AimcMapping) : ISubmodelElement =
        let transform, factor, offset, expr =
            match m.Transform with
            | Identity                    -> "identity", None,       None,       None
            | LinearScale (f, o)          -> "linearScale", Some f,  Some o,     None
            | Expression src              -> "expression", None,     None,       Some src
        let value = [
            mkProp "source" m.SourceAidPath
            mkProp "sink" m.SinkAasElementPath
            mkProp "transform" transform
            yield! (mkDoublePropOpt "factor" factor |> Option.toList)
            yield! (mkDoublePropOpt "offset" offset |> Option.toList)
            yield! (mkPropOpt "expression" expr |> Option.toList)
        ]
        let idShort = KpiIdentifiers.aimcMappingIdShort m.SourceAidPath m.SinkAasElementPath
        mkSmc idShort value

    /// AAS Submodel "AssetInterfacesMappingConfiguration" 생성.
    let aimcToSubmodel (aimc: AssetInterfacesMappingConfiguration) (assetId: string) : Submodel =
        let sm = Submodel($"urn:dualsoft:aimc:{assetId}")
        sm.IdShort <- aimc.IdShort
        sm.SemanticId <- mkSemanticRef AimcSubmodelSemanticId
        // NOTE: AASd-120 로 SML 자식은 IdShort 가 null 로 초기화되지만, tagIfAuto 는 tag 부착
        // 시점(SML 진입 전) 에 원본 idShort 로 판정하므로 여기서 tag 를 붙여야 함.
        let mappingList =
            aimc.Mappings
            |> Seq.map (mappingSmc >> tagIfAuto aimc.AutoOriginIdShorts)
            |> List.ofSeq
        let mappings =
            match mkSml "Mappings" mappingList with
            | Some sml -> [ sml ]
            | None     -> [ mkSmc "Mappings" [] ]
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>(mappings)
        attachSuppressedExtension sm aimc.SuppressedAutoIdShorts
        sm

    // -------------------------------------------------------------------------
    // OperationalData (사내 발행)
    // -------------------------------------------------------------------------

    let private operationalItemSmc (item: OperationalDataItem) : ISubmodelElement =
        let valueStr =
            match item.CurrentValue with
            | Some v when not (isNull v) -> v.ToString()
            | _ -> ""
        let value = [
            mkPropOfXsdType "value" item.ValueType valueStr
            yield! (mkPropOpt "unit" item.Unit |> Option.toList)
            yield! (item.LastUpdated
                    |> Option.map (fun t -> mkPropOfType "lastUpdated" DataTypeDefXsd.DateTime (t.ToString("O", CultureInfo.InvariantCulture)))
                    |> Option.toList)
        ]
        mkSmc item.IdShort value |> withSemId (Some item.SemanticId.Value)

    let operationalDataToSubmodel (od: OperationalData) (assetId: string) : Submodel =
        let sm = Submodel($"urn:dualsoft:operational:{assetId}")
        sm.IdShort <- od.IdShort
        sm.SemanticId <- mkSemanticRef OperationalDataSubmodelSemanticId
        sm.SubmodelElements <-
            ResizeArray<ISubmodelElement>(
                od.Items
                |> List.ofSeq
                |> List.map (operationalItemSmc >> tagIfAuto od.AutoOriginIdShorts))
        attachSuppressedExtension sm od.SuppressedAutoIdShorts
        sm

    // -------------------------------------------------------------------------
    // TimeSeries (IDTA 02008 v1.1) - external Collector LinkedSegments
    // -------------------------------------------------------------------------

    type private TimeSeriesSignal = {
        IdShort: string
        SignalId: SignalId
        SemanticId: SemanticId
        ValueType: XsdType
        Unit: string option
    }

    let private aidTimeSeriesSignals (aid: AssetInterfacesDescription) = [
        for binding in aid.Interfaces do
            match binding with
            | OpcUa (_, interactions, _)
            | Xgt (_, interactions) ->
                for interaction in interactions do
                    yield {
                        IdShort = interaction.IdShort
                        SignalId = interaction.SignalId
                        SemanticId = interaction.SemanticId
                        ValueType = interaction.ValueType
                        Unit = interaction.Unit
                    }
            | Modbus (_, interactions) ->
                for interaction in interactions do
                    yield {
                        IdShort = interaction.IdShort
                        SignalId = interaction.SignalId
                        SemanticId = interaction.SemanticId
                        ValueType = interaction.ValueType
                        Unit = interaction.Unit
                    }
            | Mqtt (_, interactions) ->
                for interaction in interactions do
                    yield {
                        IdShort = interaction.IdShort
                        SignalId = interaction.SignalId
                        SemanticId = interaction.SemanticId
                        ValueType = interaction.ValueType
                        Unit = interaction.Unit
                    }
            | Http (_, interactions) ->
                for interaction in interactions do
                    yield {
                        IdShort = interaction.IdShort
                        SignalId = interaction.SignalId
                        SemanticId = interaction.SemanticId
                        ValueType = interaction.ValueType
                        Unit = interaction.Unit
                    }
    ]

    let private linkedSegmentIdShort (signalId: SignalId) =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(signalId.Value))
        "LinkedSegment_" + Convert.ToHexString(bytes.AsSpan(0, 8))

    let private variableMetadataSmc (signal: TimeSeriesSignal) =
        let elements = [
            yield mkProp "RecordId" signal.SignalId.Value
                |> withSemId (Some TimeSeriesIdExtensionSemanticId)
            yield mkPropOfXsdType "Value" signal.ValueType ""
                |> withSemId (Some signal.SemanticId.Value)
            match signal.Unit with
            | Some unitName when not (String.IsNullOrWhiteSpace unitName) ->
                yield mkProp "Unit" unitName
            | _ -> ()
        ]
        mkSmc ("Variable_" + linkedSegmentIdShort signal.SignalId) elements

    let private linkedSegmentSmc
        (assetId: GlobalAssetId)
        (dataApiEndpoint: string)
        (signal: TimeSeriesSignal) =
        let seriesId = AssetTelemetryIdentity.seriesId assetId signal.SignalId
        let query = "seriesId=" + Uri.EscapeDataString(seriesId)
        mkSmc (linkedSegmentIdShort signal.SignalId) [
            mkMlp "Name" signal.IdShort
            mkMlp "Description" (sprintf "Collector history for %s" signal.SignalId.Value)
            mkProp "Endpoint" dataApiEndpoint
                |> withSemId (Some TimeSeriesEndpointSemanticId)
            mkProp "Query" query
                |> withSemId (Some TimeSeriesQuerySemanticId)
            mkProp "SeriesId" seriesId
                |> withSemId (Some TimeSeriesIdExtensionSemanticId)
            signalIdProp signal.SignalId
        ]
        |> withSemId (Some TimeSeriesLinkedSegmentSemanticId)

    /// Creates static external-history access points. The endpoint is the full
    /// Data API series URL; callers add fromUs/toUs to the stored query.
    let timeSeriesToSubmodel
        (aid: AssetInterfacesDescription)
        (assetId: GlobalAssetId)
        (ownerId: Guid)
        (ownerName: string)
        (dataApiEndpoint: string) : Submodel =
        if String.IsNullOrWhiteSpace dataApiEndpoint then
            invalidArg "dataApiEndpoint" "Data API endpoint must not be empty"
        let signals =
            aidTimeSeriesSignals aid
            |> List.filter (fun signal -> not (String.IsNullOrWhiteSpace signal.SignalId.Value))
            |> List.distinctBy (fun signal -> signal.SignalId.Value)
            |> List.sortBy (fun signal -> signal.SignalId.Value)
        let metadata =
            mkSmc "Metadata" [
                mkMlp "Name" ownerName
                mkSmc "Record" (
                    [ mkPropOfType "UtcTime" DataTypeDefXsd.DateTime "" ]
                    @ (signals |> List.map variableMetadataSmc))
            ]
            |> withSemId (Some TimeSeriesMetadataSemanticId)
        let segments =
            mkSmc "Segments" (signals |> List.map (linkedSegmentSmc assetId dataApiEndpoint))
            |> withSemId (Some TimeSeriesSegmentsSemanticId)
        let sm = Submodel(sprintf "urn:dualsoft:timeseries:%s" (ownerId.ToString("N")))
        sm.IdShort <- TimeSeriesSubmodelIdShort
        sm.SemanticId <- mkSemanticRef TimeSeriesSubmodelSemanticId
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>([ metadata; segments ])
        sm

    // -------------------------------------------------------------------------
    // SequenceLogging 확장 — SignalPolicies SMC
    //   기존 SequenceLogging exporter 가 build 하는 Submodel 위에 이 헬퍼로 필드 추가.
    // -------------------------------------------------------------------------

    let signalPolicyToSmc (p: SignalPolicy) : ISubmodelElement =
        let mode =
            match p.AcquisitionMode with
            | AcquisitionMode.Sampled       -> "sampled"
            | AcquisitionMode.ChangeOfValue -> "changeOfValue"
            | AcquisitionMode.EventDriven   -> "eventDriven"
        let value = [
            signalIdProp p.SignalId
            mkProp "acquisitionMode" mode |> withSemId (Some SignalAcquisitionModeSemanticId)
            yield! (mkIntPropOpt "samplingIntervalMs" p.SamplingIntervalMs |> Option.toList)
            yield! (mkIntPropOpt "publishingIntervalMs" p.PublishingIntervalMs |> Option.toList)
            yield! (mkDoublePropOpt "deadbandAbsolute" p.DeadbandAbsolute |> Option.toList)
            yield! (mkDoublePropOpt "deadbandPercent" p.DeadbandPercent |> Option.toList)
            yield! (mkDoublePropOpt "engineeringRangeLow" p.EngineeringRangeLow |> Option.toList)
            yield! (mkDoublePropOpt "engineeringRangeHigh" p.EngineeringRangeHigh |> Option.toList)
            yield! (mkIntPropOpt "queueSize" p.QueueSize |> Option.toList)
            mkProp "retention" p.Retention
        ]
        mkSmc (sprintf "Policy_%s" (p.SignalId.Value.Replace('.', '_').Replace('-', '_'))) value

    /// SignalPoliciesCollection 하나를 만든다. 비어 있으면 요소를 만들지 않는다.
    /// SequenceLogging의 System_<guid> 안과 구 top-level 호환 경로가 같은 표현을 공유한다.
    let signalPoliciesCollectionToSmc (policies: SignalPolicy seq) : ISubmodelElement option =
        let items = policies |> Seq.toList
        if items.IsEmpty then None
        else
            for policy in items do
                match SignalPolicy.validate policy with
                | Ok () -> ()
                | Error message -> invalidArg "policies" $"{policy.SignalId.Value}: {message}"
            mkSmc "SignalPoliciesCollection" (items |> List.map signalPolicyToSmc)
            |> withSemId (Some SignalPoliciesCollectionSemanticId)
            |> Some

    /// SequenceLogging Submodel 안에 SignalPolicies SMC 를 추가.
    let attachSignalPoliciesToLogging (loggingSm: Submodel) (policies: SignalPolicy seq) : unit =
        match signalPoliciesCollectionToSmc policies with
        | Some smc ->
            if isNull loggingSm.SubmodelElements then
                loggingSm.SubmodelElements <- ResizeArray<ISubmodelElement>()
            loggingSm.SubmodelElements.Add(smc)
        | None -> ()
