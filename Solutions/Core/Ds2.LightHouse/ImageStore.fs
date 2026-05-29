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

    /// Phase 2 task D (s6-r19) — caption 조회. 미존재 시 None.
    /// 반환 = (captionText, captionModel) — 둘 다 NULL 이면 None, text 만 NULL 이면 None (model alone 무의미).
    /// CaptionAt 컬럼은 본 표면 미노출 — invalidation 정책은 model tier 만 (MR3).
    /// caller (`Indexer.ingestImagesIntoStore` 의 eager 분기) 가 본 함수 None 반환 시 captionGen 호출 결정.
    let getCaption
        (conn: SqliteConnection)
        (imageHash: string)
        : (string * string) option =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT CaptionText, CaptionModel
            FROM ImageCache
            WHERE ImageHash = $hash
        """
        cmd.Parameters.AddWithValue("$hash", imageHash) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            if reader.IsDBNull 0 || reader.IsDBNull 1 then None
            else Some (reader.GetString 0, reader.GetString 1)
        else None

    /// Phase 2 task D (s6-r19) — caption 갱신. ImageCache row 가 이미 존재 가정 (upsertImageCache 후).
    /// `CaptionAt` = UTC ISO-8601 (SqliteStore.toIsoString 동일 형식).
    ///
    /// idempotent: 같은 hash 두 번 호출 시 overwrite (latest model 박제). caller 는
    /// 같은 hash 가 두 번 caption 호출 안 되도록 getCaption 으로 사전 dedup 의무.
    ///
    /// **PR-Img-Chunk 경고**: 본 함수는 `ImageCache.CaptionText` 만 갱신 — caption-chunk Chunks row 박제는
    /// caller 의무. SSOT caller path:
    ///   - eager: `Indexer.ingestImagesIntoStore` 가 본 함수 호출 직후 `upsertCaptionChunkForRef` 박제 (`Indexer.fs`).
    ///   - late : `updateCaptionBatch` 가 batch 안에서 자동 dispatch (`listImageRefsByHash` + `upsertCaptionChunkForRef`).
    /// 단건 caller 가 본 함수만 호출하면 caption-chunk 미박제 → BM25 retrieval 누락. 새 caller 박제 시 후속
    /// `upsertCaptionChunkForRef` 호출 의무 또는 `updateCaptionBatch` 단건 wrapping 권장.
    let updateCaption
        (conn: SqliteConnection)
        (imageHash: string)
        (captionText: string)
        (captionModel: string)
        : unit =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            UPDATE ImageCache
            SET CaptionText = $text, CaptionModel = $model, CaptionAt = $at
            WHERE ImageHash = $hash
        """
        cmd.Parameters.AddWithValue("$hash",  imageHash) |> ignore
        cmd.Parameters.AddWithValue("$text",  captionText) |> ignore
        cmd.Parameters.AddWithValue("$model", captionModel) |> ignore
        cmd.Parameters.AddWithValue("$at",    DateTime.UtcNow.ToString("o")) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// **PR-Img-Chunk** — caption 미박제 시점 caption-chunk Text 의 placeholder SSOT (사용자 요구 ① 정합).
    /// Step 1-a 색인 시점에 captionGen=noop (대표 path: `--force-without-image-caption`) → image 위치 marker 만
    /// 박제. 후속 Step 2 caption-update 가 동일 chunk row 의 Text 를 실제 caption 으로 UPDATE.
    /// 검색 noise 회피: trigram tokenizer 가 분해해도 한국어 일반 query 와 매칭 가능성 낮음.
    [<Literal>]
    let CaptionPlaceholderText = "(caption 미생성)"

    /// **PR-Img-Chunk** — caption-chunk 의 Text 형식 SSOT.
    /// 형식 = `[그림 <refDisplay> #<ord> hash=<hash12>] <captionText>`.
    /// BM25 search 시 "그림" 키워드 또는 hash prefix 로 caption-only filter 가능 + 본문 chunk 와 자연스럽게
    /// page grouping (RefLocator 동일).
    let formatCaptionChunkText
        (refLocator: string)
        (ordinal: int)
        (imageHash: string)
        (captionText: string)
        : string =
        let refDisplay =
            if refLocator.StartsWith("p=") then sprintf "p.%s" (refLocator.Substring 2)
            elif refLocator.StartsWith("slide=") then sprintf "slide %s" (refLocator.Substring 6)
            elif refLocator.StartsWith("sheet=") then sprintf "sheet %s" (refLocator.Substring 6)
            else refLocator
        let hashShort = if imageHash.Length >= 12 then imageHash.Substring(0, 12) else imageHash
        sprintf "[그림 %s #%d hash=%s] %s" refDisplay ordinal hashShort captionText

    /// **PR-Img-Chunk** — image reference 1개에 대해 caption-chunk INSERT/UPDATE + ImageReferences.CaptionChunkId
    /// 박제 (1:1 매핑). caller (`Indexer.ingestImagesIntoStore` eager path, `updateCaptionBatch` late path) 의 wrapper.
    ///
    /// idempotent: 이미 박제됐으면 UPDATE (ChunksFts AU trigger 자동 sync) / 없으면 INSERT (AI trigger 자동 sync).
    /// caller 의무 — `addImageReference` 가 먼저 호출되어 ImageReferences row 가 존재해야 함 (`setImageRefCaptionChunkId`
    /// 가 PK 4 키로 UPDATE 하기 때문). 호출 순서 보장 안 되면 silent 0-affected → 진단 불가.
    let upsertCaptionChunkForRef
        (conn: SqliteConnection)
        (documentId: int64)
        (imageHash: string)
        (refLocator: string)
        (ordinal: int)
        (captionText: string)
        : unit =
        let chunkText = formatCaptionChunkText refLocator ordinal imageHash captionText
        // TokenCount 단순 추정 (UTF-8 1 token ≈ 4 byte) — 정확성 불요. retrieval 점수 / 컬럼 통계 metadata 용.
        let tokenCount = max 1 (chunkText.Length / 4)
        match SqliteStore.lookupCaptionChunkByImageRef conn documentId imageHash refLocator ordinal with
        | Some existingChunkId ->
            SqliteStore.updateCaptionChunkText conn existingChunkId chunkText
        | None ->
            // 동일 (docId, refLocator) 안에서 본문 chunks 직후 박제 — page grouping 정합 (Chunker §3.13 ordinal scheme).
            // 같은 page 의 image N 개 박제 시 nextOrdinalForRef 가 매 호출마다 +1 ordinal 반환 (직전 caption-chunk
            // INSERT 가 반영된 max 산출).
            let chunkOrdinal = SqliteStore.nextOrdinalForRef conn documentId refLocator
            let chunkId =
                SqliteStore.insertCaptionChunk conn documentId refLocator chunkOrdinal tokenCount chunkText
            SqliteStore.setImageRefCaptionChunkId conn documentId imageHash refLocator ordinal chunkId

    /// `/indexer` skill Step 2 → `caption-update` entry (`done-lighthouse-indexer-claude-caption.md` §3 batch fenced block) —
    /// subagent 가 return 한 caption batch 를 단일 transaction 안 N 회 UPDATE → atomic commit.
    ///
    /// 본 함수가 transaction lifecycle 흡수 책임 — caller 측에서 `BeginTransaction()` + `cmd.Transaction <- tx`
    /// 박제 누락 시 Microsoft.Data.Sqlite 의 SqliteCommand.Transaction mismatch InvalidOperationException 회피.
    /// 기존 `updateCaption` (autocommit, single-row) 는 caller 변경 회피 위해 그대로 보존.
    ///
    /// 반환 = update 적용된 row 수. empty batch 시 0 (no-op, transaction 미생성).
    let updateCaptionBatch
        (conn: SqliteConnection)
        (rows: (string * string * string) seq)
        : int =
        let arr = rows |> Seq.toArray
        if arr.Length = 0 then 0
        else
            use tx = conn.BeginTransaction()
            let mutable n = 0
            for (hash, text, model) in arr do
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <- """
                    UPDATE ImageCache
                    SET CaptionText = $text, CaptionModel = $model, CaptionAt = $at
                    WHERE ImageHash = $hash
                """
                cmd.Parameters.AddWithValue("$hash",  hash) |> ignore
                cmd.Parameters.AddWithValue("$text",  text) |> ignore
                cmd.Parameters.AddWithValue("$model", model) |> ignore
                cmd.Parameters.AddWithValue("$at",    DateTime.UtcNow.ToString("o")) |> ignore
                // **review B fix** (PR-H r4 review): 시도 횟수 (n + 1) 가 아닌 실제 affected row 수 반환.
                // 환각 hash (DB 에 없는 ImageHash) 입력 시 silent 0 update — 호출자가 진단할 수 있도록 정확 보고.
                let affected = cmd.ExecuteNonQuery()
                n <- n + affected
                // PR-Img-Chunk: ImageCache caption 박제 성공한 경우 (affected > 0) 동 hash 의 모든 ImageReferences
                // 에 대해 caption-chunk INSERT/UPDATE. cross-doc 같은 image 의 doc 별 caption-chunk 박제 보장.
                // ChunksFts trigger (AI/AU) 가 자동 sync → BM25 즉시 hit.
                // affected = 0 (환각 hash) 면 ImageReferences 도 없어 listImageRefsByHash 빈 array — no-op.
                if affected > 0 then
                    let refs = SqliteStore.listImageRefsByHash conn hash
                    for (docId, refLocator, ordinal, _) in refs do
                        upsertCaptionChunkForRef conn docId hash refLocator ordinal text
            tx.Commit()
            n

    /// `/indexer` skill (`done-lighthouse-indexer-claude-caption.md` §2 #6/#12) — caption 미박제 image
    /// row 의 SSOT enumeration. skill 진입 시 `lighthouse-cli list-pending-captions <folder>` 가 호출 → stdout JSON.
    ///
    /// **invariant**:
    ///   - `ImageCache.CaptionText IS NULL` row 만 (이미 caption 박제된 row 자연 제외 — idempotent retry).
    ///   - hash 당 1 row 보장 — `ImageReferences` 의 첫 rowid (대표 ref) 만 join → §2 #12 정합.
    ///   - icon-skip 정책 자연 흡수 — icon 은 ImageCache row 자체 미생성, 본 query 진입 0.
    ///
    /// 반환 필드:
    ///   - `Hash` = sha256 64 char lowercase.
    ///   - `Ext` = `StoredPath` 의 확장자 (lowercase, leading dot 제거). subagent file path 박제용.
    ///   - `RefLocator` = 대표 ref 의 locator (e.g. `slide:3`, `page:5`).
    ///   - `DocPath` = 대표 ref 의 source document `OriginalPath` — user-facing message 박제용.
    type CaptionPendingRecord = {
        Hash: string
        Ext: string
        RefLocator: string
        DocPath: string
    }

    let listPendingCaptions (conn: SqliteConnection) : CaptionPendingRecord seq =
        use cmd = conn.CreateCommand()
        // `ImageReferences.rowid` 기준 첫 ref 만 join — composite PK 에 (DocumentId, ImageHash, RefLocator, Ordinal)
        // 가 있어 rowid 안정성 보장 (SQLite WITHOUT ROWID 미사용). hash 당 1 row 보장.
        cmd.CommandText <- """
            SELECT ic.ImageHash, ic.StoredPath, ir.RefLocator, d.OriginalPath
            FROM ImageCache ic
            JOIN ImageReferences ir ON ir.rowid = (
                SELECT MIN(rowid) FROM ImageReferences WHERE ImageHash = ic.ImageHash
            )
            JOIN Documents d ON d.Id = ir.DocumentId
            WHERE ic.CaptionText IS NULL
            ORDER BY ic.ImageHash
        """
        use reader = cmd.ExecuteReader()
        let acc = ResizeArray<CaptionPendingRecord>()
        while reader.Read() do
            let hash = reader.GetString 0
            let storedPath = reader.GetString 1
            let refLocator = reader.GetString 2
            let docPath = reader.GetString 3
            let ext =
                let raw = Path.GetExtension(storedPath)
                if String.IsNullOrEmpty raw then "" else raw.TrimStart('.').ToLowerInvariant()
            acc.Add { Hash = hash; Ext = ext; RefLocator = refLocator; DocPath = docPath }
        acc :> _

    /// `/indexer` skill — caption 보존 자동화 (build-drift 시 `.lighthouse-kb/` wipe 전 dump 용).
    /// `ImageCache.CaptionText` 박제 row 의 (Hash, CaptionText, CaptionAt, CaptionModel) 4 컬럼.
    /// hash = sha256 64 char — 새 색인의 같은 image bytes 가 같은 hash 박제 → importCaptions 가 UPDATE 매치.
    /// caption 미박제 row (CaptionText IS NULL) 는 자연 제외 — backup 의 의미 정합 (caption 박제 row 만 보존).
    type ImageCaptionBackup = {
        Hash: string
        CaptionText: string
        /// ISO8601 UTC. 빈 문자열은 import 시 현재 시각으로 자동 박제.
        CaptionAt: string
        CaptionModel: string
    }

    /// `lighthouse-cli export-image-cache <folder>` SSOT — caption 박제된 ImageCache row 만 dump.
    /// 본 함수는 read-only — `withReadOnlyConn` 또는 `openConnection ... true` caller 안 안전.
    let exportCaptions (conn: SqliteConnection) : ImageCaptionBackup seq =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT ImageHash, CaptionText, CaptionAt, CaptionModel
            FROM ImageCache
            WHERE CaptionText IS NOT NULL AND CaptionText != ''
            ORDER BY ImageHash
        """
        use reader = cmd.ExecuteReader()
        let acc = ResizeArray<ImageCaptionBackup>()
        while reader.Read() do
            let hash = reader.GetString 0
            let text = reader.GetString 1
            let at = if reader.IsDBNull 2 then "" else reader.GetString 2
            let model = if reader.IsDBNull 3 then "" else reader.GetString 3
            acc.Add { Hash = hash; CaptionText = text; CaptionAt = at; CaptionModel = model }
        acc :> _

    /// `lighthouse-cli import-image-cache <folder> <json>` SSOT — backup JSON row 의 caption 4 컬럼을
    /// 신 DB 의 ImageCache 에 UPDATE. hash 매치된 row 만 갱신, **CaptionText 이미 박제된 row 는 보존**
    /// (`AND CaptionText IS NULL` 절). hash 미매치 row 는 silent skip — 반환 (updated, skipped) 로 보고.
    ///
    /// `updateCaptionBatch` 와 동형 — UPDATE 성공 시 동 hash 의 모든 ImageReferences 에 대해 caption-chunk
    /// INSERT/UPDATE 박제 (`upsertCaptionChunkForRef`) + ChunksFts trigger 자동 sync.
    /// 단일 transaction. empty rows 시 (0, 0) no-op.
    let importCaptions
        (conn: SqliteConnection)
        (rows: ImageCaptionBackup seq)
        : int * int =
        let arr = rows |> Seq.toArray
        if arr.Length = 0 then 0, 0
        else
            use tx = conn.BeginTransaction()
            let mutable updated = 0
            let mutable skipped = 0
            for row in arr do
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <- """
                    UPDATE ImageCache
                    SET CaptionText = $text, CaptionAt = $at, CaptionModel = $model
                    WHERE ImageHash = $hash AND CaptionText IS NULL
                """
                let atValue =
                    if String.IsNullOrWhiteSpace row.CaptionAt
                    then DateTime.UtcNow.ToString("o")
                    else row.CaptionAt
                cmd.Parameters.AddWithValue("$hash",  row.Hash) |> ignore
                cmd.Parameters.AddWithValue("$text",  row.CaptionText) |> ignore
                cmd.Parameters.AddWithValue("$at",    atValue) |> ignore
                cmd.Parameters.AddWithValue("$model", row.CaptionModel) |> ignore
                let affected = cmd.ExecuteNonQuery()
                if affected > 0 then
                    updated <- updated + affected
                    let refs = SqliteStore.listImageRefsByHash conn row.Hash
                    for (docId, refLocator, ordinal, _) in refs do
                        upsertCaptionChunkForRef conn docId row.Hash refLocator ordinal row.CaptionText
                else
                    skipped <- skipped + 1
            tx.Commit()
            updated, skipped

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
