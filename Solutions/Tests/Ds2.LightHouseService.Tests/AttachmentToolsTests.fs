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
    Indexer.ingest dir extractors CaptionGenerator.noop None noProgress CancellationToken.None |> ignore
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
        let result = AttachmentTools.attachment_list(accessor, reg)
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
            let result = AttachmentTools.attachment_list(accessor, reg)
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
            let result = AttachmentTools.attachment_search(accessor, reg, "컨베이어", 5, null)
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
            let result = AttachmentTools.attachment_search(accessor, reg, "vendor", 5, bogusFileId)
            let doc = JsonDocument.Parse result
            Assert.Equal(0, doc.RootElement.GetProperty("results").GetArrayLength())
            let hint = doc.RootElement.GetProperty("hint").GetString()
            Assert.Equal("fileId not in active session", hint)
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


// **s6-r54 M11 (보안 sweep)** — attachment_search DoS amplification 가드.
// topK upper bound clamp + query length truncate, hint 박제로 caller 가 검출 가능.

[<Fact>]
let ``M11 — topK upper bound clamp (>100 → 100, hint 박제)`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "컨베이어 vendor A"
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            // topK = 5000 → clamp to 100, hint 박제
            let result = AttachmentTools.attachment_search(accessor, reg, "vendor", 5000, null)
            let doc = JsonDocument.Parse result
            let hint = doc.RootElement.GetProperty("hint").GetString()
            Assert.Contains("topK clamped to 100", hint)
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]

[<Fact>]
let ``M11 — query length truncate (>1024 → 1024, hint 박제)`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "vendor A"
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            // 2048 char query → 1024 로 truncate, hint 박제
            let longQuery = String.replicate 2048 "a"
            let result = AttachmentTools.attachment_search(accessor, reg, longQuery, 5, null)
            let doc = JsonDocument.Parse result
            let hint = doc.RootElement.GetProperty("hint").GetString()
            Assert.Contains("query truncated to 1024 chars", hint)
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
            let result = AttachmentTools.attachment_outline(accessor, reg, bogus)
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
            let blocks = AttachmentTools.attachment_read(accessor, reg, bogus, "p=1", false, true)
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
            let listJson = AttachmentTools.attachment_list(accessor, reg)
            let listDoc = JsonDocument.Parse listJson
            let fileId = listDoc.RootElement.[0].GetProperty("fileId").GetString()
            let searchJson = AttachmentTools.attachment_search(accessor, reg, "컨베이어", 5, null)
            let searchDoc = JsonDocument.Parse searchJson
            let refLoc = searchDoc.RootElement.GetProperty("results").[0].GetProperty("ref").GetString()
            // captionOnly=true (image binary 미동봉).
            let blocks = AttachmentTools.attachment_read(accessor, reg, fileId, refLoc, false, true)
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
            let listJson = AttachmentTools.attachment_list(accessor, reg)
            let listDoc = JsonDocument.Parse listJson
            let fileId = listDoc.RootElement.[0].GetProperty("fileId").GetString()
            let searchJson = AttachmentTools.attachment_search(accessor, reg, "이미지", 5, null)
            let searchDoc = JsonDocument.Parse searchJson
            let results = searchDoc.RootElement.GetProperty("results")
            Assert.True(results.GetArrayLength() >= 1)
            let refLoc = results.[0].GetProperty("ref").GetString()
            // includeImages=true, captionOnly=false — TextExtractor 산물이라 image 0개, 결과 text block 만.
            let blocks = AttachmentTools.attachment_read(accessor, reg, fileId, refLoc, true, false)
            Assert.Equal(1, blocks.Length)
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock -> ()
            | _ -> Assert.Fail "image 가 없으면 image content block 없어야 함"
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


// **s6-r58 C4 (보안 sweep + cosmetic)** — attachment_read ref EBNF 검증 (§3.13 SSOT).
// EBNF 위반 ref (e.g. `page=14` / `p` / `p=`) 시 graceful marker + chunk 조회 skip.

