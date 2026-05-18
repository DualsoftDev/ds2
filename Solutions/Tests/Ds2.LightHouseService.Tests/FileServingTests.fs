module Ds2.LightHouseService.Tests.FileServingTests

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Xunit
open Ds2.LightHouseService

/// Phase S4 file serving 단위 시험 (todo-lighthouse-kb-server.md §4.2 Phase S4 DoD).
///
/// 케이스:
/// - contentTypeOf — MIME 추정 (pdf/docx/xlsx/pptx/txt/md/csv/unknown)
/// - findSourceFile — basename + size match / size mismatch / 미존재 / sub-directory
/// - getFile — 정상 stream / 404 collection / 400 fileId / 404 docId / 404 source-file / 304 If-None-Match / Range
///
/// 시험 setup: temp storage root → Collections\<id>-<title>\ 디렉토리 + minimal index.db (SqliteStore 직접 INSERT)
/// + source\<basename> 파일 작성.

let private withTempRoot (action: string -> Task<'r>) : Task<'r> = task {
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lhs-fs-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try return! action dir
    finally
        // SqliteConnection.ClearPool 가 lookupDocument finally 에서 호출되지만,
        // Windows 의 file lock 가 잠시 잔존할 수 있어 Delete 실패 시 swallow.
        try Directory.Delete(dir, true) with _ -> ()
}

/// 한 collection 의 minimal index.db 생성. Documents 1행 INSERT 후 (id, originalPath, fileHash, sizeBytes) 반환.
let private setupCollection
    (storageRoot: string)
    (collectionId: string)
    (displayName: string)
    (sourceContent: byte array)
    (basenameOpt: string option)
    : int64 * string * string =
    let dirName = ZipImport.collectionDirName collectionId displayName
    let collectionRoot = Path.Combine(Storage.collectionsDir storageRoot, dirName)
    Directory.CreateDirectory collectionRoot |> ignore
    let sourceDir = Path.Combine(collectionRoot, "source")
    Directory.CreateDirectory sourceDir |> ignore

    let basename = defaultArg basenameOpt "doc.pdf"
    let sourceFile = Path.Combine(sourceDir, basename)
    File.WriteAllBytes(sourceFile, sourceContent)

    let dbPath = Ds2.LightHouse.SqliteStore.dbPath collectionRoot
    let conn = Ds2.LightHouse.SqliteStore.openConnection dbPath false
    try
        Ds2.LightHouse.SqliteStore.ensureSchema conn
        // FileHash 는 임의 sha256 hex 32 byte = 64 char.
        let hashBytes = System.Security.Cryptography.SHA256.HashData sourceContent
        let fileHash =
            (StringBuilder(), hashBytes)
            ||> Array.fold (fun sb b -> sb.AppendFormat("{0:x2}", b))
            |> string
        let docType : Ds2.LightHouse.FileKind = Ds2.LightHouse.Pdf
        let docId =
            Ds2.LightHouse.SqliteStore.insertDocument
                conn fileHash basename docType (int64 sourceContent.Length) (Some 1) (Some "title")
        docId, fileHash, basename
    finally
        conn.Close()
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn
        conn.Dispose()

let private registerCollection
    (storageRoot: string)
    (collectionId: string)
    (displayName: string)
    : Task<unit> = task {
    let dirName = ZipImport.collectionDirName collectionId displayName
    let entry : CollectionEntry = {
        Id = collectionId
        DisplayName = displayName
        IndexerVersion = "1.0.0"
        FileCount = 1
        TotalSourceBytes = 0L
        CreatedAt = "2026-05-17T00:00:00Z"
        ImportedAt = "2026-05-17T00:00:00Z"
        ImportedBy = "tester"
        StorageRelPath = sprintf "Collections\\%s\\" dirName
        Status = "idle"
        ErrorReason = null
        LastImportedAt = "2026-05-17T00:00:00Z"
    }
    do! Registry.upsertAsync storageRoot entry
}

/// `Results.File.ExecuteAsync` 가 RequestServices 에서 ILoggerFactory 등 GetRequiredService 호출 →
/// DefaultHttpContext 의 RequestServices=null 인 채로 호출 시 ArgumentNullException.
/// 본 helper 가 minimal ServiceProvider (AddLogging) 박제 — Range / ETag / FileStream 동작 보장.
let private buildMinimalServices () : IServiceProvider =
    let services = ServiceCollection()
    services.AddLogging() |> ignore
    services.BuildServiceProvider() :> IServiceProvider

let private newCtx (path: string) (headers: (string * string) list) : DefaultHttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- buildMinimalServices ()
    ctx.Request.Method <- "GET"
    ctx.Request.Path <- PathString path
    for (k, v) in headers do
        ctx.Request.Headers.[k] <- Microsoft.Extensions.Primitives.StringValues v
    ctx.Items.[AuthMiddleware.UserIdentityItemKey] <- box "tester"
    ctx.Response.Body <- new MemoryStream()
    ctx


// ── contentTypeOf ───────────────────────────────────────────────────────

[<Fact>]
let ``contentTypeOf — pdf/docx/xlsx/pptx/txt/md/csv 매핑`` () =
    Assert.Equal("application/pdf", FileServing.contentTypeOf "a.pdf")
    Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        FileServing.contentTypeOf "a.docx")
    Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        FileServing.contentTypeOf "a.xlsx")
    Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation",
        FileServing.contentTypeOf "a.pptx")
    Assert.Equal("text/plain; charset=utf-8", FileServing.contentTypeOf "a.txt")
    Assert.Equal("text/markdown; charset=utf-8", FileServing.contentTypeOf "a.md")
    Assert.Equal("text/csv; charset=utf-8", FileServing.contentTypeOf "a.csv")

