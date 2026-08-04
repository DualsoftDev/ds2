namespace Ds2.Collector

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography.X509Certificates
open System.Threading
open System.Threading.Channels
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

type UaSignalNode = {
    NodeId: NodeId
    GlobalAssetId: GlobalAssetId
    SignalId: SignalId
    Policy: UaCollectionPolicy option
}

and UaCollectionPolicy = {
    AcquisitionMode: AcquisitionMode
    SamplingIntervalMs: int option
    PublishingIntervalMs: int option
    DeadbandAbsolute: float option
    DeadbandPercent: float option
    QueueSize: int option
    Retention: string
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
    let private assetNamespacePrefix = "urn:ds:asset:"

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
                QueueSize = intValue SignalPolicyUaMetadata.QueueSize
                Retention = stringValue SignalPolicyUaMetadata.Retention |> Option.defaultValue ""
            }

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
                // OPC UA Percent deadband requires a signal EURange. Current AID/XGT
                // interaction schema has no engineering range, so do not submit an
                // invalid server filter. Metadata is preserved and a warning is logged.
                | None, Some _ -> DeadbandType.None, 0.0
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
                                                Policy = readCollectionPolicy session childId
                                            }
                                        with _ -> ()
                                    | _ -> ()
                                | NodeClass.Object when childId.NamespaceIndex = nsIndex -> visit childId
                                | _ -> ()
                visit (NodeId("Asset", nsIndex))
        List.ofSeq found

/// Agent OPC UA 서버의 모든 결정론적 SignalId Variable을 구독해 SQLite sink로 전달한다.
type UaSubscriptionService(
        options: UaSubscriptionOptions,
        sink: SqliteSinkWriter,
        registry: SeriesIdRegistry,
        logger: ILogger<UaSubscriptionService>) =
    inherit BackgroundService()

    let queue =
        BoundedChannelOptions(8192, FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false)
        |> Channel.CreateBounded<Envelope>

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
        try
            while not ct.IsCancellationRequested do
                let! available = queue.Reader.WaitToReadAsync(ct).AsTask()
                if available then
                    let batch = ResizeArray<Envelope>(512)
                    let mutable item = Unchecked.defaultof<Envelope>
                    while batch.Count < 512 && queue.Reader.TryRead(&item) do batch.Add item
                    if batch.Count > 0 then
                        let! rows = sink.WriteBatchAsync batch
                        logger.LogDebug("OPC UA batch persisted: received={Received}, inserted={Inserted}", batch.Count, rows)
        with :? OperationCanceledException -> ()
    }

    let attachSubscriptions (session: Session) =
        let nodes = UaSubscription.discoverSignalNodes session
        for node in nodes do
            let seriesId = Base64Url.encode node.GlobalAssetId.Value + "." + node.SignalId.Value
            registry.Register(seriesId, {
                GlobalAssetId = node.GlobalAssetId.Value
                SignalId = node.SignalId.Value
                DefaultTable = "signals"
                Retention =
                    node.Policy
                    |> Option.map (fun policy -> policy.Retention)
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
            })

        let subscriptions = ResizeArray<Subscription>()
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
                match node.Policy with
                | Some policy when policy.DeadbandAbsolute.IsNone && policy.DeadbandPercent.IsSome ->
                    logger.LogWarning(
                        "CollectionPolicy percent deadband not applied because EURange is unavailable: signalId={SignalId}",
                        node.SignalId.Value)
                | _ -> ()
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
                        let envelope = {
                            EnvelopeId = Guid.NewGuid()
                            Kind = Sample
                            GlobalAssetId = node.GlobalAssetId
                            SignalId = node.SignalId
                            SourceTimestamp = sourceTimestamp
                            ServerTimestamp = serverTimestamp
                            Value = UaSubscription.toSampleValue value.Value
                            StatusCode = value.StatusCode.Code
                            Unit = None
                            SeqNo = None
                            Origin = "opcua:" + options.EndpointUrl
                            EventPayloadJson = None
                            EventTypeSemanticId = None
                        }
                        queue.Writer.TryWrite envelope |> ignore
                    | _ -> ()))
                subscription.AddItem item

            session.AddSubscription subscription |> ignore
            subscription.Create()
            subscriptions.Add subscription)

        logger.LogInformation(
            "OPC UA subscribed: endpoint={Endpoint}, signals={SignalCount}, policyGroups={GroupCount}",
            options.EndpointUrl,
            nodes.Length,
            subscriptions.Count)
        List.ofSeq subscriptions

    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        if not options.Enabled then
            logger.LogInformation("OPC UA subscription disabled (DS2_UA_SUBSCRIBE_ENABLED=false).")
        else
            let writer = writerLoop stoppingToken
            try
                try
                    while not stoppingToken.IsCancellationRequested do
                        let mutable session : Session = null
                        let mutable subscriptions : Subscription list = []
                        try
                            try
                                let! connected = createSession ()
                                session <- connected
                                subscriptions <- attachSubscriptions session
                                while session.Connected && not stoppingToken.IsCancellationRequested do
                                    do! Task.Delay(1000, stoppingToken)
                            with
                            | :? OperationCanceledException -> ()
                            | ex ->
                                logger.LogWarning(ex, "OPC UA subscription disconnected; retrying in {DelayMs}ms", options.ReconnectDelayMs)
                        finally
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
                queue.Writer.TryComplete() |> ignore
            try
                do! writer
            with :? OperationCanceledException -> ()
    }
