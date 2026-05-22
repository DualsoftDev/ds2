module Ds2.LightHouse.Tests.ImageStoreTests

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open Ds2.LightHouse

/// Phase 2 task B (s6-r11) — ImageStore.fs 회귀 차단.
///
/// 검증 범위 (parent §3.15.5 MR1/MR2 + §3.12 schema 정합):
/// - sha256 결정성 (같은 bytes → 같은 hex, 64 char lowercase)
/// - blobFilePath SSOT (`<root>/.lighthouse-kb/blobs/images/<hash>.<ext>`)
/// - saveBlob idempotent (이미 존재 시 skip)
/// - upsertImageCache + getImageCache round-trip + INSERT OR IGNORE
/// - addImageReference 복합 PK (4 키) 중복 시 INSERT OR IGNORE
/// - cross-document 공유 — 같은 image 가 두 document 의 ImageReferences 에 박제

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-imgstore-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try action dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

let private openFresh (dir: string) : SqliteConnection =
    let dbPath = SqliteStore.dbPath dir
    let conn = SqliteStore.openConnection dbPath false
    SqliteStore.ensureSchema conn
    SqliteStore.stampVersion conn
    conn

// 1×1 px PNG (8-byte signature + minimal IHDR/IDAT/IEND). 회귀 차단용 deterministic bytes.
let private pngBytes : byte[] =
    [|
        0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy   // signature
        0x00uy; 0x00uy; 0x00uy; 0x0Duy; 0x49uy; 0x48uy; 0x44uy; 0x52uy   // IHDR length + tag
        0x00uy; 0x00uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy; 0x00uy; 0x01uy   // width=1 / height=1
        0x08uy; 0x06uy; 0x00uy; 0x00uy; 0x00uy
        0x1Fuy; 0x15uy; 0xC4uy; 0x89uy                                  // IHDR CRC
        0x00uy; 0x00uy; 0x00uy; 0x0Auy; 0x49uy; 0x44uy; 0x41uy; 0x54uy   // IDAT
        0x78uy; 0x9Cuy; 0x63uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy; 0x05uy; 0x00uy; 0x01uy
        0x0Duy; 0x0Auy; 0x2Duy; 0xB4uy
        0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x49uy; 0x45uy; 0x4Euy; 0x44uy   // IEND
        0xAEuy; 0x42uy; 0x60uy; 0x82uy
    |]

[<Fact>]
let ``computeSha256 — 결정성 + 64 hex lowercase`` () =
    let h1 = ImageStore.computeSha256 pngBytes
    let h2 = ImageStore.computeSha256 pngBytes
    Assert.Equal(h1, h2)
    Assert.Equal(64, h1.Length)
    // lowercase hex 강제 — ZipImport blob regex `^[0-9a-f]{64}\.(png|jpg|...)$` 정합 (s6-r9 §3.3 mn11 SSOT).
    Assert.Matches(@"^[0-9a-f]{64}$", h1)
    // 입력 다르면 다른 hash.
    let other = ImageStore.computeSha256 [| 0uy; 1uy; 2uy |]
    Assert.NotEqual<string>(h1, other)

[<Fact>]
let ``blobFilePath — SSOT path 형식 .lighthouse-kb/blobs/images/<hash>.<ext>`` () =
    withTempDir (fun root ->
        let hash = ImageStore.computeSha256 pngBytes
        let p = ImageStore.blobFilePath root hash Png
        // ZipImport blob regex 정합 — 마지막 segment 만 검증 (디렉토리 분리는 OS 의존).
        Assert.EndsWith(sprintf "%s.png" hash, p)
        Assert.Contains(".lighthouse-kb", p)
        Assert.Contains("blobs", p)
        Assert.Contains("images", p)
        // ImageFormat 별 확장자 SSOT — JPEG → "jpg" (zip blob regex 의 "jpeg" alias 와 정합 = caller 가 결정).
        Assert.Equal("jpg",  ImageStore.extOf Jpeg)
        Assert.Equal("png",  ImageStore.extOf Png)
        Assert.Equal("gif",  ImageStore.extOf Gif)
        Assert.Equal("webp", ImageStore.extOf Webp)
        // MIME SSOT — Phase 2 task D (attachment_read includeImages) 의 content block MIME 정합.
        Assert.Equal("image/png",  ImageStore.mimeOf Png)
        Assert.Equal("image/jpeg", ImageStore.mimeOf Jpeg))

