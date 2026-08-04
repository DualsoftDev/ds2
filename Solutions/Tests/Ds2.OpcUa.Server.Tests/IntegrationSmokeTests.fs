module Ds2.OpcUa.Server.Tests.IntegrationSmokeTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Opc.Ua
open Opc.Ua.Client
open Opc.Ua.Configuration
open Ds2.Core
open Ds2.OpcUa.Server.NodeIds
open Ds2.OpcUa.Server.Server
open Xunit

// Phase 3 · Full wire-up 통합 스모크.
//
// 목표:
//   1. UA 서버가 실제 endpoint 를 열고
//   2. UA client (같은 프로세스) 가 접속 · Objects/DS/Assets 브라우징
//   3. Variable Write → Read 후 값 일치
//   4. RaiseAssetEvent Method Call → 성공 응답
//
// 검증 아키텍처: 통합 · 실제 스택 (Test Double 아님).

let private mkServerWithAssets port (managedAssets: GlobalAssetId array) =
    let root = Path.Combine(Path.GetTempPath(), "ds2-uaserver-it-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    let statePath = Path.Combine(root, "nodeset-state.json")
    let allocator = NamespaceAllocator(statePath) :> INamespaceAllocator
    let cfg = {
        ServerConfiguration.defaultConfig root with
            EndpointUrl = sprintf "opc.tcp://localhost:%d" port
            AllowAnonymous = true
            AllowUnsecuredEndpoint = true
            AutoAcceptUntrustedCertificates = true
    }
    let appConfig = ServerConfiguration.build cfg
    let prep = ServerConfiguration.validateAndPrepare appConfig
    prep.GetAwaiter().GetResult() |> ignore
    let appInstance = new ApplicationInstance(ApplicationConfiguration = appConfig)
    let managedNamespaces = managedAssets |> Array.map allocator.GlobalAssetIdToUri
    // This fixture explicitly exercises the externally callable development method.
    let server = new DsUaServer(allocator, managedNamespaces, 1000, true)
    appInstance.Start(server).GetAwaiter().GetResult()
    server, cfg, root

let private mkServer port = mkServerWithAssets port [||]

let private mkSecureServer port =
    let root = Path.Combine(Path.GetTempPath(), "ds2-uaserver-secure-it-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    let allocator = NamespaceAllocator(Path.Combine(root, "nodeset-state.json")) :> INamespaceAllocator
    let cfg =
        { ServerConfiguration.defaultConfig root with
            EndpointUrl = sprintf "opc.tcp://localhost:%d" port
            AllowAnonymous = false
            AllowUnsecuredEndpoint = false
            // The integration test auto-accepts its freshly generated peer; production does not.
            AutoAcceptUntrustedCertificates = true }
    let appConfig = ServerConfiguration.build cfg
    (ServerConfiguration.validateAndPrepare appConfig).GetAwaiter().GetResult() |> ignore
    let appInstance = ApplicationInstance(ApplicationConfiguration = appConfig)
    let server = new DsUaServer(allocator)
    appInstance.Start(server).GetAwaiter().GetResult()
    server, cfg, root

let private mkClientConfig root =
    let cfg =
        ApplicationConfiguration(
            ApplicationName = "Ds2.OpcUa.Server.IntegrationTest.Client",
            ApplicationUri = "urn:dualsoft:opcua:it:client",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration =
                SecurityConfiguration(
                    ApplicationCertificate = CertificateIdentifier(
                        StoreType = "Directory",
                        StorePath = Path.Combine(root, "client-certs", "own"),
                        SubjectName = "CN=Ds2.OpcUa.IT.Client, O=DualSoft"),
                    TrustedIssuerCertificates = CertificateTrustList(
                        StoreType = "Directory",
                        StorePath = Path.Combine(root, "client-certs", "issuers")),
                    TrustedPeerCertificates = CertificateTrustList(
                        StoreType = "Directory",
                        StorePath = Path.Combine(root, "client-certs", "trusted")),
                    RejectedCertificateStore = CertificateTrustList(
                        StoreType = "Directory",
                        StorePath = Path.Combine(root, "client-certs", "rejected")),
                    AutoAcceptUntrustedCertificates = true,
                    MinimumCertificateKeySize = 2048us),
            ClientConfiguration = ClientConfiguration(),
            TransportQuotas = TransportQuotas(OperationTimeout = 15_000),
            CertificateValidator = CertificateValidator())
    cfg.Validate(ApplicationType.Client) |> Async.AwaitTask |> Async.RunSynchronously
    let appInstance = new ApplicationInstance(ApplicationConfiguration = cfg)
    let ok = appInstance.CheckApplicationInstanceCertificates(silent = true).GetAwaiter().GetResult()
    if not ok then failwith "Client cert 발급 실패"
    cfg

let private nextTestPort =
    let counter = ref 48500
    fun () -> System.Threading.Interlocked.Increment counter

[<Fact>]
let ``Server boots + client connects + reads Server_ServerStatus`` () = task {
    let port = nextTestPort()
    let server, cfg, root = mkServer port
    try
        let clientConfig = mkClientConfig root
        let endpointUrl = cfg.EndpointUrl
        let ed = CoreClientUtils.SelectEndpoint(clientConfig, endpointUrl, false, 15_000)
        let endpointConfig = EndpointConfiguration.Create(clientConfig)
        let endpoint = new ConfiguredEndpoint(null, ed, endpointConfig)
        let! session =
            Session.Create(
                clientConfig, endpoint, false, "IT-Client",
                60_000u, new UserIdentity(new AnonymousIdentityToken()), null)

        // Server_ServerStatus 상태 읽기 (기본 표준 노드).
        let readValues =
            let ids =
                ReadValueIdCollection([|
                    ReadValueId(NodeId = VariableIds.Server_ServerStatus_State, AttributeId = Attributes.Value)
                |])
            let mutable results : DataValueCollection = DataValueCollection()
            let mutable diagnostics : DiagnosticInfoCollection = DiagnosticInfoCollection()
            session.Read(null, 0.0, TimestampsToReturn.Both, ids, &results, &diagnostics) |> ignore
            results
        Assert.NotEmpty(readValues)
        Assert.Equal(StatusCodes.Good, readValues.[0].StatusCode.Code)

        session.Close() |> ignore
        session.Dispose()
    finally
        server.Stop()
        server.Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}

[<Fact>]
let ``secure endpoint accepts certificate identity without anonymous token`` () = task {
    let port = nextTestPort()
    let server, cfg, root = mkSecureServer port
    try
        let clientConfig = mkClientConfig root
        let ed = CoreClientUtils.SelectEndpoint(clientConfig, cfg.EndpointUrl, true, 15_000)
        Assert.NotEqual(MessageSecurityMode.None, ed.SecurityMode)
        let endpoint = ConfiguredEndpoint(null, ed, EndpointConfiguration.Create(clientConfig))
        let! certificate = clientConfig.SecurityConfiguration.ApplicationCertificate.Find(true)
        Assert.NotNull(certificate)
        let! session =
            Session.Create(
                clientConfig,
                endpoint,
                false,
                "Secure-IT-Client",
                60_000u,
                UserIdentity(certificate),
                null)
        Assert.True(session.Connected)
        session.Close() |> ignore
        session.Dispose()
    finally
        server.Stop()
        server.Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}

[<Fact>]
let ``AddAsset + WriteSignal + Read via UA client roundtrip`` () = task {
    let port = nextTestPort()
    let gaid = GlobalAssetId "urn:dualsoft:asset:test01"
    let server, cfg, root = mkServerWithAssets port [| gaid |]
    try
        // 자산 등록.
        let sig' = SignalId "line1.test01.value"
        let ns = server.NodeManager.AddAssetWithDisplayNames(gaid, "TEST01", [ sig', "unit", BuiltInType.Double, "Readable Value" ])
        Assert.True(ns >= 2)
        Assert.True(server.NodeManager.ContainsNode(NodeId("Asset", uint16 ns)), "asset folder missing from predefined index")
        Assert.True(server.NodeManager.ContainsNode(NodeId(sig'.Value, uint16 ns)), "signal missing from predefined index")

        // 서버 측 WriteSignal → in-memory 갱신.
        Assert.True(server.NodeManager.WriteSignal(gaid, sig', box 42.5, DateTime.UtcNow, 0u))

        // UA client 로 접속 후 값 Read.
        let clientConfig = mkClientConfig root
        let ed = CoreClientUtils.SelectEndpoint(clientConfig, cfg.EndpointUrl, false, 15_000)
        let endpointConfig = EndpointConfiguration.Create(clientConfig)
        let endpoint = new ConfiguredEndpoint(null, ed, endpointConfig)
        let! session =
            Session.Create(
                clientConfig, endpoint, false, "IT-Client",
                60_000u, new UserIdentity(new AnonymousIdentityToken()), null)

        // Namespace tables 재동기화 (Session.Create 이후에도 동적 append 반영).
        session.FetchNamespaceTables()

        // NamespaceArray 에서 자산 URI 의 nsIndex 확인.
        let assetUri = sprintf "urn:ds:asset:%s" (Ds2.Core.Encoding.Base64Url.encode gaid.Value)
        let idx = session.NamespaceUris.GetIndex assetUri
        Assert.True(idx >= 0, sprintf "asset namespace not published: %s (server ns=%d)" assetUri ns)
        Assert.Equal(ns, int idx)

        // Variable NodeId 계산 (ADR-002).
        let nodeId = NodeId(sig'.Value, uint16 idx)

        // Hot-append 된 Variable은 NodeId 직접 Read뿐 아니라 일반 OPC client Browse에도 노출돼야 한다.
        let browseNodes =
            BrowseDescriptionCollection([|
                BrowseDescription(
                    NodeId = NodeId("Asset", uint16 idx),
                    BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = NodeId.Null,
                    IncludeSubtypes = true,
                    NodeClassMask = 0u,
                    ResultMask = uint32 BrowseResultMask.All)
            |])
        let mutable browseResults = BrowseResultCollection()
        let mutable browseDiagnostics = DiagnosticInfoCollection()
        session.Browse(null, null, 0u, browseNodes, &browseResults, &browseDiagnostics) |> ignore
        Assert.Single(browseResults) |> ignore
        Assert.Equal(StatusCodes.Good, browseResults.[0].StatusCode.Code)
        let browseNames = browseResults.[0].References |> Seq.map (fun r -> r.BrowseName.Name) |> Seq.toArray
        Assert.Contains(sig'.Value, browseNames)
        Assert.Equal(1, browseNames |> Array.filter ((=) sig'.Value) |> Array.length)
        let signalRef = browseResults.[0].References |> Seq.find (fun r -> r.BrowseName.Name = sig'.Value)
        Assert.Equal("Readable Value", signalRef.DisplayName.Text)

        let ids =
            ReadValueIdCollection([|
                ReadValueId(NodeId = nodeId, AttributeId = Attributes.Value)
            |])
        let mutable results = DataValueCollection()
        let mutable diagnostics = DiagnosticInfoCollection()
        session.Read(null, 0.0, TimestampsToReturn.Both, ids, &results, &diagnostics) |> ignore
        Assert.Equal(StatusCodes.Good, results.[0].StatusCode.Code)
        Assert.Equal(42.5, results.[0].Value :?> float)

        // String-based southbound values are coerced to the declared UA type centrally.
        Assert.True(server.NodeManager.WriteSignal(gaid, sig', box "43.25", DateTime.UtcNow, uint32 StatusCodes.Good))
        let mutable convertedResults = DataValueCollection()
        let mutable convertedDiagnostics = DiagnosticInfoCollection()
        session.Read(null, 0.0, TimestampsToReturn.Both, ids, &convertedResults, &convertedDiagnostics) |> ignore
        Assert.Equal(StatusCodes.Good, convertedResults.[0].StatusCode.Code)
        Assert.Equal(43.25, convertedResults.[0].Value :?> float)

        // Invalid data is retained as the previous typed value and explicitly marked bad.
        Assert.False(server.NodeManager.WriteSignal(gaid, sig', box "not-a-double", DateTime.UtcNow, uint32 StatusCodes.Good))
        let mutable mismatchResults = DataValueCollection()
        let mutable mismatchDiagnostics = DiagnosticInfoCollection()
        session.Read(null, 0.0, TimestampsToReturn.Both, ids, &mismatchResults, &mismatchDiagnostics) |> ignore
        Assert.Equal(StatusCodes.BadTypeMismatch, mismatchResults.[0].StatusCode.Code)
        Assert.Equal(1L, server.NodeManager.TypeMismatchCount)

        Assert.True(server.NodeManager.SetSignalQuality(gaid, sig', uint32 StatusCodes.BadNoCommunication, DateTime.UtcNow))
        let mutable qualityResults = DataValueCollection()
        let mutable qualityDiagnostics = DiagnosticInfoCollection()
        session.Read(null, 0.0, TimestampsToReturn.Both, ids, &qualityResults, &qualityDiagnostics) |> ignore
        Assert.Equal(StatusCodes.BadNoCommunication, qualityResults.[0].StatusCode.Code)

        session.Close() |> ignore
        session.Dispose()
    finally
        server.Stop()
        server.Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}

[<Fact>]
let ``CollectionPolicy is attached as deterministic UA properties`` () =
    let port = nextTestPort()
    let gaid = GlobalAssetId "urn:dualsoft:asset:policy-test"
    let server, _, root = mkServerWithAssets port [| gaid |]
    try
        let signalId = SignalId "line1.policy.temperature"
        let policy : SignalPolicy = {
            SignalId = signalId
            AcquisitionMode = AcquisitionMode.ChangeOfValue
            SamplingIntervalMs = Some 250
            PublishingIntervalMs = Some 500
            DeadbandAbsolute = Some 0.5
            DeadbandPercent = None
            EngineeringRangeLow = None
            EngineeringRangeHigh = None
            QueueSize = Some 25
            Retention = "P90D"
        }
        let ns =
            server.NodeManager.AddAssetWithHierarchyAndPolicies(
                gaid,
                "POLICY01",
                [ [], signalId, "degC", BuiltInType.Double, "Temperature", Some policy ])
        let hasPolicyProperty name =
            server.NodeManager.ContainsNode(NodeId($"Policy/{signalId.Value}/{name}", uint16 ns))
        Assert.True(hasPolicyProperty SignalPolicyUaMetadata.AcquisitionMode)
        Assert.True(hasPolicyProperty SignalPolicyUaMetadata.SamplingIntervalMs)
        Assert.True(hasPolicyProperty SignalPolicyUaMetadata.PublishingIntervalMs)
        Assert.True(hasPolicyProperty SignalPolicyUaMetadata.DeadbandAbsolute)
        Assert.True(hasPolicyProperty SignalPolicyUaMetadata.QueueSize)
        Assert.True(hasPolicyProperty SignalPolicyUaMetadata.Retention)
        Assert.False(hasPolicyProperty SignalPolicyUaMetadata.DeadbandPercent)
    finally
        server.Stop()
        server.Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()

[<Fact>]
let ``RaiseAssetEvent Method Call returns Good`` () = task {
    let port = nextTestPort()
    let server, cfg, root = mkServer port
    try
        let gaid = GlobalAssetId "urn:dualsoft:asset:evt01"
        let _ = server.NodeManager.AddAsset(gaid, "EVT01", [])

        let clientConfig = mkClientConfig root
        let ed = CoreClientUtils.SelectEndpoint(clientConfig, cfg.EndpointUrl, false, 15_000)
        let endpointConfig = EndpointConfiguration.Create(clientConfig)
        let endpoint = new ConfiguredEndpoint(null, ed, endpointConfig)
        let! session =
            Session.Create(
                clientConfig, endpoint, false, "IT-Client-Event",
                60_000u, new UserIdentity(new AnonymousIdentityToken()), null)

        session.FetchNamespaceTables()

        // Determine ns for asset.
        let assetUri = sprintf "urn:ds:asset:%s" (Ds2.Core.Encoding.Base64Url.encode gaid.Value)
        let idx = session.NamespaceUris.GetIndex assetUri
        Assert.True(idx >= 0)

        // Method 호출.
        use received = new ManualResetEventSlim(false)
        let mutable receivedMessage = ""
        use subscription = new Subscription(session.DefaultSubscription)
        subscription.PublishingInterval <- 100
        let item = new MonitoredItem(subscription.DefaultItem)
        item.StartNodeId <- ObjectIds.Server
        item.AttributeId <- Attributes.EventNotifier
        item.QueueSize <- 10u
        let filter = EventFilter()
        filter.SelectClauses <- SimpleAttributeOperandCollection()
        let messageOperand = SimpleAttributeOperand()
        messageOperand.TypeDefinitionId <- ObjectTypeIds.BaseEventType
        messageOperand.AttributeId <- Attributes.Value
        messageOperand.BrowsePath <- QualifiedNameCollection([| QualifiedName(BrowseNames.Message) |])
        filter.SelectClauses.Add messageOperand
        filter.WhereClause <- ContentFilter()
        item.Filter <- filter
        item.add_Notification(MonitoredItemNotificationEventHandler(fun _ args ->
            match args.NotificationValue with
            | :? EventFieldList as eventFields when eventFields.EventFields.Count > 0 ->
                match eventFields.EventFields.[0].Value with
                | :? LocalizedText as message ->
                    receivedMessage <- message.Text
                    received.Set()
                | _ -> ()
            | _ -> ()))
        subscription.AddItem item
        session.AddSubscription subscription |> ignore
        subscription.Create()
        Assert.True(ServiceResult.IsGood item.Status.Error, $"event subscription rejected: {item.Status.Error}")

        let methodNode = NodeId("Events/RaiseAssetEvent", uint16 idx)
        let eventsObject = NodeId("Events", uint16 idx)
        let inputs : obj array = [|
            "urn:opcfoundation:autoid:OpticalScanEventType"
            "line1.evt01.code"
            DateTime.UtcNow
            """{"code":"1234567890","symbology":"CODE128"}"""
        |]
        let outputs = session.Call(eventsObject, methodNode, inputs)
        Assert.NotNull(outputs)
        Assert.Equal(2, outputs.Count)
        Assert.True(received.Wait(5000), "raised event was not delivered to EventNotifier subscriber")
        Assert.Contains("\"eventTypeSemanticId\":\"urn:opcfoundation:autoid:OpticalScanEventType\"", receivedMessage)
        Assert.Contains("\"payload\":{\"code\":\"1234567890\"", receivedMessage)

        subscription.Delete(true)
        session.Close() |> ignore
        session.Dispose()
    finally
        server.Stop()
        server.Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}

[<Fact>]
let ``RaiseAssetEvent rejects payload with sourceTimestamp field`` () = task {
    let port = nextTestPort()
    let server, cfg, root = mkServer port
    try
        let gaid = GlobalAssetId "urn:dualsoft:asset:evt02"
        let _ = server.NodeManager.AddAsset(gaid, "EVT02", [])

        let clientConfig = mkClientConfig root
        let ed = CoreClientUtils.SelectEndpoint(clientConfig, cfg.EndpointUrl, false, 15_000)
        let endpointConfig = EndpointConfiguration.Create(clientConfig)
        let endpoint = new ConfiguredEndpoint(null, ed, endpointConfig)
        let! session =
            Session.Create(
                clientConfig, endpoint, false, "IT-Client-Event-Reject",
                60_000u, new UserIdentity(new AnonymousIdentityToken()), null)

        session.FetchNamespaceTables()

        let assetUri = sprintf "urn:ds:asset:%s" (Ds2.Core.Encoding.Base64Url.encode gaid.Value)
        let idx = session.NamespaceUris.GetIndex assetUri
        let methodNode = NodeId("Events/RaiseAssetEvent", uint16 idx)
        let eventsObject = NodeId("Events", uint16 idx)

        // ADR-003 §1a · payload 에 "sourceTimestamp" 넣으면 거부되어야 함.
        let inputs = [|
            box "urn:opcfoundation:autoid:OpticalScanEventType"
            box "line1.evt02.code"
            box DateTime.UtcNow
            box """{"code":"1234","sourceTimestamp":"2026-07-15T10:00:00Z"}"""
        |]
        let mutable rejected = false
        try
            session.Call(eventsObject, methodNode, inputs) |> ignore
        with
        | :? ServiceResultException as ex when ex.StatusCode = StatusCodes.BadArgumentsMissing ->
            rejected <- true
        | :? ServiceResultException ->
            rejected <- true    // 어떤 형태로든 거부되면 OK

        Assert.True(rejected, "payload 시각 필드 삽입은 거부되어야 함 (ADR-003 §1a)")

        session.Close() |> ignore
        session.Dispose()
    finally
        server.Stop()
        server.Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}

[<Fact>]
let ``RaiseAssetEvent rejects payload above transport-safe limit`` () =
    let port = nextTestPort()
    let server, _, root = mkServer port
    try
        let gaid = GlobalAssetId "urn:dualsoft:asset:evt-large"
        server.NodeManager.AddAsset(gaid, "EVT-LARGE", []) |> ignore
        let payload = sprintf "{\"value\":\"%s\"}" (System.String(Array.create 262_144 'x'))
        let result = server.NodeManager.RaiseAssetEvent(
            gaid,
            "urn:opcfoundation:autoid:OpticalScanEventType",
            "line1.evt-large.code",
            DateTime.UtcNow,
            payload)
        Assert.True(result.IsNone)
    finally
        server.Stop()
        server.Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
