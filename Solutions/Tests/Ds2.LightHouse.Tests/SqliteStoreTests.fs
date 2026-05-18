module Ds2.LightHouse.Tests.SqliteStoreTests

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open Ds2.LightHouse

/// todo-lighthouse-kb-index.md §4.8b — SqliteStore CRUD primitives + Meta + FTS5 trigger.

let private withTempDir (action: string -> 'r) : 'r =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-store-%s" (Guid.NewGuid().ToString("N")))
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

[<Fact>]
let ``ensureSchema idempotent — 2회 호출 OK`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        SqliteStore.ensureSchema conn   // 두 번째 호출 — IF NOT EXISTS 의 idempotent 보장
        SqliteStore.ensureSchema conn)

[<Fact>]
let ``stampVersion / needsRebuild — Current 와 일치 시 false`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        Assert.False(SqliteStore.needsRebuild conn))

[<Fact>]
let ``Meta drift — indexer_version 다르면 needsRebuild true`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        SqliteStore.setMeta conn "indexer_version" "0.0.0"
        Assert.True(SqliteStore.needsRebuild conn))

[<Fact>]
let ``Meta get/set round-trip`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        SqliteStore.setMeta conn "foo" "bar"
        Assert.Equal(Some "bar", SqliteStore.getMeta conn "foo")
        SqliteStore.setMeta conn "foo" "baz"  // upsert
        Assert.Equal(Some "baz", SqliteStore.getMeta conn "foo")
        Assert.Equal(None, SqliteStore.getMeta conn "missing"))

[<Fact>]
let ``insertDocument + findDocumentByHash — idempotent key`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let docId = SqliteStore.insertDocument conn "HASH1" "spec.pdf" Pdf 1024L (Some 10) (Some "Spec")
        Assert.True(docId > 0L)
        Assert.Equal(Some docId, SqliteStore.findDocumentByHash conn "HASH1")
        Assert.Equal(None, SqliteStore.findDocumentByHash conn "OTHER"))

[<Fact>]
let ``FileHash UNIQUE — 같은 hash 두 번 insert 시 SqliteException`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let _ = SqliteStore.insertDocument conn "HASH1" "a.pdf" Pdf 100L None None
        Assert.Throws<SqliteException>(fun () ->
            SqliteStore.insertDocument conn "HASH1" "b.pdf" Pdf 200L None None |> ignore) |> ignore)

[<Fact>]
let ``insertChunks → ChunksFts trigger sync — FTS5 검색 가능`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let docId = SqliteStore.insertDocument conn "H1" "a.txt" Text 100L None None
        let chunks = [|
            { OutlineIndex = None; RefLocator = "p=1"; Ordinal = 0; TokenCount = 5; Text = "컨베이어 동작 사양" }
            { OutlineIndex = None; RefLocator = "p=2"; Ordinal = 0; TokenCount = 3; Text = "센서 입력 정의" }
        |]
        SqliteStore.insertChunks conn docId [||] chunks SqliteStore.DefaultBatchSize CancellationToken.None

        // FTS5 trigger 가 sync 했는지 — 직접 query
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT count(*) FROM ChunksFts WHERE ChunksFts MATCH '\"컨베이어\"'"
        let count = Convert.ToInt32 (cmd.ExecuteScalar())
        Assert.Equal(1, count))

[<Fact>]
let ``Chunks UPDATE → ChunksFts trigger 동기 갱신 (AU)`` () =
    // Phase 1 ingest path 는 UPDATE 사용 안 함 — AU trigger 는 향후 매뉴얼 update 진입점 대비 SSOT.
    // r9 자가 검열 M2 잔여 우려 (FTS5 external-content trigger 검증) 의 마지막 분기 (review m9).
    withTempDir (fun dir ->
        use conn = openFresh dir
        let docId = SqliteStore.insertDocument conn "H1" "a.txt" Text 100L None None
        SqliteStore.insertChunks conn docId [||]
            [| { OutlineIndex = None; RefLocator = "p=1"; Ordinal = 0; TokenCount = 2; Text = "before" } |]
            SqliteStore.DefaultBatchSize CancellationToken.None
        // UPDATE Chunks → AU trigger 가 ChunksFts 의 old 삭제 + new 삽입
        (
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "UPDATE Chunks SET Text = 'after' WHERE DocumentId = $doc"
            cmd.Parameters.AddWithValue("$doc", docId) |> ignore
            cmd.ExecuteNonQuery() |> ignore
        )
        // before 0건 / after 1건
        let countOf (term: string) =
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sprintf "SELECT count(*) FROM ChunksFts WHERE ChunksFts MATCH '\"%s\"'" term
            Convert.ToInt32 (cmd.ExecuteScalar())
        Assert.Equal(0, countOf "before")
        Assert.Equal(1, countOf "after"))

[<Fact>]
let ``Chunks 삭제 → ChunksFts trigger 동기 삭제 (AD)`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let docId = SqliteStore.insertDocument conn "H1" "a.txt" Text 100L None None
        SqliteStore.insertChunks conn docId [||]
            [| { OutlineIndex = None; RefLocator = "p=1"; Ordinal = 0; TokenCount = 2; Text = "삭제 대상" } |]
            SqliteStore.DefaultBatchSize CancellationToken.None
        SqliteStore.deleteDocument conn docId
        // CASCADE → Chunks 삭제 → trigger 가 ChunksFts 도 sync
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT count(*) FROM ChunksFts"
        let count = Convert.ToInt32 (cmd.ExecuteScalar())
        Assert.Equal(0, count))

