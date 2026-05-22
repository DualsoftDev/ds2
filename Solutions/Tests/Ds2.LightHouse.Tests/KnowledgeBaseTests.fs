module Ds2.LightHouse.Tests.KnowledgeBaseTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// done-lighthouse-kb-index.md §4.8c — KnowledgeBase facade multi-collection ATTACH UNION.
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

let private extractors () : IExtractor list = [
    new TextExtractor() :> IExtractor
    new ImageExtractor() :> IExtractor
]
let private noProgress (_: IngestProgress) = ()

let private ingestAll (dir: string) =
    Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None |> ignore

[<Fact>]
let ``빈 active 셋 — empty result, no throw`` () =
    let kb = KnowledgeBase.openCollections [||] None
    try
        // openCollections 자체 throw 없으면 OK + search/list 가 facade contract 대로 empty 반환 (review M1).
        Assert.Empty((kb.Search { Text = "anything"; TopK = 5; FileId = None } CancellationToken.None).Results)
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
        let kb = KnowledgeBase.openCollections dirs None
        try
            let docs = kb.List()
            Assert.Equal(n, docs.Length)
        finally kb.Dispose())

[<Fact>]
let ``single collection — search hit`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "컨베이어 동작 사양 — 시스템 입력" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs None
        try
            let r = kb.Search { Text = "컨베이어"; TopK = 5; FileId = None } CancellationToken.None
            Assert.NotEmpty(r.Results)
            Assert.Contains("컨베이어", r.Results.[0].Excerpt)
            // **s6-r57 C7** — text-only file → HasImages=false 회귀 차단.
            // image 인프라 미박제 시점 정합 (Chunks.ImageCount DEFAULT 0).
            Assert.False(r.Results.[0].HasImages, "text-only file → HasImages false 의무")
        finally kb.Dispose())

[<Fact>]
let ``2 collection UNION — 각 collection 의 hit 합산`` () =
    withDirs 2 (fun dirs ->
        writeFile dirs.[0] "a.txt" "컨베이어 vendor A 사양" |> ignore
        writeFile dirs.[1] "b.txt" "컨베이어 vendor B 사양" |> ignore
        ingestAll dirs.[0]
        ingestAll dirs.[1]
        let kb = KnowledgeBase.openCollections dirs None
        try
            let r = kb.Search { Text = "컨베이어"; TopK = 10; FileId = None } CancellationToken.None
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
        let kb = KnowledgeBase.openCollections dirs None
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
        let kb = KnowledgeBase.openCollections dirs None
        try
            // kb0 의 첫 문서 fileId 알아내기
            let docs = kb.List()
            let fileId0 = docs |> Array.find (fun (id, _, _, _) -> id.StartsWith "0:") |> fun (id, _, _, _) -> id
            let r = kb.Search { Text = "alpha"; TopK = 10; FileId = Some fileId0 } CancellationToken.None
            Assert.NotEmpty(r.Results)
            for h in r.Results do
                Assert.True(h.FileId.StartsWith "0:", sprintf "fileId 한정 위반 — %s" h.FileId)
        finally kb.Dispose())

[<Fact>]
let ``fileId parse 실패 — 빈 결과 + Hint "invalid fileId"`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "본문" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs None
        try
            let r = kb.Search { Text = "본문"; TopK = 5; FileId = Some "broken-fileid" } CancellationToken.None
            Assert.Empty(r.Results)
            Assert.Equal(Some "invalid fileId", r.Hint)
        finally kb.Dispose())

