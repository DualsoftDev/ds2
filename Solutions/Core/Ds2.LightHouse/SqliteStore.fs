namespace Ds2.LightHouse

open System
open System.Globalization
open System.IO
open System.Threading
open Microsoft.Data.Sqlite

/// LightHouse KB 의 schema/parser 버전 식별자 (todo-lighthouse-kb-index.md §3.17).
///
/// `Meta.indexer_version` 과 비교하여 drift 시 자동 재색인 트리거 (shadow rebuild).
/// schema (§3.12) / Chunker / Extractor 의 *결과물* 변경 시 bump. SQL 비호환 변경은 SchemaVersion 도 동반 bump.
[<RequireQualifiedAccess>]
module IndexerVersion =
    // SSOT: 본 `Current` literal 은 module 의 첫 [<Literal>] 위치 유지 의무.
    // `Apps/Promaker/scripts/check-paired-release.ps1` (s5d-r0 박제) 가 source regex
    // 로 본 literal 을 추출 + service config 의 indexerVersionRange 정합 검증.
    // 다른 literal (SchemaVersion / Tokenizer) 을 Current 앞으로 옮기면 paired-release
    // 검증이 잘못된 값을 잡아 exit 1 (false positive) 가 됨. 추가 시 Current 뒤에 둘 것.
    [<Literal>]
    let Current = "1.1.0"

    [<Literal>]
    let SchemaVersion = "2"

    [<Literal>]
    let Tokenizer = "trigram"


