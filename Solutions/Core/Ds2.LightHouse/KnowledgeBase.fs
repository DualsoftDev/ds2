namespace Ds2.LightHouse

open System
open System.IO
open Microsoft.Data.Sqlite

/// KnowledgeBase facade — `Ds2.LightHouse` 의 외부 진입점 (done-lighthouse-kb-index.md §3.18.1).
///
/// 형식: F# **record-of-functions** (idiomatic, mock 친화, DI 컨테이너 무관 — `openCollections` 가 instance 반환).
/// lifecycle: 한 active 셋에 lock-in — 사용자가 active 토글 시 turn 종료 후 다음 turn 시작 시 새 instance (§3.18.2).
///
/// surface 는 lib 의 `Models.fs` 타입만 노출 — PdfPig / Sqlite / OpenXml 의 type 노출 0
/// (transitive NuGet 의 `PrivateAssets=all` 정합, r8 메타리뷰 m2 결정).
type KnowledgeBase = {
    /// active 셋 union 검색 (BM25 trigram). `Query.FileId` 로 특정 문서 한정 가능.
    /// **s6-r36 P4-C.0** — ct 전파 path 추가 (hybrid path 의 embedder.GenerateAsync 호출 cancel).
    /// caller 가 cancel 의무 없으면 `CancellationToken.None` 전달.
    Search: Query -> System.Threading.CancellationToken -> SearchResults
    /// 등록된 모든 문서 메타 — (fileId, originalPath, kind, pageOrSheetCnt).
    List: unit -> (string * string * FileKind * int option) array
    /// 한 문서의 outline tree raw rows — (id, parentId, ordinal, nodeType, label, ref).
    Outline: string -> (int64 * int64 option * int * OutlineNodeType * string * string) array
    /// 특정 ref 의 chunk 본문 concat. `maxExcerptTokens` 한도 절단.
    Read: string -> string -> string
    /// Phase 2 task D-iv (s6-r20) — 특정 ref 의 image references.
    /// 반환 = (ImageHash, MimeType, StoredPath, CaptionText option) array. Ordinal asc 정렬.
    /// caller (server AttachmentTools) 가 StoredPath → File.ReadAllBytes + size 정책 검사 책임.
    ReadImages: string -> string -> (string * string * string * string option) array
    /// 활성 active 셋 — caller 가 LlmConfig 의 path 와 매칭할 때 사용.
    ActivePaths: string array
    /// 명시적 dispose. record field 형태 유지 (F# idiomatic, mock 친화). `IDisposable` interface 도 박제
    /// (s6-r35 외부 --review L-Maj-4) — C# caller (server SessionKb.dispose 등) 가 `using` 키워드 자동 호출
    /// 가능. interface impl 은 본 field 위임.
    Dispose: unit -> unit
}
with
    interface System.IDisposable with
        member this.Dispose() = this.Dispose ()