[<Fact>]
let ``C4 — attachment_read ref EBNF 위반 (page=14) → marker text block`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "본문 텍스트 — 어떤 내용"
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let listJson = AttachmentTools.attachment_list(accessor, reg)
            let listDoc = JsonDocument.Parse listJson
            let fileId = listDoc.RootElement.[0].GetProperty("fileId").GetString()
            // ref EBNF 위반 — Unit token "page" 미정합 (§3.13 = p|slide|sheet 만)
            let blocks = AttachmentTools.attachment_read(accessor, reg, fileId, "page=14", false, true)
            Assert.Equal(1, blocks.Length)
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock as tb ->
                Assert.Contains("ref EBNF 위반", tb.Text)
                Assert.Contains("page=14", tb.Text)
            | _ -> Assert.Fail "TextContentBlock 기대"
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]

// **s6-r58 C5 — attachment_read image mode 5-case SSOT 정합**.
// case 4 (둘 다 false) 의 caption_only fall-back + audit log marker 의무 박제 검증.

[<Fact>]
let ``C5 — attachment_read 두 flag 모두 false (case 4) → caption_only fall-back text block`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "본문 텍스트"
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let listJson = AttachmentTools.attachment_list(accessor, reg)
            let listDoc = JsonDocument.Parse listJson
            let fileId = listDoc.RootElement.[0].GetProperty("fileId").GetString()
            let searchJson = AttachmentTools.attachment_search(accessor, reg, "본문", 5, null)
            let searchDoc = JsonDocument.Parse searchJson
            let results = searchDoc.RootElement.GetProperty("results")
            if results.GetArrayLength() = 0 then
                lock s.SyncRoot (fun () -> SessionKb.dispose s)
            else
                let refLoc = results.[0].GetProperty("ref").GetString()
                // case 4: includeImages=false, captionOnly=false → caption-only fall-back path.
                // image 0개 라 결과 text block 만 (audit log warn 박제 확인은 별 path — 본 fact 는 wire 정합).
                let blocks = AttachmentTools.attachment_read(accessor, reg, fileId, refLoc, false, false)
                Assert.Equal(1, blocks.Length)
                match blocks.[0] with
                | :? ModelContextProtocol.Protocol.TextContentBlock as tb ->
                    // case 4 fall-back path 정합 — text block + image block 없음 + ref EBNF marker 없음 (p=N valid).
                    Assert.DoesNotContain("ref EBNF 위반", tb.Text)
                | _ -> Assert.Fail "TextContentBlock 기대 (case 4 fall-back)"
                lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


[<Fact>]
let ``C4 — attachment_read ref EBNF valid (p=1) → 정상 chunk 본문`` () =
    let collId = Guid.NewGuid().ToString("D")
    let dir = newCollectionWithText "test body — content"
    try
        let resolver = mkResolver (Map.ofList [ collId, dir ])
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let r = reg.CreateSession([| collId |], "alice")
        match reg.TryGet r.Token with
        | SessionLookup.Active s ->
            let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
            let listJson = AttachmentTools.attachment_list(accessor, reg)
            let listDoc = JsonDocument.Parse listJson
            let fileId = listDoc.RootElement.[0].GetProperty("fileId").GetString()
            // p=1 = EBNF valid. chunk 조회 진행 (text-only file 이라 0 row 가능, marker 부재 검증).
            let blocks = AttachmentTools.attachment_read(accessor, reg, fileId, "p=1", false, true)
            Assert.Equal(1, blocks.Length)
            match blocks.[0] with
            | :? ModelContextProtocol.Protocol.TextContentBlock as tb ->
                Assert.DoesNotContain("ref EBNF 위반", tb.Text)
            | _ -> Assert.Fail "TextContentBlock 기대"
            lock s.SyncRoot (fun () -> SessionKb.dispose s)
        | _ -> Assert.Fail "Active 기대"
    finally cleanupDirs [ dir ]


// ─── --review 실 image fixture (s6-r21) ─────────────────────────────────────────