[<Fact>]
let ``contentTypeOf — unknown 확장자 → octet-stream`` () =
    Assert.Equal("application/octet-stream", FileServing.contentTypeOf "a.unknown")
    Assert.Equal("application/octet-stream", FileServing.contentTypeOf "noext")
    Assert.Equal("application/octet-stream", FileServing.contentTypeOf "")

[<Fact>]
let ``contentTypeOf — html/htm 강제 octet-stream (review S4-m7 XSS 방어)`` () =
    // citation source 가 html 일 때 inline render 차단 — viewer 가 download 처리 강제.
    Assert.Equal("application/octet-stream", FileServing.contentTypeOf "a.html")
    Assert.Equal("application/octet-stream", FileServing.contentTypeOf "a.htm")

[<Fact>]
let ``contentTypeOf — case-insensitive (대문자 .PDF 도 application/pdf)`` () =
    Assert.Equal("application/pdf", FileServing.contentTypeOf "a.PDF")
    Assert.Equal("text/plain; charset=utf-8", FileServing.contentTypeOf "A.TXT")


// ── findSourceFile ──────────────────────────────────────────────────────

[<Fact>]
let ``findSourceFile — basename + size match → Some`` () = withTempRoot (fun root -> task {
    let sourceDir = Path.Combine(root, "source")
    Directory.CreateDirectory sourceDir |> ignore
    let fp = Path.Combine(sourceDir, "doc.pdf")
    let body = Array.replicate 100 (byte 0xAB)
    File.WriteAllBytes(fp, body)

    match FileServing.findSourceFile sourceDir "doc.pdf" 100L with
    | Some p -> Assert.Equal(Path.GetFullPath fp, Path.GetFullPath p)
    | None -> Assert.Fail "match 기대"
    return ()
})

[<Fact>]
let ``findSourceFile — basename 일치하지만 size 불일치 → None`` () = withTempRoot (fun root -> task {
    let sourceDir = Path.Combine(root, "source")
    Directory.CreateDirectory sourceDir |> ignore
    let fp = Path.Combine(sourceDir, "doc.pdf")
    File.WriteAllBytes(fp, Array.replicate 100 (byte 0xAB))

    Assert.True((FileServing.findSourceFile sourceDir "doc.pdf" 200L).IsNone)
    return ()
})

