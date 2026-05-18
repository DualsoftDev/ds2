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
            ImageStore.addImageReference conn docId None hash "p=14#img=2" 0
        // ChunkId 만 다른 호출 — PK 4 키 중 ChunkId 없음 → 여전히 1건 (idempotent 박제 SSOT).
        ImageStore.addImageReference conn docId (Some 99L) hash "p=14#img=2" 0
        // Ordinal 만 다르면 별 행 — PK 4 키 중 Ordinal 다르므로 신규 row.
        ImageStore.addImageReference conn docId None hash "p=14#img=2" 1
        let refs = ImageStore.lookupReferencesByDocument conn docId
        Assert.Equal(2, refs.Length)
        // ordinal asc 정렬 검증 — lookupReferencesByDocument SSOT.
        Assert.Equal(0, refs.[0] |> fun (_, _, o, _) -> o)
        Assert.Equal(1, refs.[1] |> fun (_, _, o, _) -> o))

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
        ImageStore.addImageReference conn docA None hash "p=1#img=1" 0
        ImageStore.addImageReference conn docB None hash "p=5#img=3" 0
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
            ImageStore.addImageReference conn docId None "deadbeef" "p=1" 0) |> ignore)

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

[<Fact>]
let ``ImageReferences ON DELETE CASCADE — Documents 삭제 시 refs 도 자동 삭제`` () =
    // parent §3.12 결함 5항 4 (ON DELETE: Documents → CASCADE). ImageCache 는 보존 (cross-document dedup SSOT).
    withTempDir (fun dir ->
        use conn = openFresh dir
        let hash = ImageStore.computeSha256 pngBytes
        ImageStore.upsertImageCache conn hash "x" Png None None
        let docId = SqliteStore.insertDocument conn "HASH-DEL" "d.pdf" Pdf 10L None None
        ImageStore.addImageReference conn docId None hash "p=1" 0
        Assert.Equal(1, (ImageStore.lookupReferencesByDocument conn docId).Length)
        // Document 삭제 → ImageReferences 자동 CASCADE.
        SqliteStore.deleteDocument conn docId
        Assert.Equal(0, (ImageStore.lookupReferencesByDocument conn docId).Length)
        // ImageCache 는 그대로 (cross-document 공유 의도).
        Assert.True((ImageStore.getImageCache conn hash).IsSome))