/// 헬퍼: list + search 로 (fileId, refLoc) 획득.
let private resolveFileIdAndRef (accessor: IHttpContextAccessor) (reg: ISessionRegistry) (query: string) =
    let listJson = AttachmentTools.attachment_list(accessor, reg)
    let listDoc = JsonDocument.Parse listJson
    let fileId = listDoc.RootElement.[0].GetProperty("fileId").GetString()
    let searchJson = AttachmentTools.attachment_search(accessor, reg, query, 5, null)
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
            let fileId, refLoc = resolveFileIdAndRef accessor reg "컨베이어"
            let blocks = AttachmentTools.attachment_read(accessor, reg, fileId, refLoc, true, false)
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
            let fileId, refLoc = resolveFileIdAndRef accessor reg "컨베이어"
            // captionOnly=true → text block 1 + caption enumeration inline. image binary 미동봉.
            let blocks = AttachmentTools.attachment_read(accessor, reg, fileId, refLoc, false, true)
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
            let fileId, refLoc = resolveFileIdAndRef accessor reg "벤트필터"
            // includeImages=true 라도 image 6장 > 5 → 자동 caption_only 강등.
            let blocks = AttachmentTools.attachment_read(accessor, reg, fileId, refLoc, true, false)
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
            let fileId, refLoc = resolveFileIdAndRef accessor reg "대용량"
            // includeImages=true → text block + oversize footer (image binary 미동봉 — skip).
            let blocks = AttachmentTools.attachment_read(accessor, reg, fileId, refLoc, true, false)
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
            let fileId, refLoc = resolveFileIdAndRef accessor reg "미캡션"
            let blocks = AttachmentTools.attachment_read(accessor, reg, fileId, refLoc, false, true)
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
    let reg = SessionRegistry(resolver) :> ISessionRegistry
    let accessor =
        { new IHttpContextAccessor with
            member _.HttpContext
                with get () = null
                and set _ = () }
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        AttachmentTools.attachment_list(accessor, reg) |> ignore)
    Assert.Contains("HttpContext 미존재", ex.Message)


[<Fact>]
let ``SessionState 미존재 (SessionAuth 미들웨어 미통과) → InvalidOperationException`` () =
    let resolver = mkResolver Map.empty
    let reg = SessionRegistry(resolver) :> ISessionRegistry
    let ctx = DefaultHttpContext() :> HttpContext   // Items 비어있음
    let accessor = FakeAccessor(ctx) :> IHttpContextAccessor
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        AttachmentTools.attachment_list(accessor, reg) |> ignore)
    Assert.Contains("SessionState 미존재", ex.Message)


/// **PR-D (todo-lighthouse-index-summary.md §3.2 + §5)** — attachment_fulltext fixture.
/// storageRoot 폴더 + Registry entry + text/ 폴더 + markdown file 박제 후 호출.
let private newCollectionWithTextDump
    (storageRoot: string)
    (collGuid: string)
    (displayName: string)
    (docId: int64)
    (markdown: string)
    : unit =
    let collDir = AttachmentResolver.collectionPath storageRoot collGuid displayName
    Directory.CreateDirectory collDir |> ignore
    let textDir = Path.Combine(collDir, Ds2.LightHouse.Protocol.ZipLayout.KbFolderName, "text")
    Directory.CreateDirectory textDir |> ignore
    let filename = sprintf "%d-spec.md" docId
    File.WriteAllText(Path.Combine(textDir, filename), markdown, Encoding.UTF8)

let private newPrDCfg (storageRoot: string) : ServiceConfig =
    ServiceConfigBuilder.defaultConfig "http://localhost:0" storageRoot

let private upsertPrDEntry (storageRoot: string) (collGuid: string) (displayName: string) : System.Threading.Tasks.Task<unit> =
    let entry : CollectionEntry = {
        Id = collGuid
        DisplayName = displayName
        IndexerVersion = "2.2.0"
        FileCount = 1
        TotalSourceBytes = 100L
        CreatedAt = "2026-05-21T00:00:00Z"
        ImportedAt = "2026-05-21T00:00:00Z"
        ImportedBy = "tester"
        StorageRelPath = sprintf "Collections\\%s-%s" collGuid displayName
        Status = "idle"
        ErrorReason = null
        LastImportedAt = "2026-05-21T00:00:00Z"
        Description = ""
        Keywords = [||]
        Acl = Unchecked.defaultof<CollectionAcl>
    }
    Registry.upsertAsync storageRoot entry


