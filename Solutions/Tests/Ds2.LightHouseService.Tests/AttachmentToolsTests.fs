module Ds2.LightHouseService.Tests.AttachmentToolsTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.AspNetCore.Http
open Xunit
open Ds2.LightHouseService
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

/// AttachmentTools — Phase S3 MCP server-side host (4종) 의 wrapper 동작 검증.
/// Searcher / KnowledgeBase 자체는 parent §4.8 KnowledgeBaseTests 가 보호 — 본 test 는 *server-side 변환*
/// (fileId composition, SessionState lookup, 빈 active 셋 처리, fileId active 셋 밖 분기) 중점.

/// IHttpContextAccessor 의 in-memory 구현 — test 전용.
type private FakeAccessor(ctx: HttpContext) =
    interface IHttpContextAccessor with
        member _.HttpContext
            with get () = ctx
            and set _ = ()

/// 임시 collection 디렉토리 + 단일 .txt ingest + path 반환.
let private newCollectionWithText (text: string) : string =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "lh-att-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    let f = Path.Combine(dir, "doc.txt")
    File.WriteAllText(f, text, Encoding.UTF8)
    let extractors : IExtractor list = [ new TextExtractor() :> IExtractor ]
    let noProgress (_: IngestProgress) = ()
    Indexer.ingest dir extractors noProgress CancellationToken.None |> ignore
    dir

/// 본 test 의 resolver — 외부 collection id → 미리 ingest 된 dir 매핑.
let private mkResolver (idToPath: Map<string, string>) : AttachmentResolver =
    {
        Resolve = fun (ids: string array) ->
            let accepted = ResizeArray<string>()
            let paths = ResizeArray<string>()
            let unknown = ResizeArray<string>()
            for id in ids do
                match Map.tryFind id idToPath with
                | Some p ->
                    accepted.Add id
                    paths.Add p
                | None -> unknown.Add id
            {
                AcceptedIds = accepted.ToArray()
                Paths = paths.ToArray()
                UnknownIds = unknown.ToArray()
                UnindexableIds = [||]
            }
    }

/// 본 test 의 HttpContext 준비 — SessionState 박제.
let private newCtxWithSession (s: SessionState) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.Items.[SessionAuth.SessionItemKey] <- box s
    ctx :> HttpContext

let private cleanupDirs (dirs: string seq) =
    for d in dirs do
        try Directory.Delete(d, true) with _ -> ()


[<Fact>]
let ``attachment_list — 빈 active 셋 → "[]"`` () =
    let resolver = mkResolver Map.empty
    let reg = SessionRegistry(resolver) :> ISessionRegistry
    let r = reg.CreateSession([||], "alice")
    match reg.TryGet r.Token with
    | SessionLookup.Active s ->
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let result = AttachmentTools.attachment_list(accessor, resolver)
        Assert.Equal("[]", result)
    | _ -> Assert.Fail "Active 기대"


[<Fact>]
let ``attachment_list — fileId 가 <guid>:<docId> 형식 (MA23 D3 정합)`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "라인A 사양서 — 컨베이어 동작 설명"
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let result = AttachmentTools.attachment_list(accessor, resolver)
            // JSON parse 후 fileId prefix = collId
            let doc = JsonDocument.Parse result
            let root = doc.RootElement
            Assert.True(root.GetArrayLength() >= 1)
            let first = root.[0]
            let fileId = first.GetProperty("fileId").GetString()
            Assert.StartsWith(collId + ":", fileId)
            // attachment_list 의 cleanup
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``attachment_search — query hit + fileId guid prefix`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "컨베이어 vendor A 사양서"
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let result = AttachmentTools.attachment_search(accessor, resolver, "컨베이어", 5, null)
            let doc = JsonDocument.Parse result
            let results = doc.RootElement.GetProperty("results")
            Assert.True(results.GetArrayLength() >= 1)
            let firstFileId = results.[0].GetProperty("fileId").GetString()
            Assert.StartsWith(collId + ":", firstFileId)
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``attachment_search — fileId active 셋 밖 → 빈 결과 + hint 명시`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "vendor A"
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            // 다른 guid 의 fileId
            let bogusFileId = sprintf "%s:1" (Guid.NewGuid().ToString("D"))
            let result = AttachmentTools.attachment_search(accessor, resolver, "vendor", 5, bogusFileId)
            let doc = JsonDocument.Parse result
            Assert.Equal(0, doc.RootElement.GetProperty("results").GetArrayLength())
            let hint = doc.RootElement.GetProperty("hint").GetString()
            Assert.Equal("fileId not in active session", hint)
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``attachment_outline — fileId active 셋 밖 → "[]"`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "본문"
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let bogus = sprintf "%s:1" (Guid.NewGuid().ToString("D"))
            let result = AttachmentTools.attachment_outline(accessor, resolver, bogus)
            Assert.Equal("[]", result)
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``attachment_read — fileId active 셋 밖 → 빈 문자열`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "본문"
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let bogus = sprintf "%s:1" (Guid.NewGuid().ToString("D"))
            let result = AttachmentTools.attachment_read(accessor, resolver, bogus, "p=1")
            Assert.Equal("", result)
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``HttpContext 미존재 → InvalidOperationException (방어 — IHttpContextAccessor 미등록 회귀 가드)`` () =
    let resolver = mkResolver Map.empty
    let accessor =
        { new IHttpContextAccessor with
            member _.HttpContext
                with get () = null
                and set _ = () }
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        AttachmentTools.attachment_list(accessor, resolver) |> ignore)
    Assert.Contains("HttpContext 미존재", ex.Message)


[<Fact>]
let ``SessionState 미존재 (SessionAuth 미들웨어 미통과) → InvalidOperationException`` () =
    let resolver = mkResolver Map.empty
    let ctx = DefaultHttpContext() :> HttpContext   // Items 비어있음
    let accessor = FakeAccessor(ctx) :> IHttpContextAccessor
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        AttachmentTools.attachment_list(accessor, resolver) |> ignore)
    Assert.Contains("SessionState 미존재", ex.Message)
