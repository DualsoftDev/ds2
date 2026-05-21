module Ds2.LightHouseService.IntegrationTests.AdminEndpointsTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Xunit
open Ds2.LightHouseService.IntegrationTests

/// **Phase S7 D-S7-4 admin endpoint IT (B-S7-4, s6-r71+)** — multi-tenant T2/T3 owner/acl 편집 path.
///
/// 검증 범위 (3 Fact):
/// - POST /admin/collections/{id}/owner — body { user } → 200 + Registry.ImportedBy 갱신
/// - PUT /admin/collections/{id}/acl — body { users, readOnly } → 200 + Registry.Acl 갱신
/// - 잘못된 id → 404
///
/// fixture = `ServiceFixture` (E2eRoundTripTests / NegativeRoundTripTests 와 동일 pool).
/// cleanup 정책 = fact 실패 시 collection orphan 허용 (fixture 격리상 다음 fact 영향 0, fact title GUID prefix 가 충돌 차단).
type AdminEndpointsTests(fixture: ServiceFixture) =
    let userIdentity = fixture.UserIdentity

    /// 사전 등록 — POST /collections 로 collection 생성 후 id 반환.
    let registerCollectionAsync (client: HttpClient) (title: string) : System.Threading.Tasks.Task<string> =
        task {
            use content = new MultipartFormDataContent()
            let zipBytes = ZipBuilders.buildMinimalZip title userIdentity
            let zipContent = new ByteArrayContent(zipBytes)
            zipContent.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse "application/zip"
            content.Add(zipContent, "file", "minimal.zip")
            content.Add(new StringContent(title), "title")
            let! resp = client.PostAsync("/collections", content)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.True(
                resp.StatusCode = HttpStatusCode.Created || resp.StatusCode = HttpStatusCode.OK,
                sprintf "POST /collections 등록 실패 — status=%A body=%s" resp.StatusCode body)
            let json = JsonDocument.Parse body
            return json.RootElement.GetProperty("id").GetString()
        }

    interface IClassFixture<ServiceFixture>

    // ── Fact 1: POST /admin/collections/{id}/owner — ImportedBy stamp 갱신 ─────
    [<Fact>]
    member _.``POST /admin/collections/{id}/owner — ImportedBy 갱신`` () =
        task {
            use client = fixture.CreateAuthClient()
            let title = "admin-owner-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let! collectionId = registerCollectionAsync client title
            let newOwner = "transferred-owner@dualsoft.com"
            let body = sprintf "{\"user\":\"%s\"}" newOwner
            use content = new StringContent(body, Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(sprintf "/admin/collections/%s/owner" collectionId, content)
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            let! respBody = resp.Content.ReadAsStringAsync()
            let respJson = JsonDocument.Parse respBody
            Assert.Equal(collectionId, respJson.RootElement.GetProperty("id").GetString())
            Assert.Equal(newOwner, respJson.RootElement.GetProperty("importedBy").GetString())
            let! listResp = client.GetAsync("/collections")
            let! listBody = listResp.Content.ReadAsStringAsync()
            Assert.Contains(newOwner, listBody)
            // cleanup (실패 시 orphan 허용)
            let! cleanupResp = client.DeleteAsync(sprintf "/collections/%s" collectionId)
            cleanupResp.Dispose()
        }

    // ── Fact 2: PUT /admin/collections/{id}/acl — Acl 갱신 ──────────────────────
    [<Fact>]
    member _.``PUT /admin/collections/{id}/acl — Acl 갱신`` () =
        task {
            use client = fixture.CreateAuthClient()
            let title = "admin-acl-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let! collectionId = registerCollectionAsync client title
            let body = "{\"users\":[\"alice@example.com\",\"bob@example.com\"],\"readOnly\":true}"
            use content = new StringContent(body, Encoding.UTF8, "application/json")
            let! resp = client.PutAsync(sprintf "/admin/collections/%s/acl" collectionId, content)
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            let! respBody = resp.Content.ReadAsStringAsync()
            let respJson = JsonDocument.Parse respBody
            Assert.Equal(collectionId, respJson.RootElement.GetProperty("id").GetString())
            let acl = respJson.RootElement.GetProperty("acl")
            Assert.True(acl.GetProperty("readOnly").GetBoolean())
            let users = acl.GetProperty("users").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList
            Assert.Equal<string list>(["alice@example.com"; "bob@example.com"], users)
            // cleanup
            let! cleanupResp = client.DeleteAsync(sprintf "/collections/%s" collectionId)
            cleanupResp.Dispose()
        }

    // ── Fact 3: 잘못된 id → 404 ─────────────────────────────────────────────────
    [<Fact>]
    member _.``POST /admin/collections/{id}/owner — id not found 404`` () =
        task {
            use client = fixture.CreateAuthClient()
            let bogusId = Guid.NewGuid().ToString()
            let body = "{\"user\":\"x@y.com\"}"
            use content = new StringContent(body, Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(sprintf "/admin/collections/%s/owner" bogusId, content)
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode)
        }