[<Fact>]
let ``PR-D — 정상 fulltext 반환 (text dump file 존재)`` () =
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-prd-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        let collGuid = Guid.NewGuid().ToString("D")
        let displayName = "specdoc"
        let docId = 42L
        let body = "# spec.txt\n\n컨베이어 사양 본문\n\n## p.1\n\n모터 12A"
        upsertPrDEntry storageRoot collGuid displayName |> fun t -> t.Wait()
        newCollectionWithTextDump storageRoot collGuid displayName docId body
        let cfg = newPrDCfg storageRoot
        // SessionState 박제 — CollectionIds 에 guid 포함
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok-test"; UserIdentity = "tester"
            CollectionIds = [| collGuid |]
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let result = AttachmentTools.attachment_fulltext(accessor, reg, cfg, sprintf "%s:%d" collGuid docId)
        Assert.Contains("컨베이어 사양 본문", result)
        Assert.Contains("# spec.txt", result)
    finally cleanupDirs [ storageRoot ]


[<Fact>]
let ``PR-D — fileId 형식 결함 → empty + audit warn`` () =
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-prd-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok"; UserIdentity = "u"; CollectionIds = [||]
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let cfg = newPrDCfg storageRoot
        // 잘못된 형식 (콜론 없음)
        let r1 = AttachmentTools.attachment_fulltext(accessor, reg, cfg, "invalid-no-colon")
        Assert.Equal("", r1)
        // null fileId
        let r2 = AttachmentTools.attachment_fulltext(accessor, reg, cfg, null)
        Assert.Equal("", r2)
        // 두 콜론 이상
        let r3 = AttachmentTools.attachment_fulltext(accessor, reg, cfg, "a:b:c")
        Assert.Equal("", r3)
    finally cleanupDirs [ storageRoot ]


[<Fact>]
let ``PR-D — session active 셋 밖 collection → empty + audit warn`` () =
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-prd-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        let collGuid = Guid.NewGuid().ToString("D")
        let otherGuid = Guid.NewGuid().ToString("D")
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok"; UserIdentity = "u"
            CollectionIds = [| collGuid |]  // 다른 guid 만 active
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let cfg = newPrDCfg storageRoot
        // otherGuid 는 session active 셋 밖 → 빈 string
        let r = AttachmentTools.attachment_fulltext(accessor, reg, cfg, sprintf "%s:1" otherGuid)
        Assert.Equal("", r)
    finally cleanupDirs [ storageRoot ]


[<Fact>]
let ``PR-D — text/ 폴더 부재 (legacy collection) → empty`` () =
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-prd-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        let collGuid = Guid.NewGuid().ToString("D")
        let displayName = "legacy"
        // Registry entry + collection dir 만 박제, text/ 부재 (PR-C 이전 색인 시뮬레이션)
        upsertPrDEntry storageRoot collGuid displayName |> fun t -> t.Wait()
        let collDir = AttachmentResolver.collectionPath storageRoot collGuid displayName
        Directory.CreateDirectory collDir |> ignore
        Directory.CreateDirectory (Path.Combine(collDir, Ds2.LightHouse.Protocol.ZipLayout.KbFolderName)) |> ignore
        let cfg = newPrDCfg storageRoot
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok"; UserIdentity = "u"; CollectionIds = [| collGuid |]
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let r = AttachmentTools.attachment_fulltext(accessor, reg, cfg, sprintf "%s:1" collGuid)
        Assert.Equal("", r)
    finally cleanupDirs [ storageRoot ]