[<Fact>]
let ``insertOutlineTree — ParentIndex linking`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let docId = SqliteStore.insertDocument conn "H1" "a.docx" Docx 100L None None
        let nodes = [|
            { ParentIndex = None;    Ordinal = 0; NodeType = OutlineNodeType.Heading; Label = "1장";   RefLocator = "p=1" }
            { ParentIndex = Some 0;  Ordinal = 1; NodeType = OutlineNodeType.Heading; Label = "1.1";   RefLocator = "p=2" }
            { ParentIndex = Some 1;  Ordinal = 2; NodeType = OutlineNodeType.Heading; Label = "1.1.1"; RefLocator = "p=3" }
        |]
        let ids = SqliteStore.insertOutlineTree conn docId nodes
        Assert.Equal(3, ids.Length)
        Assert.True(ids.[0] > 0L && ids.[1] > 0L && ids.[2] > 0L)

        // parent linking 확인
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT ParentId FROM OutlineNodes WHERE Id = $id"
        cmd.Parameters.AddWithValue("$id", ids.[1]) |> ignore
        let parent = cmd.ExecuteScalar()
        Assert.Equal(ids.[0], Convert.ToInt64 parent))

[<Fact>]
let ``checkWritable — 신규 폴더 쓰기 가능`` () =
    withTempDir (fun dir ->
        Assert.True(SqliteStore.checkWritable dir))

[<Fact>]
let ``checkWritable — 미존재 폴더 false`` () =
    let bogus = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Assert.False(SqliteStore.checkWritable bogus)

[<Fact>]
let ``insertChunks — CancellationToken pre-cancelled 시 throw`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let docId = SqliteStore.insertDocument conn "H1" "a.txt" Text 100L None None
        use cts = new CancellationTokenSource()
        cts.Cancel()
        let chunks = [| { OutlineIndex = None; RefLocator = "p=1"; Ordinal = 0; TokenCount = 1; Text = "x" } |]
        Assert.Throws<OperationCanceledException>(fun () ->
            SqliteStore.insertChunks conn docId [||] chunks SqliteStore.DefaultBatchSize cts.Token) |> ignore)

[<Fact>]
let ``swapShadow — 신규 swap`` () =
    withTempDir (fun dir ->
        let shadowPath = SqliteStore.shadowDbPath dir
        let kbDir = SqliteStore.kbDir dir
        Directory.CreateDirectory kbDir |> ignore
        File.WriteAllBytes(shadowPath, [| 1uy; 2uy; 3uy |])
        SqliteStore.swapShadow dir
        let dbPath = SqliteStore.dbPath dir
        Assert.True(File.Exists dbPath)
        Assert.False(File.Exists shadowPath)
        Assert.False(File.Exists (dbPath + ".bak")))

[<Fact>]
let ``WAL 동시성 — 색인 중 별 connection 으로 read 가능`` () =
    withTempDir (fun dir ->
        use writeConn = openFresh dir
        let docId = SqliteStore.insertDocument writeConn "H1" "a.txt" Text 100L None None
        SqliteStore.insertChunks writeConn docId [||]
            [| { OutlineIndex = None; RefLocator = "p=1"; Ordinal = 0; TokenCount = 2; Text = "live data" } |]
            SqliteStore.DefaultBatchSize CancellationToken.None

        // 별 connection (read-only)
        let dbPath = SqliteStore.dbPath dir
        use readConn = SqliteStore.openConnection dbPath true
        use cmd = readConn.CreateCommand()
        cmd.CommandText <- "SELECT count(*) FROM Chunks"
        Assert.Equal(1, Convert.ToInt32 (cmd.ExecuteScalar())))


