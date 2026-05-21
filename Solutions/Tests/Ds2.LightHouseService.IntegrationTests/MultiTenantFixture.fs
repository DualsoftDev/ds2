namespace Ds2.LightHouseService.IntegrationTests

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Hosting
open Ds2.LightHouse
open Ds2.LightHouseService

/// **Phase S7 D-S7-4 (B-R14, s6-r74 b2)** — multi-tenant T2/T3 round-trip 검증용 fixture.
///
/// `ServiceFixture` 는 T1 default 박제 → T2/T3 분기 fact 박제 불가 (config.MultiTenant.Mode 변경 의무).
/// 본 fixture 는 두 user (alice/bob) 의 HttpClient 노출 + T2/T3 mode 별 별 fixture 분리:
/// - `MultiTenantT2Fixture` — Mode="T2" (per-user namespace, ImportedBy 일치 검증)
/// - `MultiTenantT3Fixture` — Mode="T3" (collection ACL, admin 이 acl 편집 후 분기 검증)
///
/// 공통 helper = `private MultiTenantFixtureCommon` (cert/storage/HttpClient factory). xunit 의
/// IClassFixture 가 mode parameter 미지원 → 별 fixture type 으로 분리.
[<RequireQualifiedAccess>]
module internal MultiTenantFixtureHelpers =

    [<Literal>]
    let UserAlice = "alice@dualsoft.com"
    [<Literal>]
    let UserBob = "bob@dualsoft.com"

    /// in-memory self-signed cert — ServiceFixture 의 동일 패턴 (localhost CN + serverAuth EKU + 1d).
    let createSelfSignedCert () : X509Certificate2 =
        use rsa = RSA.Create(2048)
        let req = CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        let oid = Oid("1.3.6.1.5.5.7.3.1")
        let oids = OidCollection()
        oids.Add(oid) |> ignore
        req.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(oids, true))
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

    let buildConfig (storageRoot: string) (listenUrl: string) (mode: string) : ServiceConfig =
        {
            SchemaVersion = ConfigSchema.Current
            ListenUrl = listenUrl
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
                Enabled = false
                BaseUrl = "http://localhost:11434"
                Model = "bge-m3"
                Dimension = 1024
            }
            Mtls = {
                Mode = MtlsMode.Off
                AllowedThumbprints = Array.empty
            }
            // **B-R14 분기** — T2/T3 mode 박제 (caller 결정).
            MultiTenant = { Mode = mode }
            // s6-r80 B-S7-4 — backward-compat null.
            AdminUsers = null
        }

    let createClient (baseAddress: Uri) (psk: string) (userIdentity: string) : HttpClient =
        let handler = new HttpClientHandler()
        handler.ServerCertificateCustomValidationCallback <-
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        let client = new HttpClient(handler, disposeHandler = true)
        client.BaseAddress <- baseAddress
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + psk)
        client.DefaultRequestHeaders.Add("X-User-Identity", userIdentity)
        client


