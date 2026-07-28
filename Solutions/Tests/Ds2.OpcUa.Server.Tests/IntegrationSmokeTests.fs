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
    }
    let appConfig = ServerConfiguration.build cfg
    let prep = ServerConfiguration.validateAndPrepare appConfig
    prep.GetAwaiter().GetResult() |> ignore
    let appInstance = new ApplicationInstance(ApplicationConfiguration = appConfig)
    let managedNamespaces = managedAssets |> Array.map allocator.GlobalAssetIdToUri
    let server = new DsUaServer(allocator, managedNamespaces)
    appInstance.Start(server).GetAwaiter().GetResult()
    server, cfg, root

let private mkServer port = mkServerWithAssets port [||]

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

        session.Close() |> ignore
        session.Dispose()
    finally
        server.Stop()
        server.Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}

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
