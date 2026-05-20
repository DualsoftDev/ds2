namespace Ds2.LightHouseService

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.DependencyInjection

/// Phase S7 D-S7-5 (s6-r60) — **resumable chunked upload** scaffold (server-side, minimum viable).
///
/// 박제 SSOT: tus protocol 미채택, 자체 SSOT — `POST /uploads` (시작) → `PATCH /uploads/{id}` (chunk
/// append, `Content-Range: bytes <start>-<end>/<total>`) → `POST /uploads/{id}/finalize` (collection 등록
/// 위임) → `DELETE /uploads/{id}` (cancel). `GET /uploads/{id}` 로 현 offset 조회 → 중단 후 재개 가능.
///
/// **분리 의도**: 기존 `POST /collections` (multipart) 와 공존. caller (Promaker/cli) 가 zip 크기 < 100MB
/// 시 일반 multipart, 큰 zip 시 chunked path 선택.
///
/// **wire (camelCase JSON)**:
///   - `POST /uploads { title: string, totalBytes: int64 }` → 201 + `{ uploadId, offset: 0, totalBytes }`
///   - `PATCH /uploads/{id}` (`Content-Range: bytes <start>-<end>/<total>`, body = chunk) → 200 +
///       `{ uploadId, offset: <end+1>, totalBytes }`
///   - `GET /uploads/{id}` → 200 + `{ uploadId, offset, totalBytes }` / 404 unknown
///   - `POST /uploads/{id}/finalize` → 200 + `{ id: <collection-guid> }` (POST /collections 의 ZipImport
///       위임 — staging payload.partial 을 final zip 으로 swap + Registry 등록)
///   - `DELETE /uploads/{id}` → 204 (cancel + staging cleanup)
///
/// **state machine** (per-uploadId staging dir):
///   `<storageRoot>/Staging/<uploadId>/`
///     payload.partial  — append-only chunk 누적
///     meta.json        — { uploadId, title, totalBytes, offset, createdAt, userIdentity }
///   StagingSweep 가 동일 정책 (maxAge) 으로 stale upload 회수.
///
/// **SSE 진행률 (D-S7-2 결합)**: PATCH 시점에 `EventBus.Publish (ServerEvent.uploadProgress ...)` —
/// 다른 instance / KbManagerDialog 가 동일 user 의 진행률 수신 가능.
[<RequireQualifiedAccess>]
module UploadsEndpoint =

    /// 한 upload 의 meta — `staging/<uploadId>/meta.json` SSOT.
    [<CLIMutable>]
    type UploadMeta = {
        [<JsonPropertyName("uploadId")>] UploadId: string
        [<JsonPropertyName("title")>] Title: string
        [<JsonPropertyName("totalBytes")>] TotalBytes: int64
        [<JsonPropertyName("offset")>] Offset: int64
        [<JsonPropertyName("createdAt")>] CreatedAt: string
        [<JsonPropertyName("userIdentity")>] UserIdentity: string
    }

    [<Literal>]
    let private MetaFileName = "meta.json"
    [<Literal>]
    let private PartialFileName = "payload.partial"
    // **s6-r60 M2 (자가 검열 정정)**: `Staging` subdir = `Storage.StagingSubdir` SSOT 재사용 (박제 중복 해소).

    let private jsonOpts =
        let opts = JsonSerializerOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)
        opts

    let private writeJson (ctx: HttpContext) (status: int) (body: obj) : Task =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.WriteAsync(JsonSerializer.Serialize(body, jsonOpts))

    let private writeError (ctx: HttpContext) (status: int) (message: string) : Task =
        writeJson ctx status {| error = message |}

    /// `<storageRoot>/Staging/<uploadId>/` 절대 경로. uploadId 검증 = caller 책임 (regex).
    /// **s6-r60 M2 (자가 검열)** — `Storage.stagingDir` SSOT 재사용 (Staging subdir 박제 중복 해소).
    let private uploadDir (storageRoot: string) (uploadId: string) : string =
        Path.Combine(Storage.stagingDir storageRoot, uploadId)

    let private metaPath (uploadDir': string) : string =
        Path.Combine(uploadDir', MetaFileName)

    let private partialPath (uploadDir': string) : string =
        Path.Combine(uploadDir', PartialFileName)

    /// uploadId regex 검증 — guid v4 hex 32-char ("N" format) 정합. 다른 형식 거부 (path traversal 차단).
    let private isValidUploadId (id: string) : bool =
        if String.IsNullOrEmpty id then false
        elif id.Length <> 32 then false
        else
            id |> Seq.forall (fun c ->
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

    let private readMeta (uploadDir': string) : UploadMeta option =
        let p = metaPath uploadDir'
        if not (File.Exists p) then None
        else
            let json = File.ReadAllText(p, Encoding.UTF8)
            JsonSerializer.Deserialize<UploadMeta>(json, jsonOpts) |> Some

    let private writeMeta (uploadDir': string) (meta: UploadMeta) : unit =
        let p = metaPath uploadDir'
        let tmp = p + ".tmp"
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, jsonOpts), Encoding.UTF8)
        if File.Exists p then File.Replace(tmp, p, null, ignoreMetadataErrors = true)
        else File.Move(tmp, p)

    /// HttpContext.Items 의 X-User-Identity (AuthMiddleware 박제).
    let private userIdentityOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue AuthMiddleware.UserIdentityItemKey with
        | true, v when not (isNull v) -> string v
        | _ -> "unknown"

    /// `POST /uploads { title, totalBytes }` — 신규 upload 시작. uploadId (guid v4 N) 발급 + staging dir 생성.
    let private postStart (cfg: ServiceConfig) (storageRoot: string) (_eventBus: EventBus) (ctx: HttpContext) : Task =
        task {
            try
                let! body =
                    JsonSerializer.DeserializeAsync<{| title: string; totalBytes: int64 |}>(
                        ctx.Request.Body, jsonOpts, ctx.RequestAborted).AsTask()
                if isNull (box body) then
                    do! writeError ctx 400 "request body JSON 결함"
                elif String.IsNullOrWhiteSpace body.title then
                    do! writeError ctx 400 "title 필수"
                elif body.totalBytes <= 0L then
                    do! writeError ctx 400 "totalBytes 필수 (>0)"
                elif body.totalBytes > cfg.MaxUploadBytes then
                    do! writeError ctx 413 (sprintf "totalBytes=%d > maxUploadBytes=%d" body.totalBytes cfg.MaxUploadBytes)
                else
                    let uploadId = Guid.NewGuid().ToString("N")
                    let dir = uploadDir storageRoot uploadId
                    Directory.CreateDirectory dir |> ignore
                    // partial file 초기 0 byte 생성 (FileStream Create + Close)
                    use fs = File.Create(partialPath dir)
                    fs.Close()
                    let meta : UploadMeta = {
                        UploadId = uploadId
                        Title = body.title.Trim()
                        TotalBytes = body.totalBytes
                        Offset = 0L
                        CreatedAt = DateTime.UtcNow.ToString("o")
                        UserIdentity = userIdentityOf ctx
                    }
                    writeMeta dir meta
                    Log.audit.Info(
                        sprintf "D-S7-5: POST /uploads — uploadId=%s title=%s totalBytes=%d user=%s"
                            uploadId (Log.sanitizeForLog meta.Title) meta.TotalBytes (Log.sanitizeForLog meta.UserIdentity))
                    do! writeJson ctx 201 {| uploadId = uploadId; offset = 0L; totalBytes = body.totalBytes |}
            with
            | :? JsonException -> do! writeError ctx 400 "request body JSON 결함"
        } :> Task

    /// `Content-Range: bytes <start>-<end>/<total>` 파싱. 형식 위반 시 None.
    let private parseContentRange (value: string) : (int64 * int64 * int64) option =
        if String.IsNullOrEmpty value then None
        elif not (value.StartsWith "bytes ") then None
        else
            let rest = value.Substring(6).Trim()
            let slashIdx = rest.IndexOf('/')
            if slashIdx < 0 then None
            else
                let rangePart = rest.Substring(0, slashIdx)
                let totalPart = rest.Substring(slashIdx + 1)
                let dashIdx = rangePart.IndexOf('-')
                if dashIdx < 0 then None
                else
                    let okStart, startB = Int64.TryParse(rangePart.Substring(0, dashIdx))
                    let okEnd, endB = Int64.TryParse(rangePart.Substring(dashIdx + 1))
                    let okTotal, totalB = Int64.TryParse totalPart
                    if okStart && okEnd && okTotal && startB >= 0L && endB >= startB && totalB >= 0L
                    then Some (startB, endB, totalB)
                    else None

    /// `PATCH /uploads/{id}` — chunk append. Content-Range 의 start 가 현 offset 와 일치 의무 (409 미일치).
    let private patchChunk (storageRoot: string) (eventBus: EventBus) (uploadId: string) (ctx: HttpContext) : Task =
        task {
            if not (isValidUploadId uploadId) then
                do! writeError ctx 400 "uploadId 형식 위반"
            else
                let dir = uploadDir storageRoot uploadId
                match readMeta dir with
                | None -> do! writeError ctx 404 "uploadId 미존재"
                | Some meta ->
                    let cr =
                        if ctx.Request.Headers.ContainsKey "Content-Range" then
                            parseContentRange (ctx.Request.Headers.["Content-Range"].ToString())
                        else None
                    match cr with
                    | None -> do! writeError ctx 400 "Content-Range 헤더 필수 (bytes <start>-<end>/<total>)"
                    | Some (_, _, totalB) when totalB <> meta.TotalBytes ->
                        do! writeError ctx 409 (sprintf "Content-Range total=%d ≠ meta.totalBytes=%d" totalB meta.TotalBytes)
                    | Some (startB, _, _) when startB <> meta.Offset ->
                        do! writeJson ctx 409 {| error = "offset mismatch"; expected = meta.Offset; received = startB |}
                    | Some (startB, endB, _) ->
                        let chunkLen = endB - startB + 1L
                        if meta.Offset + chunkLen > meta.TotalBytes then
                            do! writeError ctx 413 (sprintf "chunk overflow — offset+len=%d > totalBytes=%d"
                                                        (meta.Offset + chunkLen) meta.TotalBytes)
                        else
                            // append chunk to payload.partial
                            use fs = new FileStream(partialPath dir, FileMode.Open, FileAccess.Write, FileShare.None)
                            fs.Seek(meta.Offset, SeekOrigin.Begin) |> ignore
                            do! ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted)
                            fs.Flush()
                            let newOffset = meta.Offset + chunkLen
                            let updated = { meta with Offset = newOffset }
                            writeMeta dir updated
                            // SSE 진행률 publish (D-S7-2 결합)
                            let progress =
                                if meta.TotalBytes > 0L then int (newOffset * 100L / meta.TotalBytes)
                                else 0
                            eventBus.Publish(
                                ServerEvent.uploadProgress uploadId progress
                                    (sprintf "%d / %d bytes" newOffset meta.TotalBytes))
                            do! writeJson ctx 200 {| uploadId = uploadId; offset = newOffset; totalBytes = meta.TotalBytes |}
        } :> Task

    /// `GET /uploads/{id}` — 현 offset 조회 (resume 진입 hook).
    let private getStatus (storageRoot: string) (uploadId: string) (ctx: HttpContext) : Task =
        task {
            if not (isValidUploadId uploadId) then
                do! writeError ctx 400 "uploadId 형식 위반"
            else
                let dir = uploadDir storageRoot uploadId
                match readMeta dir with
                | None -> do! writeError ctx 404 "uploadId 미존재"
                | Some meta ->
                    do! writeJson ctx 200
                            {| uploadId = uploadId; offset = meta.Offset; totalBytes = meta.TotalBytes; title = meta.Title |}
        } :> Task

    /// `DELETE /uploads/{id}` — cancel. staging dir 제거.
    let private deleteUpload (storageRoot: string) (uploadId: string) (ctx: HttpContext) : Task =
        task {
            if not (isValidUploadId uploadId) then
                do! writeError ctx 400 "uploadId 형식 위반"
            else
                let dir = uploadDir storageRoot uploadId
                if Directory.Exists dir then
                    try Directory.Delete(dir, true)
                    with ex ->
                        Log.service.Warn(sprintf "D-S7-5: DELETE /uploads cleanup 실패 — %s: %s" (ex.GetType().Name) ex.Message)
                    Log.audit.Info(sprintf "D-S7-5: DELETE /uploads — uploadId=%s" uploadId)
                ctx.Response.StatusCode <- 204
        } :> Task

    /// `POST /uploads/{id}/finalize` — staging payload.partial 의 byte 가 totalBytes 정합 시 collection 등록 path
    /// 진입. 본 turn (s6-r60 scaffold) = finalize 결과를 별 path 로 위임 안 함 — payload.partial 의 완성 검증 +
    /// 응답 `{ uploadId, complete: true }` 만. 실 collection 등록은 별 turn (Promaker/cli client 변경 + POST
    /// /collections 의 multipart 위임 path 통합).
    let private postFinalize (storageRoot: string) (uploadId: string) (ctx: HttpContext) : Task =
        task {
            if not (isValidUploadId uploadId) then
                do! writeError ctx 400 "uploadId 형식 위반"
            else
                let dir = uploadDir storageRoot uploadId
                match readMeta dir with
                | None -> do! writeError ctx 404 "uploadId 미존재"
                | Some meta ->
                    if meta.Offset <> meta.TotalBytes then
                        do! writeError ctx 409
                                (sprintf "incomplete — offset=%d / totalBytes=%d" meta.Offset meta.TotalBytes)
                    else
                        // **본 turn 의 scaffold** — collection 등록 위임은 별 turn (D-S7-5 phase 2).
                        // 현 시점 응답 = upload 완료 marker. caller (Promaker/cli) 가 후속 path 로 collection 등록.
                        Log.audit.Info(
                            sprintf "D-S7-5: POST /uploads/finalize — uploadId=%s totalBytes=%d (collection 등록은 별 turn)"
                                uploadId meta.TotalBytes)
                        do! writeJson ctx 200
                                {| uploadId = uploadId; complete = true; offset = meta.Offset; totalBytes = meta.TotalBytes
                                   note = "scaffold (s6-r60) — collection 등록 위임 별 turn" |}
        } :> Task

    /// route mapping. AuthMiddleware 통과 후 진입.
    ///
    /// **path = `/uploads-rs/`** (resumable shortcut). 기존 `DELETE /uploads/{stagingId}` (CollectionEndpoints 의
    /// staging cancel hook) 와 route 분리 — wire 명확 + ASP.NET endpoint routing ambiguity 차단.
    let map (app: IEndpointRouteBuilder) (cfg: ServiceConfig) (storageRoot: string) (eventBus: EventBus) =
        app.MapPost("/uploads-rs", RequestDelegate(postStart cfg storageRoot eventBus)) |> ignore
        app.MapGet("/uploads-rs/{uploadId}",
            RequestDelegate(fun ctx ->
                let uploadId = ctx.GetRouteValue "uploadId" :?> string
                getStatus storageRoot uploadId ctx)) |> ignore
        app.MapPatch("/uploads-rs/{uploadId}",
            RequestDelegate(fun ctx ->
                let uploadId = ctx.GetRouteValue "uploadId" :?> string
                patchChunk storageRoot eventBus uploadId ctx)) |> ignore
        app.MapPost("/uploads-rs/{uploadId}/finalize",
            RequestDelegate(fun ctx ->
                let uploadId = ctx.GetRouteValue "uploadId" :?> string
                postFinalize storageRoot uploadId ctx)) |> ignore
        app.MapDelete("/uploads-rs/{uploadId}",
            RequestDelegate(fun ctx ->
                let uploadId = ctx.GetRouteValue "uploadId" :?> string
                deleteUpload storageRoot uploadId ctx)) |> ignore
