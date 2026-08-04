module Ds2.Collector.Tests.PolicyDiscoveryIntegrationTests

open System
open System.IO
open Opc.Ua
open Opc.Ua.Client
open Opc.Ua.Configuration
open Xunit
open Ds2.Core
open Ds2.Collector
open Ds2.OpcUa.Server.NodeIds
open Ds2.OpcUa.Server.Server

let private nextPort =
    // This source file is linked into two test assemblies. A process-specific port avoids
    // the first run's TIME_WAIT socket without briefly reserving/releasing an ephemeral port.
    let mutable port = 30_000 + (Environment.ProcessId % 9_000)
    fun () -> Threading.Interlocked.Increment(&port)

let private clientConfiguration root =
    let configuration =
        ApplicationConfiguration(
            ApplicationName = "Ds2.CollectionPolicy.IT.Client",
            ApplicationUri = "urn:dualsoft:collection-policy-it-client",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration =
                SecurityConfiguration(
                    ApplicationCertificate = CertificateIdentifier(
                        StoreType = "Directory",
                        StorePath = Path.Combine(root, "client", "own"),
                        SubjectName = "CN=Ds2.CollectionPolicy.IT.Client, O=DualSoft"),
                    TrustedIssuerCertificates = CertificateTrustList(
                        StoreType = "Directory", StorePath = Path.Combine(root, "client", "issuers")),
                    TrustedPeerCertificates = CertificateTrustList(
                        StoreType = "Directory", StorePath = Path.Combine(root, "client", "trusted")),
                    RejectedCertificateStore = CertificateTrustList(
                        StoreType = "Directory", StorePath = Path.Combine(root, "client", "rejected")),
                    AutoAcceptUntrustedCertificates = true,
                    MinimumCertificateKeySize = 2048us),
            ClientConfiguration = ClientConfiguration(),
            TransportQuotas = TransportQuotas(OperationTimeout = 15_000),
            CertificateValidator = CertificateValidator())
    configuration.Validate(ApplicationType.Client).GetAwaiter().GetResult()
    let instance = ApplicationInstance(ApplicationConfiguration = configuration)
    Assert.True(instance.CheckApplicationInstanceCertificates(silent = true).GetAwaiter().GetResult())
    configuration

[<Fact>]
let ``Collector discovers CollectionPolicy from UA variable properties`` () = task {
    let root = Path.Combine(Path.GetTempPath(), "ds2-policy-discovery-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    let gaid = GlobalAssetId "urn:dualsoft:asset:policy-discovery"
    let allocator = NamespaceAllocator(Path.Combine(root, "nodeset-state.json")) :> INamespaceAllocator
    let config = {
        ServerConfiguration.defaultConfig root with
            EndpointUrl = $"opc.tcp://localhost:{nextPort()}"
    }
    let appConfiguration = ServerConfiguration.build config
    ServerConfiguration.validateAndPrepare appConfiguration |> Async.AwaitTask |> Async.RunSynchronously |> ignore
    let serverInstance = ApplicationInstance(ApplicationConfiguration = appConfiguration)
    let server = new DsUaServer(allocator, [| allocator.GlobalAssetIdToUri gaid |])
    try
        try
            do! serverInstance.Start server
        with ex ->
            let tracePath = appConfiguration.TraceConfiguration.OutputFilePath
            let trace =
                if String.IsNullOrWhiteSpace tracePath || not (File.Exists tracePath) then "<no OPC UA trace>"
                else File.ReadAllText tracePath
            let serviceResult =
                match ex with
                | :? ServiceResultException as serviceEx -> $"status={serviceEx.StatusCode}; result={serviceEx.Result}"
                | _ -> "not a ServiceResultException"
            raise (InvalidOperationException($"Failed to start policy test server at {config.EndpointUrl}: {serviceResult}; {ex}\n{trace}", ex))
        let signalId = SignalId "demo.policy.temperature"
        let expected : SignalPolicy = {
            SignalId = signalId
            AcquisitionMode = AcquisitionMode.ChangeOfValue
            SamplingIntervalMs = Some 250
            PublishingIntervalMs = Some 500
            DeadbandAbsolute = Some 0.5
            DeadbandPercent = None
            QueueSize = Some 25
            Retention = "P90D"
        }
        server.NodeManager.AddAssetWithHierarchyAndPolicies(
            gaid,
            "PolicyDiscovery",
            [ [], signalId, "degC", BuiltInType.Double, "Temperature", Some expected ]) |> ignore

        let clientConfig = clientConfiguration root
        let endpointDescription = CoreClientUtils.SelectEndpoint(clientConfig, config.EndpointUrl, false, 15_000)
        let endpoint = ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(clientConfig))
        let! session =
            Session.Create(
                clientConfig,
                endpoint,
                false,
                "CollectionPolicy-IT",
                60_000u,
                UserIdentity(AnonymousIdentityToken()),
                null)
        try
            session.FetchNamespaceTables()
            let node =
                UaSubscription.discoverSignalNodes session
                |> List.find (fun node -> node.SignalId = signalId)
            let policy = node.Policy |> Option.get
            Assert.Equal(expected.AcquisitionMode, policy.AcquisitionMode)
            Assert.Equal(expected.SamplingIntervalMs, policy.SamplingIntervalMs)
            Assert.Equal(expected.PublishingIntervalMs, policy.PublishingIntervalMs)
            Assert.Equal(expected.DeadbandAbsolute, policy.DeadbandAbsolute)
            Assert.Equal(expected.QueueSize, policy.QueueSize)
            Assert.Equal(expected.Retention, policy.Retention)

            let options : UaSubscriptionOptions = {
                Enabled = true
                EndpointUrl = config.EndpointUrl
                DataRoot = root
                UseSecurity = false
                AutoAcceptUntrustedCertificates = true
                UseCertificateIdentity = false
                PairLocalCertificates = false
                PairedServerCertificateRoot = root
                PairedServerApplicationUri = config.ApplicationUri
                SamplingIntervalMs = 200
                PublishingIntervalMs = 500
                ReconnectDelayMs = 1000
            }
            let settings = UaSubscription.monitoredItemSettings options node.Policy
            use subscription = new Subscription(session.DefaultSubscription)
            subscription.PublishingInterval <- settings.PublishingIntervalMs
            let item = new MonitoredItem(subscription.DefaultItem)
            item.StartNodeId <- node.NodeId
            item.AttributeId <- Attributes.Value
            item.SamplingInterval <- settings.SamplingIntervalMs
            item.QueueSize <- settings.QueueSize
            let filter = DataChangeFilter()
            filter.Trigger <- settings.Trigger
            filter.DeadbandType <- settings.DeadbandType
            filter.DeadbandValue <- settings.DeadbandValue
            item.Filter <- filter
            subscription.AddItem item
            session.AddSubscription subscription |> ignore
            subscription.Create()
            Assert.True(ServiceResult.IsGood item.Status.Error, $"policy filter rejected: {item.Status.Error}")
            subscription.Delete(true)
        finally
            session.Close() |> ignore
            session.Dispose()
    finally
        server.Stop()
        server.Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}