[<Fact>]
let ``PR-D — 1MB cap + truncate footer`` () =
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-prd-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        let collGuid = Guid.NewGuid().ToString("D")
        let displayName = "huge"
        let docId = 99L
        // 2MB markdown (1MB cap 초과)
        let body = String.replicate 60000 "abcdefghijklmnopqrstuvwxyz0123456\n"  // ~2MB
        upsertPrDEntry storageRoot collGuid displayName |> fun t -> t.Wait()
        newCollectionWithTextDump storageRoot collGuid displayName docId body
        let cfg = newPrDCfg storageRoot
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok"; UserIdentity = "u"; CollectionIds = [| collGuid |]
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let r = AttachmentTools.attachment_fulltext(accessor, reg, cfg, sprintf "%s:%d" collGuid docId)
        let bytes = Encoding.UTF8.GetByteCount r
        Assert.True(bytes <= 1048576 + 200, sprintf "size (%d) > 1MB cap + footer 여유" bytes)
        Assert.Contains("fulltext truncated at 1MB", r)
    finally cleanupDirs [ storageRoot ]


/// **PR-H2 (todo-lighthouse-index-summary.md §11)** — attachment_summary fixture.
/// storageRoot + Registry entry + summary.md 박제 후 호출.
let private newCollectionWithSummary
    (storageRoot: string)
    (collGuid: string)
    (displayName: string)
    (markdown: string)
    : unit =
    let collDir = AttachmentResolver.collectionPath storageRoot collGuid displayName
    Directory.CreateDirectory collDir |> ignore
    let kbDir = Path.Combine(collDir, Ds2.LightHouse.Protocol.ZipLayout.KbFolderName)
    Directory.CreateDirectory kbDir |> ignore
    File.WriteAllText(Path.Combine(kbDir, "summary.md"), markdown, Encoding.UTF8)


[<Fact>]
let ``PR-H2 — 정상 summary 반환 (summary.md 존재)`` () =
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-prh2-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        let collGuid = Guid.NewGuid().ToString("D")
        let displayName = "summdoc"
        let body = "# Collection Summary\n\n| 원본 | text dump | 요약 |\n|---|---|---|\n| a.txt | text/1-a.md | A 본문 요약 |"
        upsertPrDEntry storageRoot collGuid displayName |> fun t -> t.Wait()
        newCollectionWithSummary storageRoot collGuid displayName body
        let cfg = newPrDCfg storageRoot
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok"; UserIdentity = "u"; CollectionIds = [| collGuid |]
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let result = AttachmentTools.attachment_summary(accessor, reg, cfg, collGuid, false)
        Assert.Contains("# Collection Summary", result)
        Assert.Contains("A 본문 요약", result)
    finally cleanupDirs [ storageRoot ]


[<Fact>]
let ``PR-H2 — collectionId null/empty → empty + audit warn`` () =
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-prh2-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        let cfg = newPrDCfg storageRoot
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok"; UserIdentity = "u"; CollectionIds = [| "any" |]
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let r1 = AttachmentTools.attachment_summary(accessor, reg, cfg, null, false)
        Assert.Equal("", r1)
        let r2 = AttachmentTools.attachment_summary(accessor, reg, cfg, "  ", false)
        Assert.Equal("", r2)
    finally cleanupDirs [ storageRoot ]


[<Fact>]
let ``PR-H2 — collection 가 session active 셋 밖 → empty + audit warn`` () =
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-prh2-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        let activeColl = Guid.NewGuid().ToString("D")
        let otherColl = Guid.NewGuid().ToString("D")
        let cfg = newPrDCfg storageRoot
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok"; UserIdentity = "u"; CollectionIds = [| activeColl |]
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let r = AttachmentTools.attachment_summary(accessor, reg, cfg, otherColl, false)
        Assert.Equal("", r)
    finally cleanupDirs [ storageRoot ]


