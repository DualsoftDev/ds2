namespace Ds2.LightHouseService.IntegrationTests

open System
open System.IO
open System.Net.Http
open System.Security.Authentication
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Hosting
open Ds2.LightHouse
open Ds2.LightHouseService

/// **B5 phase 4 (s6-r69)** — mTLS server mode="required" e2e round-trip.
///
/// 검증 범위:
/// 1. **valid client cert** — server 의 ClientCertificateMode.RequireCertificate 통과 + GET /collections 200
/// 2. **client cert 미박제** — TLS handshake 거부 (HttpRequestException)
/// 3. **thumbprint mismatch** — override 가 thumbprint match 실패 → handshake reject
///
/// self-signed client cert 의 chain 부재 회피 = `mtlsValidationOverride` 박제 (chain.Build skip + whitelist 만 검증).
/// configureMtls 의 production path (chain.Build) 자체는 s6-r53 의 unit fact / s6-r66 의 Mode literal 분기 fact 가 박제.
/// 본 IT 는 handshake-level e2e (Kestrel RequireCertificate + HttpClient ClientCertificates 박제 → 200/refused).
type MtlsRequiredFixture() =
    let mutable storageRoot : string = ""
    let mutable app : Microsoft.AspNetCore.Builder.WebApplication = null
    let mutable serverCert : X509Certificate2 = null
    let mutable clientCert : X509Certificate2 = null
    // **R-B N-2 (s6-r72+ external review hotfix)** — wrongCert lifetime fixture-managed.
    // 이전 `CreateMtlsClientWithWrongCert` 의 `use wrongCert = ...` 가 method return 시점 Dispose → handler 가
    // disposed cert handle 보유 → handshake reject 이유가 "disposed cert" 로 wash-out (IT 의미 약화).
    // 본 mutable field 가 fixture lifetime 동안 cert 유지, DisposeAsync 가 회수.
    let mutable wrongCert : X509Certificate2 = null
    let mutable baseAddress : Uri = null
    let psk = "test-psk-mtls-" + Guid.NewGuid().ToString("N")
    // **s6-r70 review C-3** — AuthMiddleware 가 mtls.mode != off 시 cert subject CN ↔ X-User-Identity 강제.
    // fixture 의 client cert subject = `CN=<userIdentity>` 로 박제 (handshake-level 통과 + AuthMiddleware 통과).
    let userIdentity = "mtls-it-client"

    /// in-memory self-signed cert with EKU.
    static let createCert (subject: string) (eku: string) : X509Certificate2 =
        use rsa = RSA.Create(2048)
        let req = CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        let oidColl = OidCollection()
        oidColl.Add(Oid eku) |> ignore
        req.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(oidColl, true))
        let sanBuilder = SubjectAlternativeNameBuilder()
        sanBuilder.AddDnsName("localhost")
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback)
        req.CertificateExtensions.Add(sanBuilder.Build())
        let notBefore = DateTimeOffset.UtcNow.AddMinutes(-5.0)
        let notAfter = DateTimeOffset.UtcNow.AddDays(1.0)
        let signed = req.CreateSelfSigned(notBefore, notAfter)
        let pwd = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
        let pfxBytes = signed.Export(X509ContentType.Pkcs12, pwd)
        X509CertificateLoader.LoadPkcs12(pfxBytes, pwd)

    interface IAsyncLifetime with
        member _.InitializeAsync() : Task =
            task {
                storageRoot <- Path.Combine(Path.GetTempPath(), "lhs-it-mtls-" + Guid.NewGuid().ToString("N"))
                Storage.initialize storageRoot |> ignore
                // server cert = ServerAuthentication EKU. client cert = ClientAuthentication EKU.
                serverCert <- createCert "CN=localhost" "1.3.6.1.5.5.7.3.1"
                // **s6-r70 review C-3** — client cert CN = userIdentity (AuthMiddleware mtls subject 강제 정합).
                clientCert <- createCert (sprintf "CN=%s" userIdentity) "1.3.6.1.5.5.7.3.2"
                // server cfg — Mtls.Mode="required" + AllowedThumbprints = [clientCert thumbprint]
                let clientThumb = Config.normalizeThumbprint clientCert.Thumbprint
                let cfg : ServiceConfig = {
                    SchemaVersion = ConfigSchema.Current
                    ListenUrl = "https://127.0.0.1:0"
                    TlsCertPath = ""
                    TlsCertPasswordEncrypted = ""
                    PreSharedKeyEncrypted = ""
                    StorageRoot = storageRoot
                    MaxUploadBytes = 10737418240L
                    ZipBombRatioLimit = 50
                    SessionIdleTtlMinutes = 240
                    StagingSweepIntervalMinutes = 10
                    LogRetentionDays = 30
                    LogMaxSizeMB = 100
                    AuditRetentionDays = 365
                    IndexerVersionRange = { Min = "1.0.0"; Max = "2.99.99" }
                    Embedding = {
                        Enabled = false  // BM25-only (mTLS handshake 자체가 본 fixture 의 검증 대상, embedding 무관)
                        BaseUrl = "http://localhost:11434"
                        Model = "bge-m3"
                        Dimension = 1024
                    }
                    Mtls = {
                        Mode = MtlsMode.Required
                        AllowedThumbprints = [| clientThumb |]
                    }
                    MultiTenant = { Mode = MultiTenantMode.T1 }
                    AdminUsers = null
                }
                // mTLS validation override — chain.Build 우회 + thumbprint match 만 검증 (self-signed 정합).
                let validation (cert: X509Certificate2) _chain _errors =
                    Config.normalizeThumbprint cert.Thumbprint = clientThumb
                let webApp = Program.configureApp cfg psk serverCert None (Some validation)
                do! webApp.StartAsync()
                app <- webApp
                let firstUrl =
                    app.Urls
                    |> Seq.tryHead
                    |> Option.defaultWith (fun () -> failwith "MtlsRequiredFixture: app.Urls 비어있음")
                baseAddress <- Uri firstUrl
            } :> Task

        member _.DisposeAsync() : Task =
            task {
                if not (isNull app) then
                    do! app.StopAsync(TimeSpan.FromSeconds(5.0))
                    do! (app :> IAsyncDisposable).DisposeAsync().AsTask()
                if not (isNull serverCert) then serverCert.Dispose()
                if not (isNull clientCert) then clientCert.Dispose()
                // R-B N-2 — fixture-managed wrongCert cleanup.
                if not (isNull wrongCert) then wrongCert.Dispose()
                if not (String.IsNullOrEmpty storageRoot) && Directory.Exists storageRoot then
                    try Directory.Delete(storageRoot, true) with _ -> ()
            } :> Task

    /// HttpClient with client cert 박제. server cert trust 우회 + PSK 헤더 동봉.
    member _.CreateMtlsClient(?withClientCert: bool) : HttpClient =
        let attachCert = defaultArg withClientCert true
        let handler = new HttpClientHandler()
        handler.ServerCertificateCustomValidationCallback <-
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        if attachCert then
            handler.ClientCertificates.Add(clientCert) |> ignore
        let client = new HttpClient(handler, disposeHandler = true)
        client.BaseAddress <- baseAddress
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + psk)
        client.DefaultRequestHeaders.Add("X-User-Identity", userIdentity)
        client

    /// 별 client cert (whitelist 미박제) 으로 HttpClient — thumbprint mismatch 검증용.
    ///
    /// **R-B N-2 (s6-r72+ external review hotfix)** — wrongCert lifetime fixture-managed (이전 `use wrongCert`
    /// 가 return 시점 Dispose 회귀 차단). 첫 호출 시 lazy 생성 + fixture field 박제 → DisposeAsync 가 회수.
    /// 본 method 가 여러 번 호출되어도 같은 cert 재사용 (thumbprint mismatch 의미 유지).
    member _.CreateMtlsClientWithWrongCert() : HttpClient =
        if isNull wrongCert then
            wrongCert <- createCert "CN=wrong-client" "1.3.6.1.5.5.7.3.2"
        let handler = new HttpClientHandler()
        handler.ServerCertificateCustomValidationCallback <-
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        handler.ClientCertificates.Add(wrongCert) |> ignore
        let client = new HttpClient(handler, disposeHandler = true)
        client.BaseAddress <- baseAddress
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + psk)
        client.DefaultRequestHeaders.Add("X-User-Identity", userIdentity)
        client


