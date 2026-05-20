namespace Ds2.LightHouseService

open System
open System.ComponentModel
open System.IO
open System.Text.Json
open Microsoft.AspNetCore.Http
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server

/// Phase S3 MCP tool host — server 측 4종 (todo-lighthouse-kb-server.md §3.1.3 / §3.10 / §4.2 Phase S3).
///
/// MCP host 2개 정책 (§3.1.3): Promaker in-process = mutation tool (apply_model_doc 등), service = read tool (attachment_*).
/// LLM 의 `.mcp-config` 에 server 2개 등록 — tool 14종 자연 공존 (이름 중복 0).
///
/// 본 type 은 `WithToolsFromAssembly()` 가 reflection 으로 발견 — `[<McpServerToolType>]` attribute SSOT.
/// 각 메서드의 `IHttpContextAccessor` 인자 = MCP HTTP transport 의 현재 request context 접근. SDK 1.2.0 의
/// AIFunctionMcpServerTool binder 가 `IServiceProviderIsService.IsService(type)` 로 등록 type 자동 검출 후
/// DI 주입 + JSON schema 에서 제외 (Promaker `ModelTools.cs` 동일 패턴).
///
/// 동작 전제:
/// - `SessionAuth.middleware` 가 X-LightHouse-Session 헤더 검증 후 `HttpContext.Items[SessionAuth.SessionItemKey]`
///   에 `SessionState` 박제. 본 type 의 메서드는 valid session 가정 — 누락 시 InvalidOperationException (방어).
/// - per-session `SyncRoot` lock 안에서 `SessionKb.attach` + Searcher 호출 직렬화 — Kb Dispose/swap 과 race 회피.
[<McpServerToolType>]
type AttachmentTools() =

    // 본 type 의 모든 메서드는 static — instance state 없음 (모든 state 는 session 의 SessionState).

    /// JSON 직렬화 옵션 (camelCase property name 정렬은 SDK 가 schema 자동 생성 시 처리, 본 직렬화는 응답 body).
    static let jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = false)
        opts.Converters.Add(System.Text.Json.Serialization.JsonStringEnumConverter())
        opts

    /// Phase 2 task D-iv (s6-r20) — single image bytes 한도. lib `Ds2.LightHouse.CaptionGenerator.MaxImageBytes`
    /// 직접 참조 (자가 검열 m1 정합) — SSOT drift 차단. F# `[<Literal>]` cross-assembly 참조 OK.
    static let MaxSingleImageBytes = Ds2.LightHouse.CaptionGenerator.MaxImageBytes

    /// Phase 2 task D-iv (s6-r20) — `attachment_read` 응답 한 응답당 image 최대 5장 (D-2-3 정합).
    [<Literal>]
    static let MaxImagesPerResponse = 5

    /// **s6-r25 (m2)** — `[marker]` text 를 textBuilder 끝에 blank line 분리해서 append. 본문이 비어
    /// 있으면 (length=0) 첫 줄에 marker 시작, 본문이 있으면 두 줄 띄우고 marker 진입. attachment_read 의
    /// degraded / oversize / read_fail 3 marker 패턴 SSOT.
    static let appendMarker (sb: System.Text.StringBuilder) (markerText: string) =
        if sb.Length > 0 then sb.AppendLine() |> ignore
        sb.AppendLine() |> ignore
        sb.Append(markerText: string) |> ignore

    /// **--review M3 정합 (s6-r21)** — `ImageCache.MimeType` 빈/NULL row 의 mime 추론 (확장자 기반).
    /// 정상 색인 경로 (`Indexer.ingestImagesIntoStore`) 는 항상 mime 박제 (`ImageStore.mimeOf`).
    ///
    /// **s6-r22 mn3 갱신**: 신규 collection 의 `ImageCache.MimeType` 은 NOT NULL DEFAULT 'application/octet-stream'
    /// 로 제약되어 NULL/empty 자연 발생 0. 그러나 (a) IndexerVersion 1.2.0 이하 legacy DB 의 IF NOT EXISTS skip 잔재
    /// row (shadow rebuild 전), (b) 외부 source 의 zip import 결함 row 에 대한 backstop 으로 본 helper 유지.
    /// dead code 아님 — 명시 안전망. 미지원 확장자 → `"application/octet-stream"` 반환.
    static let inferMimeFromPath (storedPath: string) : string =
        if String.IsNullOrEmpty storedPath then "application/octet-stream"
        else
            let ext = System.IO.Path.GetExtension(storedPath).TrimStart('.').ToLowerInvariant()
            match ext with
            | "png"  -> "image/png"
            | "jpg" | "jpeg" -> "image/jpeg"
            | "gif"  -> "image/gif"
            | "webp" -> "image/webp"
            | _      -> "application/octet-stream"

    /// HttpContext.Items 의 SessionState 추출. 누락 시 InvalidOperationException (방어 — SessionAuth 가 항상 박제 의무).
    static let activeSession (accessor: IHttpContextAccessor) : SessionState =
        let ctx = accessor.HttpContext
        if isNull ctx then
            raise (InvalidOperationException "AttachmentTools: HttpContext 미존재 — IHttpContextAccessor 등록 누락")
        match ctx.Items.TryGetValue SessionAuth.SessionItemKey with
        | true, v when not (isNull v) -> v :?> SessionState
        | _ ->
            raise (InvalidOperationException "AttachmentTools: SessionState 미존재 — SessionAuth 미들웨어 누락")

    /// 한 session 안에서 KB lock-and-use. SyncRoot 안에서 attach + 작업 + LastUsedAt 갱신.
    /// **s6-r39 P4-C.3** — `resolver` 인자 제거 (SessionRegistry 가 instance 안 wire 박제 lock-in). caller
    /// (`attachment_search` 등 4 method) 가 `ISessionRegistry` 만 받음 — resolver + embedderFactory 박제 통합.
    static let withKb (accessor: IHttpContextAccessor) (registry: ISessionRegistry) (work: Ds2.LightHouse.KnowledgeBase -> 'a) : 'a =
        let s = activeSession accessor
        lock s.SyncRoot (fun () ->
            let kb = registry.AttachKb s
            let r = work kb
            s.LastUsedAt <- DateTime.UtcNow
            r)

    /// FileKind DU → JSON 직렬화용 string (lower-case 통일).
    static let fileKindString (k: Ds2.LightHouse.FileKind) : string =
        match k with
        | Ds2.LightHouse.Pdf -> "pdf"
        | Ds2.LightHouse.Docx -> "docx"
        | Ds2.LightHouse.Pptx -> "pptx"
        | Ds2.LightHouse.Xlsx -> "xlsx"
        | Ds2.LightHouse.Text -> "txt"
        | Ds2.LightHouse.Markdown -> "md"
        | Ds2.LightHouse.Unsupported ext -> sprintf "unsupported(%s)" ext

    /// OutlineNodeType DU → JSON 직렬화용 string.
    static let outlineNodeTypeString (n: Ds2.LightHouse.OutlineNodeType) : string =
        match n with
        | Ds2.LightHouse.OutlineNodeType.Section -> "section"
        | Ds2.LightHouse.OutlineNodeType.Page -> "page"
        | Ds2.LightHouse.OutlineNodeType.Sheet -> "sheet"
        | Ds2.LightHouse.OutlineNodeType.Slide -> "slide"
        | Ds2.LightHouse.OutlineNodeType.Heading -> "heading"

    /// fileId 합성 SSOT — `<collection-guid>:<documents-id>` (MA23, D3 정합).
    ///
    /// 본 service 는 KnowledgeBase facade 의 `kbIdx:docId` (Searcher.composeFileId) 형식을 받아서 외부 노출 시
    /// `<collection-guid>:<docId>` 로 변환. session 의 CollectionIds 가 kbIdx ↔ collection-guid mapping SSOT.
    ///
    /// 변환: input "kb<idx>:docId" 또는 "<idx>:docId" → output "<guid>:<docId>" (collectionIds[idx] = guid).
    /// session collectionIds 범위 밖 idx → 빈 결과 (parse 실패 동일 처리).
    static let exportFileId (s: SessionState) (libFileId: string) : string =
        // Searcher 의 composeFileId 형식 = "%d:%d" = "<kbIdx>:<docId>"
        if String.IsNullOrEmpty libFileId then ""
        else
            let parts = libFileId.Split(':')
            if parts.Length <> 2 then libFileId
            else
                match Int32.TryParse parts.[0] with
                | true, idx when idx >= 0 && idx < s.CollectionIds.Length ->
                    sprintf "%s:%s" s.CollectionIds.[idx] parts.[1]
                | _ -> libFileId

    /// 외부 fileId (`<guid>:<docId>`) → lib 내부 fileId (`<kbIdx>:<docId>`).
    /// guid 가 session 의 active 셋에 없으면 None — caller (search/outline/read) 가 빈 결과 처리.
    static let importFileId (s: SessionState) (extFileId: string) : string option =
        if String.IsNullOrEmpty extFileId then None
        else
            let parts = extFileId.Split(':')
            if parts.Length <> 2 then None
            else
                match s.CollectionIds |> Array.tryFindIndex (fun id -> id = parts.[0]) with
                | Some idx -> Some (sprintf "%d:%s" idx parts.[1])
                | None -> None


    /// `attachment_list` — active 셋의 모든 등록 문서 메타.
    /// 응답: `[{fileId, fileName, fileKind, pageCount?}]`. 빈 active 셋 → `[]`.
    [<McpServerTool>]
    [<Description("List documents in the active KB collections. Returns JSON array of {fileId, fileName, fileKind, pageCount}.")>]
    static member attachment_list
        (
            accessor: IHttpContextAccessor,
            registry: ISessionRegistry
        ) : string =
        withKb accessor registry (fun kb ->
            let s = activeSession accessor
            let docs = kb.List()
            let items =
                docs
                |> Array.map (fun (libFileId, originalPath, kind, pages) ->
                    let fileName =
                        if String.IsNullOrEmpty originalPath then ""
                        else System.IO.Path.GetFileName originalPath
                    {|
                        fileId = exportFileId s libFileId
                        fileName = fileName
                        fileKind = fileKindString kind
                        pageCount = pages |> Option.defaultValue 0
                    |})
            JsonSerializer.Serialize(items, jsonOptions))


    /// `attachment_outline` — 한 문서의 outline tree raw rows.
    /// 응답: `[{id, parentId, ordinal, nodeType, label, ref}]`. fileId session active 셋 밖 → `[]`.
    [<McpServerTool>]
    [<Description("Get outline tree of a document. fileId format = <collection-guid>:<docId> (from attachment_list).")>]
    static member attachment_outline
        (
            accessor: IHttpContextAccessor,
            registry: ISessionRegistry,
            [<Description("File identifier from attachment_list, format <collection-guid>:<docId>")>]
            fileId: string
        ) : string =
        withKb accessor registry (fun kb ->
            let s = activeSession accessor
            match importFileId s fileId with
            | None -> "[]"
            | Some libFileId ->
                let rows = kb.Outline libFileId
                let items =
                    rows
                    |> Array.map (fun (id, parent, ord, nodeT, label, refLoc) ->
                        {|
                            id = id
                            parentId = parent |> Option.defaultValue 0L
                            ordinal = ord
                            nodeType = outlineNodeTypeString nodeT
                            label = label
                            ref = refLoc
                        |})
                JsonSerializer.Serialize(items, jsonOptions))


    /// `attachment_search` — active 셋 union BM25 trigram 검색.
    /// 응답: `{results: [{fileId, fileName, ref, outlinePath, score, excerpt, tokenCount, hasImages}], moreAvailable, hint?}`.
    [<McpServerTool>]
    [<Description("Search documents in active KB collections (BM25 lexical). topK default 10. fileId optional to scope to single document.")>]
    static member attachment_search
        (
            accessor: IHttpContextAccessor,
            registry: ISessionRegistry,
            [<Description("Search query text (whitespace-separated tokens, implicit AND)")>]
            query: string,
            [<Description("Max results to return (default 10)")>]
            topK: int,
            [<Description("Optional: limit search to single document (fileId from attachment_list)")>]
            fileId: string
        ) : string =
        withKb accessor registry (fun kb ->
            let s = activeSession accessor
            let effectiveTopK = if topK <= 0 then 10 else topK
            let libFileId =
                if String.IsNullOrEmpty fileId then None
                else importFileId s fileId
            // fileId 지정했는데 active 셋 밖 → 빈 결과 (silent fallback 금지 정합 — Searcher.search 의 fileIdInvalid 동일 의도)
            if not (String.IsNullOrEmpty fileId) && libFileId.IsNone then
                JsonSerializer.Serialize(
                    {| results = ([||]: obj array); moreAvailable = false; hint = "fileId not in active session" |},
                    jsonOptions)
            else
                let q : Ds2.LightHouse.Query = {
                    Text = if isNull query then "" else query
                    TopK = effectiveTopK
                    FileId = libFileId
                }
                // s6-r36 P4-C.0: ct 전파 path 추가. AttachmentTools 안에는 ct 없으나 향후 endpoint
                // pipeline cancel 박제 시 caller (CollectionEndpoints) 가 ct 주입 의무. 현재 None.
                let r = kb.Search q System.Threading.CancellationToken.None
                let hits =
                    r.Results
                    |> Array.map (fun h ->
                        {|
                            fileId = exportFileId s h.FileId
                            fileName = h.FileName
                            ref = h.Ref
                            outlinePath = h.OutlinePath
                            score = h.Score
                            excerpt = h.Excerpt
                            tokenCount = h.TokenCount
                            hasImages = h.HasImages
                        |})
                JsonSerializer.Serialize(
                    {|
                        results = hits
                        moreAvailable = r.MoreAvailable
                        hint = r.Hint |> Option.defaultValue null
                    |},
                    jsonOptions))


    /// `attachment_read` — 특정 ref 의 chunk 본문 concat + optional image content blocks (Phase 2 task D-iv, s6-r20).
    ///
    /// **시그니처 확장 (s6-r20)**: 기존 `(fileId, ref) -> string` → `(fileId, ref, includeImages, captionOnly) -> ContentBlock[]`.
    /// breaking change — 기존 caller grep 0 (lib 내부 만). MCP 표준 ContentBlock 분리 (D-2-7) 로 LLM client 의 native
    /// vision 인식 활성.
    ///
    /// **응답 분기**:
    /// - `captionOnly = true` (default 우선) — text block 1개 = chunk 본문 + (이미지가 있으면) caption 텍스트 enumeration.
    ///   "[image#1 caption: ...]" 식으로 inline append. image binary 미동봉, token 절약.
    /// - `includeImages = true && captionOnly = false` — text block + image content blocks 분리. 각 image 는
    ///   base64 inline + MIME type. **size 정책 가드** (D-2-3):
    ///     - 단일 image > MaxSingleImageBytes (~3.75MB 원본) → 자동 skip + oversize text 박제
    ///     - 응답당 image 수 > MaxImagesPerResponse (5장) → 자동 caption_only 전체 강등
    ///     - 모든 image skip 시에도 text block + skip 사유 박제 (silent drop 금지 정합)
    /// - 두 flag 모두 false → 기본 caption_only 동작 (back-compat 의도, 새 caller 가 명시 false 시 의도 표명).
    [<McpServerTool>]
    [<Description("Read chunk text of a ref + optional image content blocks. captionOnly=true (default) returns text+caption enumeration; includeImages=true with captionOnly=false returns text + base64 image blocks (size-policy gated).")>]
    static member attachment_read
        (
            accessor: IHttpContextAccessor,
            registry: ISessionRegistry,
            [<Description("File identifier (from attachment_search hit.fileId)")>]
            fileId: string,
            [<Description("Ref locator (from attachment_search hit.ref)")>]
            ref: string,
            [<Description("Include image content blocks (base64 inline). Default false. Subject to size policy: single ≤5MB body, ≤5 images per response, else auto-degrade to captionOnly.")>]
            includeImages: bool,
            [<Description("Caption-only mode: text block + inline caption enumeration (no base64 binary). Default true (effective when both flags false — back-compat caption_only path).")>]
            captionOnly: bool
        ) : ContentBlock array =
        withKb accessor registry (fun kb ->
            let s = activeSession accessor
            match importFileId s fileId with
            | None ->
                [| TextContentBlock(Text = "") :> ContentBlock |]
            | Some libFileId ->
                let chunkText = kb.Read libFileId ref
                let imgRefs = kb.ReadImages libFileId ref

                // size 정책: image 수 초과 → 전체 caption_only 강등 marker.
                let degradedByCount = imgRefs.Length > MaxImagesPerResponse

                // 효과적 mode (precedence): captionOnly=true / degraded / image 0개 → captionOnly path
                // (자가 검열 M8 정합 — 0-image 시 includeImages path 의 ResizeArray + counter 가 redundant).
                // 그 외 includeImages flag 명시 시 분리 path.
                let effectiveCaptionOnly =
                    captionOnly || degradedByCount || not includeImages || imgRefs.Length = 0

                let textBuilder = System.Text.StringBuilder()
                textBuilder.Append(chunkText) |> ignore

                if degradedByCount then
                    appendMarker textBuilder (
                        sprintf "[oversize_image_count=%d, max=%d — auto caption_only degrade]"
                            imgRefs.Length MaxImagesPerResponse)

                if effectiveCaptionOnly then
                    // text block + caption enumeration. image binary 미동봉.
                    if imgRefs.Length > 0 then
                        if textBuilder.Length > 0 then textBuilder.AppendLine() |> ignore
                        textBuilder.AppendLine() |> ignore
                        textBuilder.AppendLine("[images]") |> ignore
                        for i = 0 to imgRefs.Length - 1 do
                            let (hash, _, _, caption) = imgRefs.[i]
                            let captionStr =
                                match caption with
                                | Some c -> c
                                | None -> "(caption 미생성)"
                            textBuilder.AppendFormat(
                                "  #{0} hash={1} — {2}", i + 1, hash.Substring(0, min 12 hash.Length), captionStr)
                                |> ignore
                            textBuilder.AppendLine() |> ignore
                    [| TextContentBlock(Text = textBuilder.ToString()) :> ContentBlock |]
                else
                    // includeImages mode: text block + per-image base64 / oversize text 박제.
                    let blocks = ResizeArray<ContentBlock>()
                    let mutable oversizeCount = 0
                    let mutable skipReadFailCount = 0
                    let imgBlocks = ResizeArray<ContentBlock>()

                    for i = 0 to imgRefs.Length - 1 do
                        let (_, mime, storedPath, _) = imgRefs.[i]
                        try
                            let fi = FileInfo storedPath
                            if not fi.Exists then
                                skipReadFailCount <- skipReadFailCount + 1
                            elif fi.Length > int64 MaxSingleImageBytes then
                                oversizeCount <- oversizeCount + 1
                            else
                                let bytes = File.ReadAllBytes storedPath
                                // --review M3 정합 (s6-r21) — mime 빈/NULL row 시 확장자 추론 fallback.
                                // 정상 색인 경로는 mime 항상 박제. legacy zip / 외부 source 안전망.
                                let effectiveMime =
                                    if String.IsNullOrWhiteSpace mime then inferMimeFromPath storedPath
                                    else mime
                                // SDK 1.2.0 의 `ImageContentBlock.Data` 는 *base64-encoded UTF-8 bytes* SSOT
                                // (XML doc 명시). raw bytes 를 직접 박으면 wire 의 image data 가 invalid base64
                                // 로 전달되어 client 측 디코딩 실패 (--review C1 검증 결과).
                                // `FromBytes(bytes, mime)` factory 가 raw → DecodedData 박제 + Data 슬롯에
                                // lazy base64 인코딩 — 정합 SSOT.
                                let block = ImageContentBlock.FromBytes(ReadOnlyMemory<byte>(bytes), effectiveMime)
                                imgBlocks.Add(block :> ContentBlock)
                        with ex ->
                            // per-image fail-safe — log skip + 후속 image 진행 (자가 검열 m4 정합).
                            skipReadFailCount <- skipReadFailCount + 1
                            Log.service.Warn(
                                sprintf "AttachmentTools.attachment_read: image read 실패 (skip) — path=%s ex=%s: %s"
                                    storedPath (ex.GetType().Name) ex.Message)

                    if oversizeCount > 0 then
                        appendMarker textBuilder (
                            sprintf "[oversize_image_count=%d, max_single_bytes=%d — skipped]"
                                oversizeCount MaxSingleImageBytes)
                    if skipReadFailCount > 0 then
                        appendMarker textBuilder (
                            sprintf "[image_read_fail_count=%d — skipped]" skipReadFailCount)

                    blocks.Add(TextContentBlock(Text = textBuilder.ToString()) :> ContentBlock)
                    for b in imgBlocks do blocks.Add b
                    blocks.ToArray())