/// KnowledgeBase facade 진입점.
///
/// `openCollections(activePaths)` 가 multi-db ATTACH 된 in-memory main DB connection 을 만들고
/// surface 함수들을 closure 로 캡처하여 record 반환.
[<RequireQualifiedAccess>]
module KnowledgeBase =

    /// SQLite ATTACH 제한 (`SqliteStore.MaxAttachedDbs` SSOT 의 facade alias).
    /// review IM-6 — caller (Ds2.LightHouseService) 가 SqliteStore 직접 참조하는 우회 통합.
    /// 값 drift 회피 — SqliteStore.MaxAttachedDbs 변경 시 본 Literal 도 동시 갱신 의무.
    [<Literal>]
    let MaxAttachedDbs = 10

    /// collection root → `.lighthouse-kb/index.db` 존재 여부.
    ///
    /// review IM-6 (2/7 reviewer) — caller (Ds2.LightHouseService) 가 SqliteStore 의 KbFolderName / DbFileName
    /// 직접 참조하던 우회 통합. lib 의 PrivateAssets=all 정책 유지 (caller 가 Microsoft.Data.Sqlite 직접 의존 회피).
    let isIndexed (collectionRoot: string) : bool =
        File.Exists (SqliteStore.dbPath collectionRoot)

    /// **read-only connection lifecycle SSOT (s6-r42 / L-Maj-3 part 1 helper 추출 + s6-r47 / L-Maj-3 part 2
    /// ClearPool 제거)** — `isIndexed` 가드 + open + work + Close + Dispose. `SqliteStore.openConnection`
    /// 의 `Pooling=False` 박제 (read-only path 만) 로 본 connection 은 pool 외 — Close 시 자연 dispose,
    /// file lock 잔존 0. 매 호출 ClearPool (전역 flush) 의 hot path 무력화 부작용 해소.
    /// write path (`stampIndexerVersion`) 는 별도 — file swap path 정합 위해 ClearPool 유지.
    ///
    /// open 실패 / 정상 work 의 None 반환 모두 None 으로 전파 (caller 가 fail-safe).
    let private withReadOnlyConn (collectionRoot: string) (work: Microsoft.Data.Sqlite.SqliteConnection -> 'a option) : 'a option =
        if not (isIndexed collectionRoot) then None
        else
            let dbPath = SqliteStore.dbPath collectionRoot
            try
                let conn = SqliteStore.openConnection dbPath true
                try work conn
                finally
                    conn.Close()
                    conn.Dispose()
            with ex ->
                Log.lighthouse.Warn(sprintf "withReadOnlyConn: %s 열기 실패 — %s: %s"
                    dbPath (ex.GetType().Name) ex.Message)
                None

    /// collection root → `.lighthouse-kb/index.db` 의 `Meta.indexer_version` 값.
    /// 미존재 / open 실패 시 None. **s6-r42**: `withReadOnlyConn` SSOT 흡수 (ClearPool 제거).
    ///
    /// review IM-6 통합 — Ds2.LightHouseService.ZipImport.probeIndexerVersion 가 SqliteStore.openConnection +
    /// getMeta + ClearPool 직접 호출하던 우회 흡수. caller 측 Microsoft.Data.Sqlite PackageReference 제거 가능.
    let probeIndexerVersion (collectionRoot: string) : string option =
        withReadOnlyConn collectionRoot (fun conn -> SqliteStore.getMeta conn "indexer_version")

    /// `Meta.indexer_version` 행을 override (write). `probeIndexerVersion` 의 대칭.
    ///
    /// 용도: **test 한정** — IndexerVersion gate 415 시나리오 검증 (server-side §3.12) 시 zip 안
    /// `.lighthouse-kb/index.db` 의 indexer_version 행을 임의 값 ("0.5.0" / "9.99.99") 으로 갈아끼우는
    /// helper. production 색인 경로 (`Indexer.ingest`) 는 `IndexerVersion.Current` 만 stamp.
    /// **production 호출 금지** — `Indexer.ingest` 가 stamp 한 `IndexerVersion.Current` 를 강제 override
    /// 시 schema/version drift + 색인 결과와 메타 불일치 회귀 (server 가 호환 잘못 판정).
    ///
    /// `collectionRoot/.lighthouse-kb/index.db` 미존재 시 InvalidOperationException — caller fail-fast.
    /// `Microsoft.Data.Sqlite.SqliteConnection.ClearPool` 까지 finally 처리 (parent r10 F2 정합).
    let stampIndexerVersion (collectionRoot: string) (version: string) : unit =
        if not (isIndexed collectionRoot) then
            raise (InvalidOperationException(
                sprintf "stampIndexerVersion: %s 의 index.db 미존재 — 색인 후 호출 의무" collectionRoot))
        if String.IsNullOrWhiteSpace version then
            invalidArg (nameof version) "version 빈 값 금지."
        let dbPath = SqliteStore.dbPath collectionRoot
        let conn = SqliteStore.openConnection dbPath false   // write 가능
        try
            SqliteStore.setMeta conn "indexer_version" version
        finally
            conn.Close()
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn
            conn.Dispose()

    /// 단일 collection 의 documents.Id → (OriginalPath, FileHash, SizeBytes). Phase S4 file serving 용.
    /// `<collection-root>/.lighthouse-kb/index.db` 미존재 / open 실패 / row 미존재 시 None.
    ///
    /// **s6-r42**: `withReadOnlyConn` SSOT 흡수 — review L-Maj-3 정정 (매 file GET 호출 ClearPool 전역
    /// pool flush 제거). review IM-6 정합 (caller 가 SqliteStore 직접 참조 회피) 유지.
    let lookupDocument (collectionRoot: string) (documentId: int64) : (string * string * int64) option =
        withReadOnlyConn collectionRoot (fun conn -> SqliteStore.findDocumentById conn documentId)

    /// ATTACH URI 변환 — Windows backslash → forward slash + `?mode=ro` 강제.
    /// read-only ATTACH 라 *색인 중에도* 검색 가능 (WAL + ro mode).
    let private toAttachUri (path: string) : string =
        let normalized = path.Replace('\\', '/')
        sprintf "file:%s?mode=ro" normalized

    /// ATTACH alias 생성 (`kb0`, `kb1`, ...).
    let private aliasFor (idx: int) : string = sprintf "kb%d" idx

    /// 한 collection root → `.lighthouse-kb/index.db` path. 존재하지 않으면 None (색인되지 않은 collection).
    let private dbPathOf (collectionRoot: string) : string option =
        let p = SqliteStore.dbPath collectionRoot
        if File.Exists p then Some p else None

    /// multi-collection ATTACH facade.
    ///
    /// 동작:
    /// 1. `:memory:` main connection open + PRAGMA
    /// 2. 각 activePath 에서 index.db 가 존재하면 `kb{i}` 로 read-only ATTACH (URI mode=ro)
    /// 3. ATTACH 안 된 (DB 미존재) collection 은 skip + warn log
    /// 4. ATTACH 가드 — §3.18.2 한계 초과 시 fail-fast
    ///
    /// 빈 active 셋 (`activePaths.Length = 0`) → 모든 search/list 가 empty 반환하는 valid facade.
    ///
    /// **`embedderOpt` (Phase 4 s6-r35)**: `None` 시 `Search` 가 BM25-only path (기존 동작), `Some embedder`
    /// 시 hybrid (BM25 + vector RRF). server 측 (LightHouseService) 는 embedder 주입 시점 (P4-C 박제) 까지
    /// None 박제 — `Chunks_Vectors` 가 있어도 BM25-only fallback (회귀 0). Promaker client 측은 OllamaSharp
    /// adapter 도입 후 (P4-C/D) Some 박제 가능.
    let openCollections (activePaths: string array) (embedderOpt: IEmbeddingProvider option) : KnowledgeBase =
        if activePaths.Length > SqliteStore.MaxAttachedDbs then
            raise (InvalidOperationException(
                sprintf "ATTACH 제한 초과 — active collection %d 개, SQLite default %d 까지 (§3.18.2)."
                    activePaths.Length SqliteStore.MaxAttachedDbs))

        // :memory: main — 검색 라우터 역할만. PRAGMA 는 main 만 — ATTACHed DB 는 ATTACH 시점 ro lock.
        // private cache + unique URI — 동시 KB instance 의 cross-talk 차단 (review M4).
        let csb = SqliteConnectionStringBuilder()
        csb.DataSource <- sprintf "file:lhmain-%s?mode=memory&cache=private" (Guid.NewGuid().ToString("N"))
        csb.Mode <- SqliteOpenMode.Memory
        let conn = new SqliteConnection(csb.ToString())
        conn.Open()
        // Phase 4 (s6-r35) — main connection 에도 vec0 extension load. ATTACH 된 DB 의 Chunks_Vectors 를
        // query 할 때 SQLite 는 *main connection 의* extension list 만 본다 — ATTACH 된 DB 의 module 자체는
        // 공유되나, hybrid retrieval 시 vec0 MATCH expression 을 compile 하려면 main 에 vec0 module 박제 의무.
        SqliteStore.loadVec0Extension conn

        // ATTACH 도중 실패 (DB 파일 손상 / SQLite limit / 권한 등) 시 conn 누수 방지 — Dispose 후 reraise (review C1).
        let aliases =
            try
                let attachedAliases = ResizeArray<string>()
                for i = 0 to activePaths.Length - 1 do
                    let collectionRoot = activePaths.[i]
                    match dbPathOf collectionRoot with
                    | None ->
                        Log.lighthouse.Warn(
                            sprintf "KnowledgeBase: collection 미색인 — path=%s (index.db 없음, skip)" collectionRoot)
                    | Some dbPath ->
                        let alias = aliasFor attachedAliases.Count
                        let uri = toAttachUri dbPath
                        use cmd = conn.CreateCommand()
                        // SQLite ATTACH path 는 literal 만 받음 (parameter binding 불가) — single-quote escape + inline (review C2).
                        // alias 는 코드 생성 (kb0..kb9) 이라 SQL injection 무관.
                        cmd.CommandText <- sprintf "ATTACH DATABASE '%s' AS %s" (uri.Replace("'", "''")) alias
                        cmd.ExecuteNonQuery() |> ignore
                        attachedAliases.Add alias
                attachedAliases.ToArray()
            with _ ->
                try conn.Close() with _ -> ()
                conn.Dispose()
                reraise()

        {
            Search      = fun query ct -> Searcher.search conn aliases embedderOpt query Searcher.DefaultMaxExcerptTokens ct
            List        = fun () -> Searcher.listDocuments conn aliases
            Outline     = fun fileId -> Searcher.getOutline conn aliases fileId
            Read        = fun fileId ref -> Searcher.readByRef conn aliases fileId ref Searcher.DefaultMaxExcerptTokens
            ReadImages  = fun fileId ref -> Searcher.readImagesByRef conn aliases fileId ref
            ActivePaths = activePaths
            Dispose     = fun () ->
                // 외부 환경 사유 (DB lock 충돌 등) 외에는 예외 X — log 박제 (review M3/m8).
                try
                    conn.Close()
                with ex ->
                    Log.lighthouse.Warn(sprintf "KnowledgeBase.Dispose: close 실패 — %s" ex.Message)
                conn.Dispose()
                // s6-r36 P4-C.0 — embedderOpt ownership SSOT (facade 와 동일 lifecycle).
                // backend (OllamaSharp HttpClient 등) 의 unmanaged resource 회수. mock 은 no-op.
                match embedderOpt with
                | None -> ()
                | Some embedder ->
                    try embedder.Dispose()
                    with ex ->
                        Log.lighthouse.Warn(sprintf "KnowledgeBase.Dispose: embedder dispose 실패 — %s" ex.Message)
        }
