module Ds2.LightHouseService.IntegrationTests.CliUploadTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Runtime.ExceptionServices
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Cli
open Ds2.LightHouseService.IntegrationTests

/// Phase S6 P1 — CLI upload e2e suite (옵션 P + 보관 정책 in-place 색인).
///
/// 검증 범위:
/// - `Packager.{resetKbDir, runIngest, summarize, countIngested, writeMeta, createZip}` in-process round-trip
/// - `LightHouseClient.uploadCollection` 의 multipart wire 정합 (PSK Bearer + X-User-Identity + collectionId = sanitized 폴더명, 옵션 A)
/// - 옵션 A 멱등 overwrite (동일 title 2회 → 같은 id) + `--no-overwrite` (overwrite=false → 409)
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

    /// 3 Fact 공용 in-place pipeline (외부 --review Mj-7 정합 — 5-step 반복 제거).
    /// resetKbDir → runIngest → summarize → ingested ≥ 1 assertion → writeMeta → createZip.
    let prepareInPlaceZip (srcFolder: string) (title: string) : string * Packager.IngestSummary =
        Packager.resetKbDir srcFolder
        // 사용자 결정 (CLI API key 필수) — caller 가 captionGen 박제. test 박제는 noop (caption SkippedCaption).
        let results = Packager.runIngest srcFolder None CaptionGenerator.noop CancellationToken.None
        let summary = Packager.summarize results
        Assert.True(summary.IngestedCount >= 1, sprintf "ingested >= 1 기대, 실제 %d" summary.IngestedCount)
        // **PR-B (todo-lighthouse-index-summary.md §3.1)** — writeMeta signature 확장 (description / keywords).
        // IT round-trip 은 KeywordExtractor 통합 검증보다 zip / upload wire 정합 검증이 의도라 빈 값 박제.
        Packager.writeMeta srcFolder title srcFolder summary.FileCount summary.TotalBytes userIdentity "" [||]
        Packager.createZip srcFolder, summary

    /// 1 source 폴더 → in-place 색인 → meta.json → zip 통째 round-trip.
    [<Fact>]
    member _.``Packager — in-place 색인 + meta + zip 통과`` () =
        let srcFolder = buildSourceFolder ()
        try
            let zipPath, summary = prepareInPlaceZip srcFolder "cli-test"
            try
                Assert.Equal(1, summary.FileCount)
                Assert.True(summary.TotalBytes > 0L)
                let metaPath = Path.Combine(srcFolder, ".lighthouse-kb", "meta.json")
                Assert.True(File.Exists metaPath, sprintf "meta.json 미생성 — %s" metaPath)
                let metaText = File.ReadAllText metaPath
                Assert.Contains("\"title\": \"cli-test\"", metaText)
                Assert.Contains("\"schemaVersion\": 1", metaText)
                Assert.True(File.Exists zipPath)
                Assert.True((FileInfo zipPath).Length > 0L)
            finally
                Packager.safeDelete zipPath
        finally
            Packager.safeDelete srcFolder

    /// LightHouseClient.uploadCollection 정상 → collectionId = sanitized 폴더명 (옵션 A).
    [<Fact>]
    member _.``LightHouseClient.uploadCollection — 정상 PSK + collectionId = 폴더명`` () =
        task {
            let srcFolder = buildSourceFolder ()
            let mutable zipPath = ""
            let mutable collectionId = ""
            let mutable ediOpt : ExceptionDispatchInfo option = None
            try
                let title = "cli-upload-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                let zp, _ = prepareInPlaceZip srcFolder title
                zipPath <- zp

                use client =
                    LightHouseClient.createHttpClient
                        (fixture.BaseAddress.AbsoluteUri.TrimEnd '/')
                        true  // allowInvalidCerts — fixture 의 self-signed cert
                use stream = File.OpenRead zipPath
                let! id = LightHouseClient.uploadCollection client psk userIdentity title true stream CancellationToken.None
                Assert.False(String.IsNullOrWhiteSpace id)
                // 옵션 A (2026-05-30) — collectionId = sanitizeTitle(title) (server guid 발급 폐기).
                // title 이 valid char(영문/숫자/하이픈) 만이라 sanitizeTitle 결과 = title 그대로.
                Assert.Equal(title, id)
                collectionId <- id
            with ex ->
                ediOpt <- Some (ExceptionDispatchInfo.Capture ex)
            // local fs cleanup
            Packager.safeDelete zipPath
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
            let mutable zipPath = ""
            try
                let zp, _ = prepareInPlaceZip srcFolder "auth-fail"
                zipPath <- zp

                use client =
                    LightHouseClient.createHttpClient
                        (fixture.BaseAddress.AbsoluteUri.TrimEnd '/')
                        true
                use stream = File.OpenRead zipPath
                let mutable threw = false
                let mutable status = HttpStatusCode.OK
                try
                    let! _ = LightHouseClient.uploadCollection client "wrong-psk-xxx" userIdentity "auth-fail" true stream CancellationToken.None
                    ()
                with
                | LightHouseAuthError(_, s) ->
                    threw <- true
                    status <- s
                Assert.True(threw, "AuthError 예상")
                Assert.Equal(HttpStatusCode.Unauthorized, status)
            finally
                Packager.safeDelete zipPath
                Packager.safeDelete srcFolder
        }

    /// HTTPS-only enforcement — `http://` baseUrl 은 createHttpClient 에서 즉시 argumentException.
    [<Fact>]
    member _.``LightHouseClient.createHttpClient — http:// 거부`` () =
        Assert.Throws<ArgumentException>(fun () ->
            LightHouseClient.createHttpClient "http://localhost:8443" true
            |> ignore)
        |> ignore

    /// **옵션 A 멱등 overwrite** — 동일 title 2회 upload (overwrite=true default) → 같은 collectionId.
    /// `Registry.upsertAsync` 의 Id-PK upsert 가 같은 row 갱신 → KB dialog 중복 누적 차단의 회귀 방어 Fact.
    [<Fact>]
    member _.``LightHouseClient.uploadCollection — 동일 title 2회 → 멱등 overwrite (같은 id)`` () =
        task {
            let title = "cli-ovw-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let src1 = buildSourceFolder ()
            let src2 = buildSourceFolder ()
            let mutable z1 = ""
            let mutable z2 = ""
            let mutable cid = ""
            let mutable ediOpt : ExceptionDispatchInfo option = None
            try
                use client = LightHouseClient.createHttpClient (fixture.BaseAddress.AbsoluteUri.TrimEnd '/') true
                let zp1, _ = prepareInPlaceZip src1 title
                z1 <- zp1
                use s1 = File.OpenRead z1
                let! id1 = LightHouseClient.uploadCollection client psk userIdentity title true s1 CancellationToken.None
                cid <- id1
                let zp2, _ = prepareInPlaceZip src2 title
                z2 <- zp2
                use s2 = File.OpenRead z2
                let! id2 = LightHouseClient.uploadCollection client psk userIdentity title true s2 CancellationToken.None
                Assert.Equal(title, id1)   // collectionId = sanitized 폴더명
                Assert.Equal(id1, id2)     // 멱등 — 같은 id (registry upsert 가 같은 row 갱신, 중복 0)
            with ex ->
                ediOpt <- Some (ExceptionDispatchInfo.Capture ex)
            Packager.safeDelete z1
            Packager.safeDelete z2
            Packager.safeDelete src1
            Packager.safeDelete src2
            if not (String.IsNullOrEmpty cid) then
                use cleanupClient = fixture.CreateAuthClient()
                let! r = cleanupClient.DeleteAsync(sprintf "/collections/%s" cid)
                r.Dispose()
            match ediOpt with
            | Some edi -> edi.Throw()
            | None -> ()
        }

    /// **옵션 A --no-overwrite** — overwrite=false + 동일 title 존재 → 409 (LightHouseProtocolError, CLI exit 4 매핑).
    [<Fact>]
    member _.``LightHouseClient.uploadCollection — overwrite=false + 동일 title → 409`` () =
        task {
            let title = "cli-noovw-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let src1 = buildSourceFolder ()
            let src2 = buildSourceFolder ()
            let mutable z1 = ""
            let mutable z2 = ""
            let mutable cid = ""
            let mutable ediOpt : ExceptionDispatchInfo option = None
            try
                use client = LightHouseClient.createHttpClient (fixture.BaseAddress.AbsoluteUri.TrimEnd '/') true
                let zp1, _ = prepareInPlaceZip src1 title
                z1 <- zp1
                use s1 = File.OpenRead z1
                let! id1 = LightHouseClient.uploadCollection client psk userIdentity title true s1 CancellationToken.None
                cid <- id1
                let zp2, _ = prepareInPlaceZip src2 title
                z2 <- zp2
                use s2 = File.OpenRead z2
                let mutable status = HttpStatusCode.OK
                let mutable threw = false
                try
                    let! _ = LightHouseClient.uploadCollection client psk userIdentity title false s2 CancellationToken.None
                    ()
                with
                | LightHouseProtocolError(_, Some s) ->
                    threw <- true
                    status <- s
                Assert.True(threw, "overwrite=false + 동일 title 은 ProtocolError 기대")
                Assert.Equal(HttpStatusCode.Conflict, status)   // 409
            with ex ->
                ediOpt <- Some (ExceptionDispatchInfo.Capture ex)
            Packager.safeDelete z1
            Packager.safeDelete z2
            Packager.safeDelete src1
            Packager.safeDelete src2
            if not (String.IsNullOrEmpty cid) then
                use cleanupClient = fixture.CreateAuthClient()
                let! r = cleanupClient.DeleteAsync(sprintf "/collections/%s" cid)
                r.Dispose()
            match ediOpt with
            | Some edi -> edi.Throw()
            | None -> ()
        }

    interface IClassFixture<ServiceFixture>
