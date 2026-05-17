module Ds2.LightHouseService.Tests.MetaJsonTests

open System
open System.IO
open System.Text
open Xunit
open Ds2.LightHouseService

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lhs-meta-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

let private clientMeta () : MetaJson = {
    SchemaVersion = 1
    IndexerVersion = "1.0.0"
    Title = "라인A 사양서 v3"
    SourcePathHint = "C:\\Users\\kwak\\라인A"
    FileCount = 42
    TotalSourceBytes = 1234567890L
    CreatedAt = "2026-05-17T13:45:00Z"
    ClientHost = "WIN-ABC123"
    ClientUser = "kwak@dualsoft.com"
    // server 필드는 client 가 안 채움 — 빈 값
    Id = ""
    ImportedAt = ""
    ImportedBy = ""
    StorageRelPath = ""
}

[<Fact>]
let ``load — 미존재 시 FileNotFoundException`` () = withTempDir (fun dir ->
    Assert.Throws<FileNotFoundException>(fun () -> MetaJson.load dir |> ignore) |> ignore)

[<Fact>]
let ``save → load round-trip — 한국어 + 모든 필드 보존`` () = withTempDir (fun dir ->
    let m = clientMeta()
    MetaJson.save dir m
    let loaded = MetaJson.load dir
    Assert.Equal(m.Title, loaded.Title)
    Assert.Equal(m.IndexerVersion, loaded.IndexerVersion)
    Assert.Equal(m.FileCount, loaded.FileCount)
    Assert.Equal(m.TotalSourceBytes, loaded.TotalSourceBytes)
    Assert.Equal(m.ClientHost, loaded.ClientHost)
    Assert.Equal(m.ClientUser, loaded.ClientUser))

[<Fact>]
let ``stampServerFields — server 필드 채움 + client 가 보낸 server 필드 값 무시 (§3.3.1)`` () =
    // client 가 server 필드에 가짜 값 박아 보내도 server stamp 가 override
    let evilClient = { clientMeta() with Id = "fake-id"; ImportedBy = "attacker"; StorageRelPath = "evil" }
    let server = MetaJson.stampServerFields "real-guid" "kwak@dualsoft.com" "Collections\\real-guid-line\\" evilClient
    Assert.Equal("real-guid", server.Id)
    Assert.Equal("kwak@dualsoft.com", server.ImportedBy)
    Assert.Equal("Collections\\real-guid-line\\", server.StorageRelPath)
    Assert.NotEqual<string>("", server.ImportedAt)
    // client 필드는 보존
    Assert.Equal(evilClient.Title, server.Title)
    Assert.Equal(evilClient.IndexerVersion, server.IndexerVersion)

[<Fact>]
let ``toRegistryEntry — Status idle + lastImportedAt = importedAt`` () =
    let server = { clientMeta() with Id = "g1"; ImportedAt = "2026-05-17T13:45:30Z"; ImportedBy = "u"; StorageRelPath = "p" }
    let entry = MetaJson.toRegistryEntry server
    Assert.Equal("g1", entry.Id)
    Assert.Equal("라인A 사양서 v3", entry.DisplayName)
    Assert.Equal("idle", entry.Status)
    Assert.Null(entry.ErrorReason)
    Assert.Equal(server.ImportedAt, entry.LastImportedAt)

[<Fact>]
let ``toRegistryEntry — 0-doc collection (MA18) — fileCount/bytes 0 보존`` () =
    let zeroDoc = { clientMeta() with FileCount = 0; TotalSourceBytes = 0L }
    let server = MetaJson.stampServerFields "g1" "u" "p" zeroDoc
    let entry = MetaJson.toRegistryEntry server
    Assert.Equal(0, entry.FileCount)
    Assert.Equal(0L, entry.TotalSourceBytes)
    // 0-doc 도 status=idle 정상 등록 (MA18)
    Assert.Equal("idle", entry.Status)

[<Fact>]
let ``schemaVersion mismatch — fail-fast`` () = withTempDir (fun dir ->
    let p = MetaJson.path dir
    File.WriteAllText(p, """{"schemaVersion": 999, "title": "x"}""", Encoding.UTF8)
    Assert.Throws<InvalidDataException>(fun () -> MetaJson.load dir |> ignore) |> ignore)
