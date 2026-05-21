module Ds2.LightHouseService.Tests.MetaJsonTests

open System
open System.IO
open System.Text
open Xunit
open Ds2.LightHouse.Protocol
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
    // **PR-A (r0)** — 두 신 필드 default (KeywordExtractor 미진입 시점의 fresh 색인 정합)
    Description = ""
    Keywords = [||]
    // server 필드는 client 가 안 채움 — 빈 값
    Id = ""
    ImportedAt = ""
    ImportedBy = ""
    StorageRelPath = ""
}

[<Fact>]
let ``load — 미존재 시 FileNotFoundException`` () = withTempDir (fun dir ->
    Assert.Throws<FileNotFoundException>(fun () -> MetaJsonIO.load dir |> ignore) |> ignore)

[<Fact>]
let ``save → load round-trip — 한국어 + 모든 필드 보존`` () = withTempDir (fun dir ->
    let m = clientMeta()
    MetaJsonIO.save dir m
    let loaded = MetaJsonIO.load dir
    Assert.Equal(m.Title, loaded.Title)
    Assert.Equal(m.IndexerVersion, loaded.IndexerVersion)
    Assert.Equal(m.FileCount, loaded.FileCount)
    Assert.Equal(m.TotalSourceBytes, loaded.TotalSourceBytes)
    Assert.Equal(m.ClientHost, loaded.ClientHost)
    Assert.Equal(m.ClientUser, loaded.ClientUser))

[<Fact>]
let ``PR-A r0 — Description / Keywords round-trip + null 미발생`` () = withTempDir (fun dir ->
    // 신 필드 정상 박제 path
    let m = { clientMeta() with
                Description = "라인A 컨베이어 사양 + IO list v3"
                Keywords = [| "컨베이어"; "IO"; "안전"; "PLC" |] }
    MetaJsonIO.save dir m
    let loaded = MetaJsonIO.load dir
    Assert.Equal(m.Description, loaded.Description)
    Assert.Equal<string[]>(m.Keywords, loaded.Keywords))

[<Fact>]
let ``PR-A r0 — legacy meta.json (두 필드 누락) load → default 값 (forward-compat)`` () = withTempDir (fun dir ->
    // PR-B 진입 이전 / 외부 source 의 두 필드 누락 meta.json 시뮬레이션
    let p = MetaJsonIO.path dir
    Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
    let legacyJson = """{
        "schemaVersion": 1,
        "indexerVersion": "2.1.0",
        "title": "Legacy",
        "sourcePathHint": "",
        "fileCount": 0,
        "totalSourceBytes": 0,
        "createdAt": "",
        "clientHost": "",
        "clientUser": "",
        "id": "",
        "importedAt": "",
        "importedBy": "",
        "storageRelPath": ""
    }"""
    File.WriteAllText(p, legacyJson, Encoding.UTF8)
    let loaded = MetaJsonIO.load dir
    // STJ default — string null, array null. caller (toRegistryEntry) 가 null 가드 책임 (별 Fact)
    Assert.Null(loaded.Description)
    Assert.Null(loaded.Keywords))

[<Fact>]
let ``stampServerFields — server 필드 채움 + client 가 보낸 server 필드 값 무시 (§3.3.1)`` () =
    // client 가 server 필드에 가짜 값 박아 보내도 server stamp 가 override
    let evilClient = { clientMeta() with Id = "fake-id"; ImportedBy = "attacker"; StorageRelPath = "evil" }
    let server = MetaJsonIO.stampServerFields "real-guid" "kwak@dualsoft.com" "Collections\\real-guid-line\\" evilClient
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
    let entry = MetaJsonRegistry.toRegistryEntry server
    Assert.Equal("g1", entry.Id)
    Assert.Equal("라인A 사양서 v3", entry.DisplayName)
    Assert.Equal("idle", entry.Status)
    Assert.Null(entry.ErrorReason)
    Assert.Equal(server.ImportedAt, entry.LastImportedAt)

[<Fact>]
let ``PR-A r0 — toRegistryEntry propagates Description / Keywords + null 가드`` () =
    // (a) 명시 박제된 두 필드 propagate
    let withFields = { clientMeta() with
                         Description = "테스트 설명"
                         Keywords = [| "k1"; "k2"; "k3" |] }
    let entry = MetaJsonRegistry.toRegistryEntry withFields
    Assert.Equal("테스트 설명", entry.Description)
    Assert.Equal<string[]>([| "k1"; "k2"; "k3" |], entry.Keywords)

    // (b) null 가드 — legacy load 시 null 박제 → toRegistryEntry 가 "" / [||] 정규화
    // F# record 의 string default = null, array default = null (CLIMutable + STJ deserialize default)
    // Unchecked.defaultof<MetaJson> 으로 모든 field null/0 default 시뮬레이션은 부담 — 두 field 만 명시 null
    let withNulls = { clientMeta() with
                        Description = Unchecked.defaultof<string>
                        Keywords = Unchecked.defaultof<string array> }
    let entry2 = MetaJsonRegistry.toRegistryEntry withNulls
    Assert.Equal("", entry2.Description)
    Assert.Equal<string[]>([||], entry2.Keywords)

[<Fact>]
let ``toRegistryEntry — 0-doc collection (MA18) — fileCount/bytes 0 보존`` () =
    let zeroDoc = { clientMeta() with FileCount = 0; TotalSourceBytes = 0L }
    let server = MetaJsonIO.stampServerFields "g1" "u" "p" zeroDoc
    let entry = MetaJsonRegistry.toRegistryEntry server
    Assert.Equal(0, entry.FileCount)
    Assert.Equal(0L, entry.TotalSourceBytes)
    // 0-doc 도 status=idle 정상 등록 (MA18)
    Assert.Equal("idle", entry.Status)

[<Fact>]
let ``schemaVersion mismatch — fail-fast`` () = withTempDir (fun dir ->
    let p = MetaJsonIO.path dir
    Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
    File.WriteAllText(p, """{"schemaVersion": 999, "title": "x"}""", Encoding.UTF8)
    Assert.Throws<InvalidDataException>(fun () -> MetaJsonIO.load dir |> ignore) |> ignore)