[<Fact>]
let ``saveBlob — 신규 파일 작성 + idempotent (이미 존재 시 skip, mtime 변동 0)`` () =
    withTempDir (fun root ->
        let hash = ImageStore.computeSha256 pngBytes
        let path1 = ImageStore.saveBlob root hash Png pngBytes
        Assert.True(File.Exists path1)
        Assert.Equal(pngBytes.Length, (FileInfo path1).Length |> int)
        // idempotent — 두 번째 호출이 동일 path 반환 + 파일 mtime 유지 (skip 박제).
        let mtime1 = (FileInfo path1).LastWriteTimeUtc
        Thread.Sleep 50   // mtime 분해능 보장 — 회귀 시 (재작성) mtime 갱신 검출.
        let path2 = ImageStore.saveBlob root hash Png pngBytes
        Assert.Equal(path1, path2)
        let mtime2 = (FileInfo path2).LastWriteTimeUtc
        Assert.Equal(mtime1, mtime2))

[<Fact>]
let ``upsertImageCache + getImageCache — round-trip + INSERT OR IGNORE (caption 보존)`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        let storedPath = ImageStore.blobFilePath dir hash Png
        ImageStore.upsertImageCache conn hash storedPath Png (Some 1) (Some 1)
        match ImageStore.getImageCache conn hash with
        | None -> Assert.Fail "ImageCache row 미존재"
        | Some (p, mime, w, h) ->
            Assert.Equal(storedPath, p)
            Assert.Equal("image/png", mime)
            Assert.Equal(Some 1, w)
            Assert.Equal(Some 1, h)
        // 같은 hash 두 번째 upsert — caption 외 metadata 도 보존 (INSERT OR IGNORE).
        // 사전 caption manual update → 두 번째 upsert 가 NULL 로 덮어쓰면 회귀.
        use update = conn.CreateCommand()
        update.CommandText <- "UPDATE ImageCache SET CaptionText='manual' WHERE ImageHash=$h"
        update.Parameters.AddWithValue("$h", hash) |> ignore
        update.ExecuteNonQuery() |> ignore
        ImageStore.upsertImageCache conn hash storedPath Png (Some 99) (Some 99)
        use sel = conn.CreateCommand()
        sel.CommandText <- "SELECT CaptionText, Width FROM ImageCache WHERE ImageHash=$h"
        sel.Parameters.AddWithValue("$h", hash) |> ignore
        use reader = sel.ExecuteReader()
        Assert.True(reader.Read())
        Assert.Equal("manual", reader.GetString 0)
        // Width 도 (Some 99) 로 갱신되지 않고 기존 1 보존 — ON CONFLICT DO NOTHING SSOT.
        Assert.Equal(1, reader.GetInt32 1))

[<Fact>]
let ``addImageReference — 복합 PK 4 키 중복 시 INSERT OR IGNORE (PK count 정합)`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "irrelevant" Png None None
        let docId = SqliteStore.insertDocument conn "HASH-DOC1" "a.pdf" Pdf 1024L (Some 5) None
        // 같은 (doc, hash, ref, ord) 4건 호출 — 1건만 박제.
        for _ in 1..4 do
            ImageStore.addImageReference conn docId None hash "p=14" 1
        // ChunkId 만 다른 호출 — PK 4 키 중 ChunkId 없음 → 여전히 1건 (idempotent 박제 SSOT).
        ImageStore.addImageReference conn docId (Some 99L) hash "p=14" 1
        // Ordinal 만 다르면 별 행 — PK 4 키 중 Ordinal 다르므로 신규 row.
        ImageStore.addImageReference conn docId None hash "p=14" 2
        let refs = ImageStore.lookupReferencesByDocument conn docId
        Assert.Equal(2, refs.Length)
        // ordinal asc 정렬 검증 — lookupReferencesByDocument SSOT.
        Assert.Equal(1, refs.[0] |> fun (_, _, o, _) -> o)
        Assert.Equal(2, refs.[1] |> fun (_, _, o, _) -> o))

