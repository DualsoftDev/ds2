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
/// 단일 collection 의 색인 일관성 보장 — IndexerVersion mismatch 시 shadow rebuild,
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

    /// 단일 파일 ingest. caller 가 connection / extractor list 제공.
    /// 본 함수는 transaction 직접 열지 않음 — `insertChunks` 내부 batch txn 만.
    let ingestFile
        (conn: SqliteConnection)
        (extractors: IExtractor list)
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
                    let sizeBytes = FileInfo(path).Length
                    let title = titleOf extracted path
                    let docId =
                        SqliteStore.insertDocument
                            conn hash path extracted.DocType sizeBytes extracted.PageOrSheetCnt title
                    let outlineIds = SqliteStore.insertOutlineTree conn docId extracted.Outline
                    let chunks = Chunker.chunkify extracted.Segments
                    SqliteStore.insertChunks conn docId outlineIds chunks SqliteStore.DefaultBatchSize ct
                    Log.lighthouse.Info(
                        sprintf "Indexer: ingested — path=%s docId=%d segments=%d chunks=%d"
                            path docId extracted.Segments.Length chunks.Length)
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
    let private ingestFiles
        (conn: SqliteConnection)
        (extractors: IExtractor list)
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
            results.Add (path, ingestFile conn extractors path ct)
        progress { TotalFiles = files.Length; CompletedFiles = files.Length; CurrentFile = None }
        results.ToArray()

    /// shadow rebuild — IndexerVersion drift 시 새 DB 만들고 모든 파일 재색인 → atomic rename (§3.17).
    ///
    /// 호출 전제: 기존 DB connection 닫힘. 본 함수가 shadow DB 의 lifecycle 전부 책임.
    let private rebuildShadow
        (collectionRoot: string)
        (extractors: IExtractor list)
        (files: string array)
        (progress: IngestProgress -> unit)
        (ct: CancellationToken)
        : (string * FileIngestResult) array =
        let shadowPath = SqliteStore.shadowDbPath collectionRoot
        // 이전 실패 잔재 cleanup.
        if File.Exists shadowPath then File.Delete shadowPath

        Log.lighthouse.Info(sprintf "Indexer: shadow rebuild 시작 — collection=%s files=%d" collectionRoot files.Length)
        let results =
            use conn = SqliteStore.openConnection shadowPath false
            SqliteStore.ensureSchema conn
            SqliteStore.stampVersion conn
            let r = ingestFiles conn extractors files progress ct
            conn.Close()
            r
        // atomic swap.
        SqliteStore.swapShadow collectionRoot
        Log.lighthouse.Info(sprintf "Indexer: shadow rebuild 완료 — collection=%s" collectionRoot)
        results

    /// 단일 collection 전체 색인 (§4.4).
    ///
    /// 단계:
    /// 1. write 가능 여부 probe → read-only 면 fail-fast (§3.9 r4)
    /// 2. DB 존재 / IndexerVersion 검증
    ///    - 신규 (DB 미존재) → 일반 색인
    ///    - drift → shadow rebuild
    ///    - 정상 → 신규 파일만 색인 (idempotent)
    /// 3. 진행률 콜백
    let ingest
        (collectionRoot: string)
        (extractors: IExtractor list)
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
                use probeConn = SqliteStore.openConnection dbPath false
                SqliteStore.needsRebuild probeConn
            if needsRebuild then
                rebuildShadow collectionRoot extractors files progress ct
            else
                use conn = SqliteStore.openConnection dbPath false
                SqliteStore.ensureSchema conn
                SqliteStore.stampVersion conn
                ingestFiles conn extractors files progress ct
        else
            use conn = SqliteStore.openConnection dbPath false
            SqliteStore.ensureSchema conn
            SqliteStore.stampVersion conn
            ingestFiles conn extractors files progress ct
