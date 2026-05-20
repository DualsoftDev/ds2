namespace Ds2.LightHouse

open System
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Microsoft.Data.Sqlite
open Ds2.LightHouse.Protocol

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
    // s6-r49 #2 (L-Maj-10): 2.0.0 → 2.1.0 — **minor bump** (forward-compat). Documents.FileMTimeTicks
    //   ALTER 컬럼 신설 — mtime/size 기반 fast-skip 의 metadata. 기존 row 의 NULL = legacy → fall-back hash 계산.
    //   SchemaVersion 4→5 동반 — `needsRebuild` 가 schema_version drift 만 trigger 라 기존 collection 강제 재색인.
    // s6-r34 P4-A: 1.3.0 → 2.0.0 — **major bump (Phase 4 Chunks_Vectors virtual table 신설)**.
    //   sqlite-vec extension 의존 추가 + vector embedding 색인 path 도입. SQL 비호환 (구 lib 가 신 index.db 열기 시
    //   ChunksFts trigger 와 schema_version mismatch → needsRebuild trigger).
    //   `Apps/Promaker/scripts/check-paired-release.ps1` + service `config.json.template` 의 indexerVersionRange
    //   동시 갱신 의무 (max = "2.99.99" 박제, legacy 1.x.x backward-compat 도 유지).
    // s6-r22 mn3: 1.2.0 → 1.3.0 — ImageCache.MimeType NOT NULL DEFAULT 'application/octet-stream' schema 정합.
    //   `Apps/Promaker/scripts/check-paired-release.ps1` 가 service config 의 indexerVersionRange 안 검증.
    [<Literal>]
    let Current = "2.1.0"

    // s6-r49 #2 (L-Maj-10): SchemaVersion 4 → 5 — Documents.FileMTimeTicks ALTER 컬럼 신설 (mtime fast-skip).
    //   needsRebuild trigger — 기존 collection (SchemaVersion 4) 가 신 lib 로 열릴 때 shadow rebuild 강제.
    // s6-r34 P4-A: SchemaVersion 3 → 4 — Chunks_Vectors virtual table (sqlite-vec vec0) 신설.
    //   needsRebuild trigger — 기존 collection (SchemaVersion 3) 가 신 lib 로 열릴 때 shadow rebuild 강제.
    // s6-r22 mn3: SchemaVersion 2 → 3 — ImageCache.MimeType column constraint 변경 (NULL → NOT NULL DEFAULT).
    [<Literal>]
    let SchemaVersion = "5"

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
    /// **A2 (K4 Protocol SSOT 통합, 2026-05-20)** — `Ds2.LightHouse.Protocol.ZipLayout.KbFolderName` 단일 SSOT 의존.
    /// F# `[<Literal>]` 은 cross-module compile-time const inline 가능 — drift 0 정합 (lib / Protocol / Promaker /
    /// cli / server 5 곳 박제 → 단일 wire SSOT). M22 (zip layout 3중 박제) 본 phase 흡수 완성.
    [<Literal>]
    let KbFolderName = ZipLayout.KbFolderName

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
-- **R2-M1 (s6-r76, external review backlog)** — `findDocumentByPath` (mtime fast-skip hot path)
-- 의 O(N²) full table scan 회귀 차단. FileHash 만 UNIQUE 였음. ensureSchema 가 forward-compat
-- 적용 (기존 DB 에도 idempotent CREATE).
CREATE INDEX IF NOT EXISTS IX_Documents_OriginalPath ON Documents(OriginalPath);

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
-- s6-r22 mn3: MimeType NOT NULL DEFAULT 'application/octet-stream' — `Indexer.ingestImagesIntoStore` 가
-- `ImageStore.mimeOf` 로 항상 박제하나 legacy zip / 외부 source 안전망. AttachmentTools.inferMimeFromPath
-- fallback 의 본질 해결 (caller 가 NULL/empty 분기 처리 부담 완화).
CREATE TABLE IF NOT EXISTS ImageCache (
    ImageHash    TEXT PRIMARY KEY,
    StoredPath   TEXT NOT NULL,
    MimeType     TEXT NOT NULL DEFAULT 'application/octet-stream',
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

    /// **Phase 4 (s6-r34) — sqlite-vec virtual table SSOT** (parent §3.7 / §3.15.2).
    ///
    /// vec0 virtual table 은 LoadExtension("vec0") 후에만 CREATE 가능 — schemaSql 와 분리.
    /// `Chunks_Vectors.ChunkId` 가 PRIMARY KEY (rowid 역할) + Chunks.Id 와 1:1 매핑 SSOT.
    /// embedding dim = caller 의 `IEmbeddingProvider.Dimension` 정합 의무 — 본 lib default = 1024
    /// (bge-m3 / multilingual-e5-large / jina-v3 모두 1024). dim 변경 시 SchemaVersion bump 동반 의무.
    [<Literal>]
    let private vec0SchemaSql =
        "CREATE VIRTUAL TABLE IF NOT EXISTS Chunks_Vectors USING vec0(ChunkId INTEGER PRIMARY KEY, embedding float[1024])"

    /// Chunks_Vectors 의 embedding dim — 본 lib default 박제. caller 의 IEmbeddingProvider.Dimension 정합 의무.
    [<Literal>]
    let EmbeddingDimension = 1024

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

    /// **Phase 4 (s6-r34) — sqlite-vec extension 절대 path 산출** (SQLite OS LoadLibrary API 의 path 자동 search 부재 회피).
    ///
    /// SQLite 의 `LoadExtension` 은 OS native API (LoadLibrary on Windows / dlopen on Linux/macOS) 직접 호출 —
    /// `.NET runtime asset` (`runtimes/<rid>/native/`) 의 자동 search path 무관. 절대 path 명시 의무 (Microsoft 공식
    /// doc 박제: "SQLite can't leverage .NET's logic for locating native libraries; it calls the platform API directly").
    let private resolveVec0Path () : string =
        let rid =
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then "win-x64"
            elif RuntimeInformation.IsOSPlatform OSPlatform.Linux then "linux-x64"
            elif RuntimeInformation.IsOSPlatform OSPlatform.OSX then "osx-x64"
            else
                raise (PlatformNotSupportedException(
                    sprintf "sqlite-vec 미지원 platform — OSDescription=%s" RuntimeInformation.OSDescription))
        let fileName =
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then "vec0.dll"
            elif RuntimeInformation.IsOSPlatform OSPlatform.OSX then "vec0.dylib"
            else "vec0.so"
        Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName)

    /// **B-R12 (perf sweep, s6-r71+)** — vec0 path lazy cache. process lifetime 동안 1회 산출.
    /// 매 `openConnection` 호출마다 `RuntimeInformation.IsOSPlatform` + `Path.Combine` 반복 cost 회피
    /// (대용량 색인 path 의 `withReadOnlyConn` 매 호출 시 누적). PlatformNotSupportedException 도 lazy 박제 정합.
    let private vec0PathLazy = System.Lazy<string>(fun () -> resolveVec0Path())

    /// **Phase 4 (s6-r34)** — sqlite-vec extension load. open 직후 1회 호출 + ATTACH 별도 connection 도 호출.
    ///
    /// `vec0.dll` (Windows) / `libvec0.so` (Linux) / `libvec0.dylib` (macOS) 가 caller 의 publish output 의
    /// `runtimes/<rid>/native/` 에 있어야 함 (sqlite-vec NuGet 0.1.7-alpha.2.1 박제 의무, s6-r33 검증).
    ///
    /// Microsoft.Data.Sqlite 의 `LoadExtension` 은 SqliteConnection 의 method — `EnableExtensions(true)` 자동 호출.
    /// extension load 실패 시 SqliteException throw — caller (Indexer / Searcher) 가 명확 fail-fast.
    let loadVec0Extension (conn: SqliteConnection) : unit =
        conn.LoadExtension(vec0PathLazy.Value)

    /// SQLite connection 단일 진입점 (§3.17 SSOT). 다른 곳에서 직접 `new SqliteConnection` 금지.
    ///
    /// `readOnly = true` 시 `Mode=ReadOnly` (§3.9 r4 read-only collection). 부모 디렉토리 미존재 시:
    ///   - read-write: 폴더 자동 생성 (Indexer 가 .lighthouse-kb/ 최초 생성)
    ///   - read-only: open 실패 그대로 throw (caller fail-fast)
    ///
    /// **Phase 4 (s6-r34)**: open 후 sqlite-vec extension (vec0) load — read/write 양쪽. Chunks_Vectors
    /// virtual table 접근 (read 측 hybrid retrieval / write 측 색인 INSERT) 의무.
    let openConnection (path: string) (readOnly: bool) : SqliteConnection =
        if not readOnly then
            let dir = Path.GetDirectoryName path
            if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
                Directory.CreateDirectory dir |> ignore

        let csb = SqliteConnectionStringBuilder()
        csb.DataSource <- path
        csb.Mode <- if readOnly then SqliteOpenMode.ReadOnly else SqliteOpenMode.ReadWriteCreate
        csb.Cache <- SqliteCacheMode.Shared
        // **s6-r47 / 외부 --review L-Maj-3 part 2 정정** — read-only path 는 pool 비활성화. 전역 pool flush
        // 부작용 (caller 가 ClearPool 호출 시 다른 connection 의 pool 도 flush) 회피. write path 는 pool 정합
        // 유지 (Indexer batch 의 connection reuse perf 효과). read-only SqliteOpenMode 는 file lock 잔존 무관.
        csb.Pooling <- not readOnly
        let conn = new SqliteConnection(csb.ToString())
        conn.Open()
        applyPragmas conn
        loadVec0Extension conn  // Phase 4 — vec0 extension active 의무 (Chunks_Vectors 접근)
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
    /// Phase 4 (s6-r34) — `Chunks_Vectors` vec0 virtual table 신설 (caller 가 LoadExtension("vec0") 후 호출 — openConnection 보장).
    /// Meta key (`schema_version`/`indexer_version`/`tokenizer`/`created_at`) 도 동시 기록.
    let ensureSchema (conn: SqliteConnection) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- schemaSql
        cmd.ExecuteNonQuery() |> ignore
        // Phase 2 ALTER (SQLite IF NOT EXISTS 미지원) — Phase 1 색인 DB 에서도 안전 forward-compat.
        ensureColumn conn "Chunks" "ImageCount"
            "ALTER TABLE Chunks ADD COLUMN ImageCount INTEGER NOT NULL DEFAULT 0"
        // s6-r49 #2 (L-Maj-10): Documents.FileMTimeTicks ALTER 컬럼 — mtime fast-skip metadata. nullable
        // (기존 row NULL = legacy → Indexer 가 fall-back hash 계산). 신 색인 row 는 file.LastWriteTimeUtc.Ticks 박제.
        ensureColumn conn "Documents" "FileMTimeTicks"
            "ALTER TABLE Documents ADD COLUMN FileMTimeTicks INTEGER"
        // Phase 4 — vec0 virtual table (sqlite-vec extension 의존, openConnection 의 loadVec0Extension 박제 정합).
        use vecCmd = conn.CreateCommand()
        vecCmd.CommandText <- vec0SchemaSql
        vecCmd.ExecuteNonQuery() |> ignore

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

    /// 코드의 현재 SchemaVersion 과 DB 의 Meta.schema_version 불일치 여부 (§3.17 s6-r26 정정).
    ///
    /// **정책 — major/minor 분리 (s6-r8 m1 박제 해소, s6-r26)**:
    /// 본 함수는 SchemaVersion drift 만 rebuild 강제. IndexerVersion (1.3.0 → 1.4.0 minor / patch
    /// bump) 은 `ensureSchema` 의 ALTER forward-compat 으로 흡수 + `stampVersion` 으로 Meta 갱신.
    /// SchemaVersion bump = SQL 비호환 변경 (column constraint 변경 / FK 추가 / DROP 등) 시점 한정 —
    /// 기존 collection 재색인 cost 회피. SchemaVersion / IndexerVersion 동시 박제 책임 = lib 개발자
    /// (s6-r22 mn3 패턴 — IndexerVersion 1.2.0→1.3.0 + SchemaVersion 2→3 동반 bump 정합).
    ///
    /// Meta 미존재 (None) = pre-stamp DB 또는 손상 → 안전하게 rebuild.
    let needsRebuild (conn: SqliteConnection) : bool =
        match getMeta conn "schema_version" with
        | Some v -> v <> IndexerVersion.SchemaVersion
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

    /// **s6-r49 #2 (L-Maj-10) — OriginalPath → (Id, FileMTimeTicks option, SizeBytes)**.
    /// mtime/size 기반 fast-skip 의 entry point. NULL FileMTimeTicks (legacy row) = `None` → caller (Indexer)
    /// 가 fall-back hash 계산. 미존재 시 outer None.
    let findDocumentByPath (conn: SqliteConnection) (originalPath: string) : (int64 * int64 option * int64) option =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT Id, FileMTimeTicks, SizeBytes FROM Documents WHERE OriginalPath = $p"
        cmd.Parameters.AddWithValue("$p", originalPath) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            let id = reader.GetInt64 0
            let mtime =
                if reader.IsDBNull 1 then None
                else Some (reader.GetInt64 1)
            let size = reader.GetInt64 2
            Some (id, mtime, size)
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

    /// Document insert (mtime 포함). 반환 = 새 Id. **s6-r49 #2 (L-Maj-10)**: `fileMTimeTicks` 인자 추가 —
    /// mtime fast-skip metadata. `None` 박제 시 NULL 박제 (Tests 또는 mtime 미산출 path 정합).
    /// production caller (Indexer.ingestFile) 는 `FileInfo(path).LastWriteTimeUtc.Ticks` 박제 의무.
    /// 기존 caller (30+) backward-compat = 별 wrapper `insertDocument` (mtime=None).
    let insertDocumentWithMtime
        (conn: SqliteConnection)
        (fileHash: string)
        (originalPath: string)
        (docType: FileKind)
        (sizeBytes: int64)
        (pageOrSheetCnt: int option)
        (title: string option)
        (fileMTimeTicks: int64 option)
        : int64 =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            INSERT INTO Documents(FileHash, OriginalPath, DocType, SizeBytes, PageOrSheetCnt, Title, SummaryText, IndexerVersion, IngestedAt, FileMTimeTicks)
            VALUES($hash, $path, $type, $size, $pages, $title, NULL, $ver, $at, $mtime);
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
        cmd.Parameters.AddWithValue("$mtime", fileMTimeTicks |> Option.map box |> Option.defaultValue (box DBNull.Value)) |> ignore
        Convert.ToInt64 (cmd.ExecuteScalar())

    /// **legacy wrapper (s6-r49 #2)** — mtime 미박제 caller (Tests 30+ + ImageStoreTests 등) backward-compat.
    /// production path (Indexer.ingestFile) 는 `insertDocumentWithMtime` 직접 호출 의무.
    let insertDocument
        (conn: SqliteConnection)
        (fileHash: string)
        (originalPath: string)
        (docType: FileKind)
        (sizeBytes: int64)
        (pageOrSheetCnt: int option)
        (title: string option)
        : int64 =
        insertDocumentWithMtime conn fileHash originalPath docType sizeBytes pageOrSheetCnt title None

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
        // **s6-r51 #5 ⑰ 외부 --review** — cmd 1회 생성 + Prepare + parameters 재사용. 매 chunk 마다 cmd
        // allocation + SQL parse + Parameters.Add 회피. N=1000+ chunks 환경에서 perf 정정.
        // batchTxn 변경 시 cmd.Transaction 재할당 의무 (Microsoft.Data.Sqlite 의 SqliteCommand 는 transaction
        // setter 단순 — prepared statement 재계산 없음).
        let mutable batchTxn : SqliteTransaction = null
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            INSERT INTO Chunks(DocumentId, OutlineId, RefLocator, Ordinal, TokenCount, Text)
            VALUES($doc, $out, $ref, $ord, $tok, $text);
        """
        let pDoc = cmd.Parameters.Add("$doc", SqliteType.Integer)
        let pOut = cmd.Parameters.Add("$out", SqliteType.Integer)
        let pRef = cmd.Parameters.Add("$ref", SqliteType.Text)
        let pOrd = cmd.Parameters.Add("$ord", SqliteType.Integer)
        let pTok = cmd.Parameters.Add("$tok", SqliteType.Integer)
        let pText = cmd.Parameters.Add("$text", SqliteType.Text)
        try
            for i = 0 to chunks.Length - 1 do
                ct.ThrowIfCancellationRequested()
                if isNull batchTxn then
                    batchTxn <- conn.BeginTransaction()
                    cmd.Transaction <- batchTxn

                let c = chunks.[i]
                pDoc.Value <- documentId
                pOut.Value <-
                    match c.OutlineIndex with
                    | Some idx when idx >= 0 && idx < outlineDbIds.Length -> box outlineDbIds.[idx]
                    | _ -> box DBNull.Value
                pRef.Value <- c.RefLocator
                pOrd.Value <- c.Ordinal
                pTok.Value <- c.TokenCount
                pText.Value <- c.Text
                cmd.ExecuteNonQuery() |> ignore

                if (i + 1) % batchSize = 0 then
                    batchTxn.Commit()
                    batchTxn.Dispose()
                    batchTxn <- null
                    cmd.Transaction <- null  // 신 batch 진입 시 재할당

            if not (isNull batchTxn) then
                batchTxn.Commit()
                batchTxn.Dispose()
                batchTxn <- null
        with _ ->
            if not (isNull batchTxn) then
                try batchTxn.Rollback() with _ -> ()
                batchTxn.Dispose()
            reraise()

    /// **Phase 2 task C4 (s6-r15)** — Chunks 의 RefLocator → 첫 chunk Id 맵 빌드.
    ///
    /// Indexer.ingestImagesIntoStore 가 image 의 `RefLocator` 와 같은 chunk 의 ChunkId 를 채우기 위해 사용.
    /// 한 RefLocator 가 N chunks 로 분할된 경우 (Chunker 의 token 한도 초과) **첫 chunk** (= MIN(Ordinal) =
    /// MIN(Id), sequential INSERT 가정) 만 매핑 — C4 Q1 옵션 1 결정 (s6-r15 박제).
    ///
    /// 본 함수는 chunks 인서트 직후 호출 — 빈 chunks 도 valid (빈 Map 반환).
    let lookupChunkIdsByDocument
        (conn: SqliteConnection)
        (documentId: int64)
        : Map<string, int64> =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT RefLocator, MIN(Id) AS FirstChunkId
            FROM Chunks
            WHERE DocumentId = $doc
            GROUP BY RefLocator
        """
        cmd.Parameters.AddWithValue("$doc", documentId) |> ignore
        use reader = cmd.ExecuteReader()
        let mutable m = Map.empty
        while reader.Read() do
            let refLoc = reader.GetString 0
            let chunkId = reader.GetInt64 1
            m <- Map.add refLoc chunkId m
        m

    /// **Phase 2 task C4 (s6-r15)** — `Chunks.ImageCount` post-update.
    ///
    /// 의미 (Q3 옵션 X) = 자기 chunk 의 ImageReferences row 수 (`COUNT(*) WHERE ChunkId = Chunks.Id`).
    /// image 미참조 chunk 는 0 (DEFAULT 0 유지). image dispatch 완료 후 1회 호출.
    ///
    /// scope = `WHERE DocumentId = $doc` — re-ingest 시점에 다른 document 의 chunks 영향 없음.
    let updateChunkImageCounts
        (conn: SqliteConnection)
        (documentId: int64)
        : unit =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            UPDATE Chunks
            SET ImageCount = (
                SELECT COUNT(*) FROM ImageReferences ir
                WHERE ir.ChunkId = Chunks.Id
            )
            WHERE DocumentId = $doc
        """
        cmd.Parameters.AddWithValue("$doc", documentId) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// **Phase 4 (s6-r34)** — Document 의 모든 Chunks Id (ORDER BY Id ASC, sequential INSERT 정합).
    /// embedder dispatch 시 chunks 배열과 같은 순서로 매핑하기 위한 helper. chunks 빈 document 도 빈 array OK.
    let listChunkIdsByDocument
        (conn: SqliteConnection)
        (documentId: int64)
        : int64[] =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT Id FROM Chunks WHERE DocumentId = $doc ORDER BY Id ASC"
        cmd.Parameters.AddWithValue("$doc", documentId) |> ignore
        use reader = cmd.ExecuteReader()
        let result = ResizeArray<int64>()
        while reader.Read() do
            result.Add (reader.GetInt64 0)
        result.ToArray()

    /// **Phase 4 (s6-r34)** — Chunks_Vectors 의 chunk embedding upsert.
    ///
    /// sqlite-vec 0.1.x 표준 wire = JSON 배열 string (`"[1.0, 2.0, ...]"`). vec0 가 parse 후 float[N] storage.
    /// **vec0 virtual table 은 ON CONFLICT UPSERT 미지원** → DELETE + INSERT 패턴 (동일 ChunkId 재색인 시 idempotent).
    ///
    /// 의무: embedding.Length == SqliteStore.EmbeddingDimension (caller 의 IEmbeddingProvider.Dimension 정합).
    /// length mismatch 시 sqlite-vec 가 SqliteException throw — caller fail-fast.
    /// **B-R12 (perf sweep, s6-r71+)** — vec0 wire format (`"[v1,v2,...]"`) 직접 빌드. 매 chunk 마다
    /// `JsonSerializer.Serialize<float32[]>` reflection cost (N=10000+ chunks 시 누적) 회피. format = invariant
    /// "R" round-trip (System.Text.Json 의 float32 직렬화 format 과 정합 — G9 ≡ R round-trip). sqlite-vec 가
    /// parse 후 float32 storage. capacity hint = `Length * 12 + 2` (avg float32 round-trip ≤ 11자 + 콤마/괄호).
    let private buildVec0WireFormat (embedding: float32[]) : string =
        let sb = System.Text.StringBuilder(embedding.Length * 12 + 2)
        sb.Append '[' |> ignore
        for i = 0 to embedding.Length - 1 do
            if i > 0 then sb.Append ',' |> ignore
            sb.Append(embedding.[i].ToString("R", CultureInfo.InvariantCulture)) |> ignore
        sb.Append ']' |> ignore
        sb.ToString()

    let upsertChunkEmbedding
        (conn: SqliteConnection)
        (chunkId: int64)
        (embedding: float32[])
        : unit =
        // B-R12 (perf sweep) — JsonSerializer 의 reflection cost 회피, StringBuilder 직접 wire 빌드.
        let json = buildVec0WireFormat embedding
        // DELETE + INSERT — vec0 의 UPSERT 미지원 우회. 동일 ChunkId 재색인 시 idempotent.
        use del = conn.CreateCommand()
        del.CommandText <- "DELETE FROM Chunks_Vectors WHERE ChunkId = $cid"
        del.Parameters.AddWithValue("$cid", chunkId) |> ignore
        del.ExecuteNonQuery() |> ignore
        use ins = conn.CreateCommand()
        ins.CommandText <- "INSERT INTO Chunks_Vectors(ChunkId, embedding) VALUES($cid, $emb)"
        ins.Parameters.AddWithValue("$cid", chunkId) |> ignore
        ins.Parameters.AddWithValue("$emb", json) |> ignore
        ins.ExecuteNonQuery() |> ignore

    /// **Phase 4 (s6-r34)** — N chunks 의 embedding batch upsert. 외부 transaction 안에서 호출 가능.
    /// caller (Indexer) 가 `lookupChunkIdsByDocument` 로 ChunkId 받은 후 매핑하여 호출.
    ///
    /// **s6-r35 외부 --review 적용 (L-Maj-1)** — 본 함수가 자체 transaction wrap. 각 `upsertChunkEmbedding` 가
    /// DELETE + INSERT (2 statement) 라 autocommit 모드 시 매 chunk 마다 fsync 2회 → N chunk 시 disk IO 2N
    /// 회 + race window (DELETE 와 INSERT 사이 다른 connection 의 read). single transaction commit 으로 N
    /// 개 statement 묶음 → fsync 1회 + atomic. insertChunks 의 batch transaction 패턴 정합. `upsertChunkEmbedding`
    /// 안 SqliteCommand 는 transaction 명시 박제 없어도 active transaction 자동 inherit (Microsoft.Data.Sqlite 표준).
    let upsertChunkEmbeddingsBatch
        (conn: SqliteConnection)
        (chunkIdToEmbedding: (int64 * float32[]) array)
        (ct: CancellationToken)
        : unit =
        if chunkIdToEmbedding.Length = 0 then ()
        else
            let txn = conn.BeginTransaction()
            try
                for (cid, emb) in chunkIdToEmbedding do
                    ct.ThrowIfCancellationRequested()
                    upsertChunkEmbedding conn cid emb
                txn.Commit()
                txn.Dispose()
            with _ ->
                try txn.Rollback() with _ -> ()
                txn.Dispose()
                reraise()
