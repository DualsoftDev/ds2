module Ds2.LightHouse.Tests.IndexerTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// done-lighthouse-kb-index.md §4.8b — Indexer 전체 흐름 + 0-doc / 0-byte / idempotent / IndexerVersion bump.

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-ix-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

let private writeFile (dir: string) (name: string) (body: string) =
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, body, Encoding.UTF8)
    path

let private extractors () : IExtractor list = [
    new TextExtractor() :> IExtractor
    new PdfExtractor() :> IExtractor
    new OoxmlExtractor() :> IExtractor
    new ImageExtractor() :> IExtractor
]

let private noProgress (_: IngestProgress) = ()

[<Fact>]
let ``0-doc collection — 빈 폴더 정상 ingest (index.db 생성 + Documents 0)`` () =
    withTempDir (fun dir ->
        let results = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        Assert.Empty(results)
        Assert.True(File.Exists (SqliteStore.dbPath dir)))

[<Fact>]
let ``기본 흐름 — txt/md 파일 색인`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "첫 문서 본문" |> ignore
        writeFile dir "b.md" "# 헤더\n\n본문" |> ignore
        let results = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        Assert.Equal(2, results.Length)
        for (_, r) in results do
            match r with
            | Ingested _ -> ()
            | other -> Assert.Fail(sprintf "기대 = Ingested, 실제 = %A" other))

[<Fact>]
let ``Idempotent — 같은 파일 두 번 ingest → Documents 1개 (s6-r49 #2 mtime fast-skip path)`` () =
    // 동일 파일 두 번 ingest 시 두 번째 호출이 fast-skip path (mtime/size match, hash 계산 없이) 진입.
    // 첫 호출이 FileMTimeTicks stamp → 두 번째 호출이 mtime/size 비교 → match → "fast-skip" reason.
    withTempDir (fun dir ->
        writeFile dir "a.txt" "본문" |> ignore
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        let results2 = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        Assert.Single(results2) |> ignore
        match snd results2.[0] with
        | Skipped reason -> Assert.Contains("fast-skip", reason)
        | other -> Assert.Fail(sprintf "재 ingest 는 Skipped 기대, 실제 = %A" other))

[<Fact>]
let ``s6-r49 #2 mtime mismatch — 파일 mtime 변경 시 fast-skip 무효화 + 재색인 진입`` () =
    // 첫 ingest 후 파일 mtime 만 변경 (내용 동일 = hash 동일). fast-skip path 가 mtime mismatch → fall-back
    // hash 계산 path 진입 → findDocumentByHash 가 기존 row 발견 → "already ingested (same hash)" skip.
    withTempDir (fun dir ->
        let path = writeFile dir "a.txt" "본문"
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        // mtime 변경 (size + 내용 동일).
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1.0))
        let results2 = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        Assert.Single(results2) |> ignore
        match snd results2.[0] with
        | Skipped reason ->
            // fast-skip 무효화 — fall-back hash path 의 idempotent skip (substring "already ingested").
            Assert.Contains("already ingested", reason)
        | other -> Assert.Fail(sprintf "재 ingest 는 Skipped 기대, 실제 = %A" other))

[<Fact>]
let ``s6-r49 #2 legacy row (FileMTimeTicks NULL) — fast-skip 비활성 + hash fallback`` () =
    // legacy DB (FileMTimeTicks NULL) 시뮬레이션 — `insertDocument` legacy wrapper (mtime=None) 직접 호출.
    // ingest 호출 시 fast-skip path 가 NULL mtime 발견 → fall-back hash 계산 → 기존 hash 발견 시 skip.
    withTempDir (fun dir ->
        let path = writeFile dir "a.txt" "본문"
        // legacy DB 직접 세팅: index.db 생성 + ensureSchema + 기존 row insert (mtime NULL via legacy wrapper).
        do
            use seed = SqliteStore.openConnection (SqliteStore.dbPath dir) false
            SqliteStore.ensureSchema seed
            SqliteStore.stampVersion seed
            // 정상 컴퓨팅 hash 값 박제 — 실 파일 내용 일치 의무.
            use sha = SHA256.Create()
            use fs = File.OpenRead path
            let hash = Convert.ToHexString(sha.ComputeHash fs)
            SqliteStore.insertDocument seed hash path Text (FileInfo(path).Length) None None |> ignore
        // 본 호출 — fast-skip path = NULL mtime → fall-back hash → hash 일치 → "already ingested" skip.
        let results = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        Assert.Single(results) |> ignore
        match snd results.[0] with
        | Skipped reason -> Assert.Contains("already ingested", reason)
        | other -> Assert.Fail(sprintf "legacy row 는 hash fallback Skipped 기대, 실제 = %A" other))

[<Fact>]
let ``미지원 ext (.dwg) — Skipped`` () =
    withTempDir (fun dir ->
        let path = writeFile dir "design.dwg" "binary-ish"
        let results = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        let pair = results |> Array.find (fun (p, _) -> p = path)
        match snd pair with
        | Skipped reason -> Assert.Contains("unsupported ext", reason)
        | other -> Assert.Fail(sprintf "기대 = Skipped, 실제 = %A" other))

[<Fact>]
let ``rejected ext (.env) — Skipped`` () =
    withTempDir (fun dir ->
        let path = writeFile dir "secrets.env" "API_KEY=xxx"
        let results = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        let pair = results |> Array.find (fun (p, _) -> p = path)
        match snd pair with
        | Skipped reason -> Assert.Contains("rejected ext", reason)
        | other -> Assert.Fail(sprintf "기대 = Skipped, 실제 = %A" other))

[<Fact>]
let ``0-byte 파일 — extractor 가 빈 결과로 처리 (Ingested with 0 segments)`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "empty.txt")
        File.WriteAllBytes(path, [||])
        let results = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        Assert.Single(results) |> ignore
        match snd results.[0] with
        | Ingested _ -> ()
        | other -> Assert.Fail(sprintf "기대 = Ingested, 실제 = %A" other))