[<Fact>]
let ``findSourceFile — basename 자체 미존재 → None`` () = withTempRoot (fun root -> task {
    let sourceDir = Path.Combine(root, "source")
    Directory.CreateDirectory sourceDir |> ignore

    Assert.True((FileServing.findSourceFile sourceDir "ghost.pdf" 0L).IsNone)
    return ()
})

[<Fact>]
let ``findSourceFile — sourceRoot 자체 미존재 → None`` () = withTempRoot (fun root -> task {
    let sourceDir = Path.Combine(root, "missing-dir")
    Assert.True((FileServing.findSourceFile sourceDir "doc.pdf" 0L).IsNone)
    return ()
})

[<Fact>]
let ``findSourceFile — sub-directory 안 파일 검색 가능 (recursive)`` () = withTempRoot (fun root -> task {
    let sourceDir = Path.Combine(root, "source")
    let subDir = Path.Combine(sourceDir, "sub")
    Directory.CreateDirectory subDir |> ignore
    let fp = Path.Combine(subDir, "deep.pdf")
    let body = Array.replicate 50 (byte 0xCD)
    File.WriteAllBytes(fp, body)

    match FileServing.findSourceFile sourceDir "deep.pdf" 50L with
    | Some p -> Assert.Equal(Path.GetFullPath fp, Path.GetFullPath p)
    | None -> Assert.Fail "sub-directory 안 파일 발견 기대"
    return ()
})


// ── getFile ─────────────────────────────────────────────────────────────

[<Fact>]
let ``getFile — 정상 stream + 200 + Content-Type + body bytes match`` () = withTempRoot (fun root -> task {
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.replicate 256 (byte 0x42)
    let docId, _, basename = setupCollection root collectionId "MyDocs" body None
    do! registerCollection root collectionId "MyDocs"

    let ctx = newCtx (sprintf "/collections/%s/files/%d" collectionId docId) []
    do! FileServing.getFile root collectionId (string docId) ctx

    Assert.Equal(200, ctx.Response.StatusCode)
    Assert.Equal("application/pdf", ctx.Response.ContentType)
    ctx.Response.Body.Position <- 0L
    let received = (ctx.Response.Body :?> MemoryStream).ToArray()
    Assert.Equal(body.Length, received.Length)
    Assert.Equal<byte array>(body, received)
    // file name 박제 확인 (Content-Disposition or X-Filename — Results.File 의 fileDownloadName)
    let cd = ctx.Response.Headers.["Content-Disposition"].ToString()
    Assert.Contains(basename, cd)
})

[<Fact>]
let ``getFile — collection 미존재 → 404`` () = withTempRoot (fun root -> task {
    let ctx = newCtx "/collections/ghost/files/1" []
    do! FileServing.getFile root "ghost" "1" ctx
    Assert.Equal(404, ctx.Response.StatusCode)
})

[<Fact>]
let ``getFile — fileId Int64 아님 → 400`` () = withTempRoot (fun root -> task {
    let collectionId = Guid.NewGuid().ToString("D")
    do! registerCollection root collectionId "MyDocs"
    let body = Array.replicate 10 0uy
    setupCollection root collectionId "MyDocs" body None |> ignore

    let ctx = newCtx (sprintf "/collections/%s/files/not-a-number" collectionId) []
    do! FileServing.getFile root collectionId "not-a-number" ctx
    Assert.Equal(400, ctx.Response.StatusCode)
})

[<Fact>]
let ``getFile — documents.Id 미존재 → 404`` () = withTempRoot (fun root -> task {
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.replicate 10 0uy
    setupCollection root collectionId "MyDocs" body None |> ignore
    do! registerCollection root collectionId "MyDocs"

    let ctx = newCtx (sprintf "/collections/%s/files/99999" collectionId) []
    do! FileServing.getFile root collectionId "99999" ctx
    Assert.Equal(404, ctx.Response.StatusCode)
})

