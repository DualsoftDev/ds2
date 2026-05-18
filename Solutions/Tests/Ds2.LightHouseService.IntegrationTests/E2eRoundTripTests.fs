module Ds2.LightHouseService.IntegrationTests.E2eRoundTripTests

open System
open System.Net
open System.Net.Http
open System.Runtime.ExceptionServices
open System.Security.Authentication
open System.Text
open System.Text.Json
open Xunit
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
            let zipBytes = ZipBuilders.buildMinimalZip title userIdentity

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

    // ── Fact 8 (s6-r2): IPv6 ::1 접속 거부 — Kestrel IPv4 단일 bind 정합 ─
    [<Fact>]
    member _.``IPv6 [::1] 접속 시 connection refused — Kestrel IPv4 단일 bind`` () =
        task {
            // fixture cfg.ListenUrl = "https://127.0.0.1:0" → Kestrel 가 IPv4 loopback 만 listen.
            // 동일 port 를 IPv6 [::1] 로 시도하면 별 listen socket 부재 → connection refused 예상.
            // 만약 Kestrel 가 dual-stack bind 회귀 시 본 fact 가 검출 (보안/정합 회귀 차단).
            use handler = new HttpClientHandler()
            handler.ServerCertificateCustomValidationCallback <-
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            use client = new HttpClient(handler, disposeHandler = false)
            // 짧은 timeout — connection refused 가 즉시 발생, 단 fallback 회귀 시 hang 차단
            client.Timeout <- TimeSpan.FromSeconds 5.0
            let ipv6Url = sprintf "https://[::1]:%d/healthz" fixture.BaseAddress.Port
            let mutable threw = false
            try
                let! _ = client.GetAsync(ipv6Url)
                ()
            with
            | :? HttpRequestException -> threw <- true
            | :? System.IO.IOException -> threw <- true
            | :? System.Net.Sockets.SocketException -> threw <- true
            | :? System.Threading.Tasks.TaskCanceledException -> threw <- true   // timeout 회귀 분기 (dual-stack 미회복 시)
            Assert.True(threw, sprintf "IPv6 [::1] 접속 통과 — Kestrel 가 IPv4-only bind 아님 (url=%s)" ipv6Url)
        }

    interface IClassFixture<ServiceFixture>

