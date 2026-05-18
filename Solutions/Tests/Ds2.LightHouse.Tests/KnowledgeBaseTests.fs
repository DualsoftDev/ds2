module Ds2.LightHouse.Tests.KnowledgeBaseTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// todo-lighthouse-kb-index.md §4.8c — KnowledgeBase facade multi-collection ATTACH UNION.
///
/// 검증: ATTACH parameter binding 불가 → inline + single-quote escape (review C2 잔여),
///       FTS5 external-content trigger (review M2 잔여),
///       fileId `<kbIdx>:<docId>` cross-collection unique,
///       active 셋 union 검색, ATTACH limit 10 boundary, fileId parse 실패 처리.

let private withDirs (count: int) (action: string array -> 'r) : 'r =
    let dirs =
        [| for _ in 1..count ->
             let d = Path.Combine(Path.GetTempPath(), sprintf "lh-kb-%s" (Guid.NewGuid().ToString("N")))
             Directory.CreateDirectory d |> ignore
             d |]
    try action dirs
    finally
        for d in dirs do
            try Directory.Delete(d, true) with _ -> ()

let private writeFile (dir: string) (name: string) (body: string) =
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, body, Encoding.UTF8)
    path

let private extractors () : IExtractor list = [ new TextExtractor() :> IExtractor ]
let private noProgress (_: IngestProgress) = ()

let private ingestAll (dir: string) =
    Indexer.ingest dir (extractors()) noProgress CancellationToken.None |> ignore

[<Fact>]
let ``빈 active 셋 — empty result, no throw`` () =
    let kb = KnowledgeBase.openCollections [||]
    try
        // openCollections 자체 throw 없으면 OK + search/list 가 facade contract 대로 empty 반환 (review M1).
        Assert.Empty((kb.Search { Text = "anything"; TopK = 5; FileId = None }).Results)
        Assert.Empty(kb.List())
    finally kb.Dispose()

[<Fact>]
let ``ATTACH boundary — 10 collection 까지 정상 ATTACH 가능 (§4.8c SSOT)`` () =
    // 10 개 색인된 collection 을 동시 ATTACH — SqliteStore.MaxAttachedDbs 경계 검증.
    let n = SqliteStore.MaxAttachedDbs
    withDirs n (fun dirs ->
        for d in dirs do
            writeFile d "a.txt" (sprintf "본문 %s" (Path.GetFileName d)) |> ignore
            ingestAll d
        let kb = KnowledgeBase.openCollections dirs
        try
            let docs = kb.List()
            Assert.Equal(n, docs.Length)
        finally kb.Dispose())

[<Fact>]
let ``single collection — search hit`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "컨베이어 동작 사양 — 시스템 입력" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs
        try
            let r = kb.Search { Text = "컨베이어"; TopK = 5; FileId = None }
            Assert.NotEmpty(r.Results)
            Assert.Contains("컨베이어", r.Results.[0].Excerpt)
        finally kb.Dispose())

[<Fact>]
let ``2 collection UNION — 각 collection 의 hit 합산`` () =
    withDirs 2 (fun dirs ->
        writeFile dirs.[0] "a.txt" "컨베이어 vendor A 사양" |> ignore
        writeFile dirs.[1] "b.txt" "컨베이어 vendor B 사양" |> ignore
        ingestAll dirs.[0]
        ingestAll dirs.[1]
        let kb = KnowledgeBase.openCollections dirs
        try
            let r = kb.Search { Text = "컨베이어"; TopK = 10; FileId = None }
            Assert.Equal(2, r.Results.Length)
            // fileId 모두 unique
            let ids = r.Results |> Array.map (fun h -> h.FileId)
            Assert.Equal(ids.Length, (Set.ofArray ids).Count)
            // kbIdx 0 / kbIdx 1 모두 등장 — fileId prefix 검증
            Assert.Contains(r.Results, fun h -> h.FileId.StartsWith "0:")
            Assert.Contains(r.Results, fun h -> h.FileId.StartsWith "1:")
        finally kb.Dispose())

[<Fact>]
let ``fileId cross-collection unique — 같은 docId 라도 kbIdx 분리`` () =
    withDirs 2 (fun dirs ->
        // 두 collection 모두 첫 파일 → Document.Id = 1 (cross-collection 충돌 잠재)
        writeFile dirs.[0] "only.txt" "vendor A" |> ignore
        writeFile dirs.[1] "only.txt" "vendor B" |> ignore
        ingestAll dirs.[0]
        ingestAll dirs.[1]
        let kb = KnowledgeBase.openCollections dirs
        try
            let docs = kb.List()
            Assert.Equal(2, docs.Length)
            let ids = docs |> Array.map (fun (id, _, _, _) -> id)
            Assert.Equal(ids.Length, (Set.ofArray ids).Count)
            // 명시 — "0:..." / "1:..." 둘 다 존재
            Assert.Contains(ids, fun s -> s.StartsWith "0:")
            Assert.Contains(ids, fun s -> s.StartsWith "1:")
        finally kb.Dispose())

[<Fact>]
let ``fileId 한정 검색 — 다른 collection 은 0-hit`` () =
    withDirs 2 (fun dirs ->
        writeFile dirs.[0] "a.txt" "공통 키워드 alpha" |> ignore
        writeFile dirs.[1] "b.txt" "공통 키워드 alpha" |> ignore
        ingestAll dirs.[0]
        ingestAll dirs.[1]
        let kb = KnowledgeBase.openCollections dirs
        try
            // kb0 의 첫 문서 fileId 알아내기
            let docs = kb.List()
            let fileId0 = docs |> Array.find (fun (id, _, _, _) -> id.StartsWith "0:") |> fun (id, _, _, _) -> id
            let r = kb.Search { Text = "alpha"; TopK = 10; FileId = Some fileId0 }
            Assert.NotEmpty(r.Results)
            for h in r.Results do
                Assert.True(h.FileId.StartsWith "0:", sprintf "fileId 한정 위반 — %s" h.FileId)
        finally kb.Dispose())

[<Fact>]
let ``fileId parse 실패 — 빈 결과 + Hint "invalid fileId"`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "본문" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs
        try
            let r = kb.Search { Text = "본문"; TopK = 5; FileId = Some "broken-fileid" }
            Assert.Empty(r.Results)
            Assert.Equal(Some "invalid fileId", r.Hint)
        finally kb.Dispose())

