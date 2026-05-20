namespace Ds2.LightHouseService.IntegrationTests

open System
open System.IO
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Hosting
open Ds2.LightHouse
open Ds2.LightHouseService

/// **C1 (s6-r45)** — IT 의 hybrid path round-trip 검증용 deterministic mock embedder.
/// lib Tests 의 `MockEmbedder` (private) 와 동일 박제 — query hash 기반 1차 element + 나머지 0.
/// dim = `SqliteStore.EmbeddingDimension` (1024). stateless 라 Dispose no-op.
/// `IEmbeddingProvider.GenerateAsync` 의 ct 는 무시 (mock 즉시 반환).
type private MockEmbedder() =
    interface IEmbeddingProvider with
        member _.Dimension = SqliteStore.EmbeddingDimension
        member _.GenerateAsync(inputs, _ct) =
            let dim = SqliteStore.EmbeddingDimension
            let vectors =
                inputs |> Array.map (fun s ->
                    let h = float32 (s.GetHashCode() % 100) * 0.01f
                    Array.init dim (fun i -> if i = 0 then h else 0.0f))
            Task.FromResult(vectors)
    interface IDisposable with
        member _.Dispose() = ()

/// Phase S5e — e2e round-trip 용 in-process service fixture.
///
/// 책임:
/// 1. temp storage root + 4 subdir (Collections/Staging/Logs/Audit) 자동 생성 (`Storage.initialize`).
/// 2. in-memory self-signed cert (`CertificateRequest.CreateSelfSigned`) — PFX file IO 우회.
/// 3. 자체 `ServiceConfig` record — DPAPI 우회 (PSK 평문 직접 전달).
/// 4. Kestrel localhost:0 random port → 실 HTTPS bind + `app.Urls` 에서 actual bound port 추출.
/// 5. 신뢰 우회 `HttpClient` factory — `ServerCertificateCustomValidationCallback = always true` (test only).
/// 6. cleanup: `app.StopAsync` + temp dir 재귀 제거.
///
/// production 부담 = 0 — `Program.configureApp` 의 시그니처 (cfg/psk/cert) 그대로 사용. fixture 가 DPAPI/file IO 단계만 우회.
///
/// xunit pattern: `IClassFixture<ServiceFixture>` (per test class). 동시 실행 시 port collision 회피 = localhost:0.
type ServiceFixture() =
    let mutable storageRoot : string = ""
    let mutable app : Microsoft.AspNetCore.Builder.WebApplication = null
    let mutable cert : X509Certificate2 = null
    let mutable baseAddress : Uri = null
    let psk = "test-psk-" + Guid.NewGuid().ToString("N")
    let userIdentity = "integration-test@dualsoft.com"

    /// in-memory self-signed cert — `localhost` CN + ServerAuthentication EKU + 1d 유효.
    /// PFX byte 로 export 후 `X509CertificateLoader.LoadPkcs12FromPfx` 로 재로드 — production 의
    /// `LoadPkcs12FromFile` 와 동일 의미론 (key + EKU 보존). password 는 별 byte hex (random).
    static let createSelfSignedCert () : X509Certificate2 =
        use rsa = RSA.Create(2048)
        let req = CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        // ServerAuthentication EKU — Kestrel UseHttps 가 인증서 valid 여부 검사 시 통과.
        let oid = Oid("1.3.6.1.5.5.7.3.1") // serverAuth
        let oids = OidCollection()
        oids.Add(oid) |> ignore
        req.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(oids, true))
        // SAN — `localhost` IP/DNS 둘 다 cover.
        let sanBuilder = SubjectAlternativeNameBuilder()
        sanBuilder.AddDnsName("localhost")
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback)
        req.CertificateExtensions.Add(sanBuilder.Build())

        let notBefore = DateTimeOffset.UtcNow.AddMinutes(-5.0)
        let notAfter = DateTimeOffset.UtcNow.AddDays(1.0)
        let signed = req.CreateSelfSigned(notBefore, notAfter)
        // CertificateRequest 의 self-signed 결과는 private key 가 ephemeral — PFX export 후 재로드해야 Kestrel 가 사용 가능.
        let pwd = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
        let pfxBytes = signed.Export(X509ContentType.Pkcs12, pwd)
        X509CertificateLoader.LoadPkcs12(pfxBytes, pwd)

    /// 자체 ServiceConfig — production 의 storageRoot/listenUrl 만 test 값으로 override, 나머지는 production default.
    /// PSK / TLS password 는 평문 전달 (Program.configureApp 시그니처가 평문 받음).
    let buildConfig (storageRoot: string) (listenUrl: string) : ServiceConfig =
        {
            SchemaVersion = ConfigSchema.Current
            ListenUrl = listenUrl
            TlsCertPath = ""                            // configureApp 미사용 (cert object 직접 전달)
            TlsCertPasswordEncrypted = ""               // configureApp 미사용
            PreSharedKeyEncrypted = ""                  // configureApp 미사용 (psk 평문 직접 전달)
            StorageRoot = storageRoot
            MaxUploadBytes = 10737418240L
            ZipBombRatioLimit = 50
            SessionIdleTtlMinutes = 240
            StagingSweepIntervalMinutes = 10
            LogRetentionDays = 30
            LogMaxSizeMB = 100
            AuditRetentionDays = 365
            IndexerVersionRange = { Min = "1.0.0"; Max = "2.99.99" }
            // **s6-r45 C1** — IT default 가 hybrid path 활성 (Enabled=true). embedder factory override 박제로
            // Ollama daemon 의존 0 (configureApp 가 cfg.Embedding 무시 + MockEmbedder factory 사용).
            // BaseUrl/Model/Dimension 은 placeholder — override 박제 시 미사용 (자가 검열 정합 doc).
            Embedding = {
                Enabled = true
                BaseUrl = "http://localhost:11434"
                Model = "bge-m3"
                Dimension = 1024
            }
            // **s6-r53 D-S7-1** — IT default 가 Mtls.Mode="off" (현행 PSK 단독 인증, 회귀 0). mTLS round-trip
            // 검증은 별 fixture (MtlsRoundTripTests 의 buildConfig override) 박제.
            Mtls = {
                Mode = MtlsMode.Off
                AllowedThumbprints = Array.empty
            }
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() : Task =
            task {
                // 1. temp storage root + Storage.initialize
                storageRoot <- Path.Combine(Path.GetTempPath(), "lhs-it-" + Guid.NewGuid().ToString("N"))
                Storage.initialize storageRoot |> ignore

                // 2. in-memory self-signed cert
                cert <- createSelfSignedCert ()

                // 3. cfg — Kestrel localhost:0 random port (OS 가 free port 할당)
                let cfg = buildConfig storageRoot "https://127.0.0.1:0"

                // 4. configureApp + StartAsync — embedderFactory override 박제 (C1 mock path).
                let mockFactory : unit -> IEmbeddingProvider option =
                    fun () -> Some (new MockEmbedder() :> IEmbeddingProvider)
                let webApp = Program.configureApp cfg psk cert (Some mockFactory)
                do! webApp.StartAsync()
                app <- webApp

                // 5. actual bound port 추출 — `app.Urls` 가 listen 후 실 URL 노출 (e.g. "https://127.0.0.1:53421")
                let firstUrl =
                    app.Urls
                    |> Seq.tryHead
                    |> Option.defaultWith (fun () ->
                        failwith "ServiceFixture: app.Urls 비어있음 — Kestrel listen 실패")
                baseAddress <- Uri firstUrl
            } :> Task

        member _.DisposeAsync() : Task =
            task {
                if not (isNull app) then
                    do! app.StopAsync(TimeSpan.FromSeconds(5.0))
                    do! (app :> IAsyncDisposable).DisposeAsync().AsTask()
                if not (isNull cert) then cert.Dispose()
                if not (String.IsNullOrEmpty storageRoot) && Directory.Exists storageRoot then
                    // best-effort — Logs/ 안 log4net 핸들이 잠재적으로 잡혀있을 수 있어 try/catch 한정.
                    try Directory.Delete(storageRoot, true) with _ -> ()
            } :> Task

    /// 신뢰 우회 HttpClient — self-signed cert 검증 통과. test only.
    /// 매 호출마다 새 instance — caller 가 `use` 로 dispose 의무 (HttpClient 자체는 reuse 권장이나 fixture lifetime 안에서 무관).
    member _.CreateAuthClient() : HttpClient =
        let handler = new HttpClientHandler()
        handler.ServerCertificateCustomValidationCallback <-
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        let client = new HttpClient(handler, disposeHandler = true)
        client.BaseAddress <- baseAddress
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + psk)
        client.DefaultRequestHeaders.Add("X-User-Identity", userIdentity)
        client

    /// PSK / X-User-Identity 누락한 baseline HttpClient — 401 시나리오 검증용.
    member _.CreateBareClient() : HttpClient =
        let handler = new HttpClientHandler()
        handler.ServerCertificateCustomValidationCallback <-
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        let client = new HttpClient(handler, disposeHandler = true)
        client.BaseAddress <- baseAddress
        client

    member _.BaseAddress = baseAddress
    member _.Psk = psk
    member _.UserIdentity = userIdentity
    member _.StorageRoot = storageRoot

    /// `configureApp` 빌드 결과 의 DI container 노출 — K2 회귀 차단 Fact 의
    /// `IOptions<FormOptions>` 추출 surface (E2eRoundTripTests).
    member _.Services = app.Services
