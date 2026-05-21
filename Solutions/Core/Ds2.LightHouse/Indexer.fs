namespace Ds2.LightHouse

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Microsoft.Data.Sqlite
open Ds2.LightHouse.Extractors

/// ingest 진행 알림 (KbManagerDialog 의 progress bar). KB 색인 background worker 가 콜백 받음.
type IngestProgress = {
    TotalFiles: int
    CompletedFiles: int
    CurrentFile: string option
}

/// 단일 파일 ingest 결과. caller (KbManagerDialog) 에 reporting.
type FileIngestResult =
    | Ingested of documentId: int64
    | Skipped of reason: string         // 미지원 / rejected / hash 일치
    | Failed of reason: string          // extract / store 실패


/// Extract → Chunk → Store 파이프라인 orchestrator (todo-lighthouse-kb-index.md §4.4).
///
/// 단일 collection 의 색인 일관성 보장 — SchemaVersion drift 시 shadow rebuild (s6-r26 정합 — SQL
/// 비호환 변경 시점만 rebuild, IndexerVersion minor / patch bump 는 ALTER forward-compat 으로 흡수),
/// FileHash 로 idempotent (같은 파일 두 번 ingest 시 1개 Document), CancellationToken 지원.
[<RequireQualifiedAccess>]
module Indexer =

    /// 파일 → SHA-256 hex. `HashAlgorithm.ComputeHash(Stream)` 은 내부적으로 4KB chunked 처리 — 큰 파일도 메모리 안전.
    let private computeFileHash (path: string) : string =
        use sha = SHA256.Create()
        use fs = File.OpenRead path
        let hashBytes = sha.ComputeHash fs
        Convert.ToHexString(hashBytes)

    /// path → extractor 라우팅. 첫 매칭 extractor 반환 (없으면 None).
    let private routeExtractor (extractors: IExtractor list) (kind: FileKind) : IExtractor option =
        extractors |> List.tryFind (fun e -> e.Supports kind)

    /// filename fallback title — extractor 가 None 반환 시 `Path.GetFileNameWithoutExtension`.
    let private titleOf (extracted: ExtractedDocument) (path: string) : string option =
        match extracted.Title with
        | Some _ -> extracted.Title
        | None ->
            let fname = Path.GetFileNameWithoutExtension path
            if String.IsNullOrWhiteSpace fname then None else Some fname

    /// **Phase 2 task C1 (s6-r12) + C2 fail-safe (s6-r13) + C4 ChunkId 매핑 (s6-r15) + D-2-2 eager caption (s6-r19)** —
    /// extracted image array → ImageStore 박제 dispatch + 색인 시점 VLM caption 채움.
    ///
    /// 각 image 에 대해: sha256 산출 → blob 파일 저장 (idempotent) → ImageCache upsert (INSERT OR IGNORE) →
    /// **caption 미존재 시 captionGen 호출 + updateCaption 박제 (D-2-2 eager at indexing time)** →
    /// ImageReferences 박제 (복합 PK 4 키 idempotent). per-collection dedup (parent §3.15.5 MR2) —
    /// 같은 hash 가 두 번 호출되어도 ImageCache 1 row + ImageReferences 만 (DocumentId, RefLocator, Ordinal) 별로 추가.
    ///
    /// **D-2-2 eager caption (s6-r19)** — getCaption 으로 사전 dedup (cross-document 같은 image dedup + 재색인
    /// idempotent) → 미존재 시 caller 제공 `captionGen` 호출 → Captioned (text, model) 시 updateCaption /
    /// SkippedCaption (cost gate / no key) 시 NULL 유지 (다음 색인 시 재시도) / FailedCaption (VLM error) 시 logWarn +
    /// NULL 유지 (D-2-4 per-image fail-safe). captionGen = `CaptionGenerator.noop` 면 항상 SkippedCaption — Phase 1
    /// 회귀 0 + caption 비활성 환경 정합.
    ///
    /// **ChunkId 매핑 (C4 Q1 옵션 1, s6-r15)** — `refToChunkId` map (caller 가 SqliteStore.lookupChunkIdsByDocument
    /// 으로 빌드) 에서 image 의 RefLocator 와 같은 chunk 의 첫 ID 를 `Some` 으로 채움. 한 RefLocator 가 N chunks
    /// 분할 시 **첫 chunk 만** 매핑 (image 의 의미상 위치 = 시작 chunk). RefLocator 가 map 에 없으면 None (segment
    /// 없는 image RefLocator — 예: empty body 의 image, 또는 chunk text 길이 0 으로 skip 된 RefLocator).
    /// 빈 배열 (`images = [||]`) = no-op (Phase 1 extractor 의 default).
    ///
    /// **M2 결론 (per-image fail-safe)** — 단일 image dispatch 실패 (saveBlob disk full / DB row 결함 등) 시
    /// `logWarn` + skip. exception 재발생 안 함 — 다른 image 와 chunks 색인 차단 안 됨. ImageStore 3 함수
    /// (saveBlob / upsertImageCache / addImageReference) 가 idempotent (File.Exists skip + INSERT OR IGNORE) 라
    /// 재실행 시 자동 회복. orphan blob risk = 다음 색인 의 sha256 dedup + Phase 3 GC job 흡수.
    ///
    /// **m6 결론 (defensive 2차 가드)** — `img.Bytes.Length = 0` 시 `logWarn` + skip. extractor primary 가드
    /// (PdfExtractor: TryGetPng false / empty 분기에서 미포함) 의 future 회귀 + 신규 extractor 분기 망각 차단.
    ///
    /// 본 함수는 transaction 직접 열지 않음 — caller (`ingestFile`) 또는 별 batching layer 가 결정.
    /// **Plan 2 (icon noise 가드)** — image 의 byte size threshold. 본 값 미만 image 는 색인 skip.
    /// 산업 .xlsx / pptx 의 logo / icon (보통 5KB 미만) 가 KB 검색 결과의 noise + caption 비용 낭비 차단.
    /// 본 SSOT 는 byte size 기반 (단순 + fast). pixel size 기반 가드는 별 turn (header parse helper 의무).
    /// 임계값 8 KB = 산업 .xlsx 의 실측 분포 — 5KB icon vs 45KB+ 본문 image 의 자연 경계.
    /// caller / config 에서 조정 가능성은 Phase 3 backlog (현재 lib 단일 SSOT literal).
    let [<Literal>] MinImageBytesForIndex = 8192   // 8 KB

    let ingestImagesIntoStore
        (conn: SqliteConnection)
        (collectionRoot: string)
        (documentId: int64)
        (refToChunkId: Map<string, int64>)
        (captionGen: byte[] -> ImageFormat -> CaptionResult)
        (images: ExtractedImage array)
        : unit =
        for img in images do
            if img.Bytes.Length = 0 then
                // m6 defensive 가드 — extractor primary 가드 회귀 차단.
                Log.lighthouse.Warn(
                    sprintf "Indexer.ingestImagesIntoStore: 빈 Bytes skip — doc=%d ref=%s ord=%d"
                        documentId img.RefLocator img.Ordinal)
            elif img.Bytes.Length < MinImageBytesForIndex then
                // Plan 2: icon size skip — debug log 만 (warning 아님, 정상 색인 정책 동작).
                Log.lighthouse.Debug(
                    sprintf "Indexer.ingestImagesIntoStore: icon size skip — doc=%d ref=%s ord=%d size=%d < %d"
                        documentId img.RefLocator img.Ordinal img.Bytes.Length MinImageBytesForIndex)
            else
                try
                    let hash = ImageStore.computeSha256 img.Bytes
                    let storedPath = ImageStore.saveBlob collectionRoot hash img.Format img.Bytes
                    ImageStore.upsertImageCache conn hash storedPath img.Format img.Width img.Height
                    // D-2-2 eager caption: 미존재 시만 captionGen 호출 (cross-doc dedup + 재색인 idempotent).
                    match ImageStore.getCaption conn hash with
                    | Some _ -> () // 이미 caption 채워짐.
                    | None ->
                        match captionGen img.Bytes img.Format with
                        | Captioned (text, model) ->
                            ImageStore.updateCaption conn hash text model
                        | SkippedCaption reason ->
                            // cost gate / no key / explicit off — NULL 유지, 다음 색인 시 재시도.
                            Log.lighthouse.Debug(
                                sprintf "Indexer: caption skip — hash=%s ref=%s reason=%s"
                                    hash img.RefLocator reason)
                        | FailedCaption reason ->
                            // D-2-4 per-image fail-safe — log + NULL 유지.
                            Log.lighthouse.Warn(
                                sprintf "Indexer: caption fail (skip) — hash=%s ref=%s reason=%s"
                                    hash img.RefLocator reason)
                    let chunkIdOpt = Map.tryFind img.RefLocator refToChunkId
                    ImageStore.addImageReference conn documentId chunkIdOpt hash img.RefLocator img.Ordinal
                with ex ->
                    // M2 per-image fail-safe — log + skip, exception 재발생 안 함.
                    Log.lighthouse.Warn(
                        sprintf "Indexer.ingestImagesIntoStore: image dispatch 실패 (skip) — doc=%d ref=%s ord=%d ex=%s"
                            documentId img.RefLocator img.Ordinal ex.Message)

    /// 단일 파일 ingest. caller 가 connection / extractor list / captionGen 제공.
    /// 본 함수는 transaction 직접 열지 않음 — `insertChunks` 내부 batch txn 만.
    ///
    /// `collectionRoot` (s6-r12 추가) = ImageStore.saveBlob 의 blob 저장 위치 산출 위해 필요.
    /// Phase 1 extractor (images=[||]) 경로는 ingestImagesIntoStore 가 no-op 이라 collectionRoot 사용 안 됨.
    ///
    /// `captionGen` (s6-r19 추가, D-2-2 eager 정합) = 색인 시점 VLM caption 채움 함수. 비활성 환경 +
    /// caption 미사용 caller (lib tests / 무인 cli) = `CaptionGenerator.noop` 전달 (Phase 1 회귀 0).
    /// **Phase 4 (s6-r34)** — chunks insert 후 embedder.IsSome 면 batch embedding 생성 + Chunks_Vectors INSERT.
    /// chunks=[||] (empty document) 도 빈 array OK — no-op. embedder=None 이면 hybrid retrieval 시 BM25 only fallback.
    ///
    /// **failure policy** — embedder.GenerateAsync throw 시 본 호출 reraise (caller `ingestFile` 의 transaction 무관 —
    /// chunks INSERT 는 이미 commit 된 별 batch). caller 가 색인 abort 또는 backoff retry. 부분 색인 상태 가능
    /// (chunks 있고 embedding 없음) → 다음 색인 호출 시 idempotent skip (hash) → 본 chunk 의 embedding 미생성
    /// 잔존. 사용자 `--no-embedding` opt-out 또는 shadow rebuild 로 해소.
    let private dispatchEmbeddings
        (conn: SqliteConnection)
        (embedderOpt: IEmbeddingProvider option)
        (documentId: int64)
        (chunks: ExtractedChunk array)
        (ct: CancellationToken)
        : unit =
        match embedderOpt with
        | None -> ()  // hybrid retrieval 의 BM25 fallback path. Chunks_Vectors empty.
        | Some _ when chunks.Length = 0 -> ()  // 빈 document — no-op.
        | Some embedder ->
            let chunkIds = SqliteStore.listChunkIdsByDocument conn documentId
            if chunkIds.Length <> chunks.Length then
                // sequential INSERT 정합 위반 — fail-fast (caller / lib 결함, 사용자 환경 결함 아님).
                raise (InvalidOperationException(
                    sprintf "Chunks_Vectors dispatch — chunkIds.Length=%d ≠ chunks.Length=%d (docId=%d)"
                        chunkIds.Length chunks.Length documentId))
            let texts = chunks |> Array.map (fun c -> c.Text)
            // batch embedding 호출 — backend (Ollama 등) 가 N 개 input 한 번에 처리.
            // Task 동기 wait — caller (ingestFile) 가 sync API 라 정합. CancellationToken 전파.
            let task = embedder.GenerateAsync(texts, ct)
            let vectors = task.GetAwaiter().GetResult()
            if vectors.Length <> chunks.Length then
                raise (InvalidOperationException(
                    sprintf "embedder.GenerateAsync 결함 — 반환 vector 수=%d ≠ inputs 수=%d (docId=%d)"
                        vectors.Length chunks.Length documentId))
            // dim 검증 — 모든 vector 가 embedder.Dimension 정합 의무.
            for v in vectors do
                if v.Length <> embedder.Dimension then
                    raise (InvalidOperationException(
                        sprintf "embedder vector dim 결함 — vector.Length=%d ≠ embedder.Dimension=%d (docId=%d)"
                            v.Length embedder.Dimension documentId))
            let pairs = Array.zip chunkIds vectors
            SqliteStore.upsertChunkEmbeddingsBatch conn pairs ct
            Log.lighthouse.Debug(
                sprintf "Indexer: embeddings dispatched — docId=%d chunks=%d dim=%d"
                    documentId chunks.Length embedder.Dimension)

    let ingestFile
        (conn: SqliteConnection)
        (collectionRoot: string)
        (extractors: IExtractor list)
        (captionGen: byte[] -> ImageFormat -> CaptionResult)
        (embedderOpt: IEmbeddingProvider option)
        (path: string)
        (ct: CancellationToken)
        : FileIngestResult =
        ct.ThrowIfCancellationRequested()
        let kind = Classifier.classifyForKb path

        match kind with
        | Unsupported ext ->
            let reason =
                if Set.contains ext Classifier.rejectedExtensions then sprintf "rejected ext: %s" ext
                else sprintf "unsupported ext: %s" ext
            Log.lighthouse.Debug(sprintf "Indexer: skip — %s (path=%s)" reason path)
            Skipped reason
        | _ ->
            match routeExtractor extractors kind with
            | None ->
                let reason = sprintf "no extractor for kind=%A" kind
                Log.lighthouse.Warn(sprintf "Indexer: skip — %s (path=%s)" reason path)
                Skipped reason
            | Some extractor ->
                // **s6-r49 #2 (L-Maj-10)** — mtime/size fast-skip. existing row 의 OriginalPath 매칭 시
                // (mtime, size) 비교 → 둘 다 match 면 hash 계산 skip + 기존 row 재활용 (대용량 PDF SHA-256 cost
                // 회피). mismatch 또는 mtime NULL (legacy row) → fall-back hash 계산 path 진입.
                let fileInfo = FileInfo(path)
                let mtimeTicks = fileInfo.LastWriteTimeUtc.Ticks
                let sizeBytes = fileInfo.Length
                let fastSkipMatched =
                    match SqliteStore.findDocumentByPath conn path with
                    | Some (existingId, Some existingMtime, existingSize)
                        when existingMtime = mtimeTicks && existingSize = sizeBytes ->
                        Log.lighthouse.Debug(
                            sprintf "Indexer: fast-skip — mtime/size match (path=%s, docId=%d)" path existingId)
                        Some existingId
                    | _ -> None
                match fastSkipMatched with
                | Some _ -> Skipped "fast-skip (mtime/size match)"
                | None ->
                    let hash = computeFileHash path
                    // idempotent: 같은 hash 가 이미 색인되어 있으면 skip (재색인은 rebuild 흐름에서만).
                    match SqliteStore.findDocumentByHash conn hash with
                    | Some existingId ->
                        Log.lighthouse.Debug(sprintf "Indexer: skip — already ingested (path=%s, docId=%d)" path existingId)
                        Skipped "already ingested (same hash)"
                    | None ->
                        // extractor 가 fail-safe (PdfExtractor/OoxmlExtractor) — 손상 파일도 빈 결과 반환.
                        // 추출 자체 throw 는 fail-fast 정책 따라 reraise (debugging 가시성).
                        let extracted = extractor.Extract(path, ct)
                        let title = titleOf extracted path
                        // **B1 (M13 perf sweep, s6-r71+)** — Document + OutlineNodes insert 매 autocommit fsync
                        // (R5 outlier — N+1 fsync) 를 outer transaction 으로 묶어 1회 fsync 로 축소. Microsoft.Data.Sqlite
                        // 의 SqliteCommand 는 cmd.Transaction 명시 없으면 connection 의 ambient transaction 자동 사용
                        // → `insertDocumentWithMtime` / `insertOutlineTree` signature 변경 0. `insertChunks` 는 자체
                        // batch transaction (s6-r51 ⑰) 사용이라 outer txn 안 nested 시 SQLite engine 거부 → outer txn
                        // 의 scope 는 Document + Outline 한정. Chunks 는 outer txn commit 후 별 batch txn 박제.
                        let docId, outlineIds =
                            use docTxn = conn.BeginTransaction()
                            let docId =
                                SqliteStore.insertDocumentWithMtime
                                    conn hash path extracted.DocType sizeBytes extracted.PageOrSheetCnt title (Some mtimeTicks)
                            let outlineIds = SqliteStore.insertOutlineTree conn docId extracted.Outline
                            docTxn.Commit()
                            docId, outlineIds
                        let chunks = Chunker.chunkify extracted.Segments
                        SqliteStore.insertChunks conn docId outlineIds chunks SqliteStore.DefaultBatchSize ct
                        // Phase 4 (s6-r34) — chunks insert 직후 embedder dispatch (None 이면 no-op).
                        dispatchEmbeddings conn embedderOpt docId chunks ct
                        // Phase 2 task C1 — extractor 가 추출한 image staging 을 ImageStore 로 dispatch.
                        // Phase 1 extractor 의 images=[||] 는 no-op.
                        // C4 (s6-r15): chunks 인서트 직후 RefLocator → 첫 chunk Id map 빌드 → ChunkId 채움.
                        // D (s6-r19): captionGen 전달 — eager caption 채움 (noop 이면 SkippedCaption 누적).
                        let refToChunkId = SqliteStore.lookupChunkIdsByDocument conn docId
                        ingestImagesIntoStore conn collectionRoot docId refToChunkId captionGen extracted.Images
                        // C4 Q3 옵션 X — image dispatch 완료 후 Chunks.ImageCount post-update.
                        // images=[||] 인 Phase 1 경로에서도 모든 chunks 의 ImageCount=0 으로 갱신 (idempotent — DEFAULT 0 정합).
                        SqliteStore.updateChunkImageCounts conn docId
                        Log.lighthouse.Info(
                            sprintf "Indexer: ingested — path=%s docId=%d segments=%d chunks=%d images=%d"
                                path docId extracted.Segments.Length chunks.Length extracted.Images.Length)
                        Ingested docId

    /// collection 폴더 안 모든 지원 파일 enumerate (`.lighthouse-kb/` 자체는 제외).
    /// recursive — 하위 폴더 포함. symlink 는 OS 정책 따름.
    let private enumerateFiles (collectionRoot: string) : string array =
        let kbFolder = SqliteStore.kbDir collectionRoot
        Directory.EnumerateFiles(collectionRoot, "*.*", SearchOption.AllDirectories)
        |> Seq.filter (fun p ->
            // .lighthouse-kb/ 안 파일은 제외 — index DB 자체가 색인 대상이 되면 안 됨.
            let dir = Path.GetDirectoryName p
            not (String.IsNullOrEmpty dir) && not (dir.StartsWith(kbFolder, StringComparison.OrdinalIgnoreCase)))
        |> Seq.toArray

    /// 한 connection 위에서 파일들을 순차 ingest. 진행률 콜백 호출. 결과 array 반환 (review m6).
    /// `collectionRoot` (s6-r12) = ImageStore.saveBlob 의 blob 저장 위치 — ingestFile 로 propagate.
    /// `captionGen` (s6-r19) — ingestFile 로 propagate.
    let private ingestFiles
        (conn: SqliteConnection)
        (collectionRoot: string)
        (extractors: IExtractor list)
        (captionGen: byte[] -> ImageFormat -> CaptionResult)
        (embedderOpt: IEmbeddingProvider option)
        (files: string array)
        (progress: IngestProgress -> unit)
        (ct: CancellationToken)
        : (string * FileIngestResult) array =
        progress { TotalFiles = files.Length; CompletedFiles = 0; CurrentFile = None }
        let results = ResizeArray<string * FileIngestResult>(files.Length)
        for i = 0 to files.Length - 1 do
            ct.ThrowIfCancellationRequested()
            let path = files.[i]
            progress { TotalFiles = files.Length; CompletedFiles = i; CurrentFile = Some path }
            results.Add (path, ingestFile conn collectionRoot extractors captionGen embedderOpt path ct)
        progress { TotalFiles = files.Length; CompletedFiles = files.Length; CurrentFile = None }
        results.ToArray()

    /// shadow rebuild — SchemaVersion drift 시 새 DB 만들고 모든 파일 재색인 → atomic rename (§3.17, s6-r26).
    ///
    /// 호출 전제: 기존 DB connection 닫힘. 본 함수가 shadow DB 의 lifecycle 전부 책임.
    ///
    /// 주의 — Microsoft.Data.Sqlite 의 connection pool 이 dispose 후에도 underlying handle 을 보존
    /// → File.Replace 시 source 파일 lock 충돌. `SqliteConnection.ClearPool` 명시 호출로 swap 직전 해제.
    let private rebuildShadow
        (collectionRoot: string)
        (extractors: IExtractor list)
        (captionGen: byte[] -> ImageFormat -> CaptionResult)
        (embedderOpt: IEmbeddingProvider option)
        (files: string array)
        (progress: IngestProgress -> unit)
        (ct: CancellationToken)
        : (string * FileIngestResult) array =
        let shadowPath = SqliteStore.shadowDbPath collectionRoot
        // 이전 실패 잔재 cleanup.
        if File.Exists shadowPath then File.Delete shadowPath

        Log.lighthouse.Info(sprintf "Indexer: shadow rebuild 시작 — collection=%s files=%d" collectionRoot files.Length)
        let conn = SqliteStore.openConnection shadowPath false
        let results =
            try
                SqliteStore.ensureSchema conn
                SqliteStore.stampVersion conn
                ingestFiles conn collectionRoot extractors captionGen embedderOpt files progress ct
            finally
                conn.Close()
                SqliteConnection.ClearPool conn
                conn.Dispose()
        // atomic swap.
        SqliteStore.swapShadow collectionRoot
        Log.lighthouse.Info(sprintf "Indexer: shadow rebuild 완료 — collection=%s" collectionRoot)
        results

    /// 단일 collection 전체 색인 (§4.4).
    ///
    /// 단계:
    /// 1. write 가능 여부 probe → read-only 면 fail-fast (§3.9 r4)
    /// 2. DB 존재 / SchemaVersion 검증 (s6-r26 — needsRebuild SSOT 정합)
    ///    - 신규 (DB 미존재) → 일반 색인
    ///    - SchemaVersion drift → shadow rebuild (SQL 비호환 변경)
    ///    - 정상 (SchemaVersion 일치, IndexerVersion minor/patch bump 무관) → 신규 파일만 색인 (idempotent)
    /// 3. 진행률 콜백
    let ingest
        (collectionRoot: string)
        (extractors: IExtractor list)
        (captionGen: byte[] -> ImageFormat -> CaptionResult)
        (embedderOpt: IEmbeddingProvider option)
        (progress: IngestProgress -> unit)
        (ct: CancellationToken)
        : (string * FileIngestResult) array =

        if not (SqliteStore.checkWritable collectionRoot) then
            raise (InvalidOperationException(
                sprintf "collection 폴더가 쓰기 불가 — read-only collection 은 색인/재색인 불가. (path=%s)" collectionRoot))

        let files = enumerateFiles collectionRoot
        let dbPath = SqliteStore.dbPath collectionRoot
        let dbExists = File.Exists dbPath

        if dbExists then
            let needsRebuild =
                let probeConn = SqliteStore.openConnection dbPath false
                try SqliteStore.needsRebuild probeConn
                finally
                    probeConn.Close()
                    SqliteConnection.ClearPool probeConn  // swap 직전 dbPath lock 해제 (rebuildShadow 의존)
                    probeConn.Dispose()
            if needsRebuild then
                rebuildShadow collectionRoot extractors captionGen embedderOpt files progress ct
            else
                // 정상 경로도 ClearPool — 호출자가 곧바로 rebuildShadow 단독 호출 시 dbPath lock 보호 (review M2).
                let conn = SqliteStore.openConnection dbPath false
                try
                    SqliteStore.ensureSchema conn
                    SqliteStore.stampVersion conn
                    ingestFiles conn collectionRoot extractors captionGen embedderOpt files progress ct
                finally
                    conn.Close()
                    SqliteConnection.ClearPool conn
                    conn.Dispose()
        else
            let conn = SqliteStore.openConnection dbPath false
            try
                SqliteStore.ensureSchema conn
                SqliteStore.stampVersion conn
                ingestFiles conn collectionRoot extractors captionGen embedderOpt files progress ct
            finally
                conn.Close()
                SqliteConnection.ClearPool conn
                conn.Dispose()