[<Fact>]
let ``ATTACH limit 초과 — InvalidOperationException`` () =
    // 실 색인 없이 가짜 path 만 11개 → activePaths.Length > MaxAttachedDbs 의 fail-fast 검증.
    let bogus = Array.init (SqliteStore.MaxAttachedDbs + 1) (fun _ ->
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
    Assert.Throws<InvalidOperationException>(fun () ->
        KnowledgeBase.openCollections bogus |> ignore) |> ignore

[<Fact>]
let ``ATTACH path 의 single-quote escape — 폴더명에 작은따옴표 허용 (review C2)`` () =
    let dirName = sprintf "lh-kb-%s-a'b" (Guid.NewGuid().ToString("N"))
    let dir = Path.Combine(Path.GetTempPath(), dirName)
    Directory.CreateDirectory dir |> ignore
    try
        writeFile dir "a.txt" "본문 alpha" |> ignore
        ingestAll dir
        let kb = KnowledgeBase.openCollections [| dir |]
        try
            let r = kb.Search { Text = "alpha"; TopK = 5; FileId = None }
            Assert.NotEmpty(r.Results)
        finally kb.Dispose()
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``read by ref — chunk 본문 concat`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "단락 1\n\n단락 2\n\n단락 3" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs
        try
            let docs = kb.List()
            let fileId = docs.[0] |> fun (id, _, _, _) -> id
            let text = kb.Read fileId "p=1"
            Assert.Equal("단락 1", text)
        finally kb.Dispose())

[<Fact>]
let ``한국어 trigram 회귀 — "컨베이어" query 가 "컨베이어가/를/는" 본문 hit`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "spec.txt" "컨베이어가 정지하면 컨베이어를 재시작한다" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs
        try
            let r = kb.Search { Text = "컨베이어"; TopK = 5; FileId = None }
            Assert.NotEmpty(r.Results)
        finally kb.Dispose())

[<Fact>]
let ``BM25 부호 반전 — Score 높을수록 hit 강도 (양수)`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "alpha alpha alpha beta" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs
        try
            let r = kb.Search { Text = "alpha"; TopK = 5; FileId = None }
            Assert.NotEmpty(r.Results)
            // FTS5 bm25() 의 음수 → Searcher 가 부호 반전 → caller 통념상 양수 (높을수록 hit)
            Assert.True(r.Results.[0].Score >= 0.0,
                        sprintf "Score = %f, 부호 반전 후 양수 기대 (review M6)" r.Results.[0].Score)
        finally kb.Dispose())

[<Fact>]
let ``Searcher.buildFtsQuery — 공백 분리 multi-token implicit AND (review M1)`` () =
    withDirs 1 (fun dirs ->
        // "alpha beta" 두 token 모두 포함하는 문서만 hit (implicit AND)
        writeFile dirs.[0] "a.txt" "alpha beta gamma" |> ignore
        writeFile dirs.[0] "b.txt" "alpha only" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs
        try
            let r = kb.Search { Text = "alpha beta"; TopK = 10; FileId = None }
            // "alpha beta" 두 token AND → "alpha beta gamma" 만 hit
            Assert.NotEmpty(r.Results)
            for h in r.Results do
                Assert.Contains("alpha", h.Excerpt)
                Assert.Contains("beta", h.Excerpt)
        finally kb.Dispose())

// ── stampIndexerVersion (test-only override facade — IndexerVersion gate 415 시나리오용) ────

[<Fact>]
let ``stampIndexerVersion — 색인 후 indexer_version 행 override → probeIndexerVersion 반영`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "stamp test content" |> ignore
        ingestAll dirs.[0]
        // 색인 직후의 baseline = IndexerVersion.Current
        let before = KnowledgeBase.probeIndexerVersion dirs.[0]
        Assert.Equal(Some IndexerVersion.Current, before)
        // override
        KnowledgeBase.stampIndexerVersion dirs.[0] "0.5.0"
        let after = KnowledgeBase.probeIndexerVersion dirs.[0]
        Assert.Equal(Some "0.5.0", after))

[<Fact>]
let ``stampIndexerVersion — index.db 미존재 시 InvalidOperationException`` () =
    withDirs 1 (fun dirs ->
        // 색인 안 함 → .lighthouse-kb/index.db 부재
        Assert.Throws<InvalidOperationException>(fun () ->
            KnowledgeBase.stampIndexerVersion dirs.[0] "1.0.0") |> ignore)
