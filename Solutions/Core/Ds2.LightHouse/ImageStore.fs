namespace Ds2.LightHouse

open System
open System.IO
open System.Security.Cryptography
open Microsoft.Data.Sqlite

/// Phase 2 task B (s6-r11) — 이미지 blob 저장 + ImageCache / ImageReferences upsert.
///
/// 책임 경계: sha256 hash 산출 / blob 파일 IO / ImageCache·ImageReferences CRUD primitives 만.
/// 호출자 (PdfExtractor / OoxmlExtractor — Phase 2 task C) 가 raw bytes + RefLocator + Ordinal 결정 후
/// 본 모듈에 dispatch. caption 채우기는 별 단계 (Phase 2 task D, attachment_read VLM 모드).
///
/// dedup 단위 = per-collection (parent §3.15.5 MR2). 같은 image 가 두 document 에서 사용되면
/// `ImageCache` 행 1개 + `ImageReferences` 행 2개. cross-collection 격리는 그대로 (§3.9).
///
/// idempotent: blob 파일 / ImageCache / ImageReferences 모두 "이미 존재 시 skip" — 재색인 / shadow rebuild 시
/// 중복 IO 최소화.
[<RequireQualifiedAccess>]
module ImageStore =

    /// blob 저장 sub-디렉토리 — `<collection-root>/.lighthouse-kb/blobs/images/`.
    /// parent §3.15.5 MR1 SSOT. ZipImport 의 `blobRegex` 도 동일 path prefix 검증.
    [<Literal>]
    let BlobsImagesSubPath = "blobs/images"

    /// ImageFormat → 파일 확장자 (`<sha256>.<ext>`). lowercase 강제 (ZipImport blob regex 정합).
    /// 신규 case 추가 시 컴파일러 강제 (FS0025).
    let extOf (fmt: ImageFormat) : string =
        match fmt with
        | Png  -> "png"
        | Jpeg -> "jpg"
        | Gif  -> "gif"
        | Webp -> "webp"

    /// ImageFormat → MIME type. `ImageCache.MimeType` 컬럼 박제용.
    /// `Ds2.LlmAgent.Attachment.mimeOf` 와 의도적 mirror (drift 시 reflection drift test 보호).
    let mimeOf (fmt: ImageFormat) : string =
        match fmt with
        | Png  -> "image/png"
        | Jpeg -> "image/jpeg"
        | Gif  -> "image/gif"
        | Webp -> "image/webp"

    /// collection root → `.lighthouse-kb/blobs/images/` 절대 경로.
    let blobsImagesDir (collectionRoot: string) : string =
        Path.Combine(SqliteStore.kbDir collectionRoot, "blobs", "images")

    /// `<root>/.lighthouse-kb/blobs/images/<sha256>.<ext>` — ZipImport blob regex 정합 (lowercase hash).
    let blobFilePath (collectionRoot: string) (imageHash: string) (fmt: ImageFormat) : string =
        Path.Combine(blobsImagesDir collectionRoot, sprintf "%s.%s" imageHash (extOf fmt))

    /// 원본 bytes 의 SHA-256 hex 표현 (64 char lowercase). dedup 키 = ImageCache.ImageHash 의 SSOT.
    let computeSha256 (bytes: byte[]) : string =
        use sha = SHA256.Create()
        let digest = sha.ComputeHash(bytes)
        // .NET 9 `Convert.ToHexString` 는 uppercase — ZipImport blob regex 가 lowercase 강제라 명시 ToLowerInvariant.
        Convert.ToHexString(digest).ToLowerInvariant()

    /// blob 파일 저장 — idempotent. 이미 존재하면 skip + 동일 path 반환.
    ///
    /// blob 파일은 immutable (sha256 hash 가 컨텐츠 식별자) 이라 "이미 존재 = 동일 내용" 가정.
    /// caller 가 별도 hash mismatch 검증할 필요 없음 — 신뢰 경계는 caller 의 hash 산출 단계.
    ///
    /// 반환 = 절대 경로 (ImageCache.StoredPath 박제용).
    let saveBlob
        (collectionRoot: string)
        (imageHash: string)
        (fmt: ImageFormat)
        (bytes: byte[])
        : string =
        let dir = blobsImagesDir collectionRoot
        if not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore
        let path = blobFilePath collectionRoot imageHash fmt
        if not (File.Exists path) then
            File.WriteAllBytes(path, bytes)
        path

    /// ImageCache upsert — INSERT OR IGNORE.
    ///
    /// idempotent: 같은 ImageHash 두 번 호출 시 첫 INSERT 만 적용, 두 번째 호출은 no-op (기존 caption 보존).
    /// caption 채우기 (`updateCaption`, Phase 2 task D) 는 별 함수 — 본 함수는 metadata 만 박제.
    ///
    /// 본 단원의 `width` / `height` 은 extractor 가 image header parse 한 값. 미상 시 None (DBNull).
    /// `storedPath` 는 절대 경로 그대로 — server 의 zip export 시 절대 경로 재계산 책임은 별 step.
    let upsertImageCache
        (conn: SqliteConnection)
        (imageHash: string)
        (storedPath: string)
        (fmt: ImageFormat)
        (width: int option)
        (height: int option)
        : unit =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            INSERT INTO ImageCache(ImageHash, StoredPath, MimeType, Width, Height, CaptionText, CaptionAt, CaptionModel)
            VALUES($hash, $path, $mime, $w, $h, NULL, NULL, NULL)
            ON CONFLICT(ImageHash) DO NOTHING;
        """
        let toBox (i: int option) : obj =
            i |> Option.map box |> Option.defaultValue (box DBNull.Value)
        cmd.Parameters.AddWithValue("$hash", imageHash) |> ignore
        cmd.Parameters.AddWithValue("$path", storedPath) |> ignore
        cmd.Parameters.AddWithValue("$mime", mimeOf fmt) |> ignore
        cmd.Parameters.AddWithValue("$w",    toBox width) |> ignore
        cmd.Parameters.AddWithValue("$h",    toBox height) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// ImageReferences 박제 — 복합 PK `(DocumentId, ImageHash, RefLocator, Ordinal)` 중복 시 INSERT OR IGNORE.
    ///
    /// FK 보장: caller 가 미리 `upsertImageCache` 호출 의무 (ImageHash → ImageCache FK).
    /// `chunkId` 는 image 가 속한 chunk 가 결정된 경우 (Phase 2 task C 의 extractor 가 결정) Some, 미결 시 None.
    let addImageReference
        (conn: SqliteConnection)
        (documentId: int64)
        (chunkId: int64 option)
        (imageHash: string)
        (refLocator: string)
        (ordinal: int)
        : unit =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            INSERT INTO ImageReferences(DocumentId, ChunkId, ImageHash, RefLocator, Ordinal)
            VALUES($doc, $chunk, $hash, $ref, $ord)
            ON CONFLICT(DocumentId, ImageHash, RefLocator, Ordinal) DO NOTHING;
        """
        let chunkParam : obj =
            chunkId |> Option.map box |> Option.defaultValue (box DBNull.Value)
        cmd.Parameters.AddWithValue("$doc",   documentId) |> ignore
        cmd.Parameters.AddWithValue("$chunk", chunkParam) |> ignore
        cmd.Parameters.AddWithValue("$hash",  imageHash) |> ignore
        cmd.Parameters.AddWithValue("$ref",   refLocator) |> ignore
        cmd.Parameters.AddWithValue("$ord",   ordinal) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// ImageCache 조회. 미존재 시 None. 반환 tuple = (storedPath, mimeType, width, height).
    /// caption fields 는 Phase 2 task D 진입 시 별 함수 (`getCaption`) 로 노출 — 본 task 미박제.
    let getImageCache
        (conn: SqliteConnection)
        (imageHash: string)
        : (string * string * int option * int option) option =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT StoredPath, MimeType, Width, Height
            FROM ImageCache
            WHERE ImageHash = $hash
        """
        cmd.Parameters.AddWithValue("$hash", imageHash) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            let path = reader.GetString 0
            let mime = if reader.IsDBNull 1 then "" else reader.GetString 1
            let w = if reader.IsDBNull 2 then None else Some (reader.GetInt32 2)
            let h = if reader.IsDBNull 3 then None else Some (reader.GetInt32 3)
            Some (path, mime, w, h)
        else None

    /// 한 문서가 참조하는 모든 image — (ImageHash, RefLocator, Ordinal, ChunkId option).
    /// PK 순서 정렬 (Ordinal asc) — page 순회 자연스러움.
    let lookupReferencesByDocument
        (conn: SqliteConnection)
        (documentId: int64)
        : (string * string * int * int64 option) array =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT ImageHash, RefLocator, Ordinal, ChunkId
            FROM ImageReferences
            WHERE DocumentId = $doc
            ORDER BY RefLocator, Ordinal
        """
        cmd.Parameters.AddWithValue("$doc", documentId) |> ignore
        use reader = cmd.ExecuteReader()
        let acc = ResizeArray<string * string * int * int64 option>()
        while reader.Read() do
            let hash = reader.GetString 0
            let ref = reader.GetString 1
            let ord = reader.GetInt32 2
            let chunk = if reader.IsDBNull 3 then None else Some (reader.GetInt64 3)
            acc.Add (hash, ref, ord, chunk)
        acc.ToArray()