[<Fact>]
let ``addImageReference — cross-document 공유 (같은 image 가 두 document 의 refs 에 박제)`` () =
    // parent §3.15.5 MR2 dedup 의 본질 — per-collection 안 같은 image 가 N document 에서 사용되면
    // ImageCache 1행 + ImageReferences N행.
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "x" Png None None
        let docA = SqliteStore.insertDocument conn "HASH-A" "a.pdf" Pdf 100L None None
        let docB = SqliteStore.insertDocument conn "HASH-B" "b.pdf" Pdf 200L None None
        ImageStore.addImageReference conn docA None hash "p=1" 1
        ImageStore.addImageReference conn docB None hash "p=5" 1
        // ImageCache 행은 그대로 1개 (per-collection dedup SSOT).
        use count = conn.CreateCommand()
        count.CommandText <- "SELECT count(*) FROM ImageCache WHERE ImageHash = $h"
        count.Parameters.AddWithValue("$h", hash) |> ignore
        Assert.Equal(1, Convert.ToInt32(count.ExecuteScalar()))
        // 각 document 별로 1개씩 ImageReferences.
        Assert.Equal(1, (ImageStore.lookupReferencesByDocument conn docA).Length)
        Assert.Equal(1, (ImageStore.lookupReferencesByDocument conn docB).Length))

[<Fact>]
let ``addImageReference — FK 위반 차단 (ImageCache 미존재 hash)`` () =
    // parent §3.12 schema 의 ImageReferences.ImageHash REFERENCES ImageCache(ImageHash) FK 정합.
    // 본 Fact = PRAGMA foreign_keys=ON SSOT (SqliteStore.applyPragmas) 회귀 차단.
    withTempDir (fun dir ->
        use conn = openFresh dir
        let docId = SqliteStore.insertDocument conn "HASH-X" "x.pdf" Pdf 10L None None
        // ImageCache upsert 누락 — 직접 addImageReference 호출 시 FK 위반으로 SqliteException.
        Assert.Throws<SqliteException>(fun () ->
            ImageStore.addImageReference conn docId None "deadbeef" "p=1" 1) |> ignore)

[<Fact>]
let ``getImageCache — 미존재 hash 조회 시 None (자가 검열 m3-c)`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        Assert.Equal(None, ImageStore.getImageCache conn "nonexistent-hash-deadbeef"))

[<Fact>]
let ``lookupReferencesByDocument — 미존재 documentId 시 빈 배열 (자가 검열 m3-b)`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        Assert.Empty (ImageStore.lookupReferencesByDocument conn 9999L))

[<Theory>]
[<InlineData("Png")>]
[<InlineData("Jpeg")>]
[<InlineData("Gif")>]
[<InlineData("Webp")>]
let ``ImageStore.mimeOf ≡ Attachment.mimeOf — 4 case SSOT cross-drift 잠금 (자가 검열 M1)`` (caseName: string) =
    // **자가 검열 M1 적용 (s6-r11)** — `ImageStore.fs` 주석에 "Ds2.LlmAgent.Attachment.mimeOf 와 의도적 mirror" 라
    // 박제했으나 cross-drift Fact 가 없으면 한쪽이 drift 해도 검출 안 됨. 본 Theory 가 4 case 전부 양쪽 일치 강제.
    let fmt =
        match caseName with
        | "Png"  -> Png
        | "Jpeg" -> Jpeg
        | "Gif"  -> Gif
        | "Webp" -> Webp
        | _      -> failwithf "unknown case %s" caseName
    Assert.Equal(Ds2.LlmAgent.Attachment.mimeOf fmt, ImageStore.mimeOf fmt)

// ── Phase 2 task D (s6-r19): caption getCaption / updateCaption / CaptionGenerator.noop 회귀 차단 ──

[<Fact>]
let ``getCaption — caption 미존재 시 None (upsertImageCache 직후)`` () =
    // D-2-2 eager 의 dedup 가드 SSOT — getCaption 이 None 반환해야 captionGen 호출 분기 진입.
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "x" Png None None
        // upsertImageCache 만으로는 CaptionText / CaptionModel 모두 NULL 박제 — getCaption = None.
        Assert.True((ImageStore.getCaption conn hash).IsNone))