[<Fact>]
let ``IndexerVersion drift → shadow rebuild 발생`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "본문" |> ignore
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None

        // drift 유도
        let dbPath = SqliteStore.dbPath dir
        (
            use conn = SqliteStore.openConnection dbPath false
            SqliteStore.setMeta conn "indexer_version" "0.0.0"
        )

        // 재 ingest → shadow rebuild → indexer_version 이 Current 로 복귀
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        use conn = SqliteStore.openConnection dbPath false
        Assert.Equal(Some IndexerVersion.Current, SqliteStore.getMeta conn "indexer_version"))

[<Fact>]
let ``.lighthouse-kb 폴더 자체는 색인 대상에서 제외`` () =
    withTempDir (fun dir ->
        // 첫 ingest 후 .lighthouse-kb/index.db 가 생성됨
        writeFile dir "a.txt" "본문" |> ignore
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None

        // .lighthouse-kb 안에 가짜 txt 추가 — 재 ingest 시 enumerate 에서 제외 확인
        let kbDir = SqliteStore.kbDir dir
        let bogus = Path.Combine(kbDir, "inside.txt")
        File.WriteAllText(bogus, "should not be ingested", Encoding.UTF8)
        let results = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        // 새 파일이 .lighthouse-kb 안이라 enumerate 단계에서 제외 — results 에 inside.txt 없음
        let touched = results |> Array.exists (fun (p, _) -> p = bogus)
        Assert.False(touched, "inside .lighthouse-kb/ 파일은 enumerate 에서 제외되어야 함"))

// ── Phase 2 task C1 (s6-r12): Indexer.ingestImagesIntoStore 회귀 차단 ──
// 본 단원 = Indexer 가 ExtractedDocument.Images 를 받아 ImageStore 로 dispatch 하는 helper 의 unit-level fact.
// 실 extractor 의 image 추출 (Phase 2 task C2 PdfExtractor / C3 OoxmlExtractor) 은 별 turn — 본 fact 는
// synthetic ExtractedImage array 로 dispatch path 만 검증.

// 1×1 px PNG + 8 KB padding — Indexer.MinImageBytesForIndex (Plan 2 icon size 가드) 통과 의무.
// SamplePng.bytes 직접 박제 (~67 bytes) 는 가드로 skip → ingestImagesIntoStore fact 회귀.
// padding 후 sha256 변경되나 본 fact 들은 dynamic computeSha256 호출 → hash 박제 검증 안 함, 영향 0.
let private samplePngBytes : byte[] =
    Array.append Ds2.LightHouse.Tests.SamplePng.bytes (Array.zeroCreate 8192)

let private openFreshAt (dir: string) : Microsoft.Data.Sqlite.SqliteConnection =
    let conn = SqliteStore.openConnection (SqliteStore.dbPath dir) false
    SqliteStore.ensureSchema conn
    SqliteStore.stampVersion conn
    conn

[<Fact>]
let ``ingestImagesIntoStore — 빈 배열은 no-op (Phase 1 extractor default)`` () =
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-empty" "a.pdf" Pdf 1L None None
        Indexer.ingestImagesIntoStore conn dir docId Map.empty CaptionGenerator.noop [||]
        Assert.Empty(ImageStore.lookupReferencesByDocument conn docId)
        // blob 디렉토리 자체도 생성 안 됨 (saveBlob 미호출).
        Assert.False(Directory.Exists (ImageStore.blobsImagesDir dir)))

[<Fact>]
let ``ingestImagesIntoStore — 단일 image dispatch 후 ImageCache + ImageReferences + blob 파일 박제`` () =
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-img" "spec.pdf" Pdf 1L None None
        let img = {
            Bytes = samplePngBytes
            Format = Png
            Width = Some 1
            Height = Some 1
            RefLocator = "p=14"
            Ordinal = 1
        }
        Indexer.ingestImagesIntoStore conn dir docId Map.empty CaptionGenerator.noop [| img |]
        // blob 파일 disk 박제.
        let hash = ImageStore.computeSha256 samplePngBytes
        Assert.True(File.Exists (ImageStore.blobFilePath dir hash Png))
        // ImageCache row 박제 — width/height 반영.
        match ImageStore.getImageCache conn hash with
        | Some (p, mime, w, h) ->
            Assert.Equal(ImageStore.blobFilePath dir hash Png, p)
            Assert.Equal("image/png", mime)
            Assert.Equal(Some 1, w)
            Assert.Equal(Some 1, h)
        | None -> Assert.Fail "ImageCache row 미박제"
        // ImageReferences row — DocumentId + RefLocator + Ordinal 정합.
        let refs = ImageStore.lookupReferencesByDocument conn docId
        Assert.Single refs |> ignore
        let (refHash, refLoc, refOrd, refChunk) = refs.[0]
        Assert.Equal(hash, refHash)
        Assert.Equal("p=14", refLoc)
        Assert.Equal(1, refOrd)
        Assert.Equal(None, refChunk))   // Phase 2 task C4 진입 전에는 항상 None.

[<Fact>]
let ``ingestImagesIntoStore — 같은 image 가 두 document 에 dispatch 시 per-collection dedup (ImageCache 1행)`` () =
    // parent §3.15.5 MR2 정합 — 본 fact = e2e dispatch path 도 ImageStoreTests 의 단위 dedup invariant 정합.
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docA = SqliteStore.insertDocument conn "H-A" "a.pdf" Pdf 1L None None
        let docB = SqliteStore.insertDocument conn "H-B" "b.pdf" Pdf 1L None None
        let imgFor refLoc = {
            Bytes = samplePngBytes
            Format = Png
            Width = None
            Height = None
            RefLocator = refLoc
            Ordinal = 1
        }
        Indexer.ingestImagesIntoStore conn dir docA Map.empty CaptionGenerator.noop [| imgFor "p=1" |]
        Indexer.ingestImagesIntoStore conn dir docB Map.empty CaptionGenerator.noop [| imgFor "p=5" |]
        // ImageCache 1 row.
        use count = conn.CreateCommand()
        count.CommandText <- "SELECT count(*) FROM ImageCache"
        Assert.Equal(1, Convert.ToInt32(count.ExecuteScalar()))
        // 각 document 별 ImageReferences 1 row.
        Assert.Equal(1, (ImageStore.lookupReferencesByDocument conn docA).Length)
        Assert.Equal(1, (ImageStore.lookupReferencesByDocument conn docB).Length))

[<Fact>]
let ``ingestImagesIntoStore — 동일 PK 4 키 중복 호출은 INSERT OR IGNORE 으로 idempotent`` () =
    // re-ingest 시나리오 — 같은 document 의 같은 RefLocator/Ordinal 두 번 호출도 PK 1 row.
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-dup" "x.pdf" Pdf 1L None None
        let img = {
            Bytes = samplePngBytes
            Format = Png
            Width = None
            Height = None
            RefLocator = "p=1"
            Ordinal = 1
        }
        Indexer.ingestImagesIntoStore conn dir docId Map.empty CaptionGenerator.noop [| img; img; img |]
        Assert.Equal(1, (ImageStore.lookupReferencesByDocument conn docId).Length))

[<Fact>]
let ``ingestImagesIntoStore — m6 defensive 가드 + M2 single-skip 후속 정상 (empty Bytes 와 valid Bytes 혼합)`` () =
    // m6 결론 회귀 차단 — extractor primary 가드 (PdfExtractor TryGetPng false 분기) 회귀 시
    //   ingestImagesIntoStore 의 defensive 2차 가드가 empty Bytes 를 잡아 skip.
    // M2 결론 의미 검증 — single image skip 이 후속 image dispatch 차단 안 함 (per-image fail-safe).
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-mix" "mix.pdf" Pdf 1L None None
        let imgEmpty = {
            Bytes = [||]
            Format = Png
            Width = None
            Height = None
            RefLocator = "p=1"
            Ordinal = 1
        }
        let imgValid = {
            Bytes = samplePngBytes
            Format = Png
            Width = Some 1
            Height = Some 1
            RefLocator = "p=2"
            Ordinal = 1
        }
        Indexer.ingestImagesIntoStore conn dir docId Map.empty CaptionGenerator.noop [| imgEmpty; imgValid |]
        // empty skip — ImageReferences 1 row (valid 만), RefLocator = valid 의 위치.
        let refs = ImageStore.lookupReferencesByDocument conn docId
        Assert.Single refs |> ignore
        let (_, refLoc, _, _) = refs.[0]
        Assert.Equal("p=2", refLoc)
        // ImageCache 도 1 row (valid 만) — empty Bytes 는 sha256 산출 직전 skip 이라 cache 항목 0.
        use count = conn.CreateCommand()
        count.CommandText <- "SELECT count(*) FROM ImageCache"
        Assert.Equal(1, Convert.ToInt32(count.ExecuteScalar()))
        // blob 디렉토리는 valid 처리 시 saveBlob 가 생성 — 존재 + 정확히 1 파일.
        let blobsDir = ImageStore.blobsImagesDir dir
        Assert.True(Directory.Exists blobsDir)
        Assert.Equal(1, Directory.GetFiles(blobsDir).Length))

[<Fact>]
let ``Indexer.ingest e2e — Phase 1 extractor (Images=[||]) 경로는 ImageCache 0 + ImageReferences 0 박제`` () =
    // Phase 1 extractor 의 default Images=[||] 회귀 차단 — 본 turn 이후에도 phase 1 e2e flow 가 이미지 무영향 보장.
    withTempDir (fun dir ->
        writeFile dir "a.txt" "본문 텍스트" |> ignore
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        use conn = SqliteStore.openConnection (SqliteStore.dbPath dir) false
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT (SELECT count(*) FROM ImageCache), (SELECT count(*) FROM ImageReferences)"
        use reader = cmd.ExecuteReader()
        Assert.True(reader.Read())
        Assert.Equal(0, reader.GetInt32 0)
        Assert.Equal(0, reader.GetInt32 1)
        // blob 디렉토리 자체도 미생성.
        Assert.False(Directory.Exists (ImageStore.blobsImagesDir dir))
        // C4 (s6-r15) — Phase 1 e2e 회귀 차단: 모든 chunks 의 ImageCount=0 (DEFAULT 0 + post-update 0).
        use sumCmd = conn.CreateCommand()
        sumCmd.CommandText <- "SELECT COALESCE(SUM(ImageCount), 0) FROM Chunks"
        Assert.Equal(0, Convert.ToInt32(sumCmd.ExecuteScalar())))

// ── Phase 2 task C4 (s6-r15): ChunkId 매핑 (Q1=1) + ImageCount post-update (Q3=X) 회귀 차단 ──

[<Fact>]
let ``ingestImagesIntoStore — image RefLocator 가 chunks 와 매칭 시 ChunkId Some (C4 Q1 옵션 1)`` () =
    // C4 결정: refToChunkId map 이 image 의 RefLocator 와 같은 chunk 의 첫 ID 를 Some 으로 채움.
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-chk" "a.pdf" Pdf 1L None None
        // chunks 인서트 — 두 RefLocator (p=14, p=99) 박제.
        let chunks : ExtractedChunk array = [|
            { OutlineIndex = None; RefLocator = "p=14"; Ordinal = 0; TokenCount = 10; Text = "page 14 본문" }
            { OutlineIndex = None; RefLocator = "p=99"; Ordinal = 0; TokenCount = 10; Text = "page 99 본문" }
        |]
        SqliteStore.insertChunks conn docId [||] chunks SqliteStore.DefaultBatchSize CancellationToken.None
        let refToChunkId = SqliteStore.lookupChunkIdsByDocument conn docId
        Assert.Equal(2, refToChunkId.Count)
        let img = {
            Bytes = samplePngBytes
            Format = Png
            Width = None
            Height = None
            RefLocator = "p=14"
            Ordinal = 1
        }
        Indexer.ingestImagesIntoStore conn dir docId refToChunkId CaptionGenerator.noop [| img |]
        // ImageReferences row 의 ChunkId 가 p=14 chunk 의 ID.
        let refs = ImageStore.lookupReferencesByDocument conn docId
        Assert.Single refs |> ignore
        let (_, _, _, refChunk) = refs.[0]
        Assert.Equal(Map.tryFind "p=14" refToChunkId, refChunk))

[<Fact>]
let ``ingestImagesIntoStore — image RefLocator 가 chunks 에 없으면 ChunkId None`` () =
    // 정합: segment text 0 인 page (chunk 미생성) 의 image 는 ChunkId None.
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-no-chk" "a.pdf" Pdf 1L None None
        // 빈 chunks (image only document 시뮬레이션).
        let refToChunkId = SqliteStore.lookupChunkIdsByDocument conn docId
        Assert.Equal(0, refToChunkId.Count)
        let img = {
            Bytes = samplePngBytes
            Format = Png
            Width = None
            Height = None
            RefLocator = "p=14"
            Ordinal = 1
        }
        Indexer.ingestImagesIntoStore conn dir docId refToChunkId CaptionGenerator.noop [| img |]
        let refs = ImageStore.lookupReferencesByDocument conn docId
        Assert.Single refs |> ignore
        let (_, _, _, refChunk) = refs.[0]
        Assert.Equal(None, refChunk))

[<Fact>]
let ``lookupChunkIdsByDocument — 한 RefLocator N chunks 분할 시 첫 chunk (MIN(Id)) 만 매핑 (C4 Q1 옵션 1)`` () =
    // Chunker token 한도 초과 분할 시뮬레이션 — 같은 RefLocator p=1 의 두 chunk (Ordinal 0, 1).
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-split" "a.pdf" Pdf 1L None None
        let chunks : ExtractedChunk array = [|
            { OutlineIndex = None; RefLocator = "p=1"; Ordinal = 0; TokenCount = 100; Text = "첫 chunk" }
            { OutlineIndex = None; RefLocator = "p=1"; Ordinal = 1; TokenCount = 100; Text = "둘째 chunk" }
        |]
        SqliteStore.insertChunks conn docId [||] chunks SqliteStore.DefaultBatchSize CancellationToken.None
        let refToChunkId = SqliteStore.lookupChunkIdsByDocument conn docId
        // 매핑 1건 (RefLocator p=1 의 첫 chunk = MIN(Id)).
        Assert.Equal(1, refToChunkId.Count)
        // MIN(Id) 가 sequential INSERT 의 첫 row Id 정합 검증.
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT MIN(Id) FROM Chunks WHERE RefLocator='p=1' AND DocumentId=$d"
        cmd.Parameters.AddWithValue("$d", docId) |> ignore
        let firstChunkId = Convert.ToInt64(cmd.ExecuteScalar())
        Assert.Equal(Some firstChunkId, Map.tryFind "p=1" refToChunkId))

[<Fact>]
let ``updateChunkImageCounts — image dispatch 후 Chunks.ImageCount post-update SUM 정합 (C4 Q3 옵션 X)`` () =
    // 의미: COUNT(*) FROM ImageReferences WHERE ChunkId = Chunks.Id. image 미참조 chunk 는 0.
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-cnt" "a.pdf" Pdf 1L None None
        let chunks : ExtractedChunk array = [|
            { OutlineIndex = None; RefLocator = "p=1"; Ordinal = 0; TokenCount = 10; Text = "p1" }
            { OutlineIndex = None; RefLocator = "p=2"; Ordinal = 0; TokenCount = 10; Text = "p2" }
        |]
        SqliteStore.insertChunks conn docId [||] chunks SqliteStore.DefaultBatchSize CancellationToken.None
        let refToChunkId = SqliteStore.lookupChunkIdsByDocument conn docId
        // p=1 에 2 image / p=2 에 1 image — Ordinal 차이로 dedup 회피.
        let mkImg refLoc ord = {
            Bytes = Array.copy samplePngBytes   // 동일 bytes (sha256 동일) — addImageReference PK 4 키 RefLocator/Ordinal 차이로 dedup 회피.
            Format = Png
            Width = None
            Height = None
            RefLocator = refLoc
            Ordinal = ord
        }
        // 같은 image (동일 sha256) 가 PK 4 키 (RefLocator/Ordinal) 만 다르면 ImageReferences 신규 row.
        Indexer.ingestImagesIntoStore conn dir docId refToChunkId CaptionGenerator.noop [|
            mkImg "p=1" 1; mkImg "p=1" 2; mkImg "p=2" 1
        |]
        SqliteStore.updateChunkImageCounts conn docId
        // p=1 chunk = ImageCount 2, p=2 chunk = ImageCount 1.
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT RefLocator, ImageCount FROM Chunks WHERE DocumentId=$d ORDER BY RefLocator"
        cmd.Parameters.AddWithValue("$d", docId) |> ignore
        use reader = cmd.ExecuteReader()
        Assert.True(reader.Read())
        Assert.Equal("p=1", reader.GetString 0)
        Assert.Equal(2, reader.GetInt32 1)
        Assert.True(reader.Read())
        Assert.Equal("p=2", reader.GetString 0)
        Assert.Equal(1, reader.GetInt32 1))

// ── Phase 2 task D (s6-r19): Indexer.ingestImagesIntoStore 의 eager caption 채움 회귀 차단 ──

/// mock captionGen 빌더 — Captioned / SkippedCaption / FailedCaption 셋 다 시뮬레이션 +
/// 호출 횟수 카운터 (cross-document dedup 검증용).
let private mkMockCaption (result: CaptionResult) =
    let count = ref 0
    let gen =
        Microsoft.FSharp.Core.FuncConvert.FromFunc<byte[], ImageFormat, CaptionResult>(
            System.Func<byte[], ImageFormat, CaptionResult>(fun _ _ ->
                count.Value <- count.Value + 1
                result))
    gen, count

[<Fact>]
let ``ingestImagesIntoStore — captionGen Captioned 반환 시 ImageCache.CaptionText/CaptionModel 박제 (D-2-2 eager)`` () =
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-cap" "a.pdf" Pdf 1L None None
        let img = {
            Bytes = Array.copy samplePngBytes
            Format = Png; Width = None; Height = None
            RefLocator = "p=1"; Ordinal = 1
        }
        let cap, count = mkMockCaption (Captioned ("도면 CV01 설명", "claude-sonnet-4-6"))
        Indexer.ingestImagesIntoStore conn dir docId Map.empty cap [| img |]
        Assert.Equal(1, count.Value)
        let hash = ImageStore.computeSha256 samplePngBytes
        match ImageStore.getCaption conn hash with
        | Some (text, model) ->
            Assert.Equal("도면 CV01 설명", text)
            Assert.Equal("claude-sonnet-4-6", model)
        | None -> Assert.Fail("getCaption 이 None — Captioned 분기 후 updateCaption 미박제"))

[<Fact>]
let ``ingestImagesIntoStore — captionGen FailedCaption 시 CaptionText NULL 유지 + 후속 image dispatch 정상 (D-2-4 fail-safe)`` () =
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-fail" "a.pdf" Pdf 1L None None
        let img1 = {
            Bytes = Array.copy samplePngBytes
            Format = Png; Width = None; Height = None
            RefLocator = "p=1"; Ordinal = 1
        }
        let img2 = {
            Bytes = [| for b in samplePngBytes -> b ^^^ 0xFFuy |]   // 다른 sha256
            Format = Png; Width = None; Height = None
            RefLocator = "p=2"; Ordinal = 1
        }
        let cap, count = mkMockCaption (FailedCaption "HTTP 500")
        Indexer.ingestImagesIntoStore conn dir docId Map.empty cap [| img1; img2 |]
        // captionGen 은 두 image 모두에 호출 (per-image fail-safe — 후속 차단 안 함).
        Assert.Equal(2, count.Value)
        // 두 image 모두 ImageCache row 박제 (caption 만 NULL).
        let hash1 = ImageStore.computeSha256 img1.Bytes
        let hash2 = ImageStore.computeSha256 img2.Bytes
        Assert.True((ImageStore.getImageCache conn hash1).IsSome)
        Assert.True((ImageStore.getImageCache conn hash2).IsSome)
        // CaptionText NULL 유지 — getCaption None.
        Assert.True((ImageStore.getCaption conn hash1).IsNone)
        Assert.True((ImageStore.getCaption conn hash2).IsNone))

[<Fact>]
let ``ingestImagesIntoStore — 같은 hash 가 두 document 에 dispatch 시 captionGen 1회만 호출 (cross-doc dedup)`` () =
    // D-2-2 정합 — getCaption pre-check 가 cross-document dedup 가드. 재색인 idempotent 정합.
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docA = SqliteStore.insertDocument conn "H-A" "a.pdf" Pdf 1L None None
        let docB = SqliteStore.insertDocument conn "H-B" "b.pdf" Pdf 1L None None
        let img = {
            Bytes = Array.copy samplePngBytes
            Format = Png; Width = None; Height = None
            RefLocator = "p=1"; Ordinal = 1
        }
        let cap, count = mkMockCaption (Captioned ("공유 도면", "claude-sonnet-4-6"))
        Indexer.ingestImagesIntoStore conn dir docA Map.empty cap [| img |]
        Indexer.ingestImagesIntoStore conn dir docB Map.empty cap [| img |]
        // captionGen 은 단 1회만 호출 (두 번째 호출은 getCaption Some 분기로 skip).
        Assert.Equal(1, count.Value))


// ── Phase 4 (s6-r34) — Indexer embedder dispatch ──

/// fixed-vector mock IEmbeddingProvider — 모든 input 에 같은 vector 반환.
/// dim = SqliteStore.EmbeddingDimension (1024). stateless 라 Dispose no-op (s6-r36 P4-C.0 IDisposable contract 정합).
type private MockEmbedder(callCount: ref<int>) =
    interface IEmbeddingProvider with
        member _.Dimension = SqliteStore.EmbeddingDimension
        member _.GenerateAsync(inputs, _ct) =
            callCount.Value <- callCount.Value + 1
            let dim = SqliteStore.EmbeddingDimension
            let vectors =
                inputs |> Array.map (fun s ->
                    // 단순 deterministic 박제 — 문자열 hash 기반 1차 element + 나머지 0.
                    let h = float32 (s.GetHashCode() % 100) * 0.01f
                    Array.init dim (fun i -> if i = 0 then h else 0.0f))
            System.Threading.Tasks.Task.FromResult(vectors)
    interface System.IDisposable with
        member _.Dispose() = ()

let private countVectors (dir: string) : int =
    let conn = SqliteStore.openConnection (SqliteStore.dbPath dir) true
    try
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM Chunks_Vectors"
        Convert.ToInt32 (cmd.ExecuteScalar())
    finally
        conn.Close()
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn
        conn.Dispose()

[<Fact>]
let ``Phase 4 — embedder=None 시 Chunks_Vectors 빈 row (BM25 fallback path)`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "hello world. another sentence." |> ignore
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop None noProgress CancellationToken.None
        Assert.Equal(0, countVectors dir))

