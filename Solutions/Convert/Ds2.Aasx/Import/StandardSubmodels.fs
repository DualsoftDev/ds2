namespace Ds2.Aasx

open System
open System.Globalization
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Core.Kpi
open Ds2.Core.StandardSubmodels
open Ds2.Aasx.AasxSemantics

/// 사내 표준 서브모델 (AID / AIMC / OperationalData / SignalPolicy) 의 AAS Submodel 변환 (Import).
///
/// 매칭 우선순위 (v1 결함 정정): **semanticId > idShort**.
/// semanticId 가 있으면 우선 매칭하고, 없거나 못 찾으면 idShort fallback.
module AasxImportStandardSubmodels =

    open AasxImportCore

    // -------------------------------------------------------------------------
    // 공통 헬퍼
    // -------------------------------------------------------------------------

    let private aasToXsd (dt: DataTypeDefXsd) : XsdType =
        match dt with
        | DataTypeDefXsd.Double        -> XsDouble
        | DataTypeDefXsd.Float         -> XsFloat
        | DataTypeDefXsd.Int           -> XsInt
        | DataTypeDefXsd.Long          -> XsLong
        | DataTypeDefXsd.UnsignedInt   -> XsUnsignedInt
        | DataTypeDefXsd.UnsignedLong  -> XsUnsignedLong
        | DataTypeDefXsd.Boolean       -> XsBoolean
        | DataTypeDefXsd.String        -> XsString
        | DataTypeDefXsd.DateTime      -> XsDateTime
        | DataTypeDefXsd.HexBinary
        | DataTypeDefXsd.Base64Binary  -> XsByteString
        | _                            -> XsString

    /// SMC 안의 자식 요소 중 idShort 로 SMC 찾기.
    let private findSmc (smc: SubmodelElementCollection) (idShort: string) : SubmodelElementCollection option =
        if isNull smc.Value then None
        else
            smc.Value |> Seq.tryPick (function
                | :? SubmodelElementCollection as c when c.IdShort = idShort -> Some c
                | _ -> None)

    /// Submodel 안의 최상위 SMC 를 idShort 로 찾기.
    let private findTopSmc (sm: Submodel) (idShort: string) : SubmodelElementCollection option =
        if isNull sm.SubmodelElements then None
        else
            sm.SubmodelElements |> Seq.tryPick (function
                | :? SubmodelElementCollection as c when c.IdShort = idShort -> Some c
                | _ -> None)

    let private findAllChildrenSmc (smc: SubmodelElementCollection) : SubmodelElementCollection list =
        if isNull smc.Value then []
        else
            smc.Value
            |> Seq.choose (function :? SubmodelElementCollection as c -> Some c | _ -> None)
            |> List.ofSeq

    /// SMC 안의 Property `idShort` 값 (string).
    let private propStr (smc: SubmodelElementCollection) (idShort: string) : string =
        getProp smc idShort |> Option.defaultValue ""

    let private propOpt (smc: SubmodelElementCollection) (idShort: string) : string option =
        getProp smc idShort

    let private propBool (smc: SubmodelElementCollection) (idShort: string) : bool =
        match getProp smc idShort with
        | Some v ->
            match Boolean.TryParse v with
            | true, b -> b
            | _ -> false
        | None -> false

    let private propInt (smc: SubmodelElementCollection) (idShort: string) : int option =
        getProp smc idShort
        |> Option.bind (fun s ->
            match Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None)

    let private propDouble (smc: SubmodelElementCollection) (idShort: string) : float option =
        getProp smc idShort
        |> Option.bind (fun s ->
            match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None)

    let private propByte (smc: SubmodelElementCollection) (idShort: string) : byte option =
        getProp smc idShort
        |> Option.bind (fun s ->
            match Byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None)

    let private propDateTime (smc: SubmodelElementCollection) (idShort: string) : DateTimeOffset option =
        getProp smc idShort
        |> Option.bind (fun s ->
            match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, dt -> Some dt
            | _ -> None)

    let private semanticIdOf (elem: ISubmodelElement) : SemanticId =
        match elem.SemanticId with
        | null -> SemanticId ""
        | r ->
            if isNull r.Keys || r.Keys.Count = 0 then SemanticId ""
            else SemanticId (r.Keys.[0].Value)

    let private xsdOfString (s: string) : XsdType =
        // `Double` 문자열 → DataTypeDefXsd.Double 로 파싱 후 우리 XsdType 으로 매핑.
        match Enum.TryParse<DataTypeDefXsd>(s, ignoreCase = true) with
        | true, dt -> aasToXsd dt
        | _ -> XsString

    /// Resolved external-history access point carried by a TimeSeries
    /// LinkedSegment. Query is retained verbatim for forward compatibility.
    type LinkedSeriesAccess = {
        SeriesId: string
        SignalId: string
        Endpoint: string
        Query: string
    }

    let private queryValue (name: string) (query: string) =
        if String.IsNullOrWhiteSpace query then None
        else
            query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> Seq.tryPick (fun pair ->
                let parts = pair.Split('=', 2)
                if parts.Length = 2 && String.Equals(Uri.UnescapeDataString parts.[0], name, StringComparison.Ordinal) then
                    Some(Uri.UnescapeDataString parts.[1])
                else None)

    /// Reads approved Data API series from IDTA 02008 LinkedSegments.
    let linkedSeriesFromTimeSeries (sm: Submodel) : LinkedSeriesAccess list =
        match findTopSmc sm "Segments" with
        | None -> []
        | Some segments ->
            findAllChildrenSmc segments
            |> List.choose (fun linked ->
                let endpoint = propStr linked "Endpoint"
                let query = propStr linked "Query"
                let seriesId =
                    propOpt linked "SeriesId"
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.orElseWith (fun () -> queryValue "seriesId" query)
                    |> Option.defaultValue ""
                let signalId = propStr linked "signalId"
                if String.IsNullOrWhiteSpace endpoint || String.IsNullOrWhiteSpace seriesId then None
                else Some {
                    SeriesId = seriesId
                    SignalId = signalId
                    Endpoint = endpoint
                    Query = query
                })

    // -------------------------------------------------------------------------
    // Provenance §C — Qualifier(dualsoft:origin) + Submodel Extension(dualsoft:auto-suppressed)
    // -------------------------------------------------------------------------

    /// SME 의 Qualifier(dualsoft:origin) 값이 Auto 인지 검사. Qualifier 없거나 다른 값이면 false.
    let private isAutoOrigin (elem: ISubmodelElement) : bool =
        let qualifiable = elem :> IQualifiable
        if isNull qualifiable.Qualifiers then false
        else
            qualifiable.Qualifiers
            |> Seq.exists (fun q ->
                q.Type = ProvenanceOriginQualifierType
                && q.Value = ProvenanceOriginAuto)

    /// Submodel 의 dualsoft:auto-suppressed Extension 값을 파싱 (`;` 구분 IdShort 목록).
    let private parseSuppressedExtension (sm: Submodel) : string seq =
        if isNull sm.Extensions then Seq.empty
        else
            sm.Extensions
            |> Seq.tryFind (fun e -> e.Name = ProvenanceSuppressedExtensionName)
            |> function
            | None -> Seq.empty
            | Some ext when isNull ext.Value -> Seq.empty
            | Some ext ->
                ext.Value.Split(';')
                |> Seq.map (fun s -> s.Trim())
                |> Seq.filter (fun s -> s.Length > 0)

    // -------------------------------------------------------------------------
    // AID · AssetInterfacesDescription
    // -------------------------------------------------------------------------

    let private endpointFromSmc (smc: SubmodelElementCollection) : EndpointMetadata =
        {
            Base = propStr smc "base"
            Security = propOpt smc "security"
            UnitId = propByte smc "unitId"
            AuthReferenceVault = propOpt smc "authReferenceVault"
        }

    let private opcUaInteractionFromSmc (smc: SubmodelElementCollection) : OpcUaInteraction =
        {
            IdShort = smc.IdShort
            SemanticId = semanticIdOf smc
            ValueType = propStr smc "type" |> xsdOfString
            Unit = propOpt smc "unit"
            Href = propStr smc "href"
            SignalId = SignalId (propStr smc "signalId")
        }

    let private modbusInteractionFromSmc (smc: SubmodelElementCollection) : ModbusInteraction =
        let fn =
            match propStr smc "function" with
            | "readHoldingRegisters"   -> ReadHoldingRegisters
            | "readInputRegisters"     -> ReadInputRegisters
            | "readCoils"              -> ReadCoils
            | "readDiscreteInputs"     -> ReadDiscreteInputs
            | "writeSingleRegister"    -> WriteSingleRegister
            | "writeMultipleRegisters" -> WriteMultipleRegisters
            | _                        -> ReadHoldingRegisters
        {
            IdShort = smc.IdShort
            SemanticId = semanticIdOf smc
            ValueType = propStr smc "type" |> xsdOfString
            Unit = propOpt smc "unit"
            Href = propStr smc "href"
            Function = fn
            MostSignificantWord = propBool smc "mostSignificantWord"
            Scale = propDouble smc "scale" |> Option.defaultValue 1.0
            Offset = propDouble smc "offset" |> Option.defaultValue 0.0
            SignalId = SignalId (propStr smc "signalId")
        }

    let private mqttInteractionFromSmc (smc: SubmodelElementCollection) : MqttInteraction =
        let cp =
            match propStr smc "controlPacket" with
            | "publish" -> Publish
            | _         -> Subscribe
        {
            IdShort = smc.IdShort
            SemanticId = semanticIdOf smc
            ValueType = propStr smc "type" |> xsdOfString
            Unit = propOpt smc "unit"
            Href = propStr smc "href"
            ControlPacket = cp
            Qos = propInt smc "qos" |> Option.defaultValue 0
            ContentType = propStr smc "contentType"
            PayloadPath = propStr smc "payloadPath"
            SignalId = SignalId (propStr smc "signalId")
        }

    let private httpInteractionFromSmc (smc: SubmodelElementCollection) : HttpInteraction =
        let m =
            match propStr smc "method" with
            | "POST"   -> Post
            | "PUT"    -> Put
            | "DELETE" -> Delete
            | _        -> Get
        {
            IdShort = smc.IdShort
            SemanticId = semanticIdOf smc
            ValueType = propStr smc "type" |> xsdOfString
            Unit = propOpt smc "unit"
            Href = propStr smc "href"
            Method = m
            ContentType = propStr smc "contentType"
            PayloadPath = propStr smc "payloadPath"
            PollIntervalMs = propInt smc "pollIntervalMs"
            SignalId = SignalId (propStr smc "signalId")
        }

    let private xgtInteractionFromSmc (smc: SubmodelElementCollection) : OpcUaInteraction =
        {
            IdShort = smc.IdShort
            SemanticId = semanticIdOf smc
            ValueType = propStr smc "type" |> xsdOfString
            Unit = propOpt smc "unit"
            Href = propStr smc "href"
            SignalId = SignalId (propStr smc "signalId")
        }

    let private xgtEndpointFromSmc (smc: SubmodelElementCollection) : XgtEndpointMetadata =
        {
            Base = propStr smc "base"
            CpuModel =
                match propStr smc "cpuModel" with
                | "XGK" -> Xgk
                | "XGB" -> Xgb
                | _ -> Xgi
            LocalEthernet = propBool smc "localEthernet"
            NetworkNumber = propByte smc "networkNumber" |> Option.defaultValue 0uy
            StationNumber = propByte smc "stationNumber" |> Option.defaultValue 0xFFuy
            Transport = if propStr smc "transport" = "udp" then XgtUdp else XgtTcp
            TimeoutMs = propInt smc "timeoutMs" |> Option.defaultValue 3000
            ScanIntervalMs = propInt smc "scanIntervalMs" |> Option.defaultValue 100
            AuthReferenceVault = propOpt smc "authReferenceVault"
        }

    let private autoIdFromSmc (smc: SubmodelElementCollection) : AutoIdEventBinding =
        {
            IdShort = smc.IdShort
            SemanticId = semanticIdOf smc
            EventType = SemanticId (propStr smc "eventType")
            SourceNodeHref = propStr smc "href"
            PayloadPath = propStr smc "payloadPath"
            SignalId = SignalId (propStr smc "signalId")
        }

    let private bindingFromSmc (autos: System.Collections.Generic.HashSet<string>) (bindingSmc: SubmodelElementCollection) : AidBinding option =
        let endpoint =
            findSmc bindingSmc "EndpointMetadata"
            |> Option.map endpointFromSmc
            |> Option.defaultValue EndpointMetadata.empty
        let interactionSmcs =
            findSmc bindingSmc "InteractionMetadata"
            |> Option.map findAllChildrenSmc
            |> Option.defaultValue []
        for c in interactionSmcs do
            if isAutoOrigin (c :> ISubmodelElement) then autos.Add(c.IdShort) |> ignore
        match bindingSmc.IdShort with
        | "InterfaceOPCUA" ->
            let eventSmcs =
                findSmc bindingSmc "Events"
                |> Option.map findAllChildrenSmc
                |> Option.defaultValue []
            for c in eventSmcs do
                if isAutoOrigin (c :> ISubmodelElement) then autos.Add(c.IdShort) |> ignore
            Some (OpcUa (endpoint,
                         interactionSmcs |> List.map opcUaInteractionFromSmc,
                         eventSmcs |> List.map autoIdFromSmc))
        | "InterfaceMODBUS" ->
            Some (Modbus (endpoint, interactionSmcs |> List.map modbusInteractionFromSmc))
        | "InterfaceMQTT" ->
            Some (Mqtt (endpoint, interactionSmcs |> List.map mqttInteractionFromSmc))
        | "InterfaceHTTP" ->
            Some (Http (endpoint, interactionSmcs |> List.map httpInteractionFromSmc))
        | "InterfaceXGT" ->
            let xgtEndpoint =
                findSmc bindingSmc "EndpointMetadata"
                |> Option.map xgtEndpointFromSmc
                |> Option.defaultValue XgtEndpointMetadata.empty
            Some (Xgt (xgtEndpoint, interactionSmcs |> List.map xgtInteractionFromSmc))
        | _ -> None

    /// Submodel → AssetInterfacesDescription 도메인 값.
    let submodelToAid (sm: Submodel) : AssetInterfacesDescription =
        let aid = AssetInterfacesDescription()
        aid.IdShort <- (if isNull sm.IdShort then aid.IdShort else sm.IdShort)
        if not (isNull sm.SubmodelElements) then
            for elem in sm.SubmodelElements do
                match elem with
                | :? SubmodelElementCollection as c ->
                    match bindingFromSmc aid.AutoOriginIdShorts c with
                    | Some b -> aid.Interfaces.Add b
                    | None   -> ()
                | _ -> ()
        for s in parseSuppressedExtension sm do aid.SuppressedAutoIdShorts.Add(s) |> ignore
        aid

    // -------------------------------------------------------------------------
    // AIMC
    // -------------------------------------------------------------------------

    let private mappingFromSmc (smc: SubmodelElementCollection) : AimcMapping =
        let source = propStr smc "source"
        let sink = propStr smc "sink"
        let transform =
            match propStr smc "transform" with
            | "linearScale" ->
                LinearScale (propDouble smc "factor" |> Option.defaultValue 1.0,
                             propDouble smc "offset" |> Option.defaultValue 0.0)
            | "expression" ->
                Expression (propStr smc "expression")
            | _ -> Identity
        { SourceAidPath = source; SinkAasElementPath = sink; Transform = transform }

    let submodelToAimc (sm: Submodel) : AssetInterfacesMappingConfiguration =
        let aimc = AssetInterfacesMappingConfiguration()
        aimc.IdShort <- (if isNull sm.IdShort then aimc.IdShort else sm.IdShort)
        // SML 자식은 AASd-120 로 idShort 가 null — Auto 판정만 Qualifier 기반, 저장은 재계산된 SME idShort.
        let recordMapping (elem: ISubmodelElement) (m: AimcMapping) =
            let smeIdShort = KpiIdentifiers.aimcMappingIdShort m.SourceAidPath m.SinkAasElementPath
            aimc.Mappings.Add(m)
            if isAutoOrigin elem then aimc.AutoOriginIdShorts.Add(smeIdShort) |> ignore
        if not (isNull sm.SubmodelElements) then
            for elem in sm.SubmodelElements do
                match elem with
                | :? SubmodelElementList as l when l.IdShort = "Mappings" ->
                    if not (isNull l.Value) then
                        for child in l.Value do
                            match child with
                            | :? SubmodelElementCollection as c ->
                                recordMapping (c :> ISubmodelElement) (mappingFromSmc c)
                            | _ -> ()
                | :? SubmodelElementCollection as c when c.IdShort = "Mappings" ->
                    for child in findAllChildrenSmc c do
                        recordMapping (child :> ISubmodelElement) (mappingFromSmc child)
                | _ -> ()
        for s in parseSuppressedExtension sm do aimc.SuppressedAutoIdShorts.Add(s) |> ignore
        aimc

    // -------------------------------------------------------------------------
    // OperationalData
    // -------------------------------------------------------------------------

    let private operationalItemFromSmc (smc: SubmodelElementCollection) : OperationalDataItem =
        let item = OperationalDataItem()
        item.IdShort <- smc.IdShort
        item.SemanticId <- semanticIdOf smc
        // Property.ValueType 을 직접 조회 · CurrentValue 는 원문 문자열 그대로
        if not (isNull smc.Value) then
            for e in smc.Value do
                match e with
                | :? Property as p when p.IdShort = "value" ->
                    item.ValueType <- aasToXsd p.ValueType
                    item.CurrentValue <- Some (box p.Value)
                | _ -> ()
        item.Unit <- propOpt smc "unit"
        item.LastUpdated <- propDateTime smc "lastUpdated"
        item

    let submodelToOperationalData (sm: Submodel) : OperationalData =
        let od = OperationalData()
        od.IdShort <- (if isNull sm.IdShort then od.IdShort else sm.IdShort)
        if not (isNull sm.SubmodelElements) then
            for elem in sm.SubmodelElements do
                match elem with
                | :? SubmodelElementCollection as c ->
                    od.Items.Add(operationalItemFromSmc c)
                    if isAutoOrigin (c :> ISubmodelElement) then od.AutoOriginIdShorts.Add(c.IdShort) |> ignore
                | _ -> ()
        for s in parseSuppressedExtension sm do od.SuppressedAutoIdShorts.Add(s) |> ignore
        od

    // -------------------------------------------------------------------------
    // SequenceLogging.SignalPolicies 추출 (역방향 헬퍼)
    // -------------------------------------------------------------------------

    let signalPolicyFromSmc (smc: SubmodelElementCollection) : SignalPolicy =
        let mode =
            match propStr smc "acquisitionMode" with
            | "sampled"     -> AcquisitionMode.Sampled
            | "eventDriven" -> AcquisitionMode.EventDriven
            | _             -> AcquisitionMode.ChangeOfValue
        {
            SignalId = SignalId (propStr smc "signalId")
            AcquisitionMode = mode
            SamplingIntervalMs = propInt smc "samplingIntervalMs"
            PublishingIntervalMs = propInt smc "publishingIntervalMs"
            DeadbandAbsolute = propDouble smc "deadbandAbsolute"
            DeadbandPercent = propDouble smc "deadbandPercent"
            EngineeringRangeLow = propDouble smc "engineeringRangeLow"
            EngineeringRangeHigh = propDouble smc "engineeringRangeHigh"
            QueueSize = propInt smc "queueSize"
            Retention = propStr smc "retention"
        }

    /// SequenceLogging Submodel 안의 SignalPoliciesCollection SMC 에서 정책 리스트 추출.
    let signalPoliciesFromLogging (loggingSm: Submodel) : SignalPolicy list =
        match findTopSmc loggingSm "SignalPoliciesCollection" with
        | Some smc ->
            findAllChildrenSmc smc |> List.map signalPolicyFromSmc
        | None -> []

    /// 신규 정식 표현: SequenceLogging/SystemProperties/System_<guid> 아래 정책 복원.
    /// 시스템별 소유권을 유지하므로 여러 active system에서도 정책이 섞이지 않는다.
    let signalPoliciesFromSystemProperties (systemSmc: SubmodelElementCollection) : SignalPolicy list =
        match findSmc systemSmc "SignalPoliciesCollection" with
        | Some smc -> findAllChildrenSmc smc |> List.map signalPolicyFromSmc
        | None -> []