// ── Phase 2 (s6-r8): schema 확장 (ImageCache / ImageReferences / Chunks.ImageCount) ──
// parent §3.12 의 Phase 2 주석 처리 블록 활성. backward-compat (CREATE IF NOT EXISTS + ALTER DEFAULT 0).

/// helper: PRAGMA table_info 로 컬럼 목록 추출.
let private tableColumns (conn: SqliteConnection) (table: string) : string list =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sprintf "PRAGMA table_info(%s)" table
    use reader = cmd.ExecuteReader()
    let mutable cols = []
    while reader.Read() do
        cols <- reader.GetString(1) :: cols
    List.rev cols

/// helper: 테이블 존재 확인.
let private tableExists (conn: SqliteConnection) (table: string) : bool =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n"
    cmd.Parameters.AddWithValue("$n", table) |> ignore
    Convert.ToInt32 (cmd.ExecuteScalar()) = 1

[<Fact>]
let ``Phase 2 schema — IndexerVersion 1.3.0 / SchemaVersion 3 (s6-r22 mn3 ImageCache.MimeType NOT NULL DEFAULT)`` () =
    // parent §3.17 정합 — schema 변경 동반 시 SchemaVersion 도 bump.
    // s6-r22 (mn3): 1.2.0 → 1.3.0 minor bump — `ImageCache.MimeType` 컬럼 제약 NULL → NOT NULL DEFAULT 'application/octet-stream'.
    //   SchemaVersion 도 "2" → "3" 동반 bump (column constraint 형식 변경).
    Assert.Equal("1.3.0", IndexerVersion.Current)
    Assert.Equal("3", IndexerVersion.SchemaVersion)

[<Fact>]
let ``Phase 2 schema — ImageCache.MimeType NOT NULL DEFAULT 'application/octet-stream' (s6-r22 mn3)`` () =
    // sqlite_master 의 CREATE TABLE SQL parse — column constraint 박제 검증.
    withTempDir (fun dir ->
        use conn = openFresh dir
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT sql FROM sqlite_master WHERE type='table' AND name='ImageCache'"
        let sql = cmd.ExecuteScalar() :?> string
        Assert.Contains("MimeType", sql)
        Assert.Contains("NOT NULL DEFAULT 'application/octet-stream'", sql)
        // DEFAULT 동작 검증: 명시 박제 없이 INSERT (`MimeType` 미 specify) 시 default 적용.
        use ins = conn.CreateCommand()
        ins.CommandText <- "INSERT INTO ImageCache(ImageHash, StoredPath) VALUES('h', 'p')"
        ins.ExecuteNonQuery() |> ignore
        use sel = conn.CreateCommand()
        sel.CommandText <- "SELECT MimeType FROM ImageCache WHERE ImageHash = 'h'"
        Assert.Equal("application/octet-stream", string (sel.ExecuteScalar())))

[<Fact>]
let ``Phase 2 schema — ImageCache 테이블 + 8 컬럼 PK=ImageHash`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        Assert.True(tableExists conn "ImageCache")
        let cols = tableColumns conn "ImageCache"
        // parent §3.12 정합 — ImageHash / StoredPath / MimeType / Width / Height / CaptionText / CaptionAt / CaptionModel.
        Assert.Equal<string list>(
            [ "ImageHash"; "StoredPath"; "MimeType"; "Width"; "Height"; "CaptionText"; "CaptionAt"; "CaptionModel" ],
            cols))

[<Fact>]
let ``Phase 2 schema — ImageReferences 복합 PK 4 키 + FK Documents/Chunks/ImageCache`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        Assert.True(tableExists conn "ImageReferences")
        // PK 복합 검증 — sqlite_master 의 CREATE TABLE SQL parse.
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT sql FROM sqlite_master WHERE type='table' AND name='ImageReferences'"
        let sql = cmd.ExecuteScalar() :?> string
        Assert.Contains("PRIMARY KEY (DocumentId, ImageHash, RefLocator, Ordinal)", sql)
        // FK 3 종 검증.
        Assert.Contains("REFERENCES Documents(Id)", sql)
        Assert.Contains("REFERENCES Chunks(Id)", sql)
        Assert.Contains("REFERENCES ImageCache(ImageHash)", sql))