[<Fact>]
let ``ATTACH limit 초과 — InvalidOperationException`` () =
    // 실 색인 없이 가짜 path 만 11개 → activePaths.Length > MaxAttachedDbs 의 fail-fast 검증.
    let bogus = Array.init (SqliteStore.MaxAttachedDbs + 1) (fun _ ->
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
    Assert.Throws<InvalidOperationException>(fun () ->
        KnowledgeBase.openCollections bogus None |> ignore) |> ignore

[<Fact>]
let ``ATTACH path 의 single-quote escape — 폴더명에 작은따옴표 허용 (review C2)`` () =
    let dirName = sprintf "lh-kb-%s-a'b" (Guid.NewGuid().ToString("N"))
    let dir = Path.Combine(Path.GetTempPath(), dirName)
    Directory.CreateDirectory dir |> ignore
    try
        writeFile dir "a.txt" "본문 alpha" |> ignore
        ingestAll dir
        let kb = KnowledgeBase.openCollections [| dir |] None
        try
            let r = kb.Search { Text = "alpha"; TopK = 5; FileId = None } CancellationToken.None
            Assert.NotEmpty(r.Results)
        finally kb.Dispose()
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``read by ref — chunk 본문 concat`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "단락 1\n\n단락 2\n\n단락 3" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs None
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
        let kb = KnowledgeBase.openCollections dirs None
        try
            let r = kb.Search { Text = "컨베이어"; TopK = 5; FileId = None } CancellationToken.None
            Assert.NotEmpty(r.Results)
        finally kb.Dispose())

[<Fact>]
let ``BM25 부호 반전 — Score 높을수록 hit 강도 (양수)`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "alpha alpha alpha beta" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs None
        try
            let r = kb.Search { Text = "alpha"; TopK = 5; FileId = None } CancellationToken.None
            Assert.NotEmpty(r.Results)
            // FTS5 bm25() 의 음수 → Searcher 가 부호 반전 → caller 통념상 양수 (높을수록 hit)
            Assert.True(r.Results.[0].Score >= 0.0,
                        sprintf "Score = %f, 부호 반전 후 양수 기대 (review M6)" r.Results.[0].Score)
        finally kb.Dispose())

[<Fact>]
let ``Searcher.buildFtsQuery — 정책 #3 OR 결합 (phrase OR token), phrase 매칭이 top rank`` () =
    withDirs 1 (fun dirs ->
        // 정책 #3: query "alpha beta" → `"alphabeta" OR "alpha beta" OR "alpha" OR "beta"`
        // a.txt 는 phrase "alpha beta" 매칭 (BM25 rank ↑) + 개별 token 매칭 둘 다 가산.
        // b.txt 는 "alpha" 단일 token 매칭만 → hit 후보 진입 (회수 확보) but rank 낮음.
        writeFile dirs.[0] "a.txt" "alpha beta gamma" |> ignore
        writeFile dirs.[0] "b.txt" "alpha only" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs None
        try
            let r = kb.Search { Text = "alpha beta"; TopK = 10; FileId = None } CancellationToken.None
            Assert.NotEmpty(r.Results)
            // 두 문서 모두 hit (recall 정책 — alpha 단독 매칭 b.txt 도 결과 진입).
            Assert.True(r.Results.Length >= 2,
                        sprintf "정책 #3 recall — a.txt + b.txt 모두 hit 기대, 실제 %d" r.Results.Length)
            // top1 은 phrase 매칭이 강한 a.txt — alpha + beta 둘 다 포함.
            let top = r.Results.[0]
            Assert.Contains("alpha", top.Excerpt)
            Assert.Contains("beta", top.Excerpt)
        finally kb.Dispose())

[<Fact>]
let ``Searcher.buildFtsQuery — 정책 #3 공백 손실 phrase (PPT→PDF 표 chunk) 매칭`` () =
    withDirs 1 (fun dirs ->
        // 정책 #3 의 핵심 가치: 공백 손실 chunk 도 phraseNoWs 로 매칭.
        // 표 본문이 공백 없이 `공장개요...` 박제된 케이스. 사용자 query "공장 개요" (공백 보존).
        writeFile dirs.[0] "table.txt" "1.광명2차체공장개요리노베이션UPH42.7생산능력13.7만" |> ignore
        writeFile dirs.[0] "prose.txt" "본문에서 공장 개요 항목을 설명한다." |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs None
        try
            let r = kb.Search { Text = "공장 개요"; TopK = 10; FileId = None } CancellationToken.None
            // 공백 손실 + 공백 보존 두 chunk 모두 hit 기대.
            Assert.True(r.Results.Length >= 2,
                        sprintf "정책 #3 — 공백 손실 + 보존 둘 다 매칭 기대, 실제 %d" r.Results.Length)
            // FileName 박제 형식은 Title (있으면) 또는 stem 우선 — 확장자 유무 불문 substring 매칭.
            let names = r.Results |> Array.map (fun h -> h.FileName)
            Assert.Contains(names, fun n -> n.Contains("table"))
            Assert.Contains(names, fun n -> n.Contains("prose"))
        finally kb.Dispose())

