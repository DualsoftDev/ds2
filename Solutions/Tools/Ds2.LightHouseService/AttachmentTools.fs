namespace Ds2.LightHouseService

open System
open System.ComponentModel
open System.Text.Json
open Microsoft.AspNetCore.Http
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
    static let withKb (accessor: IHttpContextAccessor) (resolver: AttachmentResolver) (work: Ds2.LightHouse.KnowledgeBase -> 'a) : 'a =
        let s = activeSession accessor
        lock s.SyncRoot (fun () ->
            let kb = SessionKb.attach resolver s
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
            resolver: AttachmentResolver
        ) : string =
        withKb accessor resolver (fun kb ->
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
            resolver: AttachmentResolver,
            [<Description("File identifier from attachment_list, format <collection-guid>:<docId>")>]
            fileId: string
        ) : string =
        withKb accessor resolver (fun kb ->
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
            resolver: AttachmentResolver,
            [<Description("Search query text (whitespace-separated tokens, implicit AND)")>]
            query: string,
            [<Description("Max results to return (default 10)")>]
            topK: int,
            [<Description("Optional: limit search to single document (fileId from attachment_list)")>]
            fileId: string
        ) : string =
        withKb accessor resolver (fun kb ->
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
                let r = kb.Search q
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


    /// `attachment_read` — 특정 ref 의 chunk 본문 concat (token 한도 절단).
    /// 응답: plain text. fileId active 셋 밖 → 빈 문자열.
    [<McpServerTool>]
    [<Description("Read concatenated chunk text of a specific ref in a document. Returns plain text (token-limited).")>]
    static member attachment_read
        (
            accessor: IHttpContextAccessor,
            resolver: AttachmentResolver,
            [<Description("File identifier (from attachment_search hit.fileId)")>]
            fileId: string,
            [<Description("Ref locator (from attachment_search hit.ref)")>]
            ref: string
        ) : string =
        withKb accessor resolver (fun kb ->
            let s = activeSession accessor
            match importFileId s fileId with
            | None -> ""
            | Some libFileId -> kb.Read libFileId ref)
