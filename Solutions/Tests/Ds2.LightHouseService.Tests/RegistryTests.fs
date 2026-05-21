module Ds2.LightHouseService.Tests.RegistryTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Ds2.LightHouseService

let private withTempRoot (action: string -> Task<'r>) : Task<'r> = task {
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lhs-reg-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try return! action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()
}

let private mkEntry (id: string) (name: string) : CollectionEntry = {
    Id = id
    DisplayName = name
    IndexerVersion = "1.0.0"
    FileCount = 0
    TotalSourceBytes = 0L
    CreatedAt = "2026-05-17T00:00:00Z"
    ImportedAt = "2026-05-17T00:00:00Z"
    ImportedBy = "tester"
    StorageRelPath = sprintf "Collections\\%s-%s\\" id name
    Status = "idle"
    ErrorReason = null
    LastImportedAt = "2026-05-17T00:00:00Z"
    // **PR-A (r0)** — 두 신 필드 default (KeywordExtractor 미진입 시점)
    Description = ""
    Keywords = [||]
    Acl = Unchecked.defaultof<CollectionAcl>
}

[<Fact>]
let ``load — 미존재 시 빈 registry 반환`` () = withTempRoot (fun root -> task {
    let reg = Registry.load root
    Assert.Equal(1, reg.SchemaVersion)
    Assert.Empty(reg.Collections)
})

[<Fact>]
let ``upsert + listSnapshot — 신규 추가 round-trip`` () = withTempRoot (fun root -> task {
    let entry = mkEntry "id-1" "first"
    do! Registry.upsertAsync root entry
    let list = Registry.listSnapshot root
    Assert.Single(list) |> ignore
    Assert.Equal("id-1", list.[0].Id)
    Assert.Equal("first", list.[0].DisplayName)
})

[<Fact>]
let ``upsert — 기존 id 갱신 (중복 추가 X)`` () = withTempRoot (fun root -> task {
    do! Registry.upsertAsync root (mkEntry "id-1" "first")
    do! Registry.upsertAsync root (mkEntry "id-1" "first-updated")
    let list = Registry.listSnapshot root
    Assert.Single(list) |> ignore
    Assert.Equal("first-updated", list.[0].DisplayName)
})

[<Fact>]
let ``remove — 일치 항목 제거 후 true, 미일치 false`` () = withTempRoot (fun root -> task {
    do! Registry.upsertAsync root (mkEntry "id-1" "a")
    do! Registry.upsertAsync root (mkEntry "id-2" "b")
    let! r1 = Registry.removeAsync root "id-1"
    let! r2 = Registry.removeAsync root "id-none"
    Assert.True(r1)
    Assert.False(r2)
    let remaining = Registry.listSnapshot root
    Assert.Single(remaining) |> ignore
    Assert.Equal("id-2", remaining.[0].Id)
})

[<Fact>]
let ``tryFindById — 일치 / 미일치`` () = withTempRoot (fun root -> task {
    do! Registry.upsertAsync root (mkEntry "id-1" "a")
    Assert.Equal(Some "id-1", Registry.tryFindById root "id-1" |> Option.map (fun e -> e.Id))
    Assert.Equal(None, Registry.tryFindById root "id-none")
})

[<Fact>]
let ``동시성 — 100개 task 가 같은 registry 에 upsert race 후 무결성 유지 (CR5)`` () = withTempRoot (fun root -> task {
    let n = 100
    let tasks =
        [| for i in 1..n ->
            Registry.upsertAsync root (mkEntry (sprintf "id-%d" i) (sprintf "name-%d" i)) |]
    let! _ = Task.WhenAll tasks
    let list = Registry.listSnapshot root
    Assert.Equal(n, list.Length)
    // id 모두 unique
    let ids = list |> Array.map (fun e -> e.Id) |> Set.ofArray
    Assert.Equal(n, ids.Count)
})

[<Fact>]
let ``동시성 — upsert + remove 혼합 race 무결성`` () = withTempRoot (fun root -> task {
    // 초기 entry 50 개
    for i in 1..50 do
        do! Registry.upsertAsync root (mkEntry (sprintf "id-%d" i) "init")
    // 절반은 remove, 나머지는 upsert (DisplayName 갱신).
    // 두 API 의 Task return type 이 다름 (Task<bool> vs Task<unit>) — Task base 로 normalize.
    let tasks : Task array =
        [| for i in 1..50 ->
            if i % 2 = 0 then
                Registry.removeAsync root (sprintf "id-%d" i) :> Task
            else
                Registry.upsertAsync root (mkEntry (sprintf "id-%d" i) "updated") :> Task |]
    do! Task.WhenAll tasks
    let list = Registry.listSnapshot root
    // 홀수 id 25 개만 남아야
    Assert.Equal(25, list.Length)
    for e in list do
        Assert.Equal("updated", e.DisplayName)
})

[<Fact>]
let ``schemaVersion mismatch — fail-fast`` () = withTempRoot (fun root -> task {
    // 직접 잘못된 registry.json 작성
    let p = Registry.path root
    File.WriteAllText(p, """{"schemaVersion": 999, "collections": []}""")
    Assert.Throws<InvalidDataException>(fun () -> Registry.load root |> ignore) |> ignore
})

[<Fact>]
let ``atomic save — write to .tmp 후 rename (다른 process 가 partial 읽지 않음)`` () = withTempRoot (fun root -> task {
    do! Registry.upsertAsync root (mkEntry "id-1" "atomic-test")
    let p = Registry.path root
    // .tmp 파일 잔재 0
    let tmpRemains = File.Exists (p + ".tmp")
    Assert.False(tmpRemains, ".tmp 잔재 없어야 함")
    Assert.True(File.Exists p)
})

// **s6-r54 K6 (보안 sweep)** — registry.json tampering 의심 entry skip + audit log.
// admin 침해 / 외부 수동 편집 시나리오 — 의심 entry 만 빠지고 정상 entry 는 보존.

[<Fact>]
let ``K6 — DisplayName 의 path traversal (..) 의심 entry skip`` () = withTempRoot (fun root -> task {
    let p = Registry.path root
    // 정상 + 의심 두 entry 박제 (의심 = DisplayName 에 ".." 포함)
    let raw = """{"schemaVersion":1,"collections":[
        {"id":"id-good","displayName":"normal","indexerVersion":"1.0.0","fileCount":0,"totalSourceBytes":0,
         "createdAt":"","importedAt":"","importedBy":"","storageRelPath":"Collections\\id-good-normal\\",
         "status":"idle","errorReason":null,"lastImportedAt":""},
        {"id":"id-bad","displayName":"..\\\\windows\\\\system32","indexerVersion":"1.0.0","fileCount":0,"totalSourceBytes":0,
         "createdAt":"","importedAt":"","importedBy":"","storageRelPath":"Collections\\id-bad\\",
         "status":"idle","errorReason":null,"lastImportedAt":""}
    ]}"""
    File.WriteAllText(p, raw)
    let reg = Registry.load root
    // 의심 entry 1건 skip → 정상 1건만
    Assert.Single(reg.Collections) |> ignore
    Assert.Equal("id-good", reg.Collections.[0].Id)
})

[<Fact>]
let ``K6 — StorageRelPath 절대경로 / 의심 char (':') 의심 entry skip`` () = withTempRoot (fun root -> task {
    let p = Registry.path root
    // StorageRelPath 에 ":" (Windows drive letter) → 절대경로 의심 → skip
    let raw = """{"schemaVersion":1,"collections":[
        {"id":"id-bad","displayName":"normal","indexerVersion":"1.0.0","fileCount":0,"totalSourceBytes":0,
         "createdAt":"","importedAt":"","importedBy":"","storageRelPath":"C:\\\\evil\\\\path",
         "status":"idle","errorReason":null,"lastImportedAt":""}
    ]}"""
    File.WriteAllText(p, raw)
    let reg = Registry.load root
    Assert.Empty(reg.Collections)
})

[<Fact>]
let ``K6 — Id empty 의심 entry skip`` () = withTempRoot (fun root -> task {
    let p = Registry.path root
    let raw = """{"schemaVersion":1,"collections":[
        {"id":"","displayName":"normal","indexerVersion":"1.0.0","fileCount":0,"totalSourceBytes":0,
         "createdAt":"","importedAt":"","importedBy":"","storageRelPath":"Collections\\x\\",
         "status":"idle","errorReason":null,"lastImportedAt":""}
    ]}"""
    File.WriteAllText(p, raw)
    let reg = Registry.load root
    Assert.Empty(reg.Collections)
})

[<Fact>]
let ``PR-A r0 — upsert + load round-trip Description / Keywords 보존`` () = withTempRoot (fun root -> task {
    let entry = { mkEntry "id-pr-a" "summary-test" with
                    Description = "라인A 컨베이어 사양 요약"
                    Keywords = [| "컨베이어"; "PLC"; "IO"; "안전"; "센서" |] }
    do! Registry.upsertAsync root entry
    let list = Registry.listSnapshot root
    Assert.Single(list) |> ignore
    Assert.Equal("라인A 컨베이어 사양 요약", list.[0].Description)
    Assert.Equal<string[]>([| "컨베이어"; "PLC"; "IO"; "안전"; "센서" |], list.[0].Keywords)
})

[<Fact>]
let ``PR-A r0 — legacy registry.json (두 필드 누락) load → null default (forward-compat)`` () = withTempRoot (fun root -> task {
    // PR-A 진입 이전 시점의 registry.json 시뮬레이션 (description / keywords 누락).
    // STJ default 가 null 박제 — caller (UI / KbDigestBuilder) 가 null 가드 책임.
    let p = Registry.path root
    let raw = """{"schemaVersion":1,"collections":[
        {"id":"legacy-1","displayName":"legacy","indexerVersion":"1.0.0","fileCount":0,"totalSourceBytes":0,
         "createdAt":"2026-05-17T00:00:00Z","importedAt":"2026-05-17T00:00:00Z","importedBy":"old",
         "storageRelPath":"Collections\\legacy-1-legacy\\","status":"idle","errorReason":null,
         "lastImportedAt":"2026-05-17T00:00:00Z","acl":null}
    ]}"""
    File.WriteAllText(p, raw)
    let reg = Registry.load root
    Assert.Single(reg.Collections) |> ignore
    // legacy entry 의 두 필드 = STJ default. caller (KbDigestBuilder) 가 null 가드.
    Assert.Null(reg.Collections.[0].Description)
    Assert.Null(reg.Collections.[0].Keywords)
    return ()
})
