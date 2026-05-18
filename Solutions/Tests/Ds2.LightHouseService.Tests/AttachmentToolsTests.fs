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
    Indexer.ingest dir extractors CaptionGenerator.noop noProgress CancellationToken.None |> ignore
    dir

/// 1×1 px PNG deterministic bytes — `Ds2.LightHouseService.Tests.SamplePng.bytes` SSOT (s6-r22 mn7).
let private samplePngBytes : byte[] = Ds2.LightHouseService.Tests.SamplePng.bytes

/// **s6-r21 fixture** — 본문 text ingest 후 ImageStore primitives 로 image fixture 박제.
/// `text` 안 RefLocator (TextExtractor 의 `body` 가 default) 에 image N 장 + caption 박제.
/// 반환 = (dir, fileId hint = "0:1" — TextExtractor 산물의 첫 docId 가정).
///
/// 의도: attachment_list / search 흐름은 그대로 두고, fileId 는 caller 가 list 응답에서 동적 획득.
/// 본 helper 는 dir + caption 박제만 책임 — image 가 chunks 의 RefLocator 와 매칭되도록 `body` 사용.
let private newCollectionWithImages
    (text: string)
    (imageCount: int)
    (captionTemplate: string option)
    : string =
    let dir = newCollectionWithText text
    // index.db 직접 open 후 image fixture 박제.
    let dbPath = SqliteStore.dbPath dir
    let conn = SqliteStore.openConnection dbPath false
    try
        // Documents 의 첫 row id + chunks 의 첫 RefLocator 획득 (TextExtractor = "p=%d" scheme).
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT d.Id, c.RefLocator
            FROM Documents d
            JOIN Chunks c ON c.DocumentId = d.Id
            ORDER BY d.Id, c.Id
            LIMIT 1
        """
        use reader = cmd.ExecuteReader()
        if not (reader.Read()) then failwith "fixture: chunks 미박제 — TextExtractor ingest 결과 검사 의무"
        let docId = reader.GetInt64 0
        let refLoc = reader.GetString 1
        reader.Close()
        for i = 1 to imageCount do
            // image bytes 가 i 마다 다르도록 마지막 byte 변조 → 다른 hash → cross-image dedup 비활성.
            let bytes = Array.copy samplePngBytes
            bytes.[bytes.Length - 1] <- byte (int bytes.[bytes.Length - 1] ^^^ i)
            let hash = ImageStore.computeSha256 bytes
            let storedPath = ImageStore.saveBlob dir hash Png bytes
            ImageStore.upsertImageCache conn hash storedPath Png (Some 1) (Some 1)
            ImageStore.addImageReference conn docId None hash refLoc i
            match captionTemplate with
            | Some t -> ImageStore.updateCaption conn hash (sprintf "%s #%d" t i) "claude-sonnet-4-6"
            | None -> ()
    finally
        conn.Close()
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn
        conn.Dispose()
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
let ``attachment_read — fileId active 셋 밖 → 빈 text content block`` () =
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
            // s6-r20: attachment_read 시그니처 확장 — (fileId, ref, includeImages, captionOnly) → ContentBlock[].
            let blocks = AttachmentTools.attachment_read(accessor, resolver, bogus, "p=1", false, true)
            Assert.Equal(1, blocks.Length)
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock as tb -> Assert.Equal("", tb.Text)
            | _ -> Assert.Fail "TextContentBlock 기대"
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``attachment_read — captionOnly mode → 단일 TextContentBlock + chunk 본문 포함 (s6-r20)`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "라인A 컨베이어 사양서 — 동작 설명 본문."
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            // 먼저 fileId 와 ref 를 list / search 로 획득.
            let listJson = AttachmentTools.attachment_list(accessor, resolver)
            let listDoc = JsonDocument.Parse listJson
            let fileId = listDoc.RootElement.[0].GetProperty("fileId").GetString()
            let searchJson = AttachmentTools.attachment_search(accessor, resolver, "컨베이어", 5, null)
            let searchDoc = JsonDocument.Parse searchJson
            let refLoc = searchDoc.RootElement.GetProperty("results").[0].GetProperty("ref").GetString()
            // captionOnly=true (image binary 미동봉).
            let blocks = AttachmentTools.attachment_read(accessor, resolver, fileId, refLoc, false, true)
            Assert.Equal(1, blocks.Length)
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock as tb ->
                Assert.Contains("컨베이어", tb.Text)
            | _ -> Assert.Fail "TextContentBlock 기대"
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``attachment_read — includeImages mode, image 0개 → text block 만 (s6-r20)`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "본문만 있는 txt 문서 — 이미지 없음."
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let listJson = AttachmentTools.attachment_list(accessor, resolver)
            let listDoc = JsonDocument.Parse listJson
            let fileId = listDoc.RootElement.[0].GetProperty("fileId").GetString()
            let searchJson = AttachmentTools.attachment_search(accessor, resolver, "이미지", 5, null)
            let searchDoc = JsonDocument.Parse searchJson
            let results = searchDoc.RootElement.GetProperty("results")
            Assert.True(results.GetArrayLength() >= 1)
            let refLoc = results.[0].GetProperty("ref").GetString()
            // includeImages=true, captionOnly=false — TextExtractor 산물이라 image 0개, 결과 text block 만.
            let blocks = AttachmentTools.attachment_read(accessor, resolver, fileId, refLoc, true, false)
            Assert.Equal(1, blocks.Length)
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock -> ()
            | _ -> Assert.Fail "image 가 없으면 image content block 없어야 함"
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


// ─── --review 실 image fixture (s6-r21) ─────────────────────────────────────────

/// 헬퍼: list + search 로 (fileId, refLoc) 획득.
let private resolveFileIdAndRef (accessor: IHttpContextAccessor) (resolver: AttachmentResolver) (query: string) =
    let listJson = AttachmentTools.attachment_list(accessor, resolver)
    let listDoc = JsonDocument.Parse listJson
    let fileId = listDoc.RootElement.[0].GetProperty("fileId").GetString()
    let searchJson = AttachmentTools.attachment_search(accessor, resolver, query, 5, null)
    let searchDoc = JsonDocument.Parse searchJson
    let results = searchDoc.RootElement.GetProperty("results")
    Assert.True(results.GetArrayLength() >= 1)
    let refLoc = results.[0].GetProperty("ref").GetString()
    fileId, refLoc

[<Fact>]
let ``attachment_read — includeImages mode, 단일 image fixture → text + ImageContentBlock base64 round-trip (s6-r21)`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithImages "라인A 컨베이어 사양서 본문." 1 (Some "1×1 PNG caption")
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let fileId, refLoc = resolveFileIdAndRef accessor resolver "컨베이어"
            let blocks = AttachmentTools.attachment_read(accessor, resolver, fileId, refLoc, true, false)
            // text block 1 + image block 1.
            Assert.Equal(2, blocks.Length)
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock as tb -> Assert.Contains("컨베이어", tb.Text)
            | _ -> Assert.Fail "blocks[0] TextContentBlock 기대"
            match blocks.[1] with
            | :? ModelContextProtocol.Protocol.ImageContentBlock as ib ->
                Assert.Equal("image/png", ib.MimeType)
                // C1 정합 검증 — FromBytes 의 DecodedData 가 원본 bytes 와 일치.
                let decoded = ib.DecodedData.ToArray()
                Assert.Equal(samplePngBytes.Length, decoded.Length)
                // 첫 byte 가 PNG signature (0x89).
                Assert.Equal(0x89uy, decoded.[0])
            | _ -> Assert.Fail "blocks[1] ImageContentBlock 기대"
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``attachment_read — captionOnly mode, image fixture → text block 안 caption enumeration (s6-r21)`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithImages "라인B 컨베이어 모터 다이어그램 설명." 2 (Some "모터 다이어그램")
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "bob")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let fileId, refLoc = resolveFileIdAndRef accessor resolver "컨베이어"
            // captionOnly=true → text block 1 + caption enumeration inline. image binary 미동봉.
            let blocks = AttachmentTools.attachment_read(accessor, resolver, fileId, refLoc, false, true)
            Assert.Equal(1, blocks.Length)
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock as tb ->
                Assert.Contains("컨베이어", tb.Text)
                Assert.Contains("[images]", tb.Text)
                Assert.Contains("모터 다이어그램 #1", tb.Text)
                Assert.Contains("모터 다이어그램 #2", tb.Text)
            | _ -> Assert.Fail "TextContentBlock 기대"
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``attachment_read — 6장+ image → 전량 caption_only 강등 + oversize_image_count footer (D-2-3 정합, s6-r21)`` () =
    let collId = Guid.NewGuid().ToString("D")
    // MaxImagesPerResponse = 5 초과.
    let dir = newCollectionWithImages "벤트필터 sheet." 6 (Some "필터 도식")
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "carol")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let fileId, refLoc = resolveFileIdAndRef accessor resolver "벤트필터"
            // includeImages=true 라도 image 6장 > 5 → 자동 caption_only 강등.
            let blocks = AttachmentTools.attachment_read(accessor, resolver, fileId, refLoc, true, false)
            Assert.Equal(1, blocks.Length)   // image block 없음 — caption_only 강등 결과.
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock as tb ->
                Assert.Contains("oversize_image_count=6", tb.Text)
                Assert.Contains("max=5", tb.Text)
                Assert.Contains("[images]", tb.Text)
            | _ -> Assert.Fail "TextContentBlock 기대"
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


/// **s6-r22 task 4 (image fixture oversize)** — 단일 image > MaxSingleImageBytes fixture.
/// blob 파일 size 가 lib `CaptionGenerator.MaxImageBytes` 초과하도록 dummy bytes 박제.
/// PNG signature 만 head 에 박제 (mime 추론은 path extension 기반이라 무관).
let private newCollectionWithOversizeImage (text: string) : string =
    let dir = newCollectionWithText text
    let dbPath = SqliteStore.dbPath dir
    let conn = SqliteStore.openConnection dbPath false
    try
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            SELECT d.Id, c.RefLocator FROM Documents d
            JOIN Chunks c ON c.DocumentId = d.Id
            ORDER BY d.Id, c.Id LIMIT 1
        """
        use reader = cmd.ExecuteReader()
        if not (reader.Read()) then failwith "fixture: chunks 미박제"
        let docId = reader.GetInt64 0
        let refLoc = reader.GetString 1
        reader.Close()
        // MaxImageBytes 초과 dummy bytes. (= 3.75MB + 1KB).
        let oversize = Array.create (Ds2.LightHouse.CaptionGenerator.MaxImageBytes + 1024) 0xAAuy
        // PNG signature head 박제 (mime 추론 정합 — `.png` extension).
        let sig0 = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
        Array.blit sig0 0 oversize 0 sig0.Length
        let hash = ImageStore.computeSha256 oversize
        let storedPath = ImageStore.saveBlob dir hash Png oversize
        ImageStore.upsertImageCache conn hash storedPath Png (Some 1) (Some 1)
        ImageStore.addImageReference conn docId None hash refLoc 1
        ImageStore.updateCaption conn hash "대용량 image" "claude-sonnet-4-6"
    finally
        conn.Close()
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn
        conn.Dispose()
    dir

