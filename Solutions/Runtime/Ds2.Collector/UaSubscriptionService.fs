namespace Ds2.Collector

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography.X509Certificates
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Opc.Ua
open Opc.Ua.Client
open Opc.Ua.Configuration
open Ds2.Adapter.Common
open Ds2.Collector.DataApi
open Ds2.Collector.Sinks
open Ds2.Core
open Ds2.Core.Encoding

type UaSubscriptionOptions = {
    Enabled: bool
    EndpointUrl: string
    DataRoot: string
    UseSecurity: bool
    AutoAcceptUntrustedCertificates: bool
    UseCertificateIdentity: bool
    PairLocalCertificates: bool
    PairedServerCertificateRoot: string
    PairedServerApplicationUri: string
    SamplingIntervalMs: int
    PublishingIntervalMs: int
    ReconnectDelayMs: int
}

module UaSubscriptionOptions =
    let private boolEnv name fallback =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> fallback
        | value ->
            match Boolean.TryParse value with
            | true, parsed -> parsed
            | _ -> fallback

    let private intEnv name fallback minimum =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> fallback
        | value ->
            match Int32.TryParse value with
            | true, parsed -> max minimum parsed
            | _ -> fallback

    let fromEnvironment dataRoot = {
        Enabled = boolEnv "DS2_UA_SUBSCRIBE_ENABLED" true
        EndpointUrl =
            match Environment.GetEnvironmentVariable "DS2_UA_ENDPOINT" with
            | null | "" -> "opc.tcp://localhost:62541/Ds2/OpcUa/Server"
            | value -> value.Trim()
        DataRoot = dataRoot
        UseSecurity = boolEnv "DS2_UA_USE_SECURITY" true
        AutoAcceptUntrustedCertificates = boolEnv "DS2_UA_AUTO_ACCEPT_UNTRUSTED" false
        UseCertificateIdentity = boolEnv "DS2_UA_USE_CERTIFICATE_IDENTITY" true
        PairLocalCertificates = boolEnv "DS2_UA_PAIR_LOCAL_CERTIFICATES" true
        PairedServerCertificateRoot =
            match Environment.GetEnvironmentVariable "DS2_UA_PAIRED_SERVER_CERT_ROOT" with
            | null | "" ->
                let sharedRoot =
                    match Environment.GetEnvironmentVariable "DUALSOFT_SHARED_DIR" with
                    | null | "" when OperatingSystem.IsWindows() ->
                        Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                            "DualSoft", "Shared")
                    | null | "" -> "/var/lib/dualsoft/Shared"
                    | value -> value.Trim()
                Path.Combine(sharedRoot, "agent", "opcua", "certs")
            | value -> value.Trim()
        PairedServerApplicationUri =
            match Environment.GetEnvironmentVariable "DS2_UA_PAIRED_SERVER_APPLICATION_URI" with
            | null | "" -> "urn:dualsoft:promaker-agent:opcua"
            | value -> value.Trim()
        SamplingIntervalMs = intEnv "DS2_UA_SAMPLING_MS" 200 50
        PublishingIntervalMs = intEnv "DS2_UA_PUBLISHING_MS" 500 50
        ReconnectDelayMs = intEnv "DS2_UA_RECONNECT_MS" 3000 250
    }

    let validate (options: UaSubscriptionOptions) =
        match Uri.TryCreate(options.EndpointUrl, UriKind.Absolute) with
        | false, _ -> invalidOp "DS2_UA_ENDPOINT must be an absolute opc.tcp:// URI."
        | true, uri when not (String.Equals(uri.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase)) ->
            invalidOp "DS2_UA_ENDPOINT must use the opc.tcp:// scheme."
        | true, uri ->
            let insecureLocalOptIn = boolEnv "DS2_UA_INSECURE_LOCAL_DEV" false
            if not options.UseSecurity && (not uri.IsLoopback || not insecureLocalOptIn) then
                invalidOp "Unsecured Collector OPC UA is restricted to explicit loopback development mode."
            if options.AutoAcceptUntrustedCertificates && (not uri.IsLoopback || not insecureLocalOptIn) then
                invalidOp "Automatic OPC UA certificate acceptance is restricted to explicit loopback development mode."
            if not uri.IsLoopback && not options.UseCertificateIdentity then
                invalidOp "Remote Collector OPC UA requires certificate user identity."
            if options.PairLocalCertificates && not uri.IsLoopback then
                invalidOp "DS2_UA_PAIR_LOCAL_CERTIFICATES can only be used with a loopback Agent endpoint."
            options

type UaSignalNode = {
    NodeId: NodeId
    GlobalAssetId: GlobalAssetId
    SignalId: SignalId
    Unit: string option
    Policy: UaCollectionPolicy option
}

