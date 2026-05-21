namespace Ds2.LightHouse

open System
open System.IO
open System.Text
open System.Threading
open Microsoft.Data.Sqlite

/// FTS5 trigram 검색 — multi-collection ATTACH UNION (todo-lighthouse-kb-index.md §3.7 / §3.10 / §4.4 r4).
///
/// Phase 1 = BM25 lexical-only (FTS5 trigram). **Phase 4 (s6-r35) = hybrid (BM25 + vector RRF)** —
/// `search` 시그니처에 `embedderOpt: IEmbeddingProvider option` 추가. None 시 BM25-only (기존 동작),
/// Some 시 BM25 + vector KNN (sqlite-vec MATCH) 결과를 RRF (Reciprocal Rank Fusion, k_rrf=60 standard) 로 결합.
/// 본 모듈은 검색 path 만 — write (ingest / 재색인) 은 `Indexer` 책임 (§3.0 두 경로 분리 SSOT 와 별개로 read/write 분리).
[<RequireQualifiedAccess>]
module Searcher =

    /// MCP `attachment_search` 의 excerpt 한도 (§3.10 quota 가드). 호출자 (server / Promaker) 가 enforce.
    [<Literal>]
    let DefaultMaxExcerptTokens = 4000

    /// **Phase 4 (s6-r35)** — RRF (Reciprocal Rank Fusion) k 상수. standard 값 = 60 (Cormack et al. 2009).
    ///
    /// `RRF_score(d) = sum over systems of 1/(k_rrf + rank_i(d))` — rank 0-based.
    /// k=60 은 top rank 의 영향력 (1/60 ≈ 0.0167) 과 lower rank tail (1/100 ≈ 0.01) 의 비율을 적당히 유지하여
    /// 한 system 의 outlier 가 다른 system 결과를 압도하지 않도록 함. 변경 시 hybrid quality 재검증 의무.
    [<Literal>]
    let RrfK = 60.0

    /// **Phase 4 (s6-r35)** — hybrid 시 각 system (BM25 / vector) 의 over-fetch 배수.
    ///
    /// topK 의 N배 까지 fetch 후 RRF fusion → top-K. N=2 가 RRF literature standard (양쪽 system 의
    /// 후보 union 이 적절히 큰 pool 형성). topK=10 시 양쪽 각각 20 fetch, fusion 후 10 반환.
    [<Literal>]
    let HybridFetchMultiplier = 2

    /// FTS5 query 빌드 — 공백/탭으로 토큰화 후 각 토큰을 phrase 로 감쌈 (review M1).
    ///
    /// 통상 검색 의도 = 여러 단어 implicit AND. 단일 phrase quoting 은 "정확 연속" 만 매칭 → 의도 어긋남.
    /// 토큰 안 `"` 는 `""` escape (FTS5 syntax). 한국어 trigram 은 phrase 가 자연 (3-gram 단위).
    let private buildFtsQuery (raw: string) : string =
        raw.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun token -> "\"" + token.Replace("\"", "\"\"") + "\"")
        |> String.concat " "

    /// "<kbIdx>:<docId>" → (kbIdx, docId) parse. parse 실패 시 None.
    let private parseFileId (fileId: string) : (int * int64) option =
        if String.IsNullOrEmpty fileId then None
        else
            let parts = fileId.Split(':')
            if parts.Length <> 2 then None
            else
                let okIdx, kbIdx = Int32.TryParse parts.[0]
                let okDoc, docId = Int64.TryParse parts.[1]
                if okIdx && okDoc then Some (kbIdx, docId) else None

    let private composeFileId (kbIdx: int) (docId: int64) : string =
        sprintf "%d:%d" kbIdx docId

    /// 한 collection (alias) 분의 SELECT 생성. UNION ALL 로 결합.
    /// `kbIdx` 가 fileId composition / score 보존에 쓰임. alias = `kb0`/`kb1`/... (SqliteStore.MaxAttachedDbs 안).
    ///
    /// FTS5 의 auxiliary function (`bm25()`) 과 MATCH 는 *unqualified FTS5 table name* 만 받음 — alias / schema-qualified 모두 거부.
    /// (`bm25(kb0.ChunksFts)` / `bm25(ft)` → SQLite Error 1 "no such column".)
    /// FROM 절에서만 schema-qualify 가능. UNION ALL 의 각 SELECT 는 독립 컴파일이라 ambiguous 없음.
    ///
    /// **Phase 4 (s6-r35)** — `c.Id AS ChunkId` 추가. hybrid retrieval 의 RRF dedup key (`<kbIdx>:<ChunkId>`)
    /// 산출 + vector KNN 결과와 같은 row 인지 매칭하기 위함. BM25-only path 도 동일 column (column 위치 1 신설).
    ///
    /// **s6-r57 C7** — `c.ImageCount AS ImageCount` 추가 (column 10, Score 다음). SearchHit.HasImages 정합:
    /// `ImageCount > 0` 시 true. Phase 2 의 `Chunks.ImageCount` 박제 활용 (s6-r15 박제 SSOT).
    let private buildCollectionSelect (kbIdx: int) (alias: string) (fileIdFilter: (int * int64) option) : string =
        let docFilter =
            match fileIdFilter with
            | Some (filterKb, filterDoc) when filterKb = kbIdx -> sprintf " AND c.DocumentId = %d" filterDoc
            | Some _ -> " AND 1 = 0"  // 다른 kbIdx 한정 → 본 collection 결과 0
            | None -> ""
        sprintf """
            SELECT
                %d                                 AS KbIdx,
                c.Id                               AS ChunkId,
                c.DocumentId                       AS DocumentId,
                c.RefLocator                       AS RefLocator,
                c.TokenCount                       AS TokenCount,
                c.Text                             AS Text,
                d.OriginalPath                     AS OriginalPath,
                d.Title                            AS Title,
                o.Label                            AS OutlineLabel,
                bm25(ChunksFts)                    AS Score,
                c.ImageCount                       AS ImageCount
            FROM %s.ChunksFts
            JOIN %s.Chunks       AS c ON c.Id = ChunksFts.rowid
            JOIN %s.Documents    AS d ON d.Id = c.DocumentId
            LEFT JOIN %s.OutlineNodes AS o ON o.Id = c.OutlineId
            WHERE ChunksFts MATCH $q%s
        """ kbIdx alias alias alias alias docFilter

    /// **Phase 4 (s6-r35)** — vector KNN 의 한 collection (alias) 분 SELECT.
    ///
    /// sqlite-vec 0.1.x KNN syntax — vec0 virtual table 의 MATCH 는 JOIN / 외부 column filter 와 직접 결합 시
    /// "near 'ORDER': syntax error" 와 같이 vtable xBestIndex 가 거부. **subquery 격리 (표준 패턴)** — KNN 결과를
    /// inner SELECT 로 격리 후 outer 에서 평범한 JOIN. JSON 배열 wire (`upsertChunkEmbedding` 와 동일 —
    /// `System.Text.Json` serialize 결과). v.distance ASC (낮을수록 가까움) — RRF rank 산출 시 그대로 사용.
    ///
    /// fileId 한정 (`fileIdFilter`) 시 `c.DocumentId = filterDoc` outer WHERE 박제 — vec0 KNN 은 항상 top-N
    /// 가져온 후 application-level filter (post-filter). collection 작은 시점이라 회귀 0. column 순서 =
    /// buildCollectionSelect 와 동일 (KbIdx, ChunkId, ..., Score=distance).
    let private buildVectorSelect (kbIdx: int) (alias: string) (fileIdFilter: (int * int64) option) (limit: int) : string =
        let docFilter =
            match fileIdFilter with
            | Some (filterKb, filterDoc) when filterKb = kbIdx -> sprintf " WHERE c.DocumentId = %d" filterDoc
            | Some _ -> " WHERE 1 = 0"  // 다른 kbIdx 한정 → 본 collection 결과 0
            | None -> ""
        sprintf """
            SELECT
                %d                                 AS KbIdx,
                c.Id                               AS ChunkId,
                c.DocumentId                       AS DocumentId,
                c.RefLocator                       AS RefLocator,
                c.TokenCount                       AS TokenCount,
                c.Text                             AS Text,
                d.OriginalPath                     AS OriginalPath,
                d.Title                            AS Title,
                o.Label                            AS OutlineLabel,
                knn.distance                       AS Score,
                c.ImageCount                       AS ImageCount
            FROM (
                SELECT ChunkId, distance
                FROM %s.Chunks_Vectors
                WHERE embedding MATCH $emb
                ORDER BY distance
                LIMIT %d
            ) AS knn
            JOIN %s.Chunks       AS c ON c.Id = knn.ChunkId
            JOIN %s.Documents    AS d ON d.Id = c.DocumentId
            LEFT JOIN %s.OutlineNodes AS o ON o.Id = c.OutlineId%s
        """ kbIdx alias limit alias alias alias docFilter

    /// SearchHit.Excerpt 의 token 한도 보장. estimateTokens 가 한도 안이면 그대로,
    /// 초과 시 char 단위 절단 (한국어 char/2 가중 기준 — 대략 token×2 char).
    let private truncateExcerpt (text: string) (maxTokens: int) : string =
        if Chunker.estimateTokens text <= maxTokens then text
        else
            // 한국어 가중 — token 1 ≈ char 2. ASCII 는 char 4.
            // 보수적으로 char 2 기준 cutoff (한국어 보장).
            let limit = maxTokens * 2
            if text.Length <= limit then text
            else text.Substring(0, limit) + "..."

    /// outline → display path. Phase 1 단순화 — 직속 outline 의 Label 만 (no parent walk).
    /// Phase 2 강화 시 OutlineNodes ParentId chain 따라 "root > parent > self" 조립.
    let private outlinePath (label: obj) : string =
        if isNull label || label = (box DBNull.Value) then ""
        else string label

    /// filename (citation 표시용) — OriginalPath 에서 파일명만 추출.
    let private fileNameOf (originalPath: string) : string =
        if String.IsNullOrEmpty originalPath then "" else Path.GetFileName originalPath

    /// **s6-r56 ⑬ (외부 --review)** — reader row → record (tuple→record). 4-tuple `int * int64 * SearchHit * float`
    /// 박제는 caller destructure 시 위치 의존 (의미 모호) → field name 명시 + 미래 확장 정합.
    ///
    /// `RawScore` = BM25 path 의 `bm25()` 음수 / vector path 의 `v.distance` 양수. caller (RRF or BM25-only)
    /// 가 의미 부여 (음수 반전 / RRF rank).
    [<NoComparison; NoEquality>]
    type private RawHitRow = {
        KbIdx: int
        ChunkId: int64
        Hit: SearchHit
        RawScore: float
    }

    let private readHit (reader: SqliteDataReader) (maxExcerptTokens: int) : RawHitRow =
        let kbIdx       = reader.GetInt32(0)
        let chunkId     = reader.GetInt64(1)
        let docId       = reader.GetInt64(2)
        let refLocator  = reader.GetString(3)
        let tokenCount  = reader.GetInt32(4)
        let text        = reader.GetString(5)
        let origPath    = if reader.IsDBNull(6) then "" else reader.GetString(6)
        let title       = if reader.IsDBNull(7) then None else Some (reader.GetString(7))
        let outlineLabel : obj = reader.GetValue(8)
        let rawScore    = reader.GetDouble(9)
        // **s6-r57 C7** — Chunks.ImageCount 박제 (s6-r15 SSOT). 0 = chunk 의 RefLocator 와 매칭된 ImageReference 없음.
        // legacy DB (s6-r8 schema 2 이전) 의 ImageCount column 부재 시는 ensureSchema ALTER 가 DEFAULT 0 박제.
        let imageCount =
            if reader.IsDBNull(10) then 0 else reader.GetInt32(10)

        let displayName =
            match title with
            | Some t when not (String.IsNullOrWhiteSpace t) -> t
            | _ -> fileNameOf origPath

        let hit = {
            FileId      = composeFileId kbIdx docId
            FileName    = displayName
            Ref         = refLocator
            OutlinePath = outlinePath outlineLabel
            Score       = 0.0   // caller 가 finalize
            Excerpt     = truncateExcerpt text maxExcerptTokens
            TokenCount  = tokenCount
            HasImages   = imageCount > 0   // **C7 (s6-r57)** — Chunks.ImageCount > 0 정합 (§3.15.2 Phase 2 박제 활용)
        }
        { KbIdx = kbIdx; ChunkId = chunkId; Hit = hit; RawScore = rawScore }

    /// **Phase 4 (s6-r35)** — BM25 UNION ALL 결과를 dedup key (`<kbIdx>:<ChunkId>`) 순서로 반환.
    /// 반환 = ordered list of (key, hit). rank = list index.
    let private runBm25
        (conn: SqliteConnection)
        (aliases: string array)
        (fileIdFilter: (int * int64) option)
        (queryText: string)
        (limit: int)
        (maxExcerptTokens: int)
        : (string * SearchHit) list =
        let selects =
            aliases
            |> Array.mapi (fun i alias -> buildCollectionSelect i alias fileIdFilter)
        let unionSql =
            if selects.Length = 1 then selects.[0]
            else selects |> String.concat "\nUNION ALL\n"
        let sql =
            sprintf """
                %s
                ORDER BY Score ASC
                LIMIT $limit
            """ unionSql
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.Parameters.AddWithValue("$q", buildFtsQuery queryText) |> ignore
        cmd.Parameters.AddWithValue("$limit", limit) |> ignore
        let acc = ResizeArray<string * SearchHit>()
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let row = readHit reader maxExcerptTokens
            // FTS5 bm25() 는 음수 (낮을수록 hit 강도). caller 통념 양수 정합 위해 부호 반전 (review M6).
            let finalized = { row.Hit with Score = -row.RawScore }
            let key = sprintf "%d:%d" row.KbIdx row.ChunkId
            acc.Add (key, finalized)
        acc |> List.ofSeq

    /// **Phase 4 (s6-r35)** — vector KNN UNION ALL 결과를 dedup key 순서 (distance ASC) 로 반환.
    /// 반환 = ordered list of (key, hit). rank = list index.
    let private runVector
        (conn: SqliteConnection)
        (aliases: string array)
        (fileIdFilter: (int * int64) option)
        (queryVector: float32[])
        (perAliasLimit: int)
        (maxExcerptTokens: int)
        : (string * SearchHit) list =
        // **B4 (s6-r85, 15-reviewer Major)** — vec0 wire format SSOT 통합. SqliteStore.buildVec0WireFormat
        // 단일 helper 통과 (이전 박제는 read 가 JsonSerializer, write 가 StringBuilder — drift risk).
        let embJson = SqliteStore.buildVec0WireFormat queryVector
        let selects =
            aliases
            |> Array.mapi (fun i alias -> buildVectorSelect i alias fileIdFilter perAliasLimit)
        let unionSql =
            if selects.Length = 1 then selects.[0]
            else selects |> String.concat "\nUNION ALL\n"
        // multi-collection UNION 후 전체 distance ASC 재정렬 → top-N (perAliasLimit × aliases 의 union 안에서).
        let totalLimit = perAliasLimit * (max 1 aliases.Length)
        let sql =
            sprintf """
                %s
                ORDER BY Score ASC
                LIMIT %d
            """ unionSql totalLimit
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.Parameters.AddWithValue("$emb", embJson) |> ignore
        let acc = ResizeArray<string * SearchHit>()
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let row = readHit reader maxExcerptTokens
            // distance = sqlite-vec 의 L2 또는 cosine — 낮을수록 hit. RRF 단계에서 rank 만 사용하므로 Score 자체는
            // caller 미사용. 임시 박제 (이후 RRF 단계에서 finalize).
            let withDistance = { row.Hit with Score = row.RawScore }
            let key = sprintf "%d:%d" row.KbIdx row.ChunkId
            acc.Add (key, withDistance)
        acc |> List.ofSeq

    /// **Phase 4 (s6-r35)** — Reciprocal Rank Fusion. BM25 결과 + vector 결과를 RRF score 로 통합.
    ///
    /// `RRF_score(key) = sum_over_systems(1 / (k_rrf + rank_in_system(key)))` — rank 0-based.
    /// 한 system 에만 등장한 key 도 다른 system 의 rank 항만 누락. SearchHit metadata 는 두 system 중
    /// 먼저 등장 (BM25 우선 — text snippet 일관성). 반환 = topK 만 trim, RRF score 가 최종 SearchHit.Score.
    let private rrfMerge
        (bm25: (string * SearchHit) list)
        (vector: (string * SearchHit) list)
        (topK: int)
        : (string * SearchHit) list * bool =
        // **B-R12 (perf sweep, s6-r71+)** — Map immutable copy (O(log N) per add) → Dictionary mutable (O(1)).
        // N=topK*HybridFetchMultiplier (수십~수백) 의 hot path. BM25 metadata 우선 진입 정합 유지 (TryGetValue
        // 분기로 기존 hit 보존). 정렬은 마지막 1회 — F# Seq.sortByDescending → List 변환.
        let acc = System.Collections.Generic.Dictionary<string, SearchHit * float>()
        let addRank (results: (string * SearchHit) list) =
            results
            |> List.iteri (fun rank (key, hit) ->
                let contribution = 1.0 / (RrfK + float rank)
                match acc.TryGetValue key with
                | true, (existing, score) ->
                    // 기존 hit metadata 보존 (BM25 우선 진입 — excerpt / displayName 일관).
                    acc.[key] <- (existing, score + contribution)
                | false, _ ->
                    acc.[key] <- (hit, contribution))
        addRank bm25
        addRank vector
        // RRF score DESC 정렬.
        let merged =
            acc
            |> Seq.map (fun kv ->
                let key = kv.Key
                let hit, score = kv.Value
                key, { hit with Score = score })
            |> Seq.sortByDescending (fun (_, h) -> h.Score)
            |> List.ofSeq
        let totalFetched = merged.Length
        let moreAvailable = totalFetched > topK
        let trimmed = merged |> List.truncate topK
        trimmed, moreAvailable

    /// active 셋 union 검색 (§3.10 / §4.4 r4).
    ///
    /// `aliases` = ATTACH 된 collection 의 alias 배열 (kb0, kb1, ..., kbN-1, N ≤ §3.18.2 limit). 길이 0 = empty result.
    /// `query.FileId` Some 시 해당 collection 의 해당 docId 한정. 다른 collection 은 0-hit.
    ///
    /// **`embedderOpt` (Phase 4 s6-r35)**:
    /// - `None` → BM25 lexical-only path (기존 동작, Phase 1 과 동일). Chunks_Vectors 가 비어 있어도 안전.
    /// - `Some embedder` → query → `embedder.GenerateAsync` 로 1 vector 산출 + vector KNN UNION + BM25 UNION
    ///   → RRF (k_rrf=60) fusion → top-K. Chunks_Vectors 가 비어 있어도 BM25 결과는 그대로 (vector contribution 0).
    /// 정렬: BM25-only path = BM25 ASC, hybrid path = RRF score DESC.
    ///
    /// **`ct` (s6-r36 P4-C.0)** — caller cancel 전파. hybrid path 의 embedder.GenerateAsync 가 backend HTTP
    /// roundtrip (Ollama 등 수십~수백 ms) 보유. client (MCP / Promaker) cancel 시 즉시 OperationCanceledException
    /// 전파 + handler thread 회수. BM25-only path 에서도 SQL execute 자체는 ct 미사용이나 caller 흐름 호환.
    /// `Query` record 자체는 wire-serializable 의도 (MCP JSON schema) 라 ct 는 별 parameter (default 박제 없음).
    let search
        (conn: SqliteConnection)
        (aliases: string array)
        (embedderOpt: IEmbeddingProvider option)
        (query: Query)
        (maxExcerptTokens: int)
        (ct: CancellationToken)
        : SearchResults =

        if aliases.Length = 0 || String.IsNullOrWhiteSpace query.Text then
            { Results = [||]; MoreAvailable = false; Hint = None }
        else
            // fileId Some 시 parse 실패는 *명시 한정 검색의 의도와 다른 결과* 위험 → silent fallback 금지 (review M3).
            // 사용자 의도 (특정 문서 한정) 보존을 위해 빈 결과 + warn log.
            let fileIdFilter, fileIdInvalid =
                match query.FileId with
                | None -> None, false
                | Some fid ->
                    match parseFileId fid with
                    | Some parsed -> Some parsed, false
                    | None ->
                        Log.lighthouse.Warn(
                            sprintf "Searcher.search: fileId parse 실패 — '%s' (format = '<kbIdx>:<docId>')" fid)
                        None, true

            if fileIdInvalid then
                { Results = [||]; MoreAvailable = false; Hint = Some "invalid fileId" }
            else

            let topK = max 1 query.TopK

            match embedderOpt with
            | None ->
                // BM25-only path — 기존 동작 (over-fetch +1 로 MoreAvailable 판별).
                ct.ThrowIfCancellationRequested()
                let fetchLimit = topK + 1
                let bm25Hits = runBm25 conn aliases fileIdFilter query.Text fetchLimit maxExcerptTokens
                let totalFetched = bm25Hits.Length
                let moreAvailable = totalFetched > topK
                let trimmed =
                    bm25Hits
                    |> List.truncate topK
                    |> List.map snd
                    |> List.toArray
                let hint =
                    if trimmed.Length = 0 then Some "synonym retry"
                    else None
                { Results = trimmed; MoreAvailable = moreAvailable; Hint = hint }
            | Some embedder ->
                // hybrid path — BM25 top-N + vector top-N (N = topK*HybridFetchMultiplier) → RRF fusion → top-K.
                ct.ThrowIfCancellationRequested()
                let perSystemLimit = topK * HybridFetchMultiplier
                let bm25Hits = runBm25 conn aliases fileIdFilter query.Text perSystemLimit maxExcerptTokens
                // query embedding 생성 — 단일 input. Task 동기 wait — caller (server / Promaker MCP handler) 가
                // sync API 라 정합 (s6-r34 Indexer.dispatchEmbeddings 와 동일 패턴). s6-r36 P4-C.0: ct 전파.
                //
                // **B2 반론 (s6-r85, 15-reviewer Major)** — lib API 전체 async transformation 은 별 phase 의무
                // (Searcher.search + Indexer.ingestFile + KnowledgeBase facade + 모든 caller chain 수십 파일 변경).
                // 현재 caller (server CollectionEndpoints / Promaker MCP host) 가 모두 task wrap path 안에서 호출 —
                // ASP.NET request handler 의 sync-over-async deadlock 은 ConfigureAwait(false) + Task.Run wrap 로
                // 우회 (caller 책임). 본 lib 의 sync API 박제는 의식적 backend-agnostic 결정.
                let task = embedder.GenerateAsync([| query.Text |], ct)
                let queryVectors = task.GetAwaiter().GetResult()
                if queryVectors.Length <> 1 then
                    raise (InvalidOperationException(
                        sprintf "embedder.GenerateAsync 결함 — query vector 수=%d ≠ 1" queryVectors.Length))
                let queryVector = queryVectors.[0]
                if queryVector.Length <> embedder.Dimension then
                    raise (InvalidOperationException(
                        sprintf "embedder vector dim 결함 — vector.Length=%d ≠ embedder.Dimension=%d"
                            queryVector.Length embedder.Dimension))
                let vectorHits =
                    runVector conn aliases fileIdFilter queryVector perSystemLimit maxExcerptTokens
                let merged, moreAvailable = rrfMerge bm25Hits vectorHits topK
                let trimmed = merged |> List.map snd |> List.toArray
                let hint =
                    if trimmed.Length = 0 then Some "synonym retry"
                    else None
                { Results = trimmed; MoreAvailable = moreAvailable; Hint = hint }

    /// 등록 문서 목록 — `attachment_list` (§3.10) 의 lib 측 구현.
    /// active 셋 union — 각 collection 의 Documents 전체. fileId 형식은 search 결과와 동일.
    let listDocuments (conn: SqliteConnection) (aliases: string array) : (string * string * FileKind * int option) array =
        if aliases.Length = 0 then [||]
        else
            let selects =
                aliases
                |> Array.mapi (fun i alias ->
                    sprintf "SELECT %d AS KbIdx, Id, OriginalPath, DocType, PageOrSheetCnt FROM %s.Documents" i alias)
            let unionSql = selects |> String.concat "\nUNION ALL\n"

            // SqliteStore.docTypeToString 의 역변환. DU case 추가 시 양쪽 동기 갱신.
            let parseDocType (s: string) : FileKind =
                match s with
                | "pdf"   -> Pdf
                | "docx"  -> Docx
                | "pptx"  -> Pptx
                | "xlsx"  -> Xlsx
                | "txt"   -> Text
                | "md"    -> Markdown
                | "image" -> FileKind.Image   // Task 7 (r17) — standalone image. RefUnit.Image 동명 disambiguation.
                | other   -> Unsupported other

            use cmd = conn.CreateCommand()
            cmd.CommandText <- unionSql
            use reader = cmd.ExecuteReader()
            let acc = ResizeArray<string * string * FileKind * int option>()
            while reader.Read() do
                let kbIdx = reader.GetInt32(0)
                let docId = reader.GetInt64(1)
                let path  = reader.GetString(2)
                let kind  = parseDocType (reader.GetString(3))
                let pages = if reader.IsDBNull(4) then None else Some (reader.GetInt32(4))
                acc.Add (composeFileId kbIdx docId, path, kind, pages)
            acc.ToArray()

    /// outline 트리 — `attachment_outline(fileId)` (§3.10).
    /// 본 함수는 raw rows 반환 — 트리 조립은 호출자 책임.
    let getOutline (conn: SqliteConnection) (aliases: string array) (fileId: string) : (int64 * int64 option * int * OutlineNodeType * string * string) array =
        match parseFileId fileId with
        | None -> [||]
        | Some (kbIdx, _) when kbIdx < 0 || kbIdx >= aliases.Length -> [||]
        | Some (kbIdx, docId) ->
            let alias = aliases.[kbIdx]
            let parseNodeType (s: string) : OutlineNodeType =
                // qualified case (SqliteStore.outlineNodeTypeToString 와 동일 사유 — RefUnit case 와 이름 충돌).
                match s with
                | "section" -> OutlineNodeType.Section
                | "page"    -> OutlineNodeType.Page
                | "sheet"   -> OutlineNodeType.Sheet
                | "slide"   -> OutlineNodeType.Slide
                | _         -> OutlineNodeType.Heading
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sprintf """
                SELECT Id, ParentId, Ordinal, NodeType, Label, RefLocator
                FROM %s.OutlineNodes
                WHERE DocumentId = $doc
                ORDER BY Ordinal
            """ alias
            cmd.Parameters.AddWithValue("$doc", docId) |> ignore
            use reader = cmd.ExecuteReader()
            let acc = ResizeArray<int64 * int64 option * int * OutlineNodeType * string * string>()
            while reader.Read() do
                let id      = reader.GetInt64(0)
                let parent  = if reader.IsDBNull(1) then None else Some (reader.GetInt64(1))
                let ord     = reader.GetInt32(2)
                let nodeT   = parseNodeType (reader.GetString(3))
                let label   = reader.GetString(4)
                let refLoc  = reader.GetString(5)
                acc.Add (id, parent, ord, nodeT, label, refLoc)
            acc.ToArray()

    /// Phase 2 task D-iv (s6-r20) — 특정 ref 의 image references (`attachment_read` includeImages mode).
    ///
    /// 반환 = (ImageHash, MimeType, StoredPath, CaptionText option) array. Ordinal asc 정렬.
    /// fileId / refLocator 가 해당 collection 에서 빈 결과 → 빈 배열.
    ///
    /// caller (Ds2.LightHouseService.AttachmentTools) 가 StoredPath → File.ReadAllBytes + size 정책 검사 후
    /// base64 inline 박제 또는 caption_only 강등 결정. 본 함수는 metadata 만 — bytes IO 책임 분리 정합.
    let readImagesByRef
        (conn: SqliteConnection)
        (aliases: string array)
        (fileId: string)
        (refLocator: string)
        : (string * string * string * string option) array =
        match parseFileId fileId with
        | None -> [||]
        | Some (kbIdx, _) when kbIdx < 0 || kbIdx >= aliases.Length -> [||]
        | Some (kbIdx, docId) ->
            let alias = aliases.[kbIdx]
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sprintf """
                SELECT ic.ImageHash, ic.MimeType, ic.StoredPath, ic.CaptionText
                FROM %s.ImageReferences ir
                JOIN %s.ImageCache ic ON ir.ImageHash = ic.ImageHash
                WHERE ir.DocumentId = $doc AND ir.RefLocator = $ref
                ORDER BY ir.Ordinal
            """ alias alias
            cmd.Parameters.AddWithValue("$doc", docId) |> ignore
            cmd.Parameters.AddWithValue("$ref", refLocator) |> ignore
            use reader = cmd.ExecuteReader()
            let acc = ResizeArray<string * string * string * string option>()
            while reader.Read() do
                let hash = reader.GetString 0
                let mime = if reader.IsDBNull 1 then "" else reader.GetString 1
                let path = if reader.IsDBNull 2 then "" else reader.GetString 2
                let caption = if reader.IsDBNull 3 then None else Some (reader.GetString 3)
                acc.Add (hash, mime, path, caption)
            acc.ToArray()

    /// 특정 ref (저장형) 의 chunk 본문 — `attachment_read(fileId, ref)` (§3.10).
    /// 한 ref 안 여러 chunk → ordinal 순서대로 concat. token 한도 절단.
    let readByRef
        (conn: SqliteConnection)
        (aliases: string array)
        (fileId: string)
        (refLocator: string)
        (maxExcerptTokens: int)
        : string =
        match parseFileId fileId with
        | None -> ""
        | Some (kbIdx, _) when kbIdx < 0 || kbIdx >= aliases.Length -> ""
        | Some (kbIdx, docId) ->
            let alias = aliases.[kbIdx]
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sprintf """
                SELECT Text FROM %s.Chunks
                WHERE DocumentId = $doc AND RefLocator = $ref
                ORDER BY Ordinal
            """ alias
            cmd.Parameters.AddWithValue("$doc", docId) |> ignore
            cmd.Parameters.AddWithValue("$ref", refLocator) |> ignore
            use reader = cmd.ExecuteReader()
            let sb = StringBuilder()
            while reader.Read() do
                if sb.Length > 0 then sb.Append("\n\n") |> ignore
                sb.Append(reader.GetString(0)) |> ignore
            truncateExcerpt (sb.ToString()) maxExcerptTokens
