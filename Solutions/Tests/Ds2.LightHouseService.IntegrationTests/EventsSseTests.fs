module Ds2.LightHouseService.IntegrationTests.EventsSseTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Ds2.LightHouseService.IntegrationTests

/// **Phase S7 D-S7-2 (s6-r27) — `GET /events` SSE round-trip Fact**.
///
/// 검증:
/// - auth — Bearer 누락 시 401 (AuthMiddleware 정합)
/// - SSE wire — Content-Type `text/event-stream` + Cache-Control `no-cache` + first line keepalive
/// - POST /collections → SSE stream 에 `collection-added` event publish (fan-out 정합)
///
/// **fixture 공유 주의** — IClassFixture<ServiceFixture> 가 모든 test class 에 공유되므로 본 class 가
/// stream 을 보유하는 동안 다른 class 의 Fact 는 SSE event 가 흘러갈 수 있음. 본 Fact 는 자체 publish
/// + 즉시 receive 패턴이라 cross-talk noise 무관.
type EventsSseTests(fixture: ServiceFixture) =

    // SSE stream 의 한 줄을 읽는 helper. `data: {json}` line 1개 + 빈 line 1개 패턴.
    let readEventDataAsync (reader: StreamReader) (timeoutMs: int) : Task<string option> =
        task {
            use cts = new CancellationTokenSource(timeoutMs)
            try
                let mutable result : string option = None
                let mutable stop = false
                while not stop && not cts.Token.IsCancellationRequested do
                    let! line = reader.ReadLineAsync(cts.Token).AsTask()
                    if isNull line then
                        stop <- true
                    elif line.StartsWith "data: " then
                        result <- Some (line.Substring 6)
                        stop <- true
                return result
            with :? OperationCanceledException -> return None
        }

    /// Fact 1 — auth 누락 시 401 (AuthMiddleware 가 SSE endpoint 도 검증).
    [<Fact>]
    member _.``GET /events — Authorization 누락 401`` () =
        task {
            use client = fixture.CreateBareClient()
            // Authorization 헤더 없음. X-User-Identity 도 없음.
            let! resp = client.GetAsync("/events")
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode)
        }

    /// Fact 2 — 정상 인증 + SSE wire (Content-Type / Cache-Control / first line keepalive).
    [<Fact>]
    member _.``GET /events — SSE wire 정합 + first event keepalive`` () =
        task {
            use client = fixture.CreateAuthClient()
            // streaming GET — HttpCompletionOption.ResponseHeadersRead 로 body 끝까지 읽지 않음.
            use! resp = client.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead)
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType.MediaType)
            Assert.Equal(System.Text.Encoding.UTF8.WebName, resp.Content.Headers.ContentType.CharSet)
            // Cache-Control header — HttpResponseMessage 의 CacheControl 은 단일 CacheControlHeaderValue 또는 null.
            // 본 SSE handler 는 "no-cache" string 만 set (raw header).
            let cacheControl =
                let mutable v = Unchecked.defaultof<System.Collections.Generic.IEnumerable<string>>
                if resp.Headers.TryGetValues("Cache-Control", &v) then String.Join(",", v) else ""
            Assert.Contains("no-cache", cacheControl)

            use! stream = resp.Content.ReadAsStreamAsync()
            use reader = new StreamReader(stream)
            // first event = keepalive (handler 가 setSseHeaders 직후 즉시 send).
            let! data = readEventDataAsync reader 3000
            Assert.True(data.IsSome, "first SSE data line 미수신 (3s 타임아웃)")
            use doc = JsonDocument.Parse data.Value
            let evt = doc.RootElement.GetProperty("event").GetString()
            Assert.Equal("keepalive", evt)
        }

    /// Fact 3 — round-trip — POST /collections 후 SSE 에 `collection-added` event 수신.
    [<Fact>]
    member _.``round-trip — POST /collections → SSE collection-added publish`` () =
        task {
            use subscriber = fixture.CreateAuthClient()
            use publisher = fixture.CreateAuthClient()

            // (1) SSE 구독 시작 — keepalive first event 소화 후 reader 가 다음 event 대기.
            use! resp = subscriber.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead)
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            use! stream = resp.Content.ReadAsStreamAsync()
            use reader = new StreamReader(stream)
            let! keepalive = readEventDataAsync reader 3000
            Assert.True(keepalive.IsSome, "구독 시작 직후 keepalive 미수신")

            // (2) 별 client 로 collection upload — fixture 의 buildMinimalZip helper 재사용.
            let title = sprintf "sse-test-%d" (DateTime.UtcNow.Ticks)
            let zipBytes = ZipBuilders.buildMinimalZip title fixture.UserIdentity
            use content = new MultipartFormDataContent()
            let zipContent = new ByteArrayContent(zipBytes)
            zipContent.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse "application/zip"
            content.Add(zipContent, "file", "sse.zip")
            content.Add(new StringContent(title), "title")
            use! postResp = publisher.PostAsync("/collections", content)
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode)
            let! postBody = postResp.Content.ReadAsStringAsync()
            use postDoc = JsonDocument.Parse postBody
            let collectionId = postDoc.RootElement.GetProperty("id").GetString()

            // (3) SSE 에서 `collection-added` event 수신 — 최대 5s 대기.
            // keepalive 30s 간격이라 그 사이 도착하는 event 가 우리가 publish 한 것.
            let mutable found = false
            let mutable attempt = 0
            while not found && attempt < 10 do
                let! data = readEventDataAsync reader 1000
                match data with
                | Some json ->
                    use doc = JsonDocument.Parse json
                    let evt = doc.RootElement.GetProperty("event").GetString()
                    if evt = "collection-added" then
                        let mutable idEl = Unchecked.defaultof<JsonElement>
                        if doc.RootElement.TryGetProperty("collectionId", &idEl) then
                            Assert.Equal(collectionId, idEl.GetString())
                            found <- true
                | None -> ()
                attempt <- attempt + 1
            Assert.True(found, sprintf "collection-added event 미수신 (id=%s)" collectionId)

            // cleanup — registry 정리 (다른 Fact 영향 회피). M1 (s6-r27 자가 검열) — status 확인 강제.
            let! delResp = publisher.DeleteAsync(sprintf "/collections/%s" collectionId)
            Assert.True(delResp.IsSuccessStatusCode, sprintf "cleanup DELETE 실패 status=%d" (int delResp.StatusCode))
            delResp.Dispose()
        }

    interface IClassFixture<ServiceFixture>