and UaCollectionPolicy = {
    AcquisitionMode: AcquisitionMode
    SamplingIntervalMs: int option
    PublishingIntervalMs: int option
    DeadbandAbsolute: float option
    DeadbandPercent: float option
    EngineeringRangeLow: float option
    EngineeringRangeHigh: float option
    QueueSize: int option
    Retention: string
}

type UaEventNode = {
    NodeId: NodeId
}

type UaMonitoredItemSettings = {
    SamplingIntervalMs: int
    PublishingIntervalMs: int
    QueueSize: uint32
    Trigger: DataChangeTrigger
    DeadbandType: uint32
    DeadbandValue: float
}

module UaSubscription =

    [<Literal>]
    let MaxEventPayloadBytes = 262_144
    let private assetNamespacePrefix = "urn:ds:asset:"

    let isAcceptedEndpointSecurity useSecurity (endpoint: EndpointDescription) =
        if not useSecurity then endpoint.SecurityMode = MessageSecurityMode.None
        else
            endpoint.SecurityMode = MessageSecurityMode.SignAndEncrypt
            && (endpoint.SecurityPolicyUri = SecurityPolicies.Basic256Sha256
                || endpoint.SecurityPolicyUri = SecurityPolicies.Aes128_Sha256_RsaOaep
                || endpoint.SecurityPolicyUri = SecurityPolicies.Aes256_Sha256_RsaPss)

    let tryGlobalAssetId (namespaceUri: string) =
        if String.IsNullOrWhiteSpace namespaceUri ||
           not (namespaceUri.StartsWith(assetNamespacePrefix, StringComparison.Ordinal)) then None
        else
            try
                let encoded = namespaceUri.Substring(assetNamespacePrefix.Length)
                Some (GlobalAssetId(Base64Url.decode encoded))
            with _ -> None

    let toSampleValue (value: obj) =
        match value with
        | null -> ValueNone
        | :? bool as value -> ValueBool value
        | :? byte as value -> ValueLong(int64 value)
        | :? sbyte as value -> ValueLong(int64 value)
        | :? int16 as value -> ValueLong(int64 value)
        | :? uint16 as value -> ValueLong(int64 value)
        | :? int as value -> ValueLong(int64 value)
        | :? uint32 as value -> ValueLong(int64 value)
        | :? int64 as value -> ValueLong value
        | :? uint64 as value when value <= uint64 Int64.MaxValue -> ValueLong(int64 value)
        | :? float32 as value -> ValueDouble(float value)
        | :? float as value -> ValueDouble value
        | :? decimal as value -> ValueDouble(float value)
        | :? string as value -> ValueString value
        | value -> ValueString(Convert.ToString(value, CultureInfo.InvariantCulture))

    let private browseChildren (session: Session) (nodeId: NodeId) =
        let descriptions =
            BrowseDescriptionCollection([|
                BrowseDescription(
                    NodeId = nodeId,
                    BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = NodeId.Null,
                    IncludeSubtypes = true,
                    NodeClassMask = 0u,
                    ResultMask = uint32 BrowseResultMask.All)
            |])
        let mutable results = BrowseResultCollection()
        let mutable diagnostics = DiagnosticInfoCollection()
        session.Browse(null, null, 0u, descriptions, &results, &diagnostics) |> ignore
        if results.Count = 0 || StatusCode.IsBad results.[0].StatusCode then Seq.empty
        else results.[0].References :> seq<ReferenceDescription>

    let private tryConvert<'T> (value: obj) =
        try Some(Convert.ChangeType(value, typeof<'T>, CultureInfo.InvariantCulture) :?> 'T)
        with _ -> None

    let private readCollectionPolicy (session: Session) (signalNodeId: NodeId) : UaCollectionPolicy option =
        let values = Dictionary<string, obj>(StringComparer.Ordinal)
        for reference in browseChildren session signalNodeId do
            if reference.NodeClass = NodeClass.Variable then
                let childId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris)
                if not (isNull childId) then
                    let value = session.ReadValue(childId)
                    if not (isNull value) && StatusCode.IsGood value.StatusCode && not (isNull value.Value) then
                        values.[reference.BrowseName.Name] <- value.Value

        let tryGet name =
            match values.TryGetValue name with
            | true, value -> Some value
            | _ -> None
        let intValue name = tryGet name |> Option.bind tryConvert<int>
        let floatValue name = tryGet name |> Option.bind tryConvert<float>
        let stringValue name = tryGet name |> Option.map (fun value -> Convert.ToString(value, CultureInfo.InvariantCulture))
        match stringValue SignalPolicyUaMetadata.AcquisitionMode with
        | None -> None
        | Some mode ->
            let acquisitionMode =
                match mode with
                | "sampled" -> AcquisitionMode.Sampled
                | "eventDriven" -> AcquisitionMode.EventDriven
                | _ -> AcquisitionMode.ChangeOfValue
            Some {
                AcquisitionMode = acquisitionMode
                SamplingIntervalMs = intValue SignalPolicyUaMetadata.SamplingIntervalMs
                PublishingIntervalMs = intValue SignalPolicyUaMetadata.PublishingIntervalMs
                DeadbandAbsolute = floatValue SignalPolicyUaMetadata.DeadbandAbsolute
                DeadbandPercent = floatValue SignalPolicyUaMetadata.DeadbandPercent
                EngineeringRangeLow = floatValue SignalPolicyUaMetadata.EngineeringRangeLow
                EngineeringRangeHigh = floatValue SignalPolicyUaMetadata.EngineeringRangeHigh
                QueueSize = intValue SignalPolicyUaMetadata.QueueSize
                Retention = stringValue SignalPolicyUaMetadata.Retention |> Option.defaultValue ""
            }

    let private readUnit (session: Session) (signalNodeId: NodeId) =
        browseChildren session signalNodeId
        |> Seq.tryPick (fun reference ->
            if reference.NodeClass <> NodeClass.Variable ||
               not (String.Equals(reference.BrowseName.Name, SignalUaMetadata.Unit, StringComparison.Ordinal)) then None
            else
                let childId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris)
                if isNull childId then None
                else
                    let value = session.ReadValue childId
                    if isNull value || StatusCode.IsBad value.StatusCode || isNull value.Value then None
                    else
                        Convert.ToString(value.Value, CultureInfo.InvariantCulture)
                        |> Option.ofObj
                        |> Option.filter (String.IsNullOrWhiteSpace >> not))

    let monitoredItemSettings (options: UaSubscriptionOptions) (policy: UaCollectionPolicy option) =
        let samplingInterval =
            match policy with
            | Some p when p.AcquisitionMode = AcquisitionMode.EventDriven -> 0
            | Some p -> p.SamplingIntervalMs |> Option.defaultValue options.SamplingIntervalMs
            | None -> options.SamplingIntervalMs
        let publishingInterval =
            policy
            |> Option.bind (fun p -> p.PublishingIntervalMs)
            |> Option.defaultValue options.PublishingIntervalMs
            |> max 50
        let queueSize =
            policy
            |> Option.bind (fun p -> p.QueueSize)
            |> Option.defaultValue 100
            |> max 1
            |> uint32
        let trigger =
            match policy with
            | Some p when p.AcquisitionMode = AcquisitionMode.Sampled -> DataChangeTrigger.StatusValueTimestamp
            | Some p when p.AcquisitionMode = AcquisitionMode.EventDriven -> DataChangeTrigger.StatusValueTimestamp
            | _ -> DataChangeTrigger.StatusValue
        let deadbandType, deadbandValue =
            match policy with
            | Some p when p.AcquisitionMode = AcquisitionMode.ChangeOfValue ->
                match p.DeadbandAbsolute, p.DeadbandPercent with
                | Some value, _ -> DeadbandType.Absolute, value
                | None, Some value when p.EngineeringRangeLow.IsSome && p.EngineeringRangeHigh.IsSome ->
                    DeadbandType.Percent, value
                | _ -> DeadbandType.None, 0.0
            | _ -> DeadbandType.None, 0.0
        {
            SamplingIntervalMs = samplingInterval
            PublishingIntervalMs = publishingInterval
            QueueSize = queueSize
            Trigger = trigger
            DeadbandType = uint32 deadbandType
            DeadbandValue = deadbandValue
        }

    let eventFilter () =
        let select (fieldName: string) =
            let operand = SimpleAttributeOperand()
            operand.TypeDefinitionId <- ObjectTypeIds.BaseEventType
            operand.AttributeId <- Attributes.Value
            operand.BrowsePath <- QualifiedNameCollection([| QualifiedName(fieldName) |])
            operand
        let filter = EventFilter()
        filter.SelectClauses <- SimpleAttributeOperandCollection()
        for field in
            [| BrowseNames.EventId
               BrowseNames.EventType
               BrowseNames.SourceNode
               BrowseNames.SourceName
               BrowseNames.Time
               BrowseNames.ReceiveTime
               BrowseNames.Message
               BrowseNames.Severity |] do
            filter.SelectClauses.Add(select field)
        filter.WhereClause <- ContentFilter()
        filter

    let private stableEventEnvelopeId (eventId: byte array) =
        if eventId.Length = 16 then Guid eventId
        else
            let digest = SHA256.HashData eventId
            Guid(digest.[0..15])

    /// Stable across UA retransmission/reconnect so sink dedup remains effective.
    let stableSampleEnvelopeId
        (globalAssetId: GlobalAssetId)
        (signalId: SignalId)
        (sourceTimestamp: DateTimeOffset)
        (statusCode: uint32)
        (value: SampleValue) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)
        writer.Write globalAssetId.Value
        writer.Write signalId.Value
        writer.Write sourceTimestamp.UtcDateTime.Ticks
        writer.Write statusCode
        match value with
        | ValueDouble number -> writer.Write(byte 1); writer.Write number
        | ValueLong number -> writer.Write(byte 2); writer.Write number
        | ValueString text -> writer.Write(byte 3); writer.Write(defaultArg (Option.ofObj text) "")
        | ValueBool state -> writer.Write(byte 4); writer.Write state
        | ValueNone -> writer.Write(byte 0)
        writer.Flush()
        let digest = SHA256.HashData(stream.ToArray())
        Guid(digest.[0..15])

    let tryEventGlobalAssetId (session: Session) (fields: VariantCollection) =
        if isNull fields || fields.Count < 3 then None
        else
            match fields.[2].Value with
            | :? NodeId as sourceNode ->
                session.NamespaceUris.GetString(uint32 sourceNode.NamespaceIndex)
                |> tryGlobalAssetId
            | _ -> None

    /// Decodes the JSON contract emitted by DsNodeManager from the standard
    /// BaseEvent fields selected by eventFilter.
    let tryEventEnvelope
        (origin: string)
        (globalAssetId: GlobalAssetId)
        (fields: VariantCollection) : Envelope option =
        try
            if isNull fields || fields.Count < 8 then None
            else
                let value index = fields.[index].Value
                let eventId = value 0 :?> byte array
                if isNull eventId || eventId.Length = 0 then None
                else
                    let sourceName = Convert.ToString(value 3, CultureInfo.InvariantCulture)
                    let sourceTimestamp =
                        match value 4 with
                        | :? DateTime as timestamp -> DateTimeOffset(timestamp.ToUniversalTime())
                        | _ -> DateTimeOffset.UtcNow
                    let receiveTimestamp =
                        match value 5 with
                        | :? DateTime as timestamp -> Some(DateTimeOffset(timestamp.ToUniversalTime()))
                        | _ -> None
                    let message =
                        match value 6 with
                        | :? LocalizedText as localized -> localized.Text
                        | raw -> Convert.ToString(raw, CultureInfo.InvariantCulture)
                    if String.IsNullOrWhiteSpace message || message.Length > MaxEventPayloadBytes then
                        raise (InvalidDataException "OPC UA event message exceeds the supported size.")
                    use document = JsonDocument.Parse message
                    let root = document.RootElement
                    let mutable eventTypeElement = Unchecked.defaultof<JsonElement>
                    let mutable sourceSignalElement = Unchecked.defaultof<JsonElement>
                    let mutable payloadElement = Unchecked.defaultof<JsonElement>
                    if not (root.TryGetProperty("eventTypeSemanticId", &eventTypeElement))
                       || not (root.TryGetProperty("payload", &payloadElement)) then None
                    else
                        let signalId =
                            if root.TryGetProperty("sourceSignalId", &sourceSignalElement) then
                                sourceSignalElement.GetString()
                            else sourceName
                        let payloadJson = payloadElement.GetRawText()
                        if String.IsNullOrWhiteSpace signalId
                           || Encoding.UTF8.GetByteCount(payloadJson) > MaxEventPayloadBytes then None
                        else
                            Some {
                                EnvelopeId = stableEventEnvelopeId eventId
                                Kind = Event
                                GlobalAssetId = globalAssetId
                                SignalId = SignalId signalId
                                SourceTimestamp = sourceTimestamp
                                ServerTimestamp = receiveTimestamp
                                Value = ValueNone
                                StatusCode = uint32 StatusCodes.Good
                                Unit = None
                                SeqNo = None
                                Origin = origin
                                EventPayloadJson = Some payloadJson
                                EventTypeSemanticId = Some(eventTypeElement.GetString())
                            }
        with _ -> None

    let discoverSignalNodes (session: Session) =
        let found = ResizeArray<UaSignalNode>()
        for index in 0 .. session.NamespaceUris.Count - 1 do
            let nsIndex = uint16 index
            let namespaceUri = session.NamespaceUris.GetString(uint32 index)
            match tryGlobalAssetId namespaceUri with
            | None -> ()
            | Some globalAssetId ->
                let visited = HashSet<NodeId>()
                let rec visit (nodeId: NodeId) =
                    if visited.Add nodeId then
                        for reference in browseChildren session nodeId do
                            let childId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris)
                            if not (isNull childId) then
                                match reference.NodeClass with
                                | NodeClass.Variable when childId.NamespaceIndex = nsIndex ->
                                    match childId.Identifier with
                                    | :? string as identifier when not (identifier.StartsWith("Events/", StringComparison.Ordinal)) ->
                                        try
                                            found.Add {
                                                NodeId = childId
                                                GlobalAssetId = globalAssetId
                                                SignalId = SignalId identifier
                                                Unit = readUnit session childId
                                                Policy = readCollectionPolicy session childId
                                            }
                                        with _ -> ()
                                    | _ -> ()
                                | NodeClass.Object when childId.NamespaceIndex = nsIndex -> visit childId
                                | _ -> ()
                visit (NodeId("Asset", nsIndex))
        List.ofSeq found

    let discoverEventNodes (session: Session) =
        let mutable foundAssetEvents = false
        for index in 0 .. session.NamespaceUris.Count - 1 do
            let nsIndex = uint16 index
            let namespaceUri = session.NamespaceUris.GetString(uint32 index)
            match tryGlobalAssetId namespaceUri with
            | None -> ()
            | Some _ ->
                browseChildren session (NodeId("Asset", nsIndex))
                |> Seq.tryFind (fun reference ->
                    reference.NodeClass = NodeClass.Object
                    && String.Equals(reference.BrowseName.Name, "Events", StringComparison.Ordinal))
                |> Option.iter (fun reference ->
                    let childId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris)
                    if not (isNull childId) then
                        foundAssetEvents <- true)
        // The standard Server object is the stable aggregation point for all
        // dynamically registered asset root notifiers.
        if foundAssetEvents then [ { NodeId = ObjectIds.Server } ] else []