[<Fact>]
let ``getFile — db row 있지만 source 파일 없음 → 404`` () = withTempRoot (fun root -> task {
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.replicate 30 (byte 0x77)
    let docId, _, _ = setupCollection root collectionId "Coll" body None
    do! registerCollection root collectionId "Coll"

    // source 파일 직접 삭제 — db row 만 남김
    let collectionRoot =
        Path.Combine(Storage.collectionsDir root, ZipImport.collectionDirName collectionId "Coll")
    let sourceFile = Path.Combine(collectionRoot, "source", "doc.pdf")
    File.Delete sourceFile

    let ctx = newCtx (sprintf "/collections/%s/files/%d" collectionId docId) []
    do! FileServing.getFile root collectionId (string docId) ctx
    Assert.Equal(404, ctx.Response.StatusCode)
})

[<Fact>]
let ``getFile — If-None-Match: * → 304 (review S4-M2 RFC 7232)`` () = withTempRoot (fun root -> task {
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.replicate 40 (byte 0x33)
    let docId, _, _ = setupCollection root collectionId "Coll" body None
    do! registerCollection root collectionId "Coll"

    let ctx = newCtx
                (sprintf "/collections/%s/files/%d" collectionId docId)
                [ "If-None-Match", "*" ]
    do! FileServing.getFile root collectionId (string docId) ctx
    Assert.Equal(304, ctx.Response.StatusCode)
    Assert.Equal(0L, ctx.Response.Body.Length)
})

[<Fact>]
let ``getFile — If-None-Match 일치 → 304 + body 없음`` () = withTempRoot (fun root -> task {
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.replicate 80 (byte 0x55)
    let docId, fileHash, _ = setupCollection root collectionId "Coll" body None
    do! registerCollection root collectionId "Coll"

    let ctx = newCtx
                (sprintf "/collections/%s/files/%d" collectionId docId)
                [ "If-None-Match", sprintf "\"%s\"" fileHash ]
    do! FileServing.getFile root collectionId (string docId) ctx
    Assert.Equal(304, ctx.Response.StatusCode)
    Assert.Equal(0L, ctx.Response.Body.Length)
})

[<Fact>]
let ``getFile — If-None-Match 불일치 → 정상 200 stream`` () = withTempRoot (fun root -> task {
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.replicate 60 (byte 0x66)
    let docId, _, _ = setupCollection root collectionId "Coll" body None
    do! registerCollection root collectionId "Coll"

    let ctx = newCtx
                (sprintf "/collections/%s/files/%d" collectionId docId)
                [ "If-None-Match", "\"different-hash-value\"" ]
    do! FileServing.getFile root collectionId (string docId) ctx
    Assert.Equal(200, ctx.Response.StatusCode)
    Assert.Equal(int64 body.Length, ctx.Response.Body.Length)
})

[<Fact>]
let ``getFile — Range 헤더 처리 (bytes=0-9) → 206 + 10 byte body`` () = withTempRoot (fun root -> task {
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.init 100 (fun i -> byte i)
    let docId, _, _ = setupCollection root collectionId "Coll" body None
    do! registerCollection root collectionId "Coll"

    let ctx = newCtx
                (sprintf "/collections/%s/files/%d" collectionId docId)
                [ "Range", "bytes=0-9" ]
    do! FileServing.getFile root collectionId (string docId) ctx
    Assert.Equal(206, ctx.Response.StatusCode)
    Assert.Equal(10L, ctx.Response.Body.Length)
    ctx.Response.Body.Position <- 0L
    let received = (ctx.Response.Body :?> MemoryStream).ToArray()
    Assert.Equal<byte array>(body.[0..9], received)
})

[<Fact>]
let ``getFile — ETag 헤더 박제 (sha256 hex)`` () = withTempRoot (fun root -> task {
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.replicate 20 (byte 0x99)
    let docId, fileHash, _ = setupCollection root collectionId "Coll" body None
    do! registerCollection root collectionId "Coll"

    let ctx = newCtx (sprintf "/collections/%s/files/%d" collectionId docId) []
    do! FileServing.getFile root collectionId (string docId) ctx
    Assert.Equal(200, ctx.Response.StatusCode)
    let etag = ctx.Response.Headers.["ETag"].ToString()
    Assert.Contains(fileHash, etag)
})