[<Fact>]
let ``PR-H2 — Registry 미존재 → empty + audit warn`` () =
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-prh2-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        let collGuid = Guid.NewGuid().ToString("D")
        // Registry 미upsert — tryFindById None 분기
        let cfg = newPrDCfg storageRoot
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok"; UserIdentity = "u"; CollectionIds = [| collGuid |]
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let r = AttachmentTools.attachment_summary(accessor, reg, cfg, collGuid, false)
        Assert.Equal("", r)
    finally cleanupDirs [ storageRoot ]


[<Fact>]
let ``PR-H2 — legacy collection (summary.md 부재) → empty + audit info`` () =
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-prh2-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        let collGuid = Guid.NewGuid().ToString("D")
        let displayName = "legacy"
        upsertPrDEntry storageRoot collGuid displayName |> fun t -> t.Wait()
        // 의도적으로 summary.md 미박제 — Registry 만 있고 file 없음 (PR-H1 이전 색인 시뮬레이션)
        let collDir = AttachmentResolver.collectionPath storageRoot collGuid displayName
        Directory.CreateDirectory collDir |> ignore
        let cfg = newPrDCfg storageRoot
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok"; UserIdentity = "u"; CollectionIds = [| collGuid |]
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        let r = AttachmentTools.attachment_summary(accessor, reg, cfg, collGuid, false)
        Assert.Equal("", r)
    finally cleanupDirs [ storageRoot ]


[<Fact>]
let ``attachment_summary includeSpecialized=true → summary.md + summary/*.md 합본 (false 는 미포함)`` () =
    // 사용자 지시 2026-06-01 — layer E (specialized digest) MCP fetch: includeSpecialized=true 시 summary/*.md
    // 합본 동봉. false 는 기존 동작 (summary.md 만, LLM overview 호환) 유지.
    let storageRoot = Path.Combine(Path.GetTempPath(), sprintf "lh-spec-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory storageRoot |> ignore
    try
        // 옵션 A (2026-05-30, CollectionEndpoints.fs §3.1) — collectionId = sanitizeTitle(폴더명).
        // server 발급 GUID 식별자 폐기 정합 — collection id 는 폴더명 문자열.
        let collId = "spec-folder"
        let displayName = "spec-folder"
        let body = "# Collection Summary\n\n| 원본 | text dump | 요약 |\n|---|---|---|\n| a.txt | text/1-a.md | A 요약 |"
        upsertPrDEntry storageRoot collId displayName |> fun t -> t.Wait()
        newCollectionWithSummary storageRoot collId displayName body
        // summary/ 디렉토리에 specialized markdown (user-guide 전환물 모사) 박제.
        let collDir = AttachmentResolver.collectionPath storageRoot collId displayName
        let summaryDir = Path.Combine(collDir, Ds2.LightHouse.Protocol.ZipLayout.KbFolderName, "summary")
        Directory.CreateDirectory summaryDir |> ignore
        File.WriteAllText(Path.Combine(summaryDir, "_user-guide-01-test.md"), "GUIDE_CANARY_BODY_XYZ", Encoding.UTF8)
        let cfg = newPrDCfg storageRoot
        let resolver = mkResolver Map.empty
        let reg = SessionRegistry(resolver) :> ISessionRegistry
        let s : SessionState = {
            Token = "tok"; UserIdentity = "u"; CollectionIds = [| collId |]
            LastUsedAt = DateTime.UtcNow
            Kb = None; SyncRoot = obj()
        }
        let accessor = FakeAccessor(newCtxWithSession s) :> IHttpContextAccessor
        // false → summary.md 만 (specialized 미포함).
        let rFalse = AttachmentTools.attachment_summary(accessor, reg, cfg, collId, false)
        Assert.Contains("# Collection Summary", rFalse)
        Assert.DoesNotContain("GUIDE_CANARY_BODY_XYZ", rFalse)
        // true → summary.md + summary/*.md 합본 (둘 다 포함).
        let rTrue = AttachmentTools.attachment_summary(accessor, reg, cfg, collId, true)
        Assert.Contains("# Collection Summary", rTrue)
        Assert.Contains("GUIDE_CANARY_BODY_XYZ", rTrue)
    finally cleanupDirs [ storageRoot ]