[<Fact>]
let ``Phase 4 — embedder=Some 시 모든 chunk 의 embedding INSERT`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "hello world. second sentence. third one." |> ignore
        let cc = ref 0
        let mock = MockEmbedder(cc) :> IEmbeddingProvider
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop (Some mock) noProgress CancellationToken.None
        // chunks 수 (chunker 정합) 와 Chunks_Vectors row 수 일치 — 정확 chunks 수 = chunker 박제이라 >= 1 만 검증.
        let vCount = countVectors dir
        Assert.True(vCount >= 1, sprintf "Chunks_Vectors row 수=%d (expected >= 1)" vCount)
        // embedder.GenerateAsync 가 1회 이상 호출 (file 별 1회).
        Assert.True(cc.Value >= 1, sprintf "embedder call count=%d (expected >= 1)" cc.Value))

[<Fact>]
let ``Phase 4 — embedder=Some 빈 collection 시 GenerateAsync 호출 0`` () =
    withTempDir (fun dir ->
        let cc = ref 0
        let mock = MockEmbedder(cc) :> IEmbeddingProvider
        let _ = Indexer.ingest dir (extractors()) CaptionGenerator.noop (Some mock) noProgress CancellationToken.None
        Assert.Equal(0, cc.Value))


// ────────────────────────────────────────────────────────────────────────────────
//  Plan 2 — icon min size 가드 (8 KB 미만 image skip)
// ────────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``ingestImagesIntoStore — icon size skip (Plan 2) — 8 KB 미만 image 는 ImageReferences 미박제`` () =
    // SamplePng.bytes (~67 bytes) 직접 박제 → MinImageBytesForIndex (8 KB) 미만 skip.
    // blob 파일도 생성 안 함 (saveBlob 미호출). 산업 .xlsx / pptx 의 logo / icon 색인 noise 차단.
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-icon" "icon-doc.pdf" Pdf 1L None None
        let iconBytes : byte[] = Ds2.LightHouse.Tests.SamplePng.bytes   // ~67 bytes < 8 KB
        let img = {
            Bytes = iconBytes
            Format = Png
            Width = Some 1
            Height = Some 1
            RefLocator = "p=1"
            Ordinal = 1
        }
        Indexer.ingestImagesIntoStore conn dir docId Map.empty CaptionGenerator.noop [| img |]
        // ImageReferences 미박제.
        Assert.Empty(ImageStore.lookupReferencesByDocument conn docId)
        // blob 디렉토리 자체도 생성 안 됨 (saveBlob 미호출).
        Assert.False(Directory.Exists (ImageStore.blobsImagesDir dir)))

[<Fact>]
let ``ingestImagesIntoStore — icon + 본문 image 혼합 (Plan 2) — icon skip + 본문만 박제`` () =
    // 단일 document 안 icon (small) + 본문 image (large) 혼합 → 본문만 ImageReferences 박제, icon 자리 미박제.
    withTempDir (fun dir ->
        use conn = openFreshAt dir
        let docId = SqliteStore.insertDocument conn "H-mix" "mixed.pdf" Pdf 1L None None
        let iconBytes : byte[] = Ds2.LightHouse.Tests.SamplePng.bytes
        let bigBytes : byte[] =
            Array.append Ds2.LightHouse.Tests.SamplePng.bytes (Array.zeroCreate 8192)
        let images = [|
            { Bytes = iconBytes; Format = Png; Width = Some 1; Height = Some 1; RefLocator = "p=1"; Ordinal = 1 }
            { Bytes = bigBytes; Format = Png; Width = None; Height = None; RefLocator = "p=2"; Ordinal = 1 }
        |]
        Indexer.ingestImagesIntoStore conn dir docId Map.empty CaptionGenerator.noop images
        // 본문 image (p=2) 만 박제, icon (p=1) 은 skip.
        let refs = ImageStore.lookupReferencesByDocument conn docId
        Assert.Single refs |> ignore
        let (_, refLoc, _, _) = refs.[0]
        Assert.Equal("p=2", refLoc))
