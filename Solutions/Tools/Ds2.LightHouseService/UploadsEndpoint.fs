namespace Ds2.LightHouseService

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.DependencyInjection
open Ds2.LightHouse.Protocol

/// Phase S7 D-S7-5 — **resumable chunked upload** (server-side, **phase 4 swap chunked** s6-r74 b1).
///
/// 박제 SSOT: tus protocol 미채택, 자체 SSOT — `POST /uploads-rs` (시작) → `PATCH /uploads-rs/{id}` (chunk
/// append, `Content-Range: bytes <start>-<end>/<total>`) → `POST /uploads-rs/{id}/finalize` (zip extract +
/// IndexerVersion gate + collection 등록 OR 기존 collection swap + Registry upsert + EventBus collection-added/updated)
/// → `DELETE /uploads-rs/{id}` (cancel). `GET /uploads-rs/{id}` 로 현 offset 조회 → 중단 후 재개 가능.
///
/// **phase 4 (s6-r74 b1) 변경 — swap chunked path**:
///   - `POST /uploads-rs/{id}/finalize` body 가 optional `{ swapTargetCollectionId?: string }` 받음.
///     박제 시 흐름:
///       1. Registry.tryFindById 으로 기존 collection 존재 검증 (404 미존재)
///       2. MultiTenantPolicy.evaluate 으로 Hidden=404 / ReadOnly=403 분기 (T2/T3 acl 검증)
///       3. 새 collStaging 에 zip extractAll + MetaJsonIO load → stampServerFields(existing.Id, caller, existing storageRelPath)
///       4. IndexerVersion gate (`processStagingExtractGate` swap 라벨)
///       5. `ZipImport.swapCollectionPayload` 호출 (rollback-safe atomic swap, s6-r9 K1 unique backup)
///       6. Registry.upsertAsync (existing entry 갱신 — Id 동일, Title/ACL 보존, IndexedAt/ImportedBy/IndexerVersion stamp)
///       7. `ICollectionLifecycleNotifier.OnPayloadSwapped` 호출 (active session KB 무효화 — s6-r9 K5 lock-out dispose)
///       8. `EventBus.Publish ServerEvent.collectionUpdated` (collection-added 아님 — wire SSOT 분리)
///       9. uploadId staging cleanup
///   - swapTargetCollectionId 미박제 (null/missing) = 기존 phase 2 new collection path (회귀 0).
///
/// **phase 2 (s6-r63) 변경** (s6-r60 scaffold backlog 4건 모두 해소):
/// 1. **PATCH per-uploadId SemaphoreSlim race** — `_uploadLocks: ConcurrentDictionary` 으로 동시 PATCH 차단.
///    동일 uploadId 에 동시 PATCH 진입 시 readMeta / append / writeMeta sequence 의 atomic 보장.
/// 2. **Content-Range body size 검증** — `Content-Length` header 의 byte 수 == Content-Range 의 length 의무.
///    실 append 후 fs.Position 으로 actual byte 카운트 재검증 + mismatch 시 rollback truncate.
/// 3. **crash inconsistency 회복** — readMeta 직후 partial.Length vs meta.Offset 비교. partial > offset 시
///    boundary 까지 truncate (meta.json 이 SSOT, partial 의 tail unverified). partial < offset 시 fatal log
///    + DELETE 권장.
/// 4. **finalize → collection 등록 위임** — payload.partial 의 zip stream 을 새 collectionId staging 에 extractAll +
///    MetaJson stamp + IndexerVersion gate + moveStagingToCollection + Registry.upsertAsync + EventBus.Publish
///    (collection-added). uploadId staging 은 finalize 성공 시 cleanup.
///
/// **wire (camelCase JSON)**:
///   - `POST /uploads-rs { title: string, totalBytes: int64 }` → 201 + `{ uploadId, offset: 0, totalBytes }`
///   - `PATCH /uploads-rs/{id}` (`Content-Range: bytes <start>-<end>/<total>`, body = chunk) → 200 +
///       `{ uploadId, offset: <end+1>, totalBytes }`
///   - `GET /uploads-rs/{id}` → 200 + `{ uploadId, offset, totalBytes, title }` / 404 unknown
///   - `POST /uploads-rs/{id}/finalize` body=`{ swapTargetCollectionId?: string }` →
///       (new) 201 + `{ id: <collection-guid>, storageRelPath }` (zip extract + Registry upsert + EventBus.collection-added) /
///       (swap) 200 + `{ id: <existing-id>, storageRelPath }` (swap + Registry update + EventBus.collection-updated) /
///       409 incomplete / 415 indexerVersion gate / 400 zip 결함 / 404 swap target 미존재 / 403 swap target read-only
///   - `DELETE /uploads-rs/{id}` → 204 (cancel + staging cleanup + lock dispose)
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

    /// **phase 4 (s6-r74 b1)** — finalize body schema. swapTargetCollectionId null/missing = new collection path,
    /// 박제 = 기존 collection 의 payload swap. 본 record 는 nullable 단일 필드 — JSON `{}` 또는 빈 body 도 정합 (null).
    [<CLIMutable>]
    type FinalizeBody = {
        [<JsonPropertyName("swapTargetCollectionId")>] SwapTargetCollectionId: string
    }

    [<Literal>]
    let private MetaFileName = "meta.json"
    [<Literal>]
    let private PartialFileName = "payload.partial"

    // **C-15 (s6-r79)** — response 용 jsonOpts 폐기 → `EndpointHelpers` SSOT.
    // meta.json file write 용은 별 박제 (WriteIndented=true 가독성) — `metaJsonOpts` rename 후 유지.
    let private metaJsonOpts =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)

    /// **D-S7-5 phase 2 (1)** — per-uploadId SemaphoreSlim. 동시 PATCH / DELETE / finalize 가 같은 uploadId 에
    /// 들어와도 readMeta → append → writeMeta sequence 의 atomic 보장. lock 진입 자체가 cancellable (ct).
    let private uploadLocks : ConcurrentDictionary<string, SemaphoreSlim> =
        ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal)

    let private acquireLock (uploadId: string) : SemaphoreSlim =
        uploadLocks.GetOrAdd(uploadId, fun _ -> new SemaphoreSlim(1, 1))

    /// finalize 성공 / DELETE 진입 시 호출 — dictionary entry 만 제거. SemaphoreSlim 자체는 lazy GC.
    /// **s6-r70 review C-5**: 직전 Dispose 박제는 진행 중인 다른 caller 의 WaitAsync 와 race → ObjectDisposedException
    /// 가능. 진행 중인 caller 가 보유 reference 는 그대로 정상 동작 (Release/Wait), 새 caller 는 acquireLock 으로 새 instance
    /// 생성 — 무해, 단순히 잠깐의 entry 가 2개. Dispose 책임은 GC 가 SafeHandle finalizer 처리.
    let private removeLock (uploadId: string) : unit =
        uploadLocks.TryRemove uploadId |> ignore

    let private writeJson = EndpointHelpers.writeJson
    let private writeError = EndpointHelpers.writeError

    /// `<storageRoot>/Staging/<uploadId>/` 절대 경로. uploadId 검증 = caller 책임 (regex).
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
            JsonSerializer.Deserialize<UploadMeta>(json, metaJsonOpts) |> Some

    /// **D-S7-5 phase 2 (3)** — readMeta + crash inconsistency 회복.
    /// partial.Length > meta.Offset → PATCH 중간 crash → meta.Offset 까지 truncate (meta SSOT).
    /// partial.Length < meta.Offset → 더 심각한 결손 (writeMeta 가 .tmp Replace 도중 crash 등) → fatal log,
    /// caller 가 DELETE 후 사용자 재upload 권장.
    let private readMetaReconciled (uploadDir': string) : UploadMeta option =
        match readMeta uploadDir' with
        | None -> None
        | Some meta ->
            let pp = partialPath uploadDir'
            if File.Exists pp then
                let len = (FileInfo pp).Length
                if len > meta.Offset then
                    try
                        use fs = new FileStream(pp, FileMode.Open, FileAccess.Write, FileShare.None)
                        fs.SetLength(meta.Offset)
                        Log.service.Warn(
                            sprintf "D-S7-5: crash 회복 — uploadId=%s partial truncated %d → %d"
                                meta.UploadId len meta.Offset)
                    with ex ->
                        Log.service.Error(
                            sprintf "D-S7-5: crash 회복 실패 (partial truncate) — uploadId=%s ex=%s"
                                meta.UploadId ex.Message)
                elif len < meta.Offset then
                    Log.service.Error(
                        sprintf "D-S7-5: partial 결손 — uploadId=%s partial.len=%d < meta.offset=%d (DELETE 권장)"
                            meta.UploadId len meta.Offset)
            Some meta

    let private writeMeta (uploadDir': string) (meta: UploadMeta) : unit =
        let p = metaPath uploadDir'
        let tmp = p + ".tmp"
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, metaJsonOpts), Encoding.UTF8)
        if File.Exists p then File.Replace(tmp, p, null, ignoreMetadataErrors = true)
        else File.Move(tmp, p)

    // **C-15 (s6-r79)** — userIdentityOf 폐기 → `EndpointHelpers` SSOT alias.
    let private userIdentityOf = EndpointHelpers.userIdentityOf

    /// **s6-r70 review C-4** — meta.UserIdentity ↔ caller X-User-Identity cross-check.
    /// uploadId 노출 시 다른 user 가 PATCH/GET/DELETE/finalize 탈취 차단. T1 모드 도 동일 — uploadId 발급한 user 만
    /// 해당 upload 진행 가능 (multi-tenant 정책과 무관, upload 책임 격리).
    /// `case-insensitive ordinal` 비교 — Windows username 정합.
    let private isUploadOwnedBy (meta: UploadMeta) (callerUser: string) : bool =
        // 빈 메타 (legacy upload — 본 hotfix 이전 created) 는 보수적으로 거부 (전체 403). 정합성 우선.
        if String.IsNullOrEmpty meta.UserIdentity then false
        else String.Equals(meta.UserIdentity, callerUser, StringComparison.OrdinalIgnoreCase)

    /// `POST /uploads-rs { title, totalBytes }` — 신규 upload 시작. uploadId (guid v4 N) 발급 + staging dir 생성.
    let private postStart (cfg: ServiceConfig) (storageRoot: string) (_eventBus: EventBus) (ctx: HttpContext) : Task =
        task {
            try
                let! body =
                    JsonSerializer.DeserializeAsync<{| title: string; totalBytes: int64 |}>(
                        ctx.Request.Body, EndpointHelpers.DefaultJsonOpts, ctx.RequestAborted).AsTask()
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
                        sprintf "D-S7-5: POST /uploads-rs — uploadId=%s title=%s totalBytes=%d user=%s"
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

    /// `PATCH /uploads-rs/{id}` — chunk append. Content-Range 의 start 가 현 offset 와 일치 의무 (409 미일치).
    /// **D-S7-5 phase 2 (1)+(2)+(3)** — per-uploadId lock + body size 검증 + crash 회복.
    /// **s6-r70 review C-4**: meta.UserIdentity cross-check.
    let private patchChunk (storageRoot: string) (eventBus: EventBus) (uploadId: string) (ctx: HttpContext) : Task =
        task {
            if not (isValidUploadId uploadId) then
                do! writeError ctx 400 "uploadId 형식 위반"
            else
                let caller = userIdentityOf ctx
                let lock = acquireLock uploadId
                do! lock.WaitAsync(ctx.RequestAborted)
                try
                    let dir = uploadDir storageRoot uploadId
                    match readMetaReconciled dir with
                    | None -> do! writeError ctx 404 "uploadId 미존재"
                    | Some meta when not (isUploadOwnedBy meta caller) ->
                        Log.audit.Warn(
                            sprintf "D-S7-5 C-4: PATCH owner mismatch — uploadId=%s caller=%s owner=%s"
                                uploadId (Log.sanitizeForLog caller) (Log.sanitizeForLog meta.UserIdentity))
                        do! writeError ctx 403 "upload owner mismatch"
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
                            // **(2) Content-Range body size 검증** — Content-Length header 가 명시되면 chunkLen 과 일치 의무.
                            let cl = ctx.Request.ContentLength
                            if cl.HasValue && cl.Value <> chunkLen then
                                do! writeError ctx 400
                                        (sprintf "Content-Length=%d ≠ Content-Range length=%d" cl.Value chunkLen)
                            elif meta.Offset + chunkLen > meta.TotalBytes then
                                do! writeError ctx 413
                                        (sprintf "chunk overflow — offset+len=%d > totalBytes=%d"
                                            (meta.Offset + chunkLen) meta.TotalBytes)
                            else
                                let mutable actualWritten = 0L
                                let mutable appendOk = false
                                let mutable clientDisconnect = false
                                try
                                    use fs = new FileStream(partialPath dir, FileMode.Open, FileAccess.Write, FileShare.None)
                                    fs.Seek(meta.Offset, SeekOrigin.Begin) |> ignore
                                    let beforePos = fs.Position
                                    do! ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted)
                                    fs.Flush()
                                    actualWritten <- fs.Position - beforePos
                                    // **(2) post-condition** — 실 byte 카운트 == Content-Range length 의무. 미일치 시 rollback.
                                    if actualWritten <> chunkLen then
                                        fs.SetLength meta.Offset
                                        fs.Flush()
                                    else
                                        appendOk <- true
                                with
                                | :? OperationCanceledException ->
                                    // **s6-r70 review C-8** — client disconnect / ct cancel. 정보 level log + writeError 시도 X
                                    // (응답 자체가 OCE 재발). caller 가 다음 PATCH 재시도 시 readMetaReconciled 가 truncate 회복.
                                    clientDisconnect <- true
                                    Log.service.Info(
                                        sprintf "D-S7-5: PATCH client disconnect — uploadId=%s offset=%d written=%d"
                                            uploadId meta.Offset actualWritten)
                                | ex ->
                                    Log.service.Warn(
                                        sprintf "D-S7-5: PATCH append 실패 — uploadId=%s ex=%s" uploadId ex.Message)

                                if clientDisconnect then
                                    // 응답 시도 안 함 — connection 이 이미 끊김. ASP.NET Core 가 connection abort 후 응답 시도 시 OCE.
                                    ()
                                elif not appendOk then
                                    do! writeError ctx 400
                                            (sprintf "body size mismatch — actual=%d ≠ Content-Range length=%d"
                                                actualWritten chunkLen)
                                else
                                    let newOffset = meta.Offset + chunkLen
                                    let updated = { meta with Offset = newOffset }
                                    writeMeta dir updated
                                    let progress =
                                        if meta.TotalBytes > 0L then int (newOffset * 100L / meta.TotalBytes)
                                        else 0
                                    eventBus.Publish(
                                        ServerEvent.uploadProgress uploadId progress
                                            (sprintf "%d / %d bytes" newOffset meta.TotalBytes))
                                    do! writeJson ctx 200
                                            {| uploadId = uploadId; offset = newOffset; totalBytes = meta.TotalBytes |}
                finally
                    lock.Release() |> ignore
        } :> Task

    /// `GET /uploads-rs/{id}` — 현 offset 조회 (resume 진입 hook).
    /// **s6-r70 review C-4 + C-6**: meta.UserIdentity cross-check + acquireLock (readMetaReconciled 의 FileShare.None
    /// 충돌 차단, PATCH 진행 중 GET 시 race 회피).
    let private getStatus (storageRoot: string) (uploadId: string) (ctx: HttpContext) : Task =
        task {
            if not (isValidUploadId uploadId) then
                do! writeError ctx 400 "uploadId 형식 위반"
            else
                let caller = userIdentityOf ctx
                let lock = acquireLock uploadId
                do! lock.WaitAsync(ctx.RequestAborted)
                try
                    let dir = uploadDir storageRoot uploadId
                    match readMetaReconciled dir with
                    | None -> do! writeError ctx 404 "uploadId 미존재"
                    | Some meta when not (isUploadOwnedBy meta caller) ->
                        Log.audit.Warn(
                            sprintf "D-S7-5 C-4: GET owner mismatch — uploadId=%s caller=%s owner=%s"
                                uploadId (Log.sanitizeForLog caller) (Log.sanitizeForLog meta.UserIdentity))
                        do! writeError ctx 403 "upload owner mismatch"
                    | Some meta ->
                        do! writeJson ctx 200
                                {| uploadId = uploadId; offset = meta.Offset; totalBytes = meta.TotalBytes; title = meta.Title |}
                finally
                    lock.Release() |> ignore
        } :> Task

    /// `DELETE /uploads-rs/{id}` — cancel. staging dir 제거 + lock entry 제거.
    /// **s6-r70 review C-4**: meta.UserIdentity cross-check.
    let private deleteUpload (storageRoot: string) (uploadId: string) (ctx: HttpContext) : Task =
        task {
            if not (isValidUploadId uploadId) then
                do! writeError ctx 400 "uploadId 형식 위반"
            else
                let caller = userIdentityOf ctx
                let lock = acquireLock uploadId
                do! lock.WaitAsync(ctx.RequestAborted)
                try
                    let dir = uploadDir storageRoot uploadId
                    // meta 검증 — owner mismatch 시 403, dir 보존.
                    let owned =
                        match readMeta dir with
                        | Some meta -> isUploadOwnedBy meta caller
                        | None -> true   // meta 부재 = staging 결손. caller 가 본인 인지 무관, 그대로 cleanup 허용.
                    if not owned then
                        Log.audit.Warn(
                            sprintf "D-S7-5 C-4: DELETE owner mismatch — uploadId=%s caller=%s"
                                uploadId (Log.sanitizeForLog caller))
                        do! writeError ctx 403 "upload owner mismatch"
                    else
                        if Directory.Exists dir then
                            try Directory.Delete(dir, true)
                            with ex ->
                                Log.service.Warn(sprintf "D-S7-5: DELETE cleanup 실패 — %s: %s" (ex.GetType().Name) ex.Message)
                            Log.audit.Info(sprintf "D-S7-5: DELETE /uploads-rs — uploadId=%s" uploadId)
                        ctx.Response.StatusCode <- 204
                finally
                    lock.Release() |> ignore
                    removeLock uploadId
        } :> Task

    /// **phase 4 (s6-r74 b1)** — finalize body parse. empty body / 빈 JSON `{}` → null. swapTargetCollectionId
    /// 박제 시 swap path 진입.
    let private readFinalizeBody (ctx: HttpContext) : Task<string> = task {
        let cl = ctx.Request.ContentLength
        if not cl.HasValue || cl.Value <= 0L then
            return null
        else
            try
                let! body =
                    JsonSerializer.DeserializeAsync<FinalizeBody>(
                        ctx.Request.Body, EndpointHelpers.DefaultJsonOpts, ctx.RequestAborted).AsTask()
                if obj.ReferenceEquals(box body, null)
                   || String.IsNullOrWhiteSpace body.SwapTargetCollectionId then
                    return null
                else
                    return body.SwapTargetCollectionId.Trim()
            with :? JsonException ->
                return null
    }

    /// **D-S7-5 phase 2 (4) + phase 4 (s6-r74 b1)** — `POST /uploads-rs/{id}/finalize` — collection 등록 또는 swap.
    ///
    /// **new collection path (swapTargetCollectionId null)**:
    ///   meta 검증 (offset == totalBytes) → 새 collectionId 발급 → 별 staging dir 생성 → payload.partial 의
    ///   zip stream extractAll → MetaJson load/stamp/save → IndexerVersion gate (CollectionEndpoints SSOT helper 재사용) →
    ///   moveStagingToCollection (atomic) → Registry.upsertAsync → EventBus.collection-added → uploadId staging cleanup.
    ///   실패 시 collectionId staging 만 removeStaging. uploadId staging 은 caller 가 DELETE 또는 StagingSweep 가 회수.
    ///
    /// **swap path (swapTargetCollectionId 박제)** — phase 4 (s6-r74 b1):
    ///   meta 검증 → Registry.tryFindById (404 미존재) → MultiTenantPolicy.evaluate (Hidden=404 / ReadOnly=403) →
    ///   별 staging dir 생성 → extractAll → MetaJson load/stamp (existing.Id, caller, existing.StorageRelPath, Title=existing.DisplayName)
    ///   → IndexerVersion gate (swap 라벨) → ZipImport.swapCollectionPayload (rollback-safe atomic) → Registry.upsertAsync
    ///   (existing 갱신) → notifier.OnPayloadSwapped → EventBus.collection-updated → uploadId staging cleanup.
    ///   응답 = 200 + `{ id: existing.Id, storageRelPath }`.
    let private postFinalize
        (cfg: ServiceConfig)
        (storageRoot: string)
        (notifier: ICollectionLifecycleNotifier)
        (eventBus: EventBus)
        (uploadId: string)
        (ctx: HttpContext)
        : Task =
        task {
            if not (isValidUploadId uploadId) then
                do! writeError ctx 400 "uploadId 형식 위반"
            else
                let lock = acquireLock uploadId
                do! lock.WaitAsync(ctx.RequestAborted)
                try
                    let dir = uploadDir storageRoot uploadId
                    let caller = userIdentityOf ctx
                    match readMetaReconciled dir with
                    | None -> do! writeError ctx 404 "uploadId 미존재"
                    | Some meta when not (isUploadOwnedBy meta caller) ->
                        Log.audit.Warn(
                            sprintf "D-S7-5 C-4: finalize owner mismatch — uploadId=%s caller=%s owner=%s"
                                uploadId (Log.sanitizeForLog caller) (Log.sanitizeForLog meta.UserIdentity))
                        do! writeError ctx 403 "upload owner mismatch"
                    | Some meta ->
                        if meta.Offset <> meta.TotalBytes then
                            do! writeError ctx 409
                                    (sprintf "incomplete — offset=%d / totalBytes=%d" meta.Offset meta.TotalBytes)
                        else
                        // **phase 4 (s6-r74 b1)** — body parse 후 swap target 박제 여부 분기.
                        let! swapTarget = readFinalizeBody ctx
                        let isSwap = not (String.IsNullOrWhiteSpace swapTarget)
                        // swap 분기: existing entry 검증 + acl 분기. new 분기: 새 collectionId 발급.
                        let mutable existingEntry : CollectionEntry option = None
                        let mutable aclReject : (int * string) option = None
                        if isSwap then
                            match Registry.tryFindById storageRoot swapTarget with
                            | None ->
                                aclReject <- Some (404, sprintf "swap target 미존재 — id=%s" swapTarget)
                            | Some entry ->
                                match MultiTenantPolicy.evaluate cfg.MultiTenant.Mode meta.UserIdentity entry with
                                | MultiTenantPolicy.AccessDecision.Hidden ->
                                    Log.audit.Warn(
                                        sprintf "D-S7-5 phase 4: finalize swap acl reject (hidden) — uploadId=%s target=%s user=%s mode=%s"
                                            uploadId swapTarget
                                            (Log.sanitizeForLog meta.UserIdentity) cfg.MultiTenant.Mode)
                                    aclReject <- Some (404, sprintf "swap target 미존재 — id=%s" swapTarget)
                                | MultiTenantPolicy.AccessDecision.ReadOnly ->
                                    Log.audit.Warn(
                                        sprintf "D-S7-5 phase 4: finalize swap acl reject (readOnly) — uploadId=%s target=%s user=%s mode=%s"
                                            uploadId swapTarget
                                            (Log.sanitizeForLog meta.UserIdentity) cfg.MultiTenant.Mode)
                                    aclReject <- Some (403, sprintf "read-only collection — id=%s" swapTarget)
                                | MultiTenantPolicy.AccessDecision.Allow ->
                                    existingEntry <- Some entry
                        match aclReject with
                        | Some (status, msg) -> do! writeError ctx status msg
                        | None ->
                            let collectionId, title, labelSuffix =
                                match existingEntry with
                                | Some entry ->
                                    // swap path — 기존 id + DisplayName 유지 (registry SSOT). client meta.Title overwrite.
                                    entry.Id, entry.DisplayName, " (resumable-swap)"
                                | None ->
                                    let newId = Guid.NewGuid().ToString("D")
                                    let safeTitle = ZipImport.sanitizeTitle meta.Title
                                    newId, safeTitle, " (resumable)"
                            let collStaging = Path.Combine(Storage.stagingDir storageRoot, collectionId)

                            Log.audit.Info(
                                sprintf "D-S7-5: finalize 시작%s — uploadId=%s collectionId=%s title=%s totalBytes=%d"
                                    labelSuffix uploadId collectionId title meta.TotalBytes)
                            try
                                Directory.CreateDirectory collStaging |> ignore
                                use partialFs = File.OpenRead(partialPath dir)
                                let _ =
                                    ZipImport.extractAll partialFs collStaging meta.TotalBytes cfg.ZipBombRatioLimit
                                partialFs.Close()

                                let clientMeta = MetaJsonIO.load collStaging
                                let storageRelPath =
                                    sprintf "Collections\\%s" (ZipImport.collectionDirName collectionId title)
                                let serverMeta =
                                    MetaJsonIO.stampServerFields collectionId meta.UserIdentity storageRelPath
                                        { clientMeta with Title = title }
                                MetaJsonIO.save collStaging serverMeta

                                let clientVer = ZipImport.probeIndexerVersion collStaging
                                let gate =
                                    ZipImport.evaluateIndexerVersionGate clientVer
                                        cfg.IndexerVersionRange.Min cfg.IndexerVersionRange.Max
                                let! compatible =
                                    CollectionEndpoints.processStagingExtractGate
                                        ctx storageRoot collectionId cfg.IndexerVersionRange gate collectionId
                                        labelSuffix
                                if compatible then
                                    match existingEntry with
                                    | Some existing ->
                                        // swap path — atomic rollback-safe (K1 unique backup), Acl 보존 (existing.Acl 직접 박제).
                                        let target =
                                            ZipImport.swapCollectionPayload storageRoot collStaging existing.Id title
                                        let baseEntry = MetaJsonRegistry.toRegistryEntry serverMeta
                                        let entry = { baseEntry with Acl = existing.Acl }
                                        do! Registry.upsertAsync storageRoot entry
                                        notifier.OnPayloadSwapped existing.Id
                                        eventBus.Publish(ServerEvent.collectionUpdated existing.Id)
                                        Log.audit.Info(
                                            sprintf "D-S7-5: finalize → collection swapped — uploadId=%s collectionId=%s target=%s"
                                                uploadId existing.Id target)
                                        if Directory.Exists dir then
                                            try Directory.Delete(dir, true) with _ -> ()
                                        do! writeJson ctx 200
                                                {| id = existing.Id; storageRelPath = entry.StorageRelPath |}
                                    | None ->
                                        let target =
                                            ZipImport.moveStagingToCollection storageRoot collStaging collectionId title
                                        let entry = MetaJsonRegistry.toRegistryEntry serverMeta
                                        do! Registry.upsertAsync storageRoot entry
                                        eventBus.Publish(ServerEvent.collectionAdded collectionId)
                                        Log.audit.Info(
                                            sprintf "D-S7-5: finalize → collection registered — uploadId=%s collectionId=%s target=%s"
                                                uploadId collectionId target)
                                        if Directory.Exists dir then
                                            try Directory.Delete(dir, true) with _ -> ()
                                        do! writeJson ctx 201
                                                {| id = collectionId; storageRelPath = entry.StorageRelPath |}
                                else
                                    // **s6-r70 review C-7** — indexerVersion gate false 분기 (TooLow/TooHigh/Missing 모두 영구 fail).
                                    // collStaging 은 processStagingExtractGate 가 이미 removeStaging 처리. uploadId staging 도
                                    // 즉시 cleanup — retry 의미 없음 (client lib 변경 의무, 동일 partial 재시도 무효). 디스크 leak 차단.
                                    if Directory.Exists dir then
                                        try Directory.Delete(dir, true) with _ -> ()
                                    Log.audit.Info(
                                        sprintf "D-S7-5 C-7: finalize gate fail — uploadId staging cleanup. uploadId=%s collectionId=%s"
                                            uploadId collectionId)
                            with
                            | SanitizeException err ->
                                Log.audit.Warn(
                                    sprintf "D-S7-5: finalize sanitize 실패%s — uploadId=%s collectionId=%s err=%A"
                                        labelSuffix uploadId collectionId err)
                                StagingSweep.removeStaging storageRoot collectionId |> ignore
                                do! writeError ctx 400 (sprintf "zip sanitize 실패: %A" err)
                            | :? FileNotFoundException as ex ->
                                Log.audit.Warn(
                                    sprintf "D-S7-5: finalize zip 구조 결함%s — uploadId=%s missing=%s"
                                        labelSuffix uploadId ex.FileName)
                                StagingSweep.removeStaging storageRoot collectionId |> ignore
                                do! writeError ctx 400
                                        (sprintf "zip 구조 결함 — %s 누락" (Path.GetFileName ex.FileName))
                            | :? InvalidDataException as ex ->
                                Log.audit.Warn(
                                    sprintf "D-S7-5: finalize zip 구조 결함%s — uploadId=%s ex=%s"
                                        labelSuffix uploadId ex.Message)
                                StagingSweep.removeStaging storageRoot collectionId |> ignore
                                do! writeError ctx 400 (sprintf "zip 구조 결함: %s" ex.Message)
                            | ex ->
                                Log.service.Error(
                                    sprintf "D-S7-5: finalize 실패%s — uploadId=%s ex=%s"
                                        labelSuffix uploadId ex.Message)
                                StagingSweep.removeStaging storageRoot collectionId |> ignore
                                do! writeError ctx 500 "internal error"
                finally
                    lock.Release() |> ignore
                    // 성공/실패 무관 — finalize 후 lock entry 회수 (uploadId 는 더 사용 안 됨)
                    removeLock uploadId
        } :> Task

    /// route mapping. AuthMiddleware 통과 후 진입. path `/uploads-rs/` (기존 `DELETE /uploads/{stagingId}` 와 분리).
    /// **phase 4 (s6-r74 b1)** — `notifier` 인자 추가 (swap path 의 OnPayloadSwapped 호출).
    let map (app: IEndpointRouteBuilder) (cfg: ServiceConfig) (storageRoot: string)
            (notifier: ICollectionLifecycleNotifier) (eventBus: EventBus) =
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
                postFinalize cfg storageRoot notifier eventBus uploadId ctx)) |> ignore
        app.MapDelete("/uploads-rs/{uploadId}",
            RequestDelegate(fun ctx ->
                let uploadId = ctx.GetRouteValue "uploadId" :?> string
                deleteUpload storageRoot uploadId ctx)) |> ignore