/// SQLite 저장소 — DB open / schema 초기화 / CRUD primitives (§3.12 + §3.17).
///
/// 책임 경계: connection / schema / Meta key-value / Documents / OutlineNodes / Chunks CRUD primitives 만.
/// ingest orchestration (Extract → Chunk → Store) 은 `Indexer` 책임. 검색 (BM25 / UNION) 은 `Searcher` 책임.
///
/// SSOT: 본 모듈이 DB 연결 open 의 *단일 진입점*. 다른 곳에서 `new SqliteConnection` 금지 (§3.17 PRAGMA 누락 회귀 방지).
[<RequireQualifiedAccess>]
module SqliteStore =

    /// KB 저장 폴더 이름 (§3.9 r4). 사용자 폴더 안에 자동 생성되는 hidden subfolder.
    [<Literal>]
    let KbFolderName = ".lighthouse-kb"

    /// index.db 파일명 (§3.9).
    [<Literal>]
    let DbFileName = "index.db"

    /// shadow rebuild 작업 DB 파일명 (§3.17). 완료 후 atomic rename → DbFileName.
    [<Literal>]
    let ShadowDbFileName = "index.db.new"

    /// chunk insert batch commit 단위 (§3.17). WAL 크기 / cancellation 응답성 / 메모리 균형.
    [<Literal>]
    let DefaultBatchSize = 500

    /// SQLite default `SQLITE_MAX_ATTACHED` (§3.18.2 / §4.4 r4). 초과 시 ATTACH 실패.
    [<Literal>]
    let MaxAttachedDbs = 10

    /// collection root → `<root>/.lighthouse-kb/`.
    let kbDir (collectionRoot: string) : string =
        Path.Combine(collectionRoot, KbFolderName)

    /// collection root → `<root>/.lighthouse-kb/index.db`.
    let dbPath (collectionRoot: string) : string =
        Path.Combine(kbDir collectionRoot, DbFileName)

    /// collection root → `<root>/.lighthouse-kb/index.db.new` (shadow rebuild 작업용).
    let shadowDbPath (collectionRoot: string) : string =
        Path.Combine(kbDir collectionRoot, ShadowDbFileName)

    /// collection 폴더의 *쓰기 가능 여부* probe (§3.9 r4 read-only collection 판별).
    ///
    /// 폴더 안에 임시 파일 생성/삭제 시도. 실패 시 read-only 로 간주. NAS / 공유 폴더 / 권한 부족 등.
    /// 본 함수는 collection root 폴더에 대해 호출 (`.lighthouse-kb/` 가 아직 없을 수 있음).
    let checkWritable (collectionRoot: string) : bool =
        if not (Directory.Exists collectionRoot) then false
        else
            let probe = Path.Combine(collectionRoot, ".lighthouse-kb.probe-" + Guid.NewGuid().ToString("N"))
            try
                File.WriteAllText(probe, "")
                File.Delete probe
                true
            with
            | :? UnauthorizedAccessException -> false
            | :? IOException -> false

    let private docTypeToString (kind: FileKind) : string =
        match kind with
        | Pdf -> "pdf"
        | Docx -> "docx"
        | Pptx -> "pptx"
        | Xlsx -> "xlsx"
        | Text -> "txt"
        | Markdown -> "md"
        | Unsupported _ -> "unknown"

    let private outlineNodeTypeToString (t: OutlineNodeType) : string =
        // qualified case — `Slide` / `Sheet` 가 RefUnit 과 동일 이름이라 F# 가 마지막 정의 (RefUnit) 로 해석.
        // OutlineNodeType.* 명시로 disambiguation.
        match t with
        | OutlineNodeType.Section -> "section"
        | OutlineNodeType.Page    -> "page"
        | OutlineNodeType.Sheet   -> "sheet"
        | OutlineNodeType.Slide   -> "slide"
        | OutlineNodeType.Heading -> "heading"

    /// SQL DDL — §3.12 Phase 1 schema. `IF NOT EXISTS` 로 idempotent.
    /// FTS5 trigram (한국어 필수, §3.7). FTS5 external content mirror (본문은 Chunks 에만 1부).
    /// trigger 3종 (AI/AD/AU) 으로 ChunksFts sync.
    let private schemaSql = """
CREATE TABLE IF NOT EXISTS Documents (
    Id              INTEGER PRIMARY KEY,
    FileHash        TEXT NOT NULL UNIQUE,
    OriginalPath    TEXT NOT NULL,
    DocType         TEXT NOT NULL,
    SizeBytes       INTEGER NOT NULL,
    PageOrSheetCnt  INTEGER,
    Title           TEXT,
    SummaryText     TEXT,
    IndexerVersion  TEXT NOT NULL,
    IngestedAt      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS OutlineNodes (
    Id          INTEGER PRIMARY KEY,
    DocumentId  INTEGER NOT NULL REFERENCES Documents(Id) ON DELETE CASCADE,
    ParentId    INTEGER REFERENCES OutlineNodes(Id) ON DELETE CASCADE,
    Ordinal     INTEGER NOT NULL,
    NodeType    TEXT NOT NULL,
    Label       TEXT NOT NULL,
    RefLocator  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Outline_Doc    ON OutlineNodes(DocumentId);
CREATE INDEX IF NOT EXISTS IX_Outline_Parent ON OutlineNodes(ParentId);

CREATE TABLE IF NOT EXISTS Chunks (
    Id          INTEGER PRIMARY KEY,
    DocumentId  INTEGER NOT NULL REFERENCES Documents(Id) ON DELETE CASCADE,
    OutlineId   INTEGER REFERENCES OutlineNodes(Id) ON DELETE SET NULL,
    RefLocator  TEXT NOT NULL,
    Ordinal     INTEGER NOT NULL,
    TokenCount  INTEGER NOT NULL,
    Text        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Chunks_Doc     ON Chunks(DocumentId);
CREATE INDEX IF NOT EXISTS IX_Chunks_Outline ON Chunks(OutlineId);

CREATE VIRTUAL TABLE IF NOT EXISTS ChunksFts USING fts5(
    Text,
    content='Chunks', content_rowid='Id',
    tokenize='trigram'
);

CREATE TRIGGER IF NOT EXISTS Chunks_AI AFTER INSERT ON Chunks BEGIN
    INSERT INTO ChunksFts(rowid, Text) VALUES (new.Id, new.Text);
END;
CREATE TRIGGER IF NOT EXISTS Chunks_AD AFTER DELETE ON Chunks BEGIN
    INSERT INTO ChunksFts(ChunksFts, rowid, Text) VALUES ('delete', old.Id, old.Text);
END;
CREATE TRIGGER IF NOT EXISTS Chunks_AU AFTER UPDATE ON Chunks BEGIN
    INSERT INTO ChunksFts(ChunksFts, rowid, Text) VALUES ('delete', old.Id, old.Text);
    INSERT INTO ChunksFts(rowid, Text) VALUES (new.Id, new.Text);
END;

CREATE TABLE IF NOT EXISTS Meta (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

-- ── Phase 2 (s6-r8): 이미지 인프라 schema (parent §3.12 의 주석 처리 블록 활성) ──
-- backward-compat: 신규 테이블만 IF NOT EXISTS, Chunks.ImageCount ALTER 는 ensureSchema 의 분기 처리.
-- ImageCache: cross-document 공유 cache. PK = sha256 (ImageHash).
-- ImageReferences: 문서 안 image 사용 위치 (page/slide 박제). PK = 복합 4 키 (parent §3.12 결함 5항 1).
CREATE TABLE IF NOT EXISTS ImageCache (
    ImageHash    TEXT PRIMARY KEY,
    StoredPath   TEXT NOT NULL,
    MimeType     TEXT,
    Width        INTEGER,
    Height       INTEGER,
    CaptionText  TEXT,
    CaptionAt    TEXT,
    CaptionModel TEXT
);

CREATE TABLE IF NOT EXISTS ImageReferences (
    DocumentId INTEGER NOT NULL REFERENCES Documents(Id) ON DELETE CASCADE,
    ChunkId    INTEGER REFERENCES Chunks(Id) ON DELETE SET NULL,
    ImageHash  TEXT NOT NULL REFERENCES ImageCache(ImageHash),
    RefLocator TEXT NOT NULL,
    Ordinal    INTEGER NOT NULL,
    PRIMARY KEY (DocumentId, ImageHash, RefLocator, Ordinal)
);
CREATE INDEX IF NOT EXISTS IX_ImgRef_Chunk ON ImageReferences(ChunkId);
"""

    /// PRAGMA SSOT (§3.17). DB open 직후 1회 실행. read-only mode 도 동일 set (writeMode 와 무관).
    let private applyPragmas (conn: SqliteConnection) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous  = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = ON;
        """
        cmd.ExecuteNonQuery() |> ignore

    /// SQLite connection 단일 진입점 (§3.17 SSOT). 다른 곳에서 직접 `new SqliteConnection` 금지.
    ///
    /// `readOnly = true` 시 `Mode=ReadOnly` (§3.9 r4 read-only collection). 부모 디렉토리 미존재 시:
    ///   - read-write: 폴더 자동 생성 (Indexer 가 .lighthouse-kb/ 최초 생성)
    ///   - read-only: open 실패 그대로 throw (caller fail-fast)
    let openConnection (path: string) (readOnly: bool) : SqliteConnection =
        if not readOnly then
            let dir = Path.GetDirectoryName path
            if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
                Directory.CreateDirectory dir |> ignore

        let csb = SqliteConnectionStringBuilder()
        csb.DataSource <- path
        csb.Mode <- if readOnly then SqliteOpenMode.ReadOnly else SqliteOpenMode.ReadWriteCreate
        csb.Cache <- SqliteCacheMode.Shared
        let conn = new SqliteConnection(csb.ToString())
        conn.Open()
        applyPragmas conn
        conn

    /// SQLite 의 `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` 미지원 — `PRAGMA table_info` 로 idempotent 분기.
    /// table 안에 column 가 이미 있으면 no-op, 없으면 `ALTER TABLE` 실행 (Phase 2 schema bump 정합).
    let private ensureColumn
        (conn: SqliteConnection)
        (table: string)
        (column: string)
        (ddl: string)
        : unit =
        use probe = conn.CreateCommand()
        probe.CommandText <- sprintf "PRAGMA table_info(%s)" table
        use reader = probe.ExecuteReader()
        let mutable found = false
        while reader.Read() do
            let name = reader.GetString(1)
            if String.Equals(name, column, StringComparison.OrdinalIgnoreCase) then
                found <- true
        reader.Close()
        if not found then
            use alter = conn.CreateCommand()
            alter.CommandText <- ddl
            alter.ExecuteNonQuery() |> ignore

    /// schema (§3.12) 초기화 — idempotent. 신규 DB / 기존 DB 모두 안전.
    /// Phase 2 (s6-r8) — `Chunks.ImageCount` ALTER 도 동반 (backward-compat, DEFAULT 0).
    /// Meta key (`schema_version`/`indexer_version`/`tokenizer`/`created_at`) 도 동시 기록.
    let ensureSchema (conn: SqliteConnection) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- schemaSql
        cmd.ExecuteNonQuery() |> ignore
        // Phase 2 ALTER (SQLite IF NOT EXISTS 미지원) — Phase 1 색인 DB 에서도 안전 forward-compat.
        ensureColumn conn "Chunks" "ImageCount"
            "ALTER TABLE Chunks ADD COLUMN ImageCount INTEGER NOT NULL DEFAULT 0"

    /// Meta key-value get. 미존재 시 None.
    let getMeta (conn: SqliteConnection) (key: string) : string option =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT Value FROM Meta WHERE Key = $key"
        cmd.Parameters.AddWithValue("$key", key) |> ignore
        let result = cmd.ExecuteScalar()
        if isNull result || result = (box DBNull.Value) then None
        else Some (string result)

    /// Meta key-value upsert (INSERT OR REPLACE).
    let setMeta (conn: SqliteConnection) (key: string) (value: string) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "INSERT INTO Meta(Key, Value) VALUES($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = $v"
        cmd.Parameters.AddWithValue("$k", key) |> ignore
        cmd.Parameters.AddWithValue("$v", value) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// 현재 코드의 IndexerVersion / SchemaVersion / Tokenizer 를 Meta 에 stamp.
    /// ensureSchema 직후 (신규 DB) 또는 shadow rebuild 완료 시점에 호출.
    let stampVersion (conn: SqliteConnection) =
        setMeta conn "indexer_version" IndexerVersion.Current
        setMeta conn "schema_version"  IndexerVersion.SchemaVersion
        setMeta conn "tokenizer"       IndexerVersion.Tokenizer
        match getMeta conn "created_at" with
        | None ->
            setMeta conn "created_at" (DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
        | Some _ -> ()

    /// 코드의 현재 IndexerVersion 과 DB 의 Meta.indexer_version 불일치 여부 (§3.17).
    /// true 면 호출자 (Indexer) 가 shadow rebuild 트리거.
    let needsRebuild (conn: SqliteConnection) : bool =
        match getMeta conn "indexer_version" with
        | Some v -> v <> IndexerVersion.Current
        | None -> true

    /// shadow rebuild 의 atomic swap — `index.db.new` → `index.db` (§3.17).
    ///
    /// 호출 전제: 두 DB connection 모두 close. Windows 의 File.Replace 가 atomic rename + 백업.
    /// 백업 (`.bak`) 은 swap 성공 후 즉시 삭제 — todo §3.17 미명시이나 색인이 idempotent (FileHash 검증) 이라 백업 보유 의미 적음.
    /// 사용자 폴더 안 잔존 파일 최소화 우선. 색인 도중 swap 실패 시점에는 .bak 가 이미 생성되어 있어
    /// File.Replace 자체가 atomic 보장 — 부분 상태 노출 없음. swap 실패 → reraise → caller 가 retry.
    let swapShadow (collectionRoot: string) =
        let target = dbPath collectionRoot
        let shadow = shadowDbPath collectionRoot
        if not (File.Exists shadow) then
            raise (InvalidOperationException(sprintf "shadow DB 미존재 — %s" shadow))
        if File.Exists target then
            let backup = target + ".bak"
            File.Replace(shadow, target, backup, ignoreMetadataErrors = true)
            if File.Exists backup then File.Delete backup
        else
            File.Move(shadow, target)

    /// FileHash 로 기존 Document 검색 (§3.8 idempotent ingest).
    let findDocumentByHash (conn: SqliteConnection) (hash: string) : int64 option =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT Id FROM Documents WHERE FileHash = $h"
        cmd.Parameters.AddWithValue("$h", hash) |> ignore
        let r = cmd.ExecuteScalar()
        if isNull r || r = (box DBNull.Value) then None
        else Some (Convert.ToInt64 r)

    /// documents.Id → (OriginalPath, FileHash, SizeBytes). 미존재 시 None.
    /// Phase S4 file serving 의 SQL layer — `KnowledgeBase.lookupDocument` facade 가 호출.
    /// review IM-6 정합 — caller (Ds2.LightHouseService) 가 SqliteStore 직접 참조 우회 통합.
    let findDocumentById (conn: SqliteConnection) (documentId: int64) : (string * string * int64) option =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT OriginalPath, FileHash, SizeBytes FROM Documents WHERE Id = $id"
        cmd.Parameters.AddWithValue("$id", documentId) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            let path = reader.GetString 0
            let hash = reader.GetString 1
            let size = reader.GetInt64 2
            Some (path, hash, size)
        else None

    /// 한 문서 + 그 종속 행 (OutlineNodes / Chunks / ChunksFts) 전부 삭제. CASCADE / trigger 가 sync.
    ///
    /// 현재 Indexer 의 정상 경로는 idempotent skip — 본 함수 미사용. 향후 매뉴얼 purge (사용자가 collection 안 파일 삭제)
    /// 및 §4.8 lib unit test 의 cleanup 진입점으로 보존 (review m1 — dead code 아님).
    let deleteDocument (conn: SqliteConnection) (documentId: int64) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "DELETE FROM Documents WHERE Id = $id"
        cmd.Parameters.AddWithValue("$id", documentId) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Document insert. 반환 = 새 Id.
    let insertDocument
        (conn: SqliteConnection)
        (fileHash: string)
        (originalPath: string)
        (docType: FileKind)
        (sizeBytes: int64)
        (pageOrSheetCnt: int option)
        (title: string option)
        : int64 =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            INSERT INTO Documents(FileHash, OriginalPath, DocType, SizeBytes, PageOrSheetCnt, Title, SummaryText, IndexerVersion, IngestedAt)
            VALUES($hash, $path, $type, $size, $pages, $title, NULL, $ver, $at);
            SELECT last_insert_rowid();
        """
        cmd.Parameters.AddWithValue("$hash",  fileHash) |> ignore
        cmd.Parameters.AddWithValue("$path",  originalPath) |> ignore
        cmd.Parameters.AddWithValue("$type",  docTypeToString docType) |> ignore
        cmd.Parameters.AddWithValue("$size",  sizeBytes) |> ignore
        cmd.Parameters.AddWithValue("$pages", pageOrSheetCnt |> Option.map box |> Option.defaultValue (box DBNull.Value)) |> ignore
        cmd.Parameters.AddWithValue("$title", title |> Option.map box |> Option.defaultValue (box DBNull.Value)) |> ignore
        cmd.Parameters.AddWithValue("$ver",   IndexerVersion.Current) |> ignore
        cmd.Parameters.AddWithValue("$at",    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)) |> ignore
        Convert.ToInt64 (cmd.ExecuteScalar())

    /// 한 문서의 outline tree 삽입 — `ParentIndex` 로 parent linking. 반환 = list index → DB Id 매핑 배열.
    ///
    /// 입력 array 의 순서대로 INSERT — 따라서 parent 가 child 보다 앞 (DAG 위배 시 ParentId = NULL 폴백).
    /// Extractor 가 `outline.Add` 순서대로 인덱스 부여하므로 자연스럽게 parent-first.
    let insertOutlineTree
        (conn: SqliteConnection)
        (documentId: int64)
        (nodes: ExtractedOutlineNode array)
        : int64 array =
        let ids = Array.zeroCreate<int64> nodes.Length
        for i = 0 to nodes.Length - 1 do
            let n = nodes.[i]
            use cmd = conn.CreateCommand()
            cmd.CommandText <- """
                INSERT INTO OutlineNodes(DocumentId, ParentId, Ordinal, NodeType, Label, RefLocator)
                VALUES($doc, $parent, $ord, $type, $label, $ref);
                SELECT last_insert_rowid();
            """
            let parentParam : obj =
                match n.ParentIndex with
                | Some idx when idx >= 0 && idx < i -> box ids.[idx]
                | _ -> box DBNull.Value
            cmd.Parameters.AddWithValue("$doc",    documentId) |> ignore
            cmd.Parameters.AddWithValue("$parent", parentParam) |> ignore
            cmd.Parameters.AddWithValue("$ord",    n.Ordinal) |> ignore
            cmd.Parameters.AddWithValue("$type",   outlineNodeTypeToString n.NodeType) |> ignore
            cmd.Parameters.AddWithValue("$label",  n.Label) |> ignore
            cmd.Parameters.AddWithValue("$ref",    n.RefLocator) |> ignore
            ids.[i] <- Convert.ToInt64 (cmd.ExecuteScalar())
        ids

    /// chunk 배열 batch insert (§3.17 500/commit + CancellationToken).
    ///
    /// 외부 transaction 안에서 호출 가능 — 본 함수가 직접 transaction 열지 않음. caller 가 결정.
    /// trigger 가 ChunksFts 자동 sync — 별도 mirror INSERT 불요.
    let insertChunks
        (conn: SqliteConnection)
        (documentId: int64)
        (outlineDbIds: int64 array)
        (chunks: ExtractedChunk array)
        (batchSize: int)
        (ct: CancellationToken) =
        let mutable batchTxn : SqliteTransaction = null
        try
            for i = 0 to chunks.Length - 1 do
                ct.ThrowIfCancellationRequested()
                if isNull batchTxn then
                    batchTxn <- conn.BeginTransaction()

                let c = chunks.[i]
                use cmd = conn.CreateCommand()
                cmd.Transaction <- batchTxn
                cmd.CommandText <- """
                    INSERT INTO Chunks(DocumentId, OutlineId, RefLocator, Ordinal, TokenCount, Text)
                    VALUES($doc, $out, $ref, $ord, $tok, $text);
                """
                let outlineIdParam : obj =
                    match c.OutlineIndex with
                    | Some idx when idx >= 0 && idx < outlineDbIds.Length -> box outlineDbIds.[idx]
                    | _ -> box DBNull.Value
                cmd.Parameters.AddWithValue("$doc",  documentId) |> ignore
                cmd.Parameters.AddWithValue("$out",  outlineIdParam) |> ignore
                cmd.Parameters.AddWithValue("$ref",  c.RefLocator) |> ignore
                cmd.Parameters.AddWithValue("$ord",  c.Ordinal) |> ignore
                cmd.Parameters.AddWithValue("$tok",  c.TokenCount) |> ignore
                cmd.Parameters.AddWithValue("$text", c.Text) |> ignore
                cmd.ExecuteNonQuery() |> ignore

                if (i + 1) % batchSize = 0 then
                    batchTxn.Commit()
                    batchTxn.Dispose()
                    batchTxn <- null

            if not (isNull batchTxn) then
                batchTxn.Commit()
                batchTxn.Dispose()
                batchTxn <- null
        with _ ->
            if not (isNull batchTxn) then
                try batchTxn.Rollback() with _ -> ()
                batchTxn.Dispose()
            reraise()
