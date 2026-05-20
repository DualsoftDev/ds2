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

/// **s6-r60 D-S7-5 (Phase S7 resumable upload scaffold)** — server-side endpoint round-trip 검증.
///
/// 본 fixture = IT round-trip:
/// - POST /uploads (시작) → 201 + uploadId + offset 0
/// - PATCH /uploads/{id} (chunk append) → 200 + offset 갱신
/// - GET /uploads/{id} → 200 + 현 offset (resume hook)
/// - POST /uploads/{id}/finalize → 200 + complete (scaffold, 별 turn 의 collection 등록 위임 미박제)
/// - DELETE /uploads/{id} → 204
type UploadsResumableTests(fixture: ServiceFixture) =

    let parseJson (body: string) =
        JsonDocument.Parse(body).RootElement

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
    member _.``POST /uploads + PATCH chunk → 200 + offset 갱신 + payload.partial 누적``() = task {
        use client = fixture.CreateAuthClient()
        // (1) 시작
        let totalBytes = 100L
        let startBody = sprintf """{"title":"chunk-test","totalBytes":%d}""" totalBytes
        use startContent = new StringContent(startBody, Encoding.UTF8, "application/json")
        let! startResp = client.PostAsync("uploads-rs", startContent)
        Assert.Equal(HttpStatusCode.Created, startResp.StatusCode)
        let! startBodyTxt = startResp.Content.ReadAsStringAsync()
        let uploadId = (parseJson startBodyTxt).GetProperty("uploadId").GetString()

        // (2) PATCH chunk 1 (bytes 0-49/100 = 50 bytes)
        let chunk1 = Array.init 50 (fun i -> byte i)
        use patchContent1 = new ByteArrayContent(chunk1)
        let req1 = new HttpRequestMessage(HttpMethod.Patch, sprintf "uploads-rs/%s" uploadId)
        req1.Content <- patchContent1
        req1.Content.Headers.ContentLength <- Nullable 50L
        req1.Content.Headers.Add("Content-Range", "bytes 0-49/100")
        let! patchResp1 = client.SendAsync(req1)
        Assert.Equal(HttpStatusCode.OK, patchResp1.StatusCode)
        let! patch1Body = patchResp1.Content.ReadAsStringAsync()
        Assert.Equal(50L, (parseJson patch1Body).GetProperty("offset").GetInt64())

        // (3) PATCH chunk 2 (bytes 50-99/100 = 50 bytes) — 누적 = 100
        let chunk2 = Array.init 50 (fun i -> byte (50 + i))
        use patchContent2 = new ByteArrayContent(chunk2)
        let req2 = new HttpRequestMessage(HttpMethod.Patch, sprintf "uploads-rs/%s" uploadId)
        req2.Content <- patchContent2
        req2.Content.Headers.ContentLength <- Nullable 50L
        req2.Content.Headers.Add("Content-Range", "bytes 50-99/100")
        let! patchResp2 = client.SendAsync(req2)
        Assert.Equal(HttpStatusCode.OK, patchResp2.StatusCode)
        let! patch2Body = patchResp2.Content.ReadAsStringAsync()
        Assert.Equal(100L, (parseJson patch2Body).GetProperty("offset").GetInt64())

        // (4) GET 현 status (resume hook 검증)
        let! getResp = client.GetAsync(sprintf "uploads-rs/%s" uploadId)
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode)
        let! getBody = getResp.Content.ReadAsStringAsync()
        Assert.Equal(100L, (parseJson getBody).GetProperty("offset").GetInt64())

        // (5) finalize — complete 응답
        use finalContent = new StringContent("", Encoding.UTF8, "application/json")
        let! finalResp = client.PostAsync(sprintf "uploads-rs/%s/finalize" uploadId, finalContent)
        Assert.Equal(HttpStatusCode.OK, finalResp.StatusCode)
        let! finalBody = finalResp.Content.ReadAsStringAsync()
        Assert.True((parseJson finalBody).GetProperty("complete").GetBoolean())
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
        use content = new ByteArrayContent(chunk)
        let req = new HttpRequestMessage(HttpMethod.Patch, sprintf "uploads-rs/%s" uploadId)
        req.Content <- content
        req.Content.Headers.ContentLength <- Nullable 40L
        req.Content.Headers.Add("Content-Range", "bytes 10-49/100")
        let! resp = client.SendAsync(req)
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode)
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

    interface IClassFixture<ServiceFixture>