[<Fact>]
let ``Searcher.buildFtsQuery — 정책 #3 모든 후보 길이 < 3 시 BM25 skip (빈 결과)`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "AI 도구 활용" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs None
        try
            // query "AI" — phraseNoWs="AI" (2), phraseRaw="AI" (2), tokens=[] (none ≥3).
            // 모두 길이 < 3 → BM25 skip → BM25-only path 결과 0건.
            let r = kb.Search { Text = "AI"; TopK = 10; FileId = None } CancellationToken.None
            Assert.Empty(r.Results)
        finally kb.Dispose())

[<Fact>]
let ``Searcher.buildFtsQuery — 정책 #3 phraseRaw 다중공백 정규화 (단일공백과 동일 결과)`` () =
    // query "공장  개요" (이중 공백) 와 "공장 개요" (단일 공백) 가 phraseRaw normalize 후 동일 phrase 산출.
    // 양쪽 query 의 hit 결과 = 동일해야 함 (Self.M1-a 검열 결함 회귀 차단).
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "본문 공장 개요 항목 설명." |> ignore
        writeFile dirs.[0] "b.txt" "광명2차체공장개요리노베이션." |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs None
        try
            let singleSpace = kb.Search { Text = "공장 개요";  TopK = 10; FileId = None } CancellationToken.None
            let multiSpace  = kb.Search { Text = "공장   개요"; TopK = 10; FileId = None } CancellationToken.None
            // 회귀 가드 강도 ↑ — Set 비교는 순서 변동 못 잡음. ordered array 비교 + Score 동등성.
            // phraseRaw normalize 깨질 경우 다중공백 phrase 의 trigram 매칭 score 분포가 달라져
            // top-rank 가 바뀔 수 있음 → 순서까지 동등 검증.
            let asTuple (r: SearchResults) =
                r.Results |> Array.map (fun h -> h.FileName, h.Score)
            Assert.Equal<(string * float) array>(asTuple singleSpace, asTuple multiSpace)
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

// ── Phase 4 (s6-r35) P4-B.1 — hybrid retrieval (BM25 + vector RRF) ─────────────────

/// 색인 시점 embedding 박제 + hybrid retrieval 검증용 deterministic mock.
/// 동일 input → 동일 vector. 1차 element 가 query/chunk text 의 hash 기반 deterministic 박제.
///
/// **ranking quality 검증 불가** (s6-r35 자가 검열 m4 박제) — 본 mock 은 hybrid path 의 dispatch 정합
/// (embedderOpt None/Some 분기 + Chunks_Vectors 채워짐 + RRF 진입 + fileId 한정) 만 검증. dim=1024 중
/// [0]/[1] 만 값 박제 + 나머지 1022 dim = 0.0 → sqlite-vec L2 distance 산출 시 거의 모든 chunk 비슷한
/// distance, vector path 의 ranking quality 평가 불가. 실 backend (Ollama bge-m3 등) 의 분포 차이는
/// P4-C 의 통합 Fact 가 cover.
type private QueryFriendlyEmbedder() =
    interface IEmbeddingProvider with
        member _.Dimension = SqliteStore.EmbeddingDimension
        member _.GenerateAsync(inputs, _ct) =
            let dim = SqliteStore.EmbeddingDimension
            let vectors =
                inputs |> Array.map (fun s ->
                    // text 자체의 hash 기반 — 같은 text 는 항상 같은 vector (cosine 1.0 / distance 0).
                    let h = float32 (abs (s.GetHashCode() % 1000)) * 0.001f
                    let v = Array.create dim 0.0f
                    v.[0] <- h
                    v.[1] <- 1.0f - h
                    v)
            System.Threading.Tasks.Task.FromResult(vectors)
    interface System.IDisposable with
        member _.Dispose() = ()

