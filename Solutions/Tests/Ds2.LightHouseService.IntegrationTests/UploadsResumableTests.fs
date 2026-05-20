module Ds2.LightHouseService.IntegrationTests.UploadsResumableTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Ds2.LightHouseService.IntegrationTests

/// **D-S7-5 (Phase S7 resumable upload)** — server-side endpoint round-trip 검증.
///
/// 본 fixture = IT round-trip:
/// - POST /uploads-rs (시작) → 201 + uploadId + offset 0
/// - PATCH /uploads-rs/{id} (chunk append) → 200 + offset 갱신
/// - GET /uploads-rs/{id} → 200 + 현 offset (resume hook)
/// - POST /uploads-rs/{id}/finalize → **phase 2 (s6-r63)**: 201 + collectionId + storageRelPath + Registry 등록
/// - DELETE /uploads-rs/{id} → 204
///
/// **phase 2 (s6-r63) 추가 fact**:
/// - Content-Length ≠ Content-Range length → 400 (size 검증)
/// - finalize → collection 등록 round-trip (collectionId 발급 + Registry 등록 + GET /collections 검증)
/// - PATCH lock — 동시 PATCH 가 race 없이 순차 처리
type UploadsResumableTests(fixture: ServiceFixture) =

    let parseJson (body: string) =
        JsonDocument.Parse(body).RootElement

    /// PATCH chunk 헬퍼 — bytes / Content-Length / Content-Range 박제.
    let buildPatchRequest (uploadId: string) (chunk: byte[]) (startB: int64) (endB: int64) (total: int64) : HttpRequestMessage =
        let req = new HttpRequestMessage(HttpMethod.Patch, sprintf "uploads-rs/%s" uploadId)
        req.Content <- new ByteArrayContent(chunk)
        req.Content.Headers.ContentLength <- Nullable (int64 chunk.Length)
        req.Content.Headers.Add("Content-Range", sprintf "bytes %d-%d/%d" startB endB total)
        req

    [<Fact>]
    member _.``POST /uploads — 시작 응답 201 + uploadId + offset 0``() = task {
        use client = fixture.CreateAuthClient()
        let body = """{"title":"test-upload","totalBytes":1024}"""
        use content = new StringContent(body, Encoding.UTF8, "application/json")
        let! resp = client.PostAsync("uploads-rs", content)
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode)
        let! respBody = resp.Content.ReadAsStringAsync()
        let json = parseJson respBody
        let uploadId = json.GetProperty("uploadId").GetString()
        Assert.Equal(32, uploadId.Length)  // guid v4 N format
        Assert.Equal(0L, json.GetProperty("offset").GetInt64())
        Assert.Equal(1024L, json.GetProperty("totalBytes").GetInt64())
    }

    [<Fact>]
    member _.``POST /uploads — title 빈 값 400``() = task {
        use client = fixture.CreateAuthClient()
        let body = """{"title":"","totalBytes":100}"""
        use content = new StringContent(body, Encoding.UTF8, "application/json")
        let! resp = client.PostAsync("uploads-rs", content)
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
    }

    [<Fact>]
    member _.``POST + PATCH chunk → 200 + offset 갱신 + finalize 201 + collection 등록 (phase 2)``() = task {
        use client = fixture.CreateAuthClient()
        // (0) zip payload 생성 (실 zip — finalize 시 extractAll + Registry 등록 검증)
        let title = "rs-roundtrip-test"
        let zipBytes = ZipBuilders.buildMinimalZip title fixture.UserIdentity
        let totalBytes = int64 zipBytes.Length

        // (1) 시작
        let startBody = sprintf """{"title":"%s","totalBytes":%d}""" title totalBytes
        use startContent = new StringContent(startBody, Encoding.UTF8, "application/json")
        let! startResp = client.PostAsync("uploads-rs", startContent)
        Assert.Equal(HttpStatusCode.Created, startResp.StatusCode)
        let! startBodyTxt = startResp.Content.ReadAsStringAsync()
        let uploadId = (parseJson startBodyTxt).GetProperty("uploadId").GetString()

        // (2) PATCH chunk 1 (first half)
        let half = zipBytes.Length / 2
        let chunk1 = Array.sub zipBytes 0 half
        use req1 = buildPatchRequest uploadId chunk1 0L (int64 (half - 1)) totalBytes
        let! patchResp1 = client.SendAsync(req1)
        Assert.Equal(HttpStatusCode.OK, patchResp1.StatusCode)
        Assert.Equal(int64 half, (parseJson (patchResp1.Content.ReadAsStringAsync().Result)).GetProperty("offset").GetInt64())

        // (3) PATCH chunk 2 (rest)
        let chunk2 = Array.sub zipBytes half (zipBytes.Length - half)
        use req2 = buildPatchRequest uploadId chunk2 (int64 half) (totalBytes - 1L) totalBytes
        let! patchResp2 = client.SendAsync(req2)
        Assert.Equal(HttpStatusCode.OK, patchResp2.StatusCode)
        Assert.Equal(totalBytes, (parseJson (patchResp2.Content.ReadAsStringAsync().Result)).GetProperty("offset").GetInt64())

        // (4) GET 현 status (resume hook 검증)
        let! getResp = client.GetAsync(sprintf "uploads-rs/%s" uploadId)
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode)
        Assert.Equal(totalBytes, (parseJson (getResp.Content.ReadAsStringAsync().Result)).GetProperty("offset").GetInt64())

        // (5) finalize — **phase 2** 201 + collectionId + storageRelPath
        use finalContent = new StringContent("", Encoding.UTF8, "application/json")
        let! finalResp = client.PostAsync(sprintf "uploads-rs/%s/finalize" uploadId, finalContent)
        Assert.Equal(HttpStatusCode.Created, finalResp.StatusCode)
        let! finalBody = finalResp.Content.ReadAsStringAsync()
        let finalJson = parseJson finalBody
        let collectionId = finalJson.GetProperty("id").GetString()
        Assert.NotEmpty(collectionId)
        Assert.NotEmpty(finalJson.GetProperty("storageRelPath").GetString())

        // (6) Registry 등록 검증 — GET /collections 의 list 에 collectionId 포함
        let! listResp = client.GetAsync("collections")
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode)
        let! listBody = listResp.Content.ReadAsStringAsync()
        let arr = (parseJson listBody).GetProperty("collections")
        let ids = [
            for c in arr.EnumerateArray() do yield c.GetProperty("id").GetString()
        ]
        Assert.Contains(collectionId, ids)
    }

    [<Fact>]
    member _.``PATCH /uploads — offset mismatch 409``() = task {
        use client = fixture.CreateAuthClient()
        let startBody = """{"title":"mismatch-test","totalBytes":100}"""
        use startContent = new StringContent(startBody, Encoding.UTF8, "application/json")
        let! startResp = client.PostAsync("uploads-rs", startContent)
        let! startBodyTxt = startResp.Content.ReadAsStringAsync()
        let uploadId = (parseJson startBodyTxt).GetProperty("uploadId").GetString()

        // 의도적 offset mismatch — start=10 (현 offset=0 와 불일치)
        let chunk = Array.init 40 (fun _ -> 0xAAuy)
        use req = buildPatchRequest uploadId chunk 10L 49L 100L
        let! resp = client.SendAsync(req)
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode)
    }

    [<Fact>]
    member _.``PATCH /uploads — Content-Length ≠ Content-Range length 400 (size 검증, phase 2)``() = task {
        use client = fixture.CreateAuthClient()
        let startBody = """{"title":"size-mismatch","totalBytes":100}"""
        use startContent = new StringContent(startBody, Encoding.UTF8, "application/json")
        let! startResp = client.PostAsync("uploads-rs", startContent)
        let! startBodyTxt = startResp.Content.ReadAsStringAsync()
        let uploadId = (parseJson startBodyTxt).GetProperty("uploadId").GetString()

        // body 40 bytes 박제했는데 Content-Range = bytes 0-49/100 (length=50). Content-Length 자동 = 40.
        // chunkLen=50 ≠ Content-Length=40 → 400.
        let chunk = Array.init 40 (fun _ -> 0xBBuy)
        let req = new HttpRequestMessage(HttpMethod.Patch, sprintf "uploads-rs/%s" uploadId)
        req.Content <- new ByteArrayContent(chunk)
        req.Content.Headers.ContentLength <- Nullable 40L
        req.Content.Headers.Add("Content-Range", "bytes 0-49/100")
        let! resp = client.SendAsync(req)
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
    }

    [<Fact>]
    member _.``DELETE /uploads/{id} — cancel + staging cleanup``() = task {
        use client = fixture.CreateAuthClient()
        let startBody = """{"title":"cancel-test","totalBytes":1024}"""
        use startContent = new StringContent(startBody, Encoding.UTF8, "application/json")
        let! startResp = client.PostAsync("uploads-rs", startContent)
        let! startBodyTxt = startResp.Content.ReadAsStringAsync()
        let uploadId = (parseJson startBodyTxt).GetProperty("uploadId").GetString()

        let! delResp = client.DeleteAsync(sprintf "uploads-rs/%s" uploadId)
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode)

        // GET 후 404 확인
        let! getResp = client.GetAsync(sprintf "uploads-rs/%s" uploadId)
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode)
    }

    [<Fact>]
    member _.``PATCH /uploads — 동시 PATCH 가 lock 으로 race 없이 순차 처리 (phase 2)``() = task {
        use client = fixture.CreateAuthClient()
        let title = "concurrent-patch"
        let zipBytes = ZipBuilders.buildMinimalZip title fixture.UserIdentity
        let totalBytes = int64 zipBytes.Length

        let startBody = sprintf """{"title":"%s","totalBytes":%d}""" title totalBytes
        use startContent = new StringContent(startBody, Encoding.UTF8, "application/json")
        let! startResp = client.PostAsync("uploads-rs", startContent)
        let! startBodyTxt = startResp.Content.ReadAsStringAsync()
        let uploadId = (parseJson startBodyTxt).GetProperty("uploadId").GetString()

        // 같은 chunk 를 동시에 2번 PATCH — 첫 번째 200, 두 번째는 409 (offset mismatch — 첫 번째 적용 후 offset 증가).
        let chunk = Array.sub zipBytes 0 zipBytes.Length
        let mkReq () = buildPatchRequest uploadId chunk 0L (totalBytes - 1L) totalBytes

        use req1 = mkReq ()
        use req2 = mkReq ()
        let task1 = client.SendAsync(req1)
        let task2 = client.SendAsync(req2)
        let! results = Task.WhenAll [| task1; task2 |]
        let statuses = results |> Array.map (fun r -> r.StatusCode)
        // 한 번은 200, 다른 한 번은 409 (lock 으로 인해 race 없이 순차 처리됨).
        Assert.Contains(HttpStatusCode.OK, statuses)
        Assert.Contains(HttpStatusCode.Conflict, statuses)
    }

    interface IClassFixture<ServiceFixture>