[<Fact>]
let ``getCaption + updateCaption round-trip — caption 갱신 후 정확히 반환`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "x" Png None None
        ImageStore.updateCaption conn hash "압력 센서 CV01 도면" "claude-sonnet-4-6"
        match ImageStore.getCaption conn hash with
        | Some (text, model) ->
            Assert.Equal("압력 센서 CV01 도면", text)
            Assert.Equal("claude-sonnet-4-6", model)
        | None -> Assert.Fail("getCaption 이 None 반환 — updateCaption 후에도 caption 미박제")
        // CaptionAt 도 채워졌는지 (raw SQL 검증).
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT CaptionAt FROM ImageCache WHERE ImageHash=$h"
        cmd.Parameters.AddWithValue("$h", hash) |> ignore
        let at = cmd.ExecuteScalar() :?> string
        Assert.False(String.IsNullOrWhiteSpace at, "CaptionAt NULL — updateCaption 의 ISO timestamp 누락"))

[<Fact>]
let ``updateCaption — 두 번 호출 시 overwrite (latest model 박제)`` () =
    // MR3 invalidation 정합 — model tier 변경 시 caption 재생성 + cache 덮어쓰기.
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "x" Png None None
        ImageStore.updateCaption conn hash "sonnet 캡션" "claude-sonnet-4-6"
        ImageStore.updateCaption conn hash "opus 캡션" "claude-opus-4-7"
        match ImageStore.getCaption conn hash with
        | Some (text, model) ->
            Assert.Equal("opus 캡션", text)
            Assert.Equal("claude-opus-4-7", model)
        | None -> Assert.Fail("getCaption None"))

[<Fact>]
let ``CaptionGenerator.noop — 항상 SkippedCaption 반환 (Phase 1 회귀 0 + 비활성 환경)`` () =
    // D-2-2 의 noop default 정합 — caption 미사용 caller (lib tests / 무인 cli) 가 박제 의무.
    let r1 = CaptionGenerator.noop pngBytes Png
    let r2 = CaptionGenerator.noop [| 1uy; 2uy; 3uy |] Jpeg
    match r1 with
    | SkippedCaption reason -> Assert.Equal("no caption gen", reason)
    | _ -> Assert.Fail(sprintf "noop 이 SkippedCaption 외 반환 — %A" r1)
    match r2 with
    | SkippedCaption _ -> ()
    | _ -> Assert.Fail(sprintf "noop 이 SkippedCaption 외 반환 — %A" r2)

[<Fact>]
let ``CaptionGenerator.MaxImageBytes — s6-r20 정정 후 ~3.75MB (base64 팽창 후 ~5MB body) 정합`` () =
    // m-r19-1 정정 박제 회귀 차단 — 원본 bytes 한도 = 5MB * 3/4 = 3,932,160.
    Assert.Equal(3932160, CaptionGenerator.MaxImageBytes)
    // 정확히 5MB body 와의 대응 — base64(N) = ceil(N/3)*4. N=3932160 → base64 = 5242880 = 5MB.

[<Fact>]
let ``ImageReferences ON DELETE CASCADE — Documents 삭제 시 refs 도 자동 삭제`` () =
    // parent §3.12 결함 5항 4 (ON DELETE: Documents → CASCADE). ImageCache 는 보존 (cross-document dedup SSOT).
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "x" Png None None
        let docId = SqliteStore.insertDocument conn "HASH-DEL" "d.pdf" Pdf 10L None None
        ImageStore.addImageReference conn docId None hash "p=1" 1
        Assert.Equal(1, (ImageStore.lookupReferencesByDocument conn docId).Length)
        // Document 삭제 → ImageReferences 자동 CASCADE.
        SqliteStore.deleteDocument conn docId
        Assert.Equal(0, (ImageStore.lookupReferencesByDocument conn docId).Length)
        // ImageCache 는 그대로 (cross-document 공유 의도).
        Assert.True((ImageStore.getImageCache conn hash).IsSome))

// ── PR-Img-Chunk (caption-as-chunk r0) — image caption 을 Chunks row 로 박제 ──

/// caption-chunk Chunks row count for given DocumentId — Ordinal 무관, RefLocator 매칭으로 enumerate.
let private countCaptionChunks (conn: SqliteConnection) (docId: int64) : int =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT COUNT(*) FROM Chunks
        WHERE DocumentId = $d AND Text LIKE '[그림 %' ESCAPE '\'
    """
    cmd.Parameters.AddWithValue("$d", docId) |> ignore
    Convert.ToInt32 (cmd.ExecuteScalar())

[<Fact>]
let ``PR-Img-Chunk — formatCaptionChunkText SSOT 형식 검증`` () =
    let text = ImageStore.formatCaptionChunkText "p=5" 3 "abcdef0123456789ffffffff" "컨트롤러 결선도"
    Assert.Equal("[그림 p.5 #3 hash=abcdef012345] 컨트롤러 결선도", text)
    // slide / sheet refLocator 변환 검증.
    let textSlide = ImageStore.formatCaptionChunkText "slide=2" 1 "0123456789abcdef" "주제 슬라이드"
    Assert.Equal("[그림 slide 2 #1 hash=0123456789ab] 주제 슬라이드", textSlide)
    // hash 길이 12 미만 — 그대로 사용 (방어 코드 정합).
    let textShort = ImageStore.formatCaptionChunkText "p=1" 0 "abcd" "test"
    Assert.Equal("[그림 p.1 #0 hash=abcd] test", textShort)

[<Fact>]
let ``PR-Img-Chunk — upsertCaptionChunkForRef INSERT path: Chunks row + ImageReferences.CaptionChunkId 박제`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "blob.png" Png None None
        let docId = SqliteStore.insertDocument conn "H-1" "d.pdf" Pdf 100L None None
        ImageStore.addImageReference conn docId None hash "p=5" 1
        // caption-chunk 박제 전 — Chunks 비어있음, CaptionChunkId NULL.
        Assert.Equal(0, countCaptionChunks conn docId)
        Assert.Equal(None, SqliteStore.lookupCaptionChunkByImageRef conn docId hash "p=5" 1)
        // INSERT.
        ImageStore.upsertCaptionChunkForRef conn docId hash "p=5" 1 "컨트롤러 결선도 그림"
        Assert.Equal(1, countCaptionChunks conn docId)
        match SqliteStore.lookupCaptionChunkByImageRef conn docId hash "p=5" 1 with
        | None -> Assert.Fail "CaptionChunkId 미박제 — setImageRefCaptionChunkId 누락 회귀"
        | Some cid ->
            // 박제된 chunk Text = formatCaptionChunkText 정합.
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT RefLocator, Text FROM Chunks WHERE Id = $id"
            cmd.Parameters.AddWithValue("$id", cid) |> ignore
            use reader = cmd.ExecuteReader()
            Assert.True(reader.Read())
            Assert.Equal("p=5", reader.GetString 0)
            Assert.Contains("컨트롤러 결선도 그림", reader.GetString 1)
            Assert.Contains("[그림 p.5 #1 hash=", reader.GetString 1))

