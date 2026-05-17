namespace Ds2.LightHouse

open System
open System.IO
open Microsoft.Data.Sqlite

/// KnowledgeBase facade — `Ds2.LightHouse` 의 외부 진입점 (todo-lighthouse-kb-index.md §3.18.1).
///
/// 형식: F# **record-of-functions** (idiomatic, mock 친화, DI 컨테이너 무관 — `openCollections` 가 instance 반환).
/// lifecycle: 한 active 셋에 lock-in — 사용자가 active 토글 시 turn 종료 후 다음 turn 시작 시 새 instance (§3.18.2).
///
/// surface 는 lib 의 `Models.fs` 타입만 노출 — PdfPig / Sqlite / OpenXml 의 type 노출 0
/// (transitive NuGet 의 `PrivateAssets=all` 정합, r8 메타리뷰 m2 결정).
type KnowledgeBase = {
    /// active 셋 union 검색 (BM25 trigram). `Query.FileId` 로 특정 문서 한정 가능.
    Search: Query -> SearchResults
    /// 등록된 모든 문서 메타 — (fileId, originalPath, kind, pageOrSheetCnt).
    List: unit -> (string * string * FileKind * int option) array
    /// 한 문서의 outline tree raw rows — (id, parentId, ordinal, nodeType, label, ref).
    Outline: string -> (int64 * int64 option * int * OutlineNodeType * string * string) array
    /// 특정 ref 의 chunk 본문 concat. `maxExcerptTokens` 한도 절단.
    Read: string -> string -> string
    /// 활성 active 셋 — caller 가 LlmConfig 의 path 와 매칭할 때 사용.
    ActivePaths: string array
    /// 명시적 dispose. `using` / `IDisposable` 대신 record 의 함수 필드로 (F# idiomatic).
    Dispose: unit -> unit
}


/// KnowledgeBase facade 진입점.
///
/// `openCollections(activePaths)` 가 multi-db ATTACH 된 in-memory main DB connection 을 만들고
/// surface 함수들을 closure 로 캡처하여 record 반환.
[<RequireQualifiedAccess>]
module KnowledgeBase =

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
    let openCollections (activePaths: string array) : KnowledgeBase =
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
            Search      = fun query -> Searcher.search conn aliases query Searcher.DefaultMaxExcerptTokens
            List        = fun () -> Searcher.listDocuments conn aliases
            Outline     = fun fileId -> Searcher.getOutline conn aliases fileId
            Read        = fun fileId ref -> Searcher.readByRef conn aliases fileId ref Searcher.DefaultMaxExcerptTokens
            ActivePaths = activePaths
            Dispose     = fun () ->
                // 외부 환경 사유 (DB lock 충돌 등) 외에는 예외 X — log 박제 (review M3/m8).
                try
                    conn.Close()
                with ex ->
                    Log.lighthouse.Warn(sprintf "KnowledgeBase.Dispose: close 실패 — %s" ex.Message)
                conn.Dispose()
        }