/// **Phase S7 D-S7-4 (B-R14, s6-r74 b2)** — MultiTenant Mode="T2" round-trip fixture.
/// T2 = ImportedBy 일치 검증 (per-user namespace). alice 가 collection 등록 시 ImportedBy=alice 자동 stamp,
/// bob 의 GET/POST/DELETE 시도는 Hidden 분기 (404).
type MultiTenantT2Fixture() =
    let mutable storageRoot : string = ""
    let mutable app : Microsoft.AspNetCore.Builder.WebApplication = null
    let mutable cert : X509Certificate2 = null
    let mutable baseAddress : Uri = null
    let psk = "test-psk-mt2-" + Guid.NewGuid().ToString("N")

    interface IAsyncLifetime with
        member _.InitializeAsync() : Task =
            task {
                storageRoot <- Path.Combine(Path.GetTempPath(), "lhs-it-mt2-" + Guid.NewGuid().ToString("N"))
                Storage.initialize storageRoot |> ignore
                cert <- MultiTenantFixtureHelpers.createSelfSignedCert ()
                let cfg = MultiTenantFixtureHelpers.buildConfig storageRoot "https://127.0.0.1:0" MultiTenantMode.T2
                let webApp = Program.configureApp cfg psk cert None None
                do! webApp.StartAsync()
                app <- webApp
                let firstUrl =
                    app.Urls
                    |> Seq.tryHead
                    |> Option.defaultWith (fun () ->
                        failwith "MultiTenantT2Fixture: app.Urls 비어있음 — Kestrel listen 실패")
                baseAddress <- Uri firstUrl
            } :> Task

        member _.DisposeAsync() : Task =
            task {
                if not (isNull app) then
                    do! app.StopAsync(TimeSpan.FromSeconds(5.0))
                    do! (app :> IAsyncDisposable).DisposeAsync().AsTask()
                if not (isNull cert) then cert.Dispose()
                if not (String.IsNullOrEmpty storageRoot) && Directory.Exists storageRoot then
                    try Directory.Delete(storageRoot, true) with _ -> ()
            } :> Task

    member _.CreateAliceClient() : HttpClient =
        MultiTenantFixtureHelpers.createClient baseAddress psk MultiTenantFixtureHelpers.UserAlice

    member _.CreateBobClient() : HttpClient =
        MultiTenantFixtureHelpers.createClient baseAddress psk MultiTenantFixtureHelpers.UserBob

    member _.BaseAddress = baseAddress
    member _.Psk = psk
    member _.UserAlice = MultiTenantFixtureHelpers.UserAlice
    member _.UserBob = MultiTenantFixtureHelpers.UserBob


/// **Phase S7 D-S7-4 (B-R14, s6-r74 b2)** — MultiTenant Mode="T3" round-trip fixture.
/// T3 = collection ACL 검증. admin (alice — single trust pool 정합으로 모든 user 가 admin endpoint 호출 가능)
/// 이 `PUT /admin/collections/{id}/acl` 으로 acl 박제 후 alice/bob/charlie 의 visibility/readOnly 분기 검증.
type MultiTenantT3Fixture() =
    let mutable storageRoot : string = ""
    let mutable app : Microsoft.AspNetCore.Builder.WebApplication = null
    let mutable cert : X509Certificate2 = null
    let mutable baseAddress : Uri = null
    let psk = "test-psk-mt3-" + Guid.NewGuid().ToString("N")

    interface IAsyncLifetime with
        member _.InitializeAsync() : Task =
            task {
                storageRoot <- Path.Combine(Path.GetTempPath(), "lhs-it-mt3-" + Guid.NewGuid().ToString("N"))
                Storage.initialize storageRoot |> ignore
                cert <- MultiTenantFixtureHelpers.createSelfSignedCert ()
                let cfg = MultiTenantFixtureHelpers.buildConfig storageRoot "https://127.0.0.1:0" MultiTenantMode.T3
                let webApp = Program.configureApp cfg psk cert None None
                do! webApp.StartAsync()
                app <- webApp
                let firstUrl =
                    app.Urls
                    |> Seq.tryHead
                    |> Option.defaultWith (fun () ->
                        failwith "MultiTenantT3Fixture: app.Urls 비어있음 — Kestrel listen 실패")
                baseAddress <- Uri firstUrl
            } :> Task

        member _.DisposeAsync() : Task =
            task {
                if not (isNull app) then
                    do! app.StopAsync(TimeSpan.FromSeconds(5.0))
                    do! (app :> IAsyncDisposable).DisposeAsync().AsTask()
                if not (isNull cert) then cert.Dispose()
                if not (String.IsNullOrEmpty storageRoot) && Directory.Exists storageRoot then
                    try Directory.Delete(storageRoot, true) with _ -> ()
            } :> Task

    member _.CreateAliceClient() : HttpClient =
        MultiTenantFixtureHelpers.createClient baseAddress psk MultiTenantFixtureHelpers.UserAlice

    member _.CreateBobClient() : HttpClient =
        MultiTenantFixtureHelpers.createClient baseAddress psk MultiTenantFixtureHelpers.UserBob

    member _.BaseAddress = baseAddress
    member _.Psk = psk
    member _.UserAlice = MultiTenantFixtureHelpers.UserAlice
    member _.UserBob = MultiTenantFixtureHelpers.UserBob