[<Fact>]
let ``Phase 2 schema — Chunks.ImageCount 컬럼 추가 (DEFAULT 0) + INSERT 시 자동 0`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        let cols = tableColumns conn "Chunks"
        Assert.Contains("ImageCount", cols)
        // DEFAULT 0 동작 확인 — ImageCount 미지정 INSERT 시 자동 0.
        let docId = SqliteStore.insertDocument conn "H-img" "a.txt" Text 10L None None
        SqliteStore.insertChunks conn docId [||]
            [| { OutlineIndex = None; RefLocator = "p=1"; Ordinal = 0; TokenCount = 1; Text = "txt" } |]
            SqliteStore.DefaultBatchSize CancellationToken.None
        use sel = conn.CreateCommand()
        sel.CommandText <- "SELECT ImageCount FROM Chunks WHERE DocumentId = $d"
        sel.Parameters.AddWithValue("$d", docId) |> ignore
        Assert.Equal(0, Convert.ToInt32 (sel.ExecuteScalar())))

[<Fact>]
let ``Phase 2 schema — IX_ImgRef_Chunk index 존재`` () =
    withTempDir (fun dir ->
        use conn = openFresh dir
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='IX_ImgRef_Chunk'"
        Assert.Equal(1, Convert.ToInt32 (cmd.ExecuteScalar())))

[<Fact>]
let ``Phase 2 schema — ensureColumn idempotent (재호출 시 no-op)`` () =
    // ensureSchema 자체가 ensureColumn 호출 — 같은 connection 에서 2회 호출도 안전.
    withTempDir (fun dir ->
        use conn = openFresh dir
        SqliteStore.ensureSchema conn   // 2회
        SqliteStore.ensureSchema conn   // 3회
        let cols = tableColumns conn "Chunks"
        // ImageCount 가 정확히 1개 (중복 ALTER 회피).
        let count = cols |> List.filter (fun c -> c = "ImageCount") |> List.length
        Assert.Equal(1, count))

[<Fact>]
let ``Phase 2 schema — forward-compat: Phase 1 DB 에 ensureSchema 호출 시 ImageCount + Image* 자동 추가`` () =
    // Phase 1 색인된 collection 의 index.db 에 새 lib (s6-r8) 으로 ensureSchema 호출 — 자동 forward-compat.
    withTempDir (fun dir ->
        let dbPath = SqliteStore.dbPath dir
        // (a) Phase 1 환원 schema 만 직접 작성 (Image* 없음, ImageCount 없음).
        let phase1Sql = """
            CREATE TABLE Documents (
              Id INTEGER PRIMARY KEY, FileHash TEXT NOT NULL UNIQUE, OriginalPath TEXT NOT NULL,
              DocType TEXT NOT NULL, SizeBytes INTEGER NOT NULL, PageOrSheetCnt INTEGER,
              Title TEXT, SummaryText TEXT, IndexerVersion TEXT NOT NULL, IngestedAt TEXT NOT NULL);
            CREATE TABLE OutlineNodes (
              Id INTEGER PRIMARY KEY, DocumentId INTEGER NOT NULL REFERENCES Documents(Id) ON DELETE CASCADE,
              ParentId INTEGER REFERENCES OutlineNodes(Id) ON DELETE CASCADE,
              Ordinal INTEGER NOT NULL, NodeType TEXT NOT NULL, Label TEXT NOT NULL, RefLocator TEXT NOT NULL);
            CREATE TABLE Chunks (
              Id INTEGER PRIMARY KEY, DocumentId INTEGER NOT NULL REFERENCES Documents(Id) ON DELETE CASCADE,
              OutlineId INTEGER REFERENCES OutlineNodes(Id) ON DELETE SET NULL,
              RefLocator TEXT NOT NULL, Ordinal INTEGER NOT NULL, TokenCount INTEGER NOT NULL, Text TEXT NOT NULL);
            CREATE TABLE Meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        """
        do
            use seed = SqliteStore.openConnection dbPath false
            use cmd = seed.CreateCommand()
            cmd.CommandText <- phase1Sql
            cmd.ExecuteNonQuery() |> ignore
            // ImageCount + Image* 미존재 확인.
            Assert.DoesNotContain("ImageCount", tableColumns seed "Chunks")
            Assert.False(tableExists seed "ImageCache")
        // (b) 새 lib 의 ensureSchema 호출 → ImageCount + Image* 자동 추가.
        use upgraded = SqliteStore.openConnection dbPath false
        SqliteStore.ensureSchema upgraded
        Assert.Contains("ImageCount", tableColumns upgraded "Chunks")
        Assert.True(tableExists upgraded "ImageCache")
        Assert.True(tableExists upgraded "ImageReferences")
        // 자가 검열 M1: ensureSchema 만으로는 schema_version stale (Phase 1 = "1" 잔존).
        // stampVersion 까지 호출되어야 Meta 갱신 — Indexer 진입 경로는 자동 (needsRebuild → shadow rebuild → stampVersion).
        SqliteStore.stampVersion upgraded
        Assert.Equal(Some "3", SqliteStore.getMeta upgraded "schema_version")
        Assert.Equal(Some "1.3.0", SqliteStore.getMeta upgraded "indexer_version"))
