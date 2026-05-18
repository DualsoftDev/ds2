module Ds2.LightHouseService.IntegrationTests.E2eRoundTripTests

open System
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Runtime.ExceptionServices
open System.Security.Authentication
open System.Text
open System.Text.Json
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors
open Ds2.LightHouseService
open Ds2.LightHouseService.IntegrationTests

/// Phase S5e — 본격 e2e round-trip suite (MA22).
///
/// 검증 범위:
/// - 실 Kestrel HTTPS bind (in-memory self-signed cert) 위에서 HttpClient round-trip
/// - PSK auth middleware (Bearer + X-User-Identity) 의 인증/거부 분기
/// - POST /collections (minimal zip — Ds2.LightHouse.Indexer 가 in-process 색인) → server 가 발급 id 응답
/// - GET /collections / GET /collections/{id}/status / POST,DELETE /sessions / DELETE /collections 의 wire 정합
///
/// fixture = `IClassFixture<ServiceFixture>` (단일 fixture 가 단일 service instance 공유,
/// 각 Fact 가 자체 collection 등록 + cleanup 책임으로 격리).

type E2eRoundTripTests(fixture: ServiceFixture) =
    let psk = fixture.Psk
    let userIdentity = fixture.UserIdentity

    /// minimal valid zip — Ds2.LightHouse Indexer 로 실 색인 후 source/ + .lighthouse-kb/ + meta.json 패키징.
    /// title 은 caller 가 지정 (중복 등록 분리). 반환 = (zip byte array, expectedIndexerVersion).
    let buildMinimalZip (title: string) : byte[] * string =
        let stagingDir = Path.Combine(Path.GetTempPath(), "lhs-zip-" + Guid.NewGuid().ToString("N"))
        let sourceDir = Path.Combine(stagingDir, "source")
        Directory.CreateDirectory sourceDir |> ignore
        try
            // dummy text file 1개 → TextExtractor 가 색인
            let sampleTxt = Path.Combine(sourceDir, "sample.txt")
            File.WriteAllText(sampleTxt, "# Heading\n\nSample content for e2e round-trip.\n", Encoding.UTF8)
            let sampleBytes = (new FileInfo(sampleTxt)).Length

            // in-process 색인 — `.lighthouse-kb/index.db` 생성
            let extractors : IExtractor list = [ new TextExtractor() :> IExtractor ]
            let progressCb (_: IngestProgress) = ()
            let results = Indexer.ingest stagingDir extractors progressCb CancellationToken.None
            // s5e-m5 follow-up: `Ingested` variant 존재 명시 검증 — Skipped/Failed 만 반환되면
            // build 는 통과해도 server upload 후 attachment_search 가 0 hit (회귀). 1+ Ingested 강제.
            let ingestedCount =
                results
                |> Array.filter (fun (_, r) -> match r with | Ingested _ -> true | _ -> false)
                |> Array.length
            Assert.True(
                ingestedCount >= 1,
                sprintf "Indexer.ingest 결과에 Ingested variant 없음 — %A" results)

            // meta.json 작성 — §3.3.1 SSOT. server 필드 (id/importedAt/...) 는 null/공백 (server 가 stamp).
            let meta : MetaJson = {
                SchemaVersion = MetaJsonSchema.Current
                IndexerVersion = IndexerVersion.Current
                Title = title
                SourcePathHint = sourceDir
                FileCount = 1
                TotalSourceBytes = sampleBytes
                CreatedAt = DateTime.UtcNow.ToString("o", Globalization.CultureInfo.InvariantCulture)
                ClientHost = "integration-test-host"
                ClientUser = userIdentity
                // server 가 채울 필드 (client 가 빈 값 보내도 server 가 stampServerFields 로 덮어씀)
                Id = ""
                ImportedAt = ""
                ImportedBy = ""
                StorageRelPath = ""
            }
            MetaJson.save stagingDir meta

            // zip 패키징 — stagingDir 통째로 ZipFile.CreateFromDirectory (relative path = zip entry)
            let zipPath = Path.Combine(Path.GetTempPath(), "lhs-zip-" + Guid.NewGuid().ToString("N") + ".zip")
            ZipFile.CreateFromDirectory(stagingDir, zipPath, CompressionLevel.Fastest, false)
            let bytes = File.ReadAllBytes zipPath
            File.Delete zipPath
            bytes, IndexerVersion.Current
        finally
            try Directory.Delete(stagingDir, true) with _ -> ()

    /// multipart/form-data POST — `file` field 에 zip + filename. Bearer + X-User-Identity 자동 동봉.
    let postCollectionAsync (client: HttpClient) (title: string) (zipBytes: byte[]) : System.Threading.Tasks.Task<HttpResponseMessage> =
        task {
            use content = new MultipartFormDataContent()
            let zipContent = new ByteArrayContent(zipBytes)
            zipContent.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse "application/zip"
            content.Add(zipContent, "file", "minimal.zip")
            content.Add(new StringContent(title), "title")
            return! client.PostAsync("/collections", content)
        }

    // ── Fact 1: /healthz — auth-free 200 ───────────────────────────
    [<Fact>]
    member _.``GET /healthz — auth 무관 200`` () =
        task {
            use client = fixture.CreateBareClient()
            let! resp = client.GetAsync("/healthz")
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.Equal("ok", body)
        }

    // ── Fact 2: 인증 통과 GET /collections (registry empty 또는 어떤 상태든 200) ─
    [<Fact>]
    member _.``GET /collections — PSK + identity 통과 200`` () =
        task {
            use client = fixture.CreateAuthClient()
            let! resp = client.GetAsync("/collections")
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            // 응답 body 는 `{ "collections": [...], "schemaVersion": 1 }` 객체 (CollectionEndpoints.getCollectionsList)
            Assert.Contains("\"collections\":", body)
            Assert.Contains("\"schemaVersion\":", body)
        }

    // ── Fact 3: Authorization 누락 → 401 ───────────────────────────
    [<Fact>]
    member _.``GET /collections — Authorization 누락 401`` () =
        task {
            use client = fixture.CreateBareClient()
            let! resp = client.GetAsync("/collections")
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode)
        }

    // ── Fact 4: 잘못된 PSK → 401 ──────────────────────────────────
    [<Fact>]
    member _.``GET /collections — 잘못된 PSK 401`` () =
        task {
            use client = fixture.CreateBareClient()
            client.DefaultRequestHeaders.Add("Authorization", "Bearer wrong-psk-xxx")
            client.DefaultRequestHeaders.Add("X-User-Identity", userIdentity)
            let! resp = client.GetAsync("/collections")
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode)
        }

    // ── Fact 5: X-User-Identity 누락 → 401 ────────────────────────
    [<Fact>]
    member _.``GET /collections — X-User-Identity 누락 401`` () =
        task {
            use client = fixture.CreateBareClient()
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + psk)
            let! resp = client.GetAsync("/collections")
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode)
        }

    // ── Fact 6: 본격 round-trip — POST /collections → GET → POST/DELETE /sessions → DELETE /collections ──
    [<Fact>]
    member _.``round-trip — POST collections → list → session 발급/해제 → DELETE collections`` () =
        task {
            use client = fixture.CreateAuthClient()
            let title = "e2e-roundtrip-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let zipBytes, _ = buildMinimalZip title

            // (1) POST /collections — 201 + body.id 발급
            let! postResp = postCollectionAsync client title zipBytes
            let! postBody = postResp.Content.ReadAsStringAsync()
            Assert.True(
                postResp.StatusCode = HttpStatusCode.Created || postResp.StatusCode = HttpStatusCode.OK,
                sprintf "POST /collections 예상 201/200, 실제 %A — body=%s" postResp.StatusCode postBody)
            let postJson = JsonDocument.Parse postBody
            let collectionId = postJson.RootElement.GetProperty("id").GetString()
            Assert.False(String.IsNullOrWhiteSpace collectionId)
            Assert.True(Guid.TryParse(collectionId, ref Unchecked.defaultof<Guid>),
                sprintf "발급된 collection id 가 guid 형식 아님 — %s" collectionId)

            try
                // (2) GET /collections — 등록된 entry 포함 확인
                let! listResp = client.GetAsync("/collections")
                Assert.Equal(HttpStatusCode.OK, listResp.StatusCode)
                let! listBody = listResp.Content.ReadAsStringAsync()
                Assert.Contains(collectionId, listBody)

                // (3) GET /collections/{id}/status — idle
                let! statusResp = client.GetAsync(sprintf "/collections/%s/status" collectionId)
                Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode)
                let! statusBody = statusResp.Content.ReadAsStringAsync()
                let statusJson = JsonDocument.Parse statusBody
                Assert.Equal("idle", statusJson.RootElement.GetProperty("status").GetString())

                // (4) POST /sessions { collectionIds: [collectionId] } → token + unknownIds=[]
                let sessionReq = sprintf "{\"collectionIds\":[\"%s\"]}" collectionId
                use sessionContent = new StringContent(sessionReq, Encoding.UTF8, "application/json")
                let! sessionResp = client.PostAsync("/sessions", sessionContent)
                Assert.Equal(HttpStatusCode.Created, sessionResp.StatusCode)
                let! sessionBody = sessionResp.Content.ReadAsStringAsync()
                let sessionJson = JsonDocument.Parse sessionBody
                let token = sessionJson.RootElement.GetProperty("token").GetString()
                Assert.False(String.IsNullOrWhiteSpace token)
                let unknownIds = sessionJson.RootElement.GetProperty("unknownIds").EnumerateArray() |> Seq.length
                Assert.Equal(0, unknownIds)

                // (5) DELETE /sessions/{token} → 204
                let! delSessionResp = client.DeleteAsync(sprintf "/sessions/%s" token)
                Assert.Equal(HttpStatusCode.NoContent, delSessionResp.StatusCode)
            with ex ->
                // s5e-M2 follow-up: try/finally `.Result.Dispose()` (async-over-sync) →
                // try/with + ExceptionDispatchInfo (F# task CE 가 reraise 직접 호출 금지).
                let edi = ExceptionDispatchInfo.Capture(ex)
                let! cleanupResp = client.DeleteAsync(sprintf "/collections/%s" collectionId)
                cleanupResp.Dispose()
                edi.Throw()
            // (6) cleanup (정상 경로) — DELETE /collections/{id} → 204 (fact 격리)
            let! cleanupResp = client.DeleteAsync(sprintf "/collections/%s" collectionId)
            cleanupResp.Dispose()
        }

    // ── Fact 7 (s5e-I): HTTPS-only 검증 — http:// scheme → connection refused / SSL exception ─
    [<Fact>]
    member _.``http:// scheme — Kestrel HTTPS-only bind 차단`` () =
        task {
            // fixture 의 baseAddress 는 `https://127.0.0.1:<port>` — 동일 port 를 http:// 로 시도하면
            // Kestrel 가 HTTPS handshake 만 listen 하므로 connection 실패 또는 SSL exception 예상.
            use handler = new HttpClientHandler()
            handler.ServerCertificateCustomValidationCallback <-
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            use client = new HttpClient(handler, disposeHandler = false)
            let httpUrl = sprintf "http://%s:%d/healthz" fixture.BaseAddress.Host fixture.BaseAddress.Port
            let mutable threw = false
            try
                let! _ = client.GetAsync(httpUrl)
                ()
            with
            | :? HttpRequestException -> threw <- true
            | :? System.IO.IOException -> threw <- true
            | :? AuthenticationException -> threw <- true
            Assert.True(threw, sprintf "http:// 요청이 거부되지 않음 — Kestrel 가 HTTPS-only bind 가 아님 (url=%s)" httpUrl)
        }

    interface IClassFixture<ServiceFixture>

