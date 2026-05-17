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
