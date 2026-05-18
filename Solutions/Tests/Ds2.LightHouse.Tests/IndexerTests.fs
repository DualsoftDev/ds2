module Ds2.LightHouse.Tests.IndexerTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

do Ds2.LightHouse.Tests.TestInit.registered |> ignore

/// todo-lighthouse-kb-index.md §4.8b — Indexer 전체 흐름 + 0-doc / 0-byte / idempotent / IndexerVersion bump.

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
]

let private noProgress (_: IngestProgress) = ()

[<Fact>]
let ``0-doc collection — 빈 폴더 정상 ingest (index.db 생성 + Documents 0)`` () =
    withTempDir (fun dir ->
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        Assert.Empty(results)
        Assert.True(File.Exists (SqliteStore.dbPath dir)))

[<Fact>]
let ``기본 흐름 — txt/md 파일 색인`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "첫 문서 본문" |> ignore
        writeFile dir "b.md" "# 헤더\n\n본문" |> ignore
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        Assert.Equal(2, results.Length)
        for (_, r) in results do
            match r with
            | Ingested _ -> ()
            | other -> Assert.Fail(sprintf "기대 = Ingested, 실제 = %A" other))

[<Fact>]
let ``FileHash idempotent — 같은 파일 두 번 ingest → Documents 1개`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "본문" |> ignore
        let _ = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        let results2 = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        Assert.Single(results2) |> ignore
        match snd results2.[0] with
        | Skipped reason -> Assert.Contains("already ingested", reason)
        | other -> Assert.Fail(sprintf "재 ingest 는 Skipped 기대, 실제 = %A" other))

[<Fact>]
let ``미지원 ext (.dwg) — Skipped`` () =
    withTempDir (fun dir ->
        let path = writeFile dir "design.dwg" "binary-ish"
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        let pair = results |> Array.find (fun (p, _) -> p = path)
        match snd pair with
        | Skipped reason -> Assert.Contains("unsupported ext", reason)
        | other -> Assert.Fail(sprintf "기대 = Skipped, 실제 = %A" other))

[<Fact>]
let ``rejected ext (.env) — Skipped`` () =
    withTempDir (fun dir ->
        let path = writeFile dir "secrets.env" "API_KEY=xxx"
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        let pair = results |> Array.find (fun (p, _) -> p = path)
        match snd pair with
        | Skipped reason -> Assert.Contains("rejected ext", reason)
        | other -> Assert.Fail(sprintf "기대 = Skipped, 실제 = %A" other))