/// e2e fact 묶음. xunit 패턴: `IClassFixture<MtlsRequiredFixture>` (per test class). 동시 실행 시 port collision
/// 회피 = localhost:0. handshake-level reject 의 예외 type 은 .NET runtime / OS / TLS layer 의존 (HttpRequestException
/// 또는 IOException 또는 AuthenticationException 또는 inner-wrap) — `isHandshakeReject` helper 가 4 가지 후보 통합 검증.
type MtlsRoundTripTests(fixture: MtlsRequiredFixture) =
    interface IClassFixture<MtlsRequiredFixture>

    static member private IsHandshakeReject (ex: exn) : bool =
        let isTlsType (e: exn) =
            e :? HttpRequestException || e :? IOException || e :? AuthenticationException
        isTlsType ex || (not (isNull ex.InnerException) && isTlsType ex.InnerException)

    [<Fact>]
    member _.``mTLS required + valid client cert → 200 OK`` () = task {
        use client = fixture.CreateMtlsClient()
        let! resp = client.GetAsync("/collections")
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode)
    }

    [<Fact>]
    member _.``mTLS required + client cert 미박제 → handshake 거부`` () = task {
        use client = fixture.CreateMtlsClient(withClientCert = false)
        // TLS handshake 자체에서 reject — HttpClient.GetAsync 가 HttpRequestException / IOException / AuthenticationException throw.
        // .NET 9 의 Kestrel + RequireCertificate 미박제 client = handshake abort.
        let! ex = Assert.ThrowsAnyAsync<Exception>(fun () -> client.GetAsync("/collections") :> Task)
        Assert.True(
            MtlsRoundTripTests.IsHandshakeReject ex,
            sprintf "예상 = TLS handshake 거부 류 예외, 실 = %s: %s" (ex.GetType().Name) ex.Message)
    }

    [<Fact>]
    member _.``mTLS required + whitelist 미일치 thumbprint → handshake 거부`` () = task {
        use client = fixture.CreateMtlsClientWithWrongCert()
        let! ex = Assert.ThrowsAnyAsync<Exception>(fun () -> client.GetAsync("/collections") :> Task)
        Assert.True(
            MtlsRoundTripTests.IsHandshakeReject ex,
            sprintf "예상 = TLS handshake 거부 류 예외, 실 = %s: %s" (ex.GetType().Name) ex.Message)
    }