[<Fact>]
let ``hybrid — embedderOpt=None (BM25-only path) 기존 동작 회귀 0`` () =
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "alpha beta gamma" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs None
        try
            let r = kb.Search { Text = "alpha"; TopK = 5; FileId = None } CancellationToken.None
            Assert.NotEmpty(r.Results)
            // BM25-only path — Score 가 부호 반전 후 양수 (review M6).
            Assert.True(r.Results.[0].Score >= 0.0,
                sprintf "BM25-only path Score=%f, 양수 기대" r.Results.[0].Score)
        finally kb.Dispose())

[<Fact>]
let ``hybrid — embedderOpt=Some (BM25 + vector RRF) Chunks_Vectors 채워진 collection 에서 hit`` () =
    // embedder=Some 으로 색인 → Chunks_Vectors 채워짐. 같은 embedder 로 검색 → RRF fusion path.
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "컨베이어 동작 사양 시스템 입력" |> ignore
        // 색인 시점에 embedder 주입 → Chunks_Vectors INSERT.
        let embedder = QueryFriendlyEmbedder() :> IEmbeddingProvider
        Indexer.ingest dirs.[0] (extractors()) CaptionGenerator.noop (Some embedder) noProgress CancellationToken.None
        |> ignore
        // 검색도 embedder=Some — hybrid path 진입.
        let kb = KnowledgeBase.openCollections dirs (Some embedder)
        try
            let r = kb.Search { Text = "컨베이어"; TopK = 5; FileId = None } CancellationToken.None
            Assert.NotEmpty(r.Results)
            // RRF score — 0 보다 큼 (양 system 에서 hit 시 더 큰 값). 1/(60+0) ≈ 0.0167 정도.
            Assert.True(r.Results.[0].Score > 0.0,
                sprintf "RRF score=%f, 양수 기대" r.Results.[0].Score)
        finally kb.Dispose())

[<Fact>]
let ``hybrid — embedderOpt=Some 이지만 Chunks_Vectors 빈 collection (legacy 색인) → BM25-only fallback 정합`` () =
    // legacy 시나리오: 기존 collection (embedder=None 으로 색인) 을 신규 embedder=Some facade 로 열기.
    // vector KNN 결과는 0-row, BM25 결과만 등장 → RRF 가 BM25-only 결과와 동일 순서 (fusion 무관).
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "alpha beta gamma" |> ignore
        ingestAll dirs.[0]  // embedder=None 색인 — Chunks_Vectors 빈 상태.
        let embedder = QueryFriendlyEmbedder() :> IEmbeddingProvider
        let kb = KnowledgeBase.openCollections dirs (Some embedder)
        try
            let r = kb.Search { Text = "alpha"; TopK = 5; FileId = None } CancellationToken.None
            Assert.NotEmpty(r.Results)
            // RRF contribution = BM25 (rank 0) 만 — 1/(60+0) ≈ 0.0167.
            let expectedTop = 1.0 / (Searcher.RrfK + 0.0)
            Assert.Equal(expectedTop, r.Results.[0].Score, 6)
        finally kb.Dispose())