[<Fact>]
let ``attachment_read — 단일 image > MaxSingleImageBytes → skip + oversize_image_count footer (s6-r22 task 4)`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithOversizeImage "라인C 대용량 image 사양서."
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "eve")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let fileId, refLoc = resolveFileIdAndRef accessor resolver "대용량"
            // includeImages=true → text block + oversize footer (image binary 미동봉 — skip).
            let blocks = AttachmentTools.attachment_read(accessor, resolver, fileId, refLoc, true, false)
            // image binary 미동봉 — text block 만 (1개).
            Assert.Equal(1, blocks.Length)
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock as tb ->
                Assert.Contains("oversize_image_count=1", tb.Text)
                Assert.Contains("max_single_bytes=", tb.Text)
            | _ -> Assert.Fail "TextContentBlock 기대"
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``attachment_read — caption 미생성 image → "(caption 미생성)" 표기 (s6-r21)`` () =
    let collId = Guid.NewGuid().ToString("D")
    // captionTemplate = None → caption NULL.
    let dir = newCollectionWithImages "image 미캡션 본문." 1 None
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "dave")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let fileId, refLoc = resolveFileIdAndRef accessor resolver "미캡션"
            let blocks = AttachmentTools.attachment_read(accessor, resolver, fileId, refLoc, false, true)
            Assert.Equal(1, blocks.Length)
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock as tb ->
                Assert.Contains("(caption 미생성)", tb.Text)
            | _ -> Assert.Fail "TextContentBlock 기대"
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
