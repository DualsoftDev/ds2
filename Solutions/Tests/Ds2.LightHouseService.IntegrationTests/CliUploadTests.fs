module Ds2.LightHouseService.IntegrationTests.CliUploadTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Runtime.ExceptionServices
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse.Cli
open Ds2.LightHouseService.IntegrationTests

/// Phase S6 P1 — CLI upload e2e suite.
///
/// 검증 범위:
/// - `Packager.{createStaging, copyToStaging, runIngestInStaging, writeMeta, createZip}` in-process round-trip
/// - `LightHouseClient.uploadCollection` 의 multipart wire 정합 (PSK Bearer + X-User-Identity + server 발급 guid)
/// - 인증 실패 (잘못된 PSK) → `LightHouseAuthError`
/// - 인증 통과 + plain HTTP 거부 (HTTPS-only enforcement)
///
/// fixture = ServiceFixture 재사용 (단일 Kestrel HTTPS bind, in-memory self-signed cert).

type CliUploadTests(fixture: ServiceFixture) =
    let psk = fixture.Psk
    let userIdentity = fixture.UserIdentity

    /// 사용자 폴더 시뮬레이션 — temp dir + 1 텍스트 파일.
    let buildSourceFolder () : string =
        let dir = Path.Combine(Path.GetTempPath(), "lh-cli-src-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(
            Path.Combine(dir, "sample.txt"),
            "# Heading\n\nSample content for CLI upload round-trip.\n",
            Encoding.UTF8)
        dir

    /// 1 source 폴더 → staging copy → 색인 → meta.json → zip 통째 round-trip.
    [<Fact>]
    member _.``Packager — staging + 색인 + meta + zip 통과`` () =
        let srcFolder = buildSourceFolder ()
        let stagingDir = Packager.createStaging ()
        try
            let fileCount, totalBytes = Packager.copyToStaging srcFolder stagingDir
            Assert.Equal(1, fileCount)
            Assert.True(totalBytes > 0L)

            let ingested = Packager.runIngestInStaging stagingDir None CancellationToken.None
            Assert.True(ingested >= 1, sprintf "ingested >= 1 기대, 실제 %d" ingested)

            Packager.writeMeta stagingDir "cli-test" srcFolder fileCount totalBytes userIdentity
            let metaPath = Path.Combine(stagingDir, "meta.json")
            Assert.True(File.Exists metaPath)
            let metaText = File.ReadAllText metaPath
            Assert.Contains("\"title\": \"cli-test\"", metaText)
            Assert.Contains("\"schemaVersion\": 1", metaText)

            let zipPath = Packager.createZip stagingDir
            try
                Assert.True(File.Exists zipPath)
                Assert.True((FileInfo zipPath).Length > 0L)
            finally
                Packager.safeDelete zipPath
        finally
            Packager.safeDelete stagingDir
            Packager.safeDelete srcFolder

    /// LightHouseClient.uploadCollection 정상 → server 발급 guid.
    [<Fact>]
    member _.``LightHouseClient.uploadCollection — 정상 PSK + 발급 guid`` () =
        task {
            let srcFolder = buildSourceFolder ()
            let stagingDir = Packager.createStaging ()
            let mutable zipPath = ""
            let mutable collectionId = ""
            let mutable ediOpt : ExceptionDispatchInfo option = None
            try
                let fileCount, totalBytes = Packager.copyToStaging srcFolder stagingDir
                let ingested = Packager.runIngestInStaging stagingDir None CancellationToken.None
                Assert.True(ingested >= 1)
                let title = "cli-upload-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                Packager.writeMeta stagingDir title srcFolder fileCount totalBytes userIdentity
                zipPath <- Packager.createZip stagingDir

                use client =
                    LightHouseClient.createHttpClient
                        (fixture.BaseAddress.AbsoluteUri.TrimEnd '/')
                        true  // allowInvalidCerts — fixture 의 self-signed cert
                use stream = File.OpenRead zipPath
                let! id = LightHouseClient.uploadCollection client psk userIdentity title stream CancellationToken.None
                Assert.False(String.IsNullOrWhiteSpace id)
                Assert.True(Guid.TryParse(id, ref Unchecked.defaultof<Guid>),
                    sprintf "발급된 collection id 가 guid 형식 아님 — %s" id)
                collectionId <- id
            with ex ->
                ediOpt <- Some (ExceptionDispatchInfo.Capture ex)
            // local fs cleanup
            Packager.safeDelete zipPath
            Packager.safeDelete stagingDir
            Packager.safeDelete srcFolder
            // server cleanup (async) — Fact 격리
            if not (String.IsNullOrEmpty collectionId) then
                use cleanupClient = fixture.CreateAuthClient()
                let! cleanupResp = cleanupClient.DeleteAsync(sprintf "/collections/%s" collectionId)
                cleanupResp.Dispose()
            match ediOpt with
            | Some edi -> edi.Throw()
            | None -> ()
        }

    /// 잘못된 PSK → LightHouseAuthError + StatusCode = Unauthorized.
    [<Fact>]
    member _.``LightHouseClient.uploadCollection — 잘못된 PSK 401 AuthError`` () =
        task {
            let srcFolder = buildSourceFolder ()
            let stagingDir = Packager.createStaging ()
            let mutable zipPath = ""
            try
                let fileCount, totalBytes = Packager.copyToStaging srcFolder stagingDir
                let ingested = Packager.runIngestInStaging stagingDir None CancellationToken.None
                Assert.True(ingested >= 1)
                Packager.writeMeta stagingDir "auth-fail" srcFolder fileCount totalBytes userIdentity
                zipPath <- Packager.createZip stagingDir

                use client =
                    LightHouseClient.createHttpClient
                        (fixture.BaseAddress.AbsoluteUri.TrimEnd '/')
                        true
                use stream = File.OpenRead zipPath
                let mutable threw = false
                let mutable status = HttpStatusCode.OK
                try
                    let! _ = LightHouseClient.uploadCollection client "wrong-psk-xxx" userIdentity "auth-fail" stream CancellationToken.None
                    ()
                with
                | LightHouseAuthError(_, s) ->
                    threw <- true
                    status <- s
                Assert.True(threw, "AuthError 예상")
                Assert.Equal(HttpStatusCode.Unauthorized, status)
            finally
                Packager.safeDelete zipPath
                Packager.safeDelete stagingDir
                Packager.safeDelete srcFolder
        }

    /// HTTPS-only enforcement — `http://` baseUrl 은 createHttpClient 에서 즉시 argumentException.
    [<Fact>]
    member _.``LightHouseClient.createHttpClient — http:// 거부`` () =
        Assert.Throws<ArgumentException>(fun () ->
            LightHouseClient.createHttpClient "http://localhost:8443" true
            |> ignore)
        |> ignore

    interface IClassFixture<ServiceFixture>
