module Ds2.LightHouseService.IntegrationTests.MultiTenantRoundTripTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Xunit
open Ds2.LightHouseService.IntegrationTests

/// **Phase S7 D-S7-4 (B-R14, s6-r74 b2)** — MultiTenant T2/T3 round-trip 검증.
///
/// fact 분포:
///   T2 (per-user namespace, ImportedBy 일치):
///     1. alice register → bob GET /collections list = bob 미공개
///     2. alice register → bob GET /collections/{id}/status = 404
///     3. alice register → bob DELETE /collections/{id} = 404
///     4. alice register → bob POST /collections/{id}/payload = 404
///   T3 (collection ACL):
///     5. alice register → alice admin PUT acl {users:[alice,bob],readOnly:false} → bob GET status = 200
///     6. alice register → alice admin PUT acl {users:[alice,bob],readOnly:true} → bob DELETE = 403
///     7. alice register → alice admin PUT acl {users:[alice],readOnly:false} → bob GET status = 404

let private parseJson (body: string) =
    JsonDocument.Parse(body).RootElement

let private registerCollectionAsync (client: HttpClient) (title: string) (userIdentity: string) =
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
        let json = parseJson body
        return json.GetProperty("id").GetString()
    }

let private setAcl (client: HttpClient) (collectionId: string) (users: string list) (readOnly: bool) =
    task {
        let usersJson =
            users
            |> List.map (fun u -> sprintf "\"%s\"" u)
            |> String.concat ","
        let body = sprintf "{\"users\":[%s],\"readOnly\":%b}" usersJson readOnly
        use content = new StringContent(body, Encoding.UTF8, "application/json")
        let! resp = client.PutAsync(sprintf "/admin/collections/%s/acl" collectionId, content)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    }


type MultiTenantT2RoundTripTests(fixture: MultiTenantT2Fixture) =

    interface IClassFixture<MultiTenantT2Fixture>

    /// **T2 Fact 1** — alice 가 등록한 collection 이 bob 의 GET /collections list 에서 보이지 않음.
    [<Fact>]
    member _.``T2 — alice register → bob list 미공개``() = task {
        use aliceClient = fixture.CreateAliceClient()
        use bobClient = fixture.CreateBobClient()
        let title = "t2-list-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let! collectionId = registerCollectionAsync aliceClient title fixture.UserAlice
        let! listResp = bobClient.GetAsync("/collections")
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode)
        let! listBody = listResp.Content.ReadAsStringAsync()
        let arr = (parseJson listBody).GetProperty("collections")
        let ids = [ for c in arr.EnumerateArray() do yield c.GetProperty("id").GetString() ]
        Assert.DoesNotContain(collectionId, ids)
    }

    /// **T2 Fact 2** — alice 가 등록한 collection 의 status 를 bob 이 조회 시 404 (Hidden 정보 leak 차단).
    [<Fact>]
    member _.``T2 — alice register → bob GET status 404``() = task {
        use aliceClient = fixture.CreateAliceClient()
        use bobClient = fixture.CreateBobClient()
        let title = "t2-status-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let! collectionId = registerCollectionAsync aliceClient title fixture.UserAlice
        let! resp = bobClient.GetAsync(sprintf "/collections/%s/status" collectionId)
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode)
    }

    /// **T2 Fact 3** — bob 의 DELETE 시도 시 404 (Hidden).
    [<Fact>]
    member _.``T2 — alice register → bob DELETE 404``() = task {
        use aliceClient = fixture.CreateAliceClient()
        use bobClient = fixture.CreateBobClient()
        let title = "t2-del-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let! collectionId = registerCollectionAsync aliceClient title fixture.UserAlice
        let! resp = bobClient.DeleteAsync(sprintf "/collections/%s" collectionId)
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode)
    }

    /// **T2 Fact 4** — bob 의 POST /collections/{id}/payload (swap) 시도 시 404 (Hidden).
    [<Fact>]
    member _.``T2 — alice register → bob POST payload swap 404``() = task {
        use aliceClient = fixture.CreateAliceClient()
        use bobClient = fixture.CreateBobClient()
        let title = "t2-swap-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let! collectionId = registerCollectionAsync aliceClient title fixture.UserAlice

        let zipBytes = ZipBuilders.buildMinimalZip title fixture.UserBob
        use multipart = new MultipartFormDataContent()
        multipart.Add(new StringContent(title), "title")
        let zipContent = new ByteArrayContent(zipBytes)
        zipContent.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse "application/zip"
        multipart.Add(zipContent, "zip", "payload.zip")
        let! resp = bobClient.PostAsync(sprintf "/collections/%s/payload" collectionId, multipart)
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode)
    }


type MultiTenantT3RoundTripTests(fixture: MultiTenantT3Fixture) =

    interface IClassFixture<MultiTenantT3Fixture>

    /// **T3 Fact 5** — alice register → acl=[alice,bob],readOnly=false → bob GET status = 200.
    [<Fact>]
    member _.``T3 — acl users 박제 시 visible``() = task {
        use aliceClient = fixture.CreateAliceClient()
        use bobClient = fixture.CreateBobClient()
        let title = "t3-vis-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let! collectionId = registerCollectionAsync aliceClient title fixture.UserAlice
        do! setAcl aliceClient collectionId [ fixture.UserAlice; fixture.UserBob ] false
        let! resp = bobClient.GetAsync(sprintf "/collections/%s/status" collectionId)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    }

    /// **T3 Fact 6** — acl readOnly=true → bob DELETE = 403 (visible 이지만 mutation reject).
    [<Fact>]
    member _.``T3 — readOnly 박제 시 mutation 403``() = task {
        use aliceClient = fixture.CreateAliceClient()
        use bobClient = fixture.CreateBobClient()
        let title = "t3-ro-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let! collectionId = registerCollectionAsync aliceClient title fixture.UserAlice
        do! setAcl aliceClient collectionId [ fixture.UserAlice; fixture.UserBob ] true
        let! resp = bobClient.DeleteAsync(sprintf "/collections/%s" collectionId)
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode)
    }

    /// **T3 Fact 7** — acl users=[alice] (bob 제외) → bob GET status = 404 (Hidden).
    [<Fact>]
    member _.``T3 — acl 외 user GET status 404``() = task {
        use aliceClient = fixture.CreateAliceClient()
        use bobClient = fixture.CreateBobClient()
        let title = "t3-hidden-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        let! collectionId = registerCollectionAsync aliceClient title fixture.UserAlice
        do! setAcl aliceClient collectionId [ fixture.UserAlice ] false
        let! resp = bobClient.GetAsync(sprintf "/collections/%s/status" collectionId)
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode)
    }