[<Fact>]
let ``hybrid — fileId 한정 검색 시 다른 collection 의 vector hit 도 0`` () =
    // 두 collection 모두 embedder=Some 으로 색인 → fileId 한정 시 한쪽 collection 만 hit (BM25 + vector 양쪽).
    withDirs 2 (fun dirs ->
        writeFile dirs.[0] "a.txt" "공통 키워드 alpha" |> ignore
        writeFile dirs.[1] "b.txt" "공통 키워드 alpha" |> ignore
        let embedder = QueryFriendlyEmbedder() :> IEmbeddingProvider
        Indexer.ingest dirs.[0] (extractors()) CaptionGenerator.noop (Some embedder) noProgress CancellationToken.None |> ignore
        Indexer.ingest dirs.[1] (extractors()) CaptionGenerator.noop (Some embedder) noProgress CancellationToken.None |> ignore
        let kb = KnowledgeBase.openCollections dirs (Some embedder)
        try
            let docs = kb.List()
            let fileId0 = docs |> Array.find (fun (id, _, _, _) -> id.StartsWith "0:") |> fun (id, _, _, _) -> id
            let r = kb.Search { Text = "alpha"; TopK = 10; FileId = Some fileId0 } CancellationToken.None
            Assert.NotEmpty(r.Results)
            for h in r.Results do
                Assert.True(h.FileId.StartsWith "0:",
                    sprintf "fileId 한정 위반 (hybrid path) — %s" h.FileId)
        finally kb.Dispose())

[<Fact>]
let ``hybrid — empty query / 빈 active 셋 정합`` () =
    let embedder = QueryFriendlyEmbedder() :> IEmbeddingProvider
    // 빈 active 셋 — embedder 가 호출되지 않고 즉시 empty 반환.
    let kb = KnowledgeBase.openCollections [||] (Some embedder)
    try
        Assert.Empty((kb.Search { Text = "something"; TopK = 5; FileId = None } CancellationToken.None).Results)
    finally kb.Dispose()
    // 빈 query text — 마찬가지로 즉시 empty.
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "본문" |> ignore
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs (Some embedder)
        try
            Assert.Empty((kb.Search { Text = "   "; TopK = 5; FileId = None } CancellationToken.None).Results)
        finally kb.Dispose())

[<Fact>]
let ``hybrid — 정책 #3 모든 후보 < 3 시 BM25 skip + vector 만 hit (fallback 정합)`` () =
    // query "AI" (length 2) — phraseNoWs/phraseRaw 모두 < 3, tokens [].
    // BM25 path 는 빈 list 반환 (runBm25 의 ftsQuery 빈 string 분기), vector 만 hit 진입.
    // RRF 가 vectorHits 단독으로 fusion → top-K 산출 (BM25 contribution 0).
    withDirs 1 (fun dirs ->
        writeFile dirs.[0] "a.txt" "AI 도구 활용 검토" |> ignore
        let embedder = QueryFriendlyEmbedder() :> IEmbeddingProvider
        Indexer.ingest dirs.[0] (extractors()) CaptionGenerator.noop (Some embedder) noProgress CancellationToken.None
        |> ignore
        let kb = KnowledgeBase.openCollections dirs (Some embedder)
        try
            let r = kb.Search { Text = "AI"; TopK = 5; FileId = None } CancellationToken.None
            // vector 만으로 hit — BM25 skip 시 hybrid path 가 죽지 않음을 확인.
            Assert.NotEmpty(r.Results)
            // RRF contribution = vector (rank 0) 만 — 1/(60+0) ≈ 0.0167.
            let expectedTop = 1.0 / (Searcher.RrfK + 0.0)
            Assert.Equal(expectedTop, r.Results.[0].Score, 6)
        finally kb.Dispose())

[<Fact>]
let ``Task 7 회귀 가드 — standalone PNG 색인 후 kb.List() 의 FileKind = FileKind.Image`` () =
    // Searcher.parseDocType 의 "image" 매핑 회귀 차단. write path (SqliteStore.docTypeToString) 와
    // read path (Searcher.parseDocType) 의 round-trip 정합 의무. drift 시 Unsupported "image" 로 분류됨.
    withDirs 1 (fun dirs ->
        let path = Path.Combine(dirs.[0], "logo.png")
        File.WriteAllBytes(path, SamplePng.bytes)
        ingestAll dirs.[0]
        let kb = KnowledgeBase.openCollections dirs None
        try
            let docs = kb.List()
            Assert.Single(docs) |> ignore
            let (_, originalPath, kind, _) = docs.[0]
            Assert.Equal(FileKind.Image, kind)
            Assert.EndsWith("logo.png", originalPath)
        finally kb.Dispose())