[<Fact>]
let ``0-byte 파일 — extractor 가 빈 결과로 처리 (Ingested with 0 segments)`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "empty.txt")
        File.WriteAllBytes(path, [||])
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        Assert.Single(results) |> ignore
        match snd results.[0] with
        | Ingested _ -> ()
        | other -> Assert.Fail(sprintf "기대 = Ingested, 실제 = %A" other))

[<Fact>]
let ``IndexerVersion drift → shadow rebuild 발생`` () =
    withTempDir (fun dir ->
        writeFile dir "a.txt" "본문" |> ignore
        let _ = Indexer.ingest dir (extractors()) noProgress CancellationToken.None

        // drift 유도
        let dbPath = SqliteStore.dbPath dir
        (
            use conn = SqliteStore.openConnection dbPath false
            SqliteStore.setMeta conn "indexer_version" "0.0.0"
        )

        // 재 ingest → shadow rebuild → indexer_version 이 Current 로 복귀
        let _ = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        use conn = SqliteStore.openConnection dbPath false
        Assert.Equal(Some IndexerVersion.Current, SqliteStore.getMeta conn "indexer_version"))

[<Fact>]
let ``.lighthouse-kb 폴더 자체는 색인 대상에서 제외`` () =
    withTempDir (fun dir ->
        // 첫 ingest 후 .lighthouse-kb/index.db 가 생성됨
        writeFile dir "a.txt" "본문" |> ignore
        let _ = Indexer.ingest dir (extractors()) noProgress CancellationToken.None

        // .lighthouse-kb 안에 가짜 txt 추가 — 재 ingest 시 enumerate 에서 제외 확인
        let kbDir = SqliteStore.kbDir dir
        let bogus = Path.Combine(kbDir, "inside.txt")
        File.WriteAllText(bogus, "should not be ingested", Encoding.UTF8)
        let results = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        // 새 파일이 .lighthouse-kb 안이라 enumerate 단계에서 제외 — results 에 inside.txt 없음
        let touched = results |> Array.exists (fun (p, _) -> p = bogus)
        Assert.False(touched, "inside .lighthouse-kb/ 파일은 enumerate 에서 제외되어야 함"))

// ── Phase 2 task C1 (s6-r12): Indexer.ingestImagesIntoStore 회귀 차단 ──
// 본 단원 = Indexer 가 ExtractedDocument.Images 를 받아 ImageStore 로 dispatch 하는 helper 의 unit-level fact.
// 실 extractor 의 image 추출 (Phase 2 task C2 PdfExtractor / C3 OoxmlExtractor) 은 별 turn — 본 fact 는
// synthetic ExtractedImage array 로 dispatch path 만 검증.

// 1×1 px PNG (ImageStoreTests 와 동일 bytes — sha256 결정성 회귀 차단 의도).
let private samplePngBytes : byte[] =
    [|
        0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy
        0x00uy; 0x00uy; 0x00uy; 0x0Duy; 0x49uy; 0x48uy; 0x44uy; 0x52uy
        0x00uy; 0x00uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy; 0x00uy; 0x01uy
        0x08uy; 0x06uy; 0x00uy; 0x00uy; 0x00uy
        0x1Fuy; 0x15uy; 0xC4uy; 0x89uy
        0x00uy; 0x00uy; 0x00uy; 0x0Auy; 0x49uy; 0x44uy; 0x41uy; 0x54uy
        0x78uy; 0x9Cuy; 0x63uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy; 0x05uy; 0x00uy; 0x01uy
        0x0Duy; 0x0Auy; 0x2Duy; 0xB4uy
        0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x49uy; 0x45uy; 0x4Euy; 0x44uy
        0xAEuy; 0x42uy; 0x60uy; 0x82uy
    |]

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
        Indexer.ingestImagesIntoStore conn dir docId [||]
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
            RefLocator = "p=14#img=2"
            Ordinal = 0
        }
        Indexer.ingestImagesIntoStore conn dir docId [| img |]
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
        Assert.Equal("p=14#img=2", refLoc)
        Assert.Equal(0, refOrd)
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
            Ordinal = 0
        }
        Indexer.ingestImagesIntoStore conn dir docA [| imgFor "p=1#img=1" |]
        Indexer.ingestImagesIntoStore conn dir docB [| imgFor "p=5#img=3" |]
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
            RefLocator = "p=1#img=1"
            Ordinal = 0
        }
        Indexer.ingestImagesIntoStore conn dir docId [| img; img; img |]
        Assert.Equal(1, (ImageStore.lookupReferencesByDocument conn docId).Length))

[<Fact>]
let ``Indexer.ingest e2e — Phase 1 extractor (Images=[||]) 경로는 ImageCache 0 + ImageReferences 0 박제`` () =
    // Phase 1 extractor 의 default Images=[||] 회귀 차단 — 본 turn 이후에도 phase 1 e2e flow 가 이미지 무영향 보장.
    withTempDir (fun dir ->
        writeFile dir "a.txt" "본문 텍스트" |> ignore
        let _ = Indexer.ingest dir (extractors()) noProgress CancellationToken.None
        use conn = SqliteStore.openConnection (SqliteStore.dbPath dir) false
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT (SELECT count(*) FROM ImageCache), (SELECT count(*) FROM ImageReferences)"
        use reader = cmd.ExecuteReader()
        Assert.True(reader.Read())
        Assert.Equal(0, reader.GetInt32 0)
        Assert.Equal(0, reader.GetInt32 1)
        // blob 디렉토리 자체도 미생성.
        Assert.False(Directory.Exists (ImageStore.blobsImagesDir dir)))