/// Agent OPC UA 서버의 모든 결정론적 SignalId Variable을 구독해 SQLite sink로 전달한다.
type UaSubscriptionService(
        options: UaSubscriptionOptions,
        sink: SqliteSinkWriter,
        outbox: SqliteEdgeBuffer,
        registry: SeriesIdRegistry,
        runtimeState: CollectorRuntimeState,
        logger: ILogger<UaSubscriptionService>) =
    inherit BackgroundService()

    let buildClientConfiguration () =
        let root = Path.Combine(options.DataRoot, "ua-client")
        Directory.CreateDirectory root |> ignore
        ApplicationConfiguration(
            ApplicationName = "Ds2.Collector",
            ApplicationUri = "urn:dualsoft:collector:opcua",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration =
                SecurityConfiguration(
                    ApplicationCertificate = CertificateIdentifier(
                        StoreType = "Directory",
                        StorePath = Path.Combine(root, "own"),
                        SubjectName = "CN=Ds2.Collector, O=DualSoft"),
                    TrustedIssuerCertificates = CertificateTrustList(
                        StoreType = "Directory",
                        StorePath = Path.Combine(root, "issuers")),
                    TrustedPeerCertificates = CertificateTrustList(
                        StoreType = "Directory",
                        StorePath = Path.Combine(root, "trusted")),
                    RejectedCertificateStore = CertificateTrustList(
                        StoreType = "Directory",
                        StorePath = Path.Combine(root, "rejected")),
                    AutoAcceptUntrustedCertificates = options.AutoAcceptUntrustedCertificates,
                    RejectSHA1SignedCertificates = true,
                    MinimumCertificateKeySize = 2048us),
            ClientConfiguration = ClientConfiguration(),
            TransportQuotas = TransportQuotas(OperationTimeout = 15_000),
            CertificateValidator = CertificateValidator())

    let isLoopbackEndpoint () =
        match Uri.TryCreate(options.EndpointUrl, UriKind.Absolute) with
        | true, uri -> uri.IsLoopback
        | _ -> false

    let tryFindPairedServerCertificate () =
        let ownStore = Path.Combine(options.PairedServerCertificateRoot, "own")
        if not (Directory.Exists ownStore) then None
        else
            let mutable newest : X509Certificate2 option = None
            for path in Directory.EnumerateFiles(ownStore, "*.der", SearchOption.AllDirectories) do
                try
                    let certificate = X509CertificateLoader.LoadCertificateFromFile path
                    if String.Equals(
                        X509Utils.GetApplicationUriFromCertificate certificate,
                        options.PairedServerApplicationUri,
                        StringComparison.Ordinal) then
                        match newest with
                        | Some current when current.NotAfter >= certificate.NotAfter -> certificate.Dispose()
                        | Some current ->
                            current.Dispose()
                            newest <- Some certificate
                        | None -> newest <- Some certificate
                    else
                        certificate.Dispose()
                with _ -> ()
            newest

    let certificateExistsInDirectoryStore (storePath: string) (thumbprint: string) =
        Directory.Exists storePath
        && (Directory.EnumerateFiles(storePath, "*.der", SearchOption.AllDirectories)
            |> Seq.exists (fun path ->
                try
                    use certificate = X509CertificateLoader.LoadCertificateFromFile path
                    String.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)
                with _ -> false))

    let addToDirectoryStoreIfMissing
        (certificate: X509Certificate2)
        (store: CertificateStoreIdentifier)
        (ct: CancellationToken) = task {
        if not (certificateExistsInDirectoryStore store.StorePath certificate.Thumbprint) then
            try
                let! _ = X509Utils.AddToStoreAsync(certificate, store, null, ct)
                return ()
            with :? ArgumentException when certificateExistsInDirectoryStore store.StorePath certificate.Thumbprint ->
                // Another startup path won the race. The required trust state is already present.
                return ()
    }

    let pairLocalCertificates
        (config: ApplicationConfiguration)
        (applicationCertificate: X509Certificate2)
        (ct: CancellationToken) = task {
        if options.PairLocalCertificates && isLoopbackEndpoint () then
            match tryFindPairedServerCertificate () with
            | None -> ()
            | Some serverCertificate ->
                use serverCertificate = serverCertificate
                // Find(true)의 인증서는 private key를 포함한다. RawData로 public-only 인스턴스를 만들어
                // Agent trusted store에 개인키가 복사되는 것을 명시적으로 차단한다.
                use collectorPublic = X509CertificateLoader.LoadCertificate(applicationCertificate.RawData)
                let collectorTrusted = CertificateStoreIdentifier(
                    StoreType = "Directory",
                    StorePath = config.SecurityConfiguration.TrustedPeerCertificates.StorePath)
                let serverTrusted = CertificateStoreIdentifier(
                    StoreType = "Directory",
                    StorePath = Path.Combine(options.PairedServerCertificateRoot, "trusted"))
                do! addToDirectoryStoreIfMissing serverCertificate collectorTrusted ct
                do! addToDirectoryStoreIfMissing collectorPublic serverTrusted ct
                logger.LogInformation(
                    "Paired local OPC UA certificates: serverUri={ServerUri}, serverThumbprint={ServerThumbprint}, collectorThumbprint={CollectorThumbprint}",
                    options.PairedServerApplicationUri,
                    serverCertificate.Thumbprint,
                    collectorPublic.Thumbprint)
    }

    let createSession () = task {
        let config = buildClientConfiguration ()
        do! config.Validate(ApplicationType.Client)
        let instance = ApplicationInstance(ApplicationConfiguration = config)
        let! certificateOk = instance.CheckApplicationInstanceCertificates(silent = true)
        if not certificateOk then invalidOp "Collector OPC UA client certificate could not be created."
        let endpointDescription = CoreClientUtils.SelectEndpoint(config, options.EndpointUrl, options.UseSecurity, 15_000)
        if not (UaSubscription.isAcceptedEndpointSecurity options.UseSecurity endpointDescription) then
            invalidOp (sprintf
                "Collector OPC UA endpoint security rejected: mode=%O policy=%s"
                endpointDescription.SecurityMode endpointDescription.SecurityPolicyUri)
        let endpoint = ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(config))
        let! applicationCertificate = config.SecurityConfiguration.ApplicationCertificate.Find(true)
        if not (isNull applicationCertificate) then
            do! pairLocalCertificates config applicationCertificate CancellationToken.None
        let identity : IUserIdentity =
            if options.UseCertificateIdentity && not (isNull applicationCertificate) then
                UserIdentity(applicationCertificate) :> IUserIdentity
            else
                UserIdentity(AnonymousIdentityToken()) :> IUserIdentity
        let! session =
            Session.Create(
                config,
                endpoint,
                false,
                "Ds2.Collector",
                60_000u,
                identity,
                null)
        session.FetchNamespaceTables()
        return session
    }

    let writerLoop (ct: CancellationToken) = task {
        let retryBackoff attempts =
            let seconds = Math.Min(60.0, Math.Pow(2.0, float (min attempts 6)))
            TimeSpan.FromSeconds seconds
        try
            while not ct.IsCancellationRequested do
                try
                    let due = outbox.PullDue 512
                    if due.IsEmpty then
                        do! Task.Delay(200, ct)
                    else
                        try
                            let! rows = sink.WriteBatchAsync(due |> Seq.map (fun row -> row.Envelope))
                            outbox.AckMany(due |> Seq.map (fun row -> row.EnvelopeId))
                            runtimeState.MarkPersisted(due.Length)
                            logger.LogDebug(
                                "Collector outbox batch persisted: received={Received}, inserted={Inserted}, pending={Pending}",
                                due.Length,
                                rows,
                                outbox.PendingCount())
                        with ex ->
                            let retries =
                                due
                                |> Seq.map (fun row -> row.EnvelopeId, retryBackoff (row.Attempts + 1))
                                |> Seq.toArray
                            outbox.RequeueMany retries
                            runtimeState.MarkWriteFailure(retries.Length, ex.Message)
                            logger.LogError(
                                ex,
                                "Collector sink write failed; durable envelopes requeued: count={Count}, pending={Pending}",
                                retries.Length,
                                outbox.PendingCount())
                            do! Task.Delay(250, ct)
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    runtimeState.MarkWriteFailure(0, ex.Message)
                    logger.LogError(ex, "Collector durable outbox loop failed; retrying.")
                    do! Task.Delay(1000, ct)
        with :? OperationCanceledException -> ()
    }

    let attachSubscriptions (session: Session) =
        let nodes = UaSubscription.discoverSignalNodes session
        let eventNodes = UaSubscription.discoverEventNodes session
        nodes
        |> Seq.map (fun node ->
            let seriesId = AssetTelemetryIdentity.seriesId node.GlobalAssetId node.SignalId
            seriesId, {
                GlobalAssetId = node.GlobalAssetId.Value
                SignalId = node.SignalId.Value
                DefaultTable = "signals"
                Retention =
                    node.Policy
                    |> Option.map (fun policy -> policy.Retention)
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
            })
        |> registry.ReplaceAll

        let subscriptions = ResizeArray<Subscription>()
        let ensureCreatedItems (subscription: Subscription) =
            let rejected =
                subscription.MonitoredItems
                |> Seq.choose (fun item ->
                    let error = item.Status.Error
                    if isNull error || ServiceResult.IsGood error then None
                    else Some($"{item.DisplayName}={error.StatusCode}"))
                |> Seq.toArray
            if rejected.Length > 0 then
                let details = String.Join(", ", rejected)
                invalidOp ($"OPC UA monitored item creation failed: {details}")
        nodes
        |> List.map (fun node -> node, UaSubscription.monitoredItemSettings options node.Policy)
        |> List.groupBy (fun (_, settings) -> settings.PublishingIntervalMs)
        |> List.iter (fun (publishingInterval, group) ->
            let subscription = new Subscription(session.DefaultSubscription)
            subscription.DisplayName <- $"Ds2.Collector.{publishingInterval}ms"
            subscription.PublishingInterval <- publishingInterval
            subscription.KeepAliveCount <- 10u
            subscription.LifetimeCount <- 100u
            subscription.MaxNotificationsPerPublish <- 0u

            for node, settings in group do
                let item = new MonitoredItem(subscription.DefaultItem)
                item.DisplayName <- node.SignalId.Value
                item.StartNodeId <- node.NodeId
                item.AttributeId <- Attributes.Value
                item.SamplingInterval <- settings.SamplingIntervalMs
                item.QueueSize <- settings.QueueSize
                item.DiscardOldest <- true
                let filter = DataChangeFilter()
                filter.Trigger <- settings.Trigger
                filter.DeadbandType <- settings.DeadbandType
                filter.DeadbandValue <- settings.DeadbandValue
                item.Filter <- filter
                item.add_Notification(MonitoredItemNotificationEventHandler(fun _ args ->
                    match args.NotificationValue with
                    | :? MonitoredItemNotification as notification ->
                        let value = notification.Value
                        let sourceTimestamp =
                            if value.SourceTimestamp = DateTime.MinValue then DateTimeOffset.UtcNow
                            else DateTimeOffset(value.SourceTimestamp.ToUniversalTime())
                        let serverTimestamp =
                            if value.ServerTimestamp = DateTime.MinValue then None
                            else Some(DateTimeOffset(value.ServerTimestamp.ToUniversalTime()))
                        let sampleValue = UaSubscription.toSampleValue value.Value
                        let envelope = {
                            EnvelopeId = UaSubscription.stableSampleEnvelopeId
                                node.GlobalAssetId node.SignalId sourceTimestamp value.StatusCode.Code sampleValue
                            Kind = Sample
                            GlobalAssetId = node.GlobalAssetId
                            SignalId = node.SignalId
                            SourceTimestamp = sourceTimestamp
                            ServerTimestamp = serverTimestamp
                            Value = sampleValue
                            StatusCode = value.StatusCode.Code
                            Unit = node.Unit
                            SeqNo = None
                            Origin = "opcua:" + options.EndpointUrl
                            EventPayloadJson = None
                            EventTypeSemanticId = None
                        }
                        runtimeState.MarkReceived()
                        try
                            // OPC UA callback 반환 전에 durable outbox에 기록한다. process crash나 sink 장애가
                            // 발생해도 다음 기동에서 ack되지 않은 envelope를 다시 flush한다.
                            outbox.Enqueue envelope
                        with ex ->
                            runtimeState.MarkEnqueueFailure(ex.Message)
                            logger.LogCritical(
                                ex,
                                "Collector durable enqueue failed; notification could not be accepted: signalId={SignalId}",
                                node.SignalId.Value)
                    | _ -> ()))
                subscription.AddItem item

            session.AddSubscription subscription |> ignore
            subscription.Create()
            ensureCreatedItems subscription
            subscriptions.Add subscription)

        if not eventNodes.IsEmpty then
            let subscription = new Subscription(session.DefaultSubscription)
            subscription.DisplayName <- "Ds2.Collector.Events"
            subscription.PublishingInterval <- options.PublishingIntervalMs
            subscription.KeepAliveCount <- 10u
            subscription.LifetimeCount <- 100u
            subscription.MaxNotificationsPerPublish <- 0u

            for eventNode in eventNodes do
                let item = new MonitoredItem(subscription.DefaultItem)
                item.DisplayName <- "Ds2.AssetEvents"
                item.StartNodeId <- eventNode.NodeId
                item.AttributeId <- Attributes.EventNotifier
                item.QueueSize <- 1000u
                item.DiscardOldest <- true
                item.Filter <- UaSubscription.eventFilter()
                item.add_Notification(MonitoredItemNotificationEventHandler(fun _ args ->
                    match args.NotificationValue with
                    | :? EventFieldList as notification ->
                        match UaSubscription.tryEventGlobalAssetId session notification.EventFields with
                        | None -> logger.LogDebug("Collector ignored a non-asset OPC UA event.")
                        | Some globalAssetId ->
                            match UaSubscription.tryEventEnvelope
                                ("opcua:" + options.EndpointUrl)
                                globalAssetId
                                notification.EventFields with
                            | None ->
                                logger.LogWarning(
                                    "Collector rejected an asset OPC UA event with an invalid wire contract: asset={Asset}",
                                    globalAssetId.Value)
                            | Some envelope ->
                                runtimeState.MarkReceived()
                                try outbox.Enqueue envelope
                                with ex ->
                                    runtimeState.MarkEnqueueFailure(ex.Message)
                                    logger.LogCritical(
                                        ex,
                                        "Collector durable enqueue failed; event could not be accepted: asset={Asset} signalId={SignalId}",
                                        globalAssetId.Value,
                                        envelope.SignalId.Value)
                    | _ -> ()))
                subscription.AddItem item

            session.AddSubscription subscription |> ignore
            subscription.Create()
            ensureCreatedItems subscription
            subscriptions.Add subscription

        logger.LogInformation(
            "OPC UA subscribed: endpoint={Endpoint}, signals={SignalCount}, eventNotifiers={EventNotifierCount}, subscriptions={SubscriptionCount}",
            options.EndpointUrl,
            nodes.Length,
            eventNodes.Length,
            subscriptions.Count)
        List.ofSeq subscriptions

    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        runtimeState.MarkStarted(options.Enabled)
        let writer = writerLoop stoppingToken
        try
            if not options.Enabled then
                logger.LogInformation("OPC UA subscription disabled (DS2_UA_SUBSCRIBE_ENABLED=false); durable outbox flush remains active.")
                try
                    do! Task.Delay(Timeout.Infinite, stoppingToken)
                with :? OperationCanceledException -> ()
            else
                try
                    while not stoppingToken.IsCancellationRequested do
                        let mutable session : Session = null
                        let mutable subscriptions : Subscription list = []
                        try
                            try
                                let! connected = createSession ()
                                session <- connected
                                subscriptions <- attachSubscriptions session
                                runtimeState.MarkConnected()
                                while session.Connected && not stoppingToken.IsCancellationRequested do
                                    do! Task.Delay(1000, stoppingToken)
                                if not stoppingToken.IsCancellationRequested then
                                    runtimeState.MarkDisconnected(Some "OPC UA session disconnected.")
                            with
                            | :? OperationCanceledException -> ()
                            | ex ->
                                runtimeState.MarkDisconnected(Some ex.Message)
                                logger.LogWarning(ex, "OPC UA subscription disconnected; retrying in {DelayMs}ms", options.ReconnectDelayMs)
                        finally
                            runtimeState.MarkDisconnected(None)
                            for subscription in subscriptions do
                                try subscription.Delete(true) with _ -> ()
                                subscription.Dispose()
                            if not (isNull session) then
                                try session.Close() |> ignore with _ -> ()
                                session.Dispose()
                        if not stoppingToken.IsCancellationRequested then
                            do! Task.Delay(options.ReconnectDelayMs, stoppingToken)
                with :? OperationCanceledException -> ()
        finally
            runtimeState.MarkStopped()
        try
            do! writer
        with :? OperationCanceledException -> ()
    }