// ── P6 follow-up (s6-r6): 200/304 ETag 일관성 + 416 / If-Range ──────────
// 자가 검열 m2: 416 / If-Range Fact 3건은 ASP.NET Core 9.0 Results.File 위임 가정 — major upgrade 시 재검증.

[<Fact>]
let ``getFile — 304 분기에도 ETag 헤더 박제 (P6-M3 200/304 일관성)`` () = withTempRoot (fun root -> task {
    // review S4-M3: 304 응답에서도 caller 가 ETag 확인 가능해야 함 (RFC 7232 §4.1).
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.replicate 50 (byte 0x44)
    let docId, fileHash, _ = setupCollection root collectionId "Coll" body None
    do! registerCollection root collectionId "Coll"

    let ctx = newCtx
                (sprintf "/collections/%s/files/%d" collectionId docId)
                [ "If-None-Match", sprintf "\"%s\"" fileHash ]
    do! FileServing.getFile root collectionId (string docId) ctx
    Assert.Equal(304, ctx.Response.StatusCode)
    let etag = ctx.Response.Headers.["ETag"].ToString()
    Assert.Contains(fileHash, etag)
    // 304 분기와 200 분기의 ETag 형식 일관성 — 둘 다 quoted hash.
    Assert.StartsWith("\"", etag)
    Assert.EndsWith("\"", etag)
})

[<Fact>]
let ``getFile — Range 가 파일 크기 초과 → 416 Range Not Satisfiable (P6-m6)`` () = withTempRoot (fun root -> task {
    // review S4-m6: ASP.NET Core Results.File 위임 정합 박제 — invalid Range 분기.
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.init 100 (fun i -> byte i)
    let docId, _, _ = setupCollection root collectionId "Coll" body None
    do! registerCollection root collectionId "Coll"

    let ctx = newCtx
                (sprintf "/collections/%s/files/%d" collectionId docId)
                [ "Range", "bytes=500-999" ]  // file size = 100, 500-999 은 범위 밖
    do! FileServing.getFile root collectionId (string docId) ctx
    Assert.Equal(416, ctx.Response.StatusCode)
})

[<Fact>]
let ``getFile — If-Range ETag 매치 → 206 partial (P6-m6)`` () = withTempRoot (fun root -> task {
    // review S4-m6: If-Range 가 현재 ETag 와 일치 → Range 처리 → 206.
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.init 100 (fun i -> byte i)
    let docId, fileHash, _ = setupCollection root collectionId "Coll" body None
    do! registerCollection root collectionId "Coll"

    let ctx = newCtx
                (sprintf "/collections/%s/files/%d" collectionId docId)
                [ "Range", "bytes=0-9"
                  "If-Range", sprintf "\"%s\"" fileHash ]
    do! FileServing.getFile root collectionId (string docId) ctx
    Assert.Equal(206, ctx.Response.StatusCode)
    Assert.Equal(10L, ctx.Response.Body.Length)
})

[<Fact>]
let ``getFile — If-Range ETag 불일치 → 200 full body (P6-m6)`` () = withTempRoot (fun root -> task {
    // review S4-m6: If-Range 가 stale ETag → Range 무시 → 200 + 전체 body.
    let collectionId = Guid.NewGuid().ToString("D")
    let body = Array.init 100 (fun i -> byte i)
    let docId, _, _ = setupCollection root collectionId "Coll" body None
    do! registerCollection root collectionId "Coll"

    let ctx = newCtx
                (sprintf "/collections/%s/files/%d" collectionId docId)
                [ "Range", "bytes=0-9"
                  "If-Range", "\"stale-etag-value\"" ]
    do! FileServing.getFile root collectionId (string docId) ctx
    Assert.Equal(200, ctx.Response.StatusCode)
    Assert.Equal(int64 body.Length, ctx.Response.Body.Length)
})