[<Fact>]
let ``PR-Img-Chunk — upsertCaptionChunkForRef UPDATE path: 재호출 시 Chunks row 증가 0 + Text 갱신`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "blob.png" Png None None
        let docId = SqliteStore.insertDocument conn "H-UPD" "d.pdf" Pdf 100L None None
        ImageStore.addImageReference conn docId None hash "p=5" 1
        ImageStore.upsertCaptionChunkForRef conn docId hash "p=5" 1 "first caption"
        let chunkIdAfter1st = SqliteStore.lookupCaptionChunkByImageRef conn docId hash "p=5" 1
        Assert.Equal(1, countCaptionChunks conn docId)
        // 동일 ref 재호출 — Chunks INSERT 안 함, Text 갱신.
        ImageStore.upsertCaptionChunkForRef conn docId hash "p=5" 1 "second caption updated"
        Assert.Equal(1, countCaptionChunks conn docId)
        // chunkId 동일 (lookup 결과 보존, 신 INSERT 회귀 차단).
        let chunkIdAfter2nd = SqliteStore.lookupCaptionChunkByImageRef conn docId hash "p=5" 1
        Assert.Equal(chunkIdAfter1st, chunkIdAfter2nd)
        // Text 갱신 검증.
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT Text FROM Chunks WHERE Id = $id"
        cmd.Parameters.AddWithValue("$id", chunkIdAfter2nd.Value) |> ignore
        let text = cmd.ExecuteScalar() :?> string
        Assert.Contains("second caption updated", text)
        Assert.DoesNotContain("first caption", text))

[<Fact>]
let ``PR-Img-Chunk — updateCaptionBatch cross-doc: 한 hash 의 모든 ImageReferences 에 caption-chunk 박제`` () =
    // 한 image (= hash) 가 doc1 (p=5) + doc2 (p=10) 에서 참조 — caption-chunk row 가 doc 별로 별개 박제 (1:1 매핑).
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "blob.png" Png None None
        let doc1 = SqliteStore.insertDocument conn "H-D1" "a.pdf" Pdf 100L None None
        let doc2 = SqliteStore.insertDocument conn "H-D2" "b.pdf" Pdf 100L None None
        ImageStore.addImageReference conn doc1 None hash "p=5"  1
        ImageStore.addImageReference conn doc2 None hash "p=10" 2
        // updateCaptionBatch — 한 row (hash, text, model) 만 입력 → 두 ImageReferences 모두 caption-chunk 박제.
        let updated = ImageStore.updateCaptionBatch conn (Seq.singleton (hash, "전기 패널 구성도", "claude-test"))
        Assert.Equal(1, updated)  // ImageCache UPDATE 는 hash 1 row.
        // 두 doc 각각 caption-chunk 1 row 박제.
        Assert.Equal(1, countCaptionChunks conn doc1)
        Assert.Equal(1, countCaptionChunks conn doc2)
        Assert.True(Option.isSome (SqliteStore.lookupCaptionChunkByImageRef conn doc1 hash "p=5"  1))
        Assert.True(Option.isSome (SqliteStore.lookupCaptionChunkByImageRef conn doc2 hash "p=10" 2)))

[<Fact>]
let ``PR-Img-Chunk — caption-chunk INSERT 시 ChunksFts AI trigger 자동 sync (BM25 hit)`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "blob.png" Png None None
        let docId = SqliteStore.insertDocument conn "H-FTS" "d.pdf" Pdf 100L None None
        ImageStore.addImageReference conn docId None hash "p=5" 1
        ImageStore.upsertCaptionChunkForRef conn docId hash "p=5" 1 "컨트롤러 결선도 박제 검증"
        // ChunksFts BM25 query — "결선도" 키워드로 caption-chunk hit.
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT COUNT(*) FROM ChunksFts
            WHERE ChunksFts MATCH '결선도'
        """
        let count = Convert.ToInt32 (cmd.ExecuteScalar())
        Assert.True(count >= 1, "ChunksFts AI trigger 가 caption-chunk Text 를 BM25 인덱스에 자동 sync 안 함 — 회귀"))

[<Fact>]
let ``PR-Img-Chunk — caption-chunk UPDATE 시 ChunksFts AU trigger 자동 sync (BM25 갱신)`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "blob.png" Png None None
        let docId = SqliteStore.insertDocument conn "H-FTS2" "d.pdf" Pdf 100L None None
        ImageStore.addImageReference conn docId None hash "p=5" 1
        ImageStore.upsertCaptionChunkForRef conn docId hash "p=5" 1 "결선도 초기 캡션"
        // back-fill UPDATE.
        ImageStore.upsertCaptionChunkForRef conn docId hash "p=5" 1 "갱신된 전기 패널 배선도"
        // ChunksFts 가 신 text 의 토큰으로 hit, 구 token (결선도) 으로는 hit 안 함.
        use cmdNew = conn.CreateCommand()
        cmdNew.CommandText <- "SELECT COUNT(*) FROM ChunksFts WHERE ChunksFts MATCH '배선도'"
        Assert.True(Convert.ToInt32 (cmdNew.ExecuteScalar()) >= 1, "AU trigger 신 text 미반영 — 회귀")
        use cmdOld = conn.CreateCommand()
        cmdOld.CommandText <- "SELECT COUNT(*) FROM ChunksFts WHERE ChunksFts MATCH '초기'"
        Assert.Equal(0, Convert.ToInt32 (cmdOld.ExecuteScalar())))

[<Fact>]
let ``PR-Img-Chunk — nextOrdinalForRef: 본문 chunks max(Ordinal) + 1 산출`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let docId = SqliteStore.insertDocument conn "H-ORD" "d.pdf" Pdf 100L None None
        // 본문 chunks 박제 (RefLocator p=5 안 Ordinal 0, 1, 2).
        let chunks = [|
            { Text = "본문 1"; RefLocator = "p=5"; Ordinal = 0; TokenCount = 1; OutlineIndex = None }
            { Text = "본문 2"; RefLocator = "p=5"; Ordinal = 1; TokenCount = 1; OutlineIndex = None }
            { Text = "본문 3"; RefLocator = "p=5"; Ordinal = 2; TokenCount = 1; OutlineIndex = None }
            { Text = "다른 페이지"; RefLocator = "p=6"; Ordinal = 0; TokenCount = 1; OutlineIndex = None }
        |]
        SqliteStore.insertChunks conn docId [||] chunks SqliteStore.DefaultBatchSize CancellationToken.None
        // p=5: max ord 2 → next = 3.
        Assert.Equal(3, SqliteStore.nextOrdinalForRef conn docId "p=5")
        // p=6: max ord 0 → next = 1.
        Assert.Equal(1, SqliteStore.nextOrdinalForRef conn docId "p=6")
        // 본문 없는 ref — next = 0 (COALESCE -1 + 1).
        Assert.Equal(0, SqliteStore.nextOrdinalForRef conn docId "p=99"))
