module Ds2.LightHouseService.IntegrationTests.NegativeRoundTripTests

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open Xunit
open Ds2.LightHouseService.IntegrationTests

/// Phase S6 P2 — server-side input validation / sanitize 회귀 차단 7 Fact.
///
/// 검증 범위 (모두 `POST /collections` + 1건 DELETE):
/// - F1: Content-Type=text/plain (not multipart)            → 415 "multipart/form-data 필수"
/// - F2: multipart 인데 title 필드 누락                       → 400 "title 필드 필수"
/// - F3: multipart 인데 zip 파일 누락                          → 400 "zip 파일 필드 필수"
/// - F4: zip 안 meta.json 부재                                → 400 "meta.json 누락"
/// - F5: zip bomb (ratio 50 초과)                              → 400 "zip sanitize 실패"
/// - F6: garbage bytes (not a zip)                            → 400 "zip 구조 결함"
/// - F7: DELETE /collections/{미존재 guid}                    → 404 "collection 미존재"
///
/// 모든 negative path = server-side state 변경 0 (Registry 무영향, staging 자동 cleanup).
/// fact 간 ordering-independent — fixture 의 Registry 공유 안전.

type NegativeRoundTripTests(fixture: ServiceFixture) =
    let userIdentity = fixture.UserIdentity

    /// multipart POST helper — title+zip 둘 다 받음. None 인자는 해당 part 생략.
    let postCollectionsMultipart
        (client: HttpClient)
        (titleOpt: string option)
        (zipBytesOpt: byte[] option)
        =
        task {
            use content = new MultipartFormDataContent()
            titleOpt
            |> Option.iter (fun t -> content.Add(new StringContent(t), "title"))
            zipBytesOpt
            |> Option.iter (fun b ->
                let zc = new ByteArrayContent(b)
                zc.Headers.ContentType <- MediaTypeHeaderValue.Parse "application/zip"
                content.Add(zc, "file", "payload.zip"))
            return! client.PostAsync("/collections", content)
        }

    /// s6-r7 (M1): swap 경로 helper — F# class 정의의 `let` 바인딩은 member 앞에 위치 의무.
    let registerCollectionForSwap (client: HttpClient) (title: string) =
        task {
            let okZip = ZipBuilders.buildMinimalZip title userIdentity
            let! resp = postCollectionsMultipart client (Some title) (Some okZip)
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            use doc = JsonDocument.Parse body
            return doc.RootElement.GetProperty("id").GetString()
        }

    let postSwapMultipart (client: HttpClient) (id: string) (zipBytes: byte[]) =
        task {
            use content = new MultipartFormDataContent()
            let zc = new ByteArrayContent(zipBytes)
            zc.Headers.ContentType <- MediaTypeHeaderValue.Parse "application/zip"
            content.Add(zc, "file", "payload.zip")
            content.Add(new StringContent("ignored-title"), "title")
            return! client.PostAsync(sprintf "/collections/%s/payload" id, content)
        }

    // ── F1: Content-Type=text/plain → 415 ────────────────────────────────
    [<Fact>]
    member _.``POST /collections — Content-Type text/plain 415 (multipart 필수)`` () =
        task {
            use client = fixture.CreateAuthClient()
            use content = new StringContent("not multipart at all", Encoding.UTF8, "text/plain")
            let! resp = client.PostAsync("/collections", content)
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.Contains("multipart", body)
        }

    // ── F2: title 누락 → 400 ─────────────────────────────────────────────
    [<Fact>]
    member _.``POST /collections — title 필드 누락 400`` () =
        task {
            use client = fixture.CreateAuthClient()
            let zipBytes = ZipBuilders.buildMinimalZip "ignored-title" userIdentity
            let! resp = postCollectionsMultipart client None (Some zipBytes)
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.Contains("title", body)
        }

    // ── F3: zip 파일 누락 → 400 ───────────────────────────────────────────
    [<Fact>]
    member _.``POST /collections — zip 파일 누락 400`` () =
        task {
            use client = fixture.CreateAuthClient()
            let title = "no-zip-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let! resp = postCollectionsMultipart client (Some title) None
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.Contains("zip", body)
        }

    // ── F4: zip 안 meta.json 누락 → 400 ───────────────────────────────────
    [<Fact>]
    member _.``POST /collections — meta.json 누락 zip 400`` () =
        task {
            use client = fixture.CreateAuthClient()
            let title = "no-meta-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let zipBytes = ZipBuilders.buildZipWithoutMeta ()
            let! resp = postCollectionsMultipart client (Some title) (Some zipBytes)
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            // server CollectionEndpoints 의 FileNotFoundException catch 분기가
            // "zip 구조 결함 — meta.json 누락" 응답 → "meta.json" 키워드만 박제 (한글 의존 회피).
            Assert.Contains("meta.json", body)
        }

    // ── F5: zip bomb (ratio 50 초과) → 400 ────────────────────────────────
    [<Fact>]
    member _.``POST /collections — zip bomb ratio 50 초과 400`` () =
        task {
            use client = fixture.CreateAuthClient()
            let title = "zipbomb-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let zipBytes = ZipBuilders.buildZipBomb ()
            let! resp = postCollectionsMultipart client (Some title) (Some zipBytes)
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            // ZipImport.extractAll → SanitizeException(ZipBombExceeded) → catch → "zip sanitize 실패: ZipBombExceeded(...)"
            Assert.Contains("sanitize", body)
        }

    // ── F6: garbage bytes (not a zip) → 400 ───────────────────────────────
    [<Fact>]
    member _.``POST /collections — garbage bytes (not a zip) 400`` () =
        task {
            use client = fixture.CreateAuthClient()
            let title = "garbage-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let zipBytes = ZipBuilders.buildGarbageZip ()
            let! resp = postCollectionsMultipart client (Some title) (Some zipBytes)
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            // `new ZipArchive(stream, Read)` → InvalidDataException → catch → "zip 구조 결함: ..."
            // 자가 검열 m3: "zip" 키워드 만으론 F2/F3/F5 와 공유라 F6 특이성 부족. JSON parse 후
            // error 필드에서 "구조" 키워드 박제 (raw body 는 unicode escape `구조` 으로 인코딩됨).
            // JsonSerializer.Deserialize 가 escape 를 자동 unescape → "구조" 매칭 OK.
            use doc = JsonDocument.Parse body
            let errorMsg = doc.RootElement.GetProperty("error").GetString()
            Assert.Contains("zip", errorMsg)
            Assert.Contains("구조", errorMsg)
        }

    // ── F8 (s6-r5): IndexerVersion gate too-low → 415 + clientVersion 박제 ─
    [<Fact>]
    member _.``POST /collections — IndexerVersion too-low 415`` () =
        task {
            use client = fixture.CreateAuthClient()
            let title = "ver-low-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            // fixture cfg.IndexerVersionRange = [1.0.0, 1.99.99] → "0.5.0" 은 hostMin 미만
            let zipBytes = ZipBuilders.buildZipWithIndexerVersion title userIdentity "0.5.0"
            let! resp = postCollectionsMultipart client (Some title) (Some zipBytes)
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            use doc = JsonDocument.Parse body
            let errorMsg = doc.RootElement.GetProperty("error").GetString()
            Assert.Contains("too low", errorMsg)
            let clientVer = doc.RootElement.GetProperty("clientVersion").GetString()
            Assert.Equal("0.5.0", clientVer)
            // hostingRange 박제 검증 — server config 정합
            let hostingRange = doc.RootElement.GetProperty("hostingRange")
            Assert.Equal("1.0.0", hostingRange.GetProperty("min").GetString())
        }

    // ── F9 (s6-r5): IndexerVersion gate too-high → 415 ────────────────────
    [<Fact>]
    member _.``POST /collections — IndexerVersion too-high 415`` () =
        task {
            use client = fixture.CreateAuthClient()
            let title = "ver-high-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            // fixture cfg.IndexerVersionRange.Max = "1.99.99" → "9.99.99" 는 초과
            let zipBytes = ZipBuilders.buildZipWithIndexerVersion title userIdentity "9.99.99"
            let! resp = postCollectionsMultipart client (Some title) (Some zipBytes)
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            use doc = JsonDocument.Parse body
            let errorMsg = doc.RootElement.GetProperty("error").GetString()
            Assert.Contains("too high", errorMsg)
            let clientVer = doc.RootElement.GetProperty("clientVersion").GetString()
            Assert.Equal("9.99.99", clientVer)
            // 자가 검열 M1: F8 의 hostingRange.min 박제 대칭 — hostingRange.max 박제 추가
            let hostingRange = doc.RootElement.GetProperty("hostingRange")
            Assert.Equal("1.99.99", hostingRange.GetProperty("max").GetString())
            // P5 (s6-r6): suggestedAction 의미론 정정 — service 업그레이드 OR client lib 다운그레이드 양 옵션 박제.
            let suggestedAction = doc.RootElement.GetProperty("suggestedAction").GetString()
            Assert.Contains("service 업그레이드", suggestedAction)
            Assert.Contains("다운그레이드", suggestedAction)
        }

    // ── F7: DELETE /collections/{미존재 guid} → 404 ──────────────────────
    [<Fact>]
    member _.``DELETE /collections/{미존재 guid} 404`` () =
        task {
            use client = fixture.CreateAuthClient()
            let randomId = Guid.NewGuid().ToString("D")
            let! resp = client.DeleteAsync(sprintf "/collections/%s" randomId)
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.Contains(randomId, body)
        }

    // ── s6-r7 (M1): payload swap 의 IndexerVersion gate negative path 정합 ──
    // postCollections 의 F8/F9 와 동일 의미론을 swap 경로 (POST /collections/{id}/payload) 도 박제.
    // SSOT = error/clientVersion/hostingRange/suggestedAction 4 키 모두 일관.
    [<Fact>]
    member _.``POST /collections/{id}/payload — IndexerVersion too-low 415 + SSOT 4 키 박제`` () =
        // cleanup = try/finally 의 finally 안에서 task `let!` 사용 불가 (F# CE) → try 끝에 명시.
        // 가정 실패 시 registry 에 stale entry 남지만 fixture.DisposeAsync 의 temp dir 재귀 delete 가 흡수.
        task {
            use client = fixture.CreateAuthClient()
            let title = "swap-low-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let! id = registerCollectionForSwap client title
            let lowZip = ZipBuilders.buildZipWithIndexerVersion title userIdentity "0.5.0"
            let! resp = postSwapMultipart client id lowZip
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            use doc = JsonDocument.Parse body
            Assert.Contains("too low", doc.RootElement.GetProperty("error").GetString())
            Assert.Equal("0.5.0", doc.RootElement.GetProperty("clientVersion").GetString())
            let hostingRange = doc.RootElement.GetProperty("hostingRange")
            Assert.Equal("1.0.0", hostingRange.GetProperty("min").GetString())
            Assert.Equal("1.99.99", hostingRange.GetProperty("max").GetString())
            Assert.Contains("업그레이드", doc.RootElement.GetProperty("suggestedAction").GetString())
            let! _ = client.DeleteAsync(sprintf "/collections/%s" id)
            ()
        }

    [<Fact>]
    member _.``POST /collections/{id}/payload — IndexerVersion too-high 415 + suggestedAction 양 옵션`` () =
        task {
            use client = fixture.CreateAuthClient()
            let title = "swap-high-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let! id = registerCollectionForSwap client title
            let highZip = ZipBuilders.buildZipWithIndexerVersion title userIdentity "9.99.99"
            let! resp = postSwapMultipart client id highZip
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            use doc = JsonDocument.Parse body
            Assert.Contains("too high", doc.RootElement.GetProperty("error").GetString())
            Assert.Equal("9.99.99", doc.RootElement.GetProperty("clientVersion").GetString())
            let hostingRange = doc.RootElement.GetProperty("hostingRange")
            Assert.Equal("1.99.99", hostingRange.GetProperty("max").GetString())
            let suggestedAction = doc.RootElement.GetProperty("suggestedAction").GetString()
            Assert.Contains("service 업그레이드", suggestedAction)
            Assert.Contains("다운그레이드", suggestedAction)
            let! _ = client.DeleteAsync(sprintf "/collections/%s" id)
            ()
        }

    interface IClassFixture<ServiceFixture>
