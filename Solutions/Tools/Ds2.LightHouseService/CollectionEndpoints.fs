namespace Ds2.LightHouseService

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.DependencyInjection
open Ds2.LightHouse.Protocol

/// Phase S2 collection 관리 endpoint (done-lighthouse-kb-server.md §3.9 / §4.2 Phase S2).
///
/// 포함:
/// - POST /collections (multipart zip + title) → server 가 guid 발급 (D3) + 등록
/// - GET /collections (registry list, T1 flat)
/// - GET /collections/{id}/status
/// - POST /collections/{id}/payload (재업로드 swap)
/// - DELETE /collections/{id} (purge)
/// - DELETE /uploads/{stagingId} (cancel hook)
[<RequireQualifiedAccess>]
module CollectionEndpoints =

    // **C-15 (s6-r79)** — 4 helper 폐기 → `EndpointHelpers` SSOT 통과 alias. caller 변경 0 (signature 정합).
    let private userIdentityOf = EndpointHelpers.userIdentityOf

    /// multipart form 의 "title" 필드 + "overwrite" 필드 + "zip" 파일 추출.
    /// title 누락 시 400. zip 누락 시 400.
    ///
    /// **옵션 A overwrite (2026-05-30)** — 동일 폴더명(collectionId) 존재 시 덮어쓰기 여부. **default true**
    /// (필드 미박제 시 overwrite — 기존 client 호환). `"false"`/`"0"`/`"off"` (대소문자 무시) 만 fail 모드.
    let private parseMultipart (ctx: HttpContext) : Task<Result<string * bool * IFormFile, int * string>> = task {
        if not (ctx.Request.HasFormContentType) then
            return Error (415, "multipart/form-data 필수")
        else
            let! form = ctx.Request.ReadFormAsync()
            let title =
                match form.TryGetValue "title" with
                | true, v -> v.ToString()
                | _ -> ""
            let overwrite =
                match form.TryGetValue "overwrite" with
                | true, v ->
                    let s = v.ToString().Trim().ToLowerInvariant()
                    not (s = "false" || s = "0" || s = "off")
                | _ -> true
            if String.IsNullOrWhiteSpace title then
                return Error (400, "title 필드 필수")
            else
                if form.Files.Count = 0 then
                    return Error (400, "zip 파일 필드 필수")
                else
                    let zip = form.Files.[0]
                    return Ok (title.Trim(), overwrite, zip)
    }

    let private writeJson = EndpointHelpers.writeJson
    let private writeError = EndpointHelpers.writeError

    /// **IndexerVersion gate 분기 처리 SSOT (s6-r43 / 외부 --review L-Maj-5 정정)** — `postCollections` /
    /// `postCollectionPayload` 양쪽 박제 중복 (415 응답 4 키 + Log.audit.Warn + removeStaging) 흡수.
    /// `Compatible` → `true` 반환 (caller 가 후속 박제). 나머지 → 415/400 응답 박제 + `false` 반환.
    /// `labelSuffix` = "" (postCollections) 또는 " (swap)" (postCollectionPayload) — log 메시지 식별용.
    /// postCollectionPayload 의 Missing 분기에 누락되어 있던 Log.audit.Warn 박제도 본 helper 안에서 일관 박제.
    // **D-S7-5 phase 2 (s6-r63)** — `private` 제거 → 다른 module (UploadsEndpoint.postFinalize) 도 본 helper 호출. SSOT 유지.
    let processStagingExtractGate
        (ctx: HttpContext)
        (storageRoot: string)
        (stagingId: string)
        (range: IndexerVersionRange)
        (gate: IndexerVersionGateResult)
        (identifier: string)
        (labelSuffix: string)
        : Task<bool> = task {
            match gate with
            | IndexerVersionGateResult.Compatible -> return true
            | IndexerVersionGateResult.TooLow(v, m) ->
                Log.audit.Warn(sprintf "indexerVersion gate too-low%s — id=%s client=%s hostMin=%s" labelSuffix identifier v m)
                StagingSweep.removeStaging storageRoot stagingId |> ignore
                do! writeJson ctx 415 {| error = "indexerVersion too low"; clientVersion = v; hostingRange = {| min = range.Min; max = range.Max |}; suggestedAction = "client Ds2.LightHouse lib 업그레이드 후 재색인 / 재업로드" |}
                return false
            | IndexerVersionGateResult.TooHigh(v, m) ->
                // suggestedAction: 두 회복 경로 모두 안내 (P5).
                // (a) host 측: Ds2.LightHouseService 업그레이드 (IndexerVersionRange.Max 확대)
                // (b) client 측: Ds2.LightHouse lib 다운그레이드 후 재색인 / 재업로드
                Log.audit.Warn(sprintf "indexerVersion gate too-high%s — id=%s client=%s hostMax=%s" labelSuffix identifier v m)
                StagingSweep.removeStaging storageRoot stagingId |> ignore
                do! writeJson ctx 415 {| error = "indexerVersion too high"; clientVersion = v; hostingRange = {| min = range.Min; max = range.Max |}; suggestedAction = "service 업그레이드 또는 client Ds2.LightHouse lib 다운그레이드 후 재색인 / 재업로드" |}
                return false
            | IndexerVersionGateResult.KeyMissing ->
                Log.audit.Warn(sprintf "indexerVersion gate key-missing%s — id=%s" labelSuffix identifier)
                StagingSweep.removeStaging storageRoot stagingId |> ignore
                do! writeJson ctx 400 {| error = "indexerVersion key missing"
                                         reason = "zip 안 index.db 에 Meta.indexer_version 키 미박제 (이전 build 산출 가능성)"
                                         suggestedAction = "client 측 caption 보존 자동 path 활용: lighthouse-cli export-image-cache → wipe .lighthouse-kb/ → 재색인 (--skip-upload) → import-image-cache → upload (--reuse-kb)" |}
                return false
            | IndexerVersionGateResult.DbMissing ->
                Log.audit.Warn(sprintf "indexerVersion gate db-missing%s — id=%s" labelSuffix identifier)
                StagingSweep.removeStaging storageRoot stagingId |> ignore
                do! writeJson ctx 400 {| error = "indexerVersion db missing"
                                         reason = "zip 안 .lighthouse-kb/index.db 부재 (zip 결함 또는 client 색인 미수행)"
                                         suggestedAction = "client 측 'lighthouse-cli index <folder> --skip-upload' 으로 Step 1-a 색인 수행 후 재upload" |}
                return false
            | IndexerVersionGateResult.OpenFailed reason ->
                Log.audit.Warn(sprintf "indexerVersion gate open-failed%s — id=%s reason=%s" labelSuffix identifier reason)
                StagingSweep.removeStaging storageRoot stagingId |> ignore
                do! writeJson ctx 400 {| error = "indexerVersion db open failed"
                                         reason = sprintf "zip 안 index.db open 실패 — %s. schema 또는 sqlite-vec extension drift 가능" reason
                                         suggestedAction = "client 측 .lighthouse-kb/ wipe 후 재색인 — open 실패라 caption 보존은 불가, 신규 caption 비용 발생" |}
                return false
        }

    /// POST /collections — multipart zip + title → 등록 OR 멱등 overwrite.
    ///
    /// **옵션 A (guid 폐기 + 폴더명 멱등, 2026-05-30)**:
    /// - `collectionId = sanitizeTitle(title)` — server 발급 guid 폐기, 폴더명이 canonical 식별 키.
    /// - 같은 이름 collection 이 이미 있으면:
    ///   - `overwrite=true` (default) → 기존 payload swap (rollback-safe, Acl 보존) + `collection-updated`.
    ///   - `overwrite=false` → 409 conflict (fail 모드).
    /// - 없으면 → 신규 등록 (atomic move) + `collection-added`.
    /// - **staging 은 unique guid 유지** — 동일 title 동시 upload 시 작업공간 충돌 회피 (최종 식별자와 분리).
    /// - 멱등 overwrite 는 swap 의미 → multi-tenant acl 검증 (Hidden=404 / ReadOnly=403, T1 모드는 항상 Allow).
    let private postCollections
        (cfg: ServiceConfig)
        (storageRoot: string)
        (notifier: ICollectionLifecycleNotifier)
        (bus: EventBus)
        (ctx: HttpContext)
        : Task =
        task {
            let user = userIdentityOf ctx
            match! parseMultipart ctx with
            | Error (status, msg) ->
                do! writeError ctx status msg
            | Ok (rawTitle, overwrite, zip) ->
                // review IC-2: title sanitize SSOT 일원화. **옵션 A** — sanitized title 자체가 collectionId.
                let title = ZipImport.sanitizeTitle rawTitle
                let collectionId = title
                // staging 은 unique guid 유지 — 동일 title 동시 upload race 회피 (최종 식별자 collectionId 와 분리).
                let stagingId = Guid.NewGuid().ToString("D")
                let stagingPath = Path.Combine(Storage.stagingDir storageRoot, stagingId)

                let existingOpt = Registry.tryFindById storageRoot collectionId

                // 멱등 overwrite = swap 의미 → 기존 entry 의 multi-tenant acl 검증 (T1 모드는 항상 Allow).
                let aclReject =
                    match existingOpt with
                    | Some existing ->
                        match MultiTenantPolicy.evaluate cfg.MultiTenant.Mode user existing with
                        | MultiTenantPolicy.AccessDecision.Hidden ->
                            Log.audit.Warn(sprintf "POST /collections overwrite acl reject (hidden) — id=%s user=%s mode=%s" collectionId user cfg.MultiTenant.Mode)
                            Some (404, sprintf "collection 미존재 — id=%s" collectionId)
                        | MultiTenantPolicy.AccessDecision.ReadOnly ->
                            Log.audit.Warn(sprintf "POST /collections overwrite acl reject (readOnly) — id=%s user=%s mode=%s" collectionId user cfg.MultiTenant.Mode)
                            Some (403, sprintf "read-only collection — id=%s" collectionId)
                        | MultiTenantPolicy.AccessDecision.Allow -> None
                    | None -> None

                match aclReject with
                | Some (status, msg) -> do! writeError ctx status msg
                | None ->
                match existingOpt with
                | Some _ when not overwrite ->
                    // fail 모드 — 동일 이름 존재 + overwrite=false.
                    Log.audit.Info(sprintf "collection upload 거부 (overwrite=false, 동일 이름 존재) — id=%s by=%s" collectionId user)
                    do! writeJson ctx 409 {| error = "collection exists"; id = collectionId
                                             reason = sprintf "동일 이름 collection 이미 존재 — id=%s" collectionId
                                             suggestedAction = "overwrite=true 로 재요청하거나 다른 폴더명 사용" |}
                | _ ->
                    Log.audit.Info(sprintf "collection upload 시작 — id=%s overwrite=%b by=%s size=%d"
                        collectionId overwrite user zip.Length)
                    try
                        Directory.CreateDirectory stagingPath |> ignore
                        // zip stream 을 staging 에 직접 extract — sanitize + bomb 가드.
                        use zipStream = zip.OpenReadStream()
                        let decompressed =
                            ZipImport.extractAll zipStream stagingPath zip.Length cfg.ZipBombRatioLimit
                        Log.service.Info(sprintf "zip extracted — collection=%s compressed=%d decompressed=%d"
                            collectionId zip.Length decompressed)

                        // meta.json 검증 + server 필드 stamp. safeTitle 로 clientMeta.Title overwrite.
                        let clientMeta = MetaJsonIO.load stagingPath
                        let storageRelPath = sprintf "Collections\\%s" (ZipImport.collectionDirName collectionId title)
                        let serverMeta =
                            MetaJsonIO.stampServerFields collectionId user storageRelPath { clientMeta with Title = title }
                        MetaJsonIO.save stagingPath serverMeta

                        // IndexerVersion gate (§3.12) — SSOT = `processStagingExtractGate`. gate fail 시 stagingId 정리.
                        let probe = Ds2.LightHouse.KnowledgeBase.probeIndexerVersionDetailed stagingPath
                        let gate = ZipImport.evaluateIndexerVersionGate probe cfg.IndexerVersionRange.Min cfg.IndexerVersionRange.Max
                        let labelSuffix = if existingOpt.IsSome then " (overwrite)" else ""
                        let! compatible = processStagingExtractGate ctx storageRoot stagingId cfg.IndexerVersionRange gate collectionId labelSuffix
                        if compatible then
                            match existingOpt with
                            | Some existing ->
                                // 멱등 overwrite — 기존 collection payload swap (rollback-safe, K1 unique backup). Acl 보존.
                                let target = ZipImport.swapCollectionPayload storageRoot stagingPath collectionId title
                                let entry = { MetaJsonRegistry.toRegistryEntry serverMeta with Acl = existing.Acl }
                                do! Registry.upsertAsync storageRoot entry
                                notifier.OnPayloadSwapped collectionId  // active session KB 무효화
                                Log.audit.Info(sprintf "collection overwritten — id=%s by=%s target=%s" collectionId user target)
                                bus.Publish(ServerEvent.collectionUpdated collectionId)
                                do! writeJson ctx 200 {| id = collectionId; storageRelPath = entry.StorageRelPath |}
                            | None ->
                                // atomic move → Collections\<sanitized-title>\
                                let target = ZipImport.moveStagingToCollection storageRoot stagingPath collectionId title
                                let entry = MetaJsonRegistry.toRegistryEntry serverMeta
                                do! Registry.upsertAsync storageRoot entry
                                Log.audit.Info(sprintf "collection registered — id=%s by=%s target=%s" collectionId user target)
                                bus.Publish(ServerEvent.collectionAdded collectionId)  // D-S7-2 s6-r27
                                do! writeJson ctx 201 {| id = collectionId; storageRelPath = entry.StorageRelPath |}
                    with
                    | SanitizeException err ->
                        Log.audit.Warn(sprintf "sanitize 실패 — id=%s by=%s err=%A" collectionId user err)
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        do! writeError ctx 400 (sprintf "zip sanitize 실패: %A" err)
                    | :? FileNotFoundException as ex ->
                        // zip 에 meta.json / index.db 누락 — client 결함 (400, review M6)
                        Log.audit.Warn(sprintf "zip 구조 결함 — id=%s by=%s missing=%s" collectionId user ex.FileName)
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        do! writeError ctx 400 (sprintf "zip 구조 결함 — %s 누락" (Path.GetFileName ex.FileName))
                    | :? InvalidDataException as ex ->
                        // meta.json schemaVersion mismatch / 역직렬화 실패 — client 결함 (400, review M6)
                        Log.audit.Warn(sprintf "zip 구조 결함 — id=%s by=%s ex=%s" collectionId user ex.Message)
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        do! writeError ctx 400 (sprintf "zip 구조 결함: %s" ex.Message)
                    | ex ->
                        Log.service.Error(sprintf "collection upload 실패 — id=%s ex=%s" collectionId ex.Message)
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        do! writeError ctx 500 "internal error"
        } :> Task

    /// GET /collections — registry list. **s6-r66 D-S7-4**: T2/T3 모드에서 visible filter.
    /// T1 = 전체 (현행). T2 = ImportedBy=user / legacy entry 만. T3 = acl 검증.
    let private getCollectionsList (cfg: ServiceConfig) (storageRoot: string) (ctx: HttpContext) : Task =
        task {
            let user = userIdentityOf ctx
            let entries = Registry.listSnapshot storageRoot
            let visible = MultiTenantPolicy.filterVisible cfg.MultiTenant.Mode user entries
            do! writeJson ctx 200 {|
                schemaVersion = RegistrySchema.Current
                collections = visible
            |}
        } :> Task

    /// GET /collections/{id}/status — **s6-r66 D-S7-4**: Hidden 시 404 (acl reject ↔ 미존재 구분 안 함, 정보 leak 차단).
    let private getCollectionStatus (cfg: ServiceConfig) (storageRoot: string) (id: string) (ctx: HttpContext) : Task =
        task {
            let user = userIdentityOf ctx
            match Registry.tryFindById storageRoot id with
            | None ->
                do! writeError ctx 404 (sprintf "collection 미존재 — id=%s" id)
            | Some entry ->
                match MultiTenantPolicy.evaluate cfg.MultiTenant.Mode user entry with
                | MultiTenantPolicy.AccessDecision.Hidden ->
                    Log.audit.Warn(sprintf "GET /collections/%s/status: acl reject — user=%s mode=%s" id user cfg.MultiTenant.Mode)
                    do! writeError ctx 404 (sprintf "collection 미존재 — id=%s" id)
                | _ ->
                    do! writeJson ctx 200 {|
                        id = entry.Id
                        status = entry.Status
                        errorReason = entry.ErrorReason
                        lastImportedAt = entry.LastImportedAt
                    |}
        } :> Task

    /// POST /collections/{id}/payload — 재업로드 swap.
    /// bus (s6-r27, D-S7-2) — Compatible 분기 swap 성공 시 `collection-updated` event publish.
    let private postCollectionPayload
        (cfg: ServiceConfig)
        (storageRoot: string)
        (notifier: ICollectionLifecycleNotifier)
        (bus: EventBus)
        (id: string)
        (ctx: HttpContext)
        : Task =
        task {
            let user = userIdentityOf ctx
            match Registry.tryFindById storageRoot id with
            | None ->
                do! writeError ctx 404 (sprintf "collection 미존재 — id=%s" id)
            | Some existing when
                (match MultiTenantPolicy.evaluate cfg.MultiTenant.Mode user existing with
                 | MultiTenantPolicy.AccessDecision.Hidden -> true
                 | _ -> false) ->
                // **s6-r66 D-S7-4** — Hidden 은 404 (acl reject ↔ 미존재 정보 leak 차단). Audit 박제.
                Log.audit.Warn(sprintf "POST /collections/%s/payload: acl reject (hidden) — user=%s mode=%s" id user cfg.MultiTenant.Mode)
                do! writeError ctx 404 (sprintf "collection 미존재 — id=%s" id)
            | Some existing when not (MultiTenantPolicy.canMutate cfg.MultiTenant.Mode user existing) ->
                // **s6-r66 D-S7-4** — ReadOnly 는 403 (visible 이지만 mutation 거부, T3 acl.readOnly=true).
                Log.audit.Warn(sprintf "POST /collections/%s/payload: acl reject (readOnly) — user=%s mode=%s" id user cfg.MultiTenant.Mode)
                do! writeError ctx 403 (sprintf "read-only collection — id=%s" id)
            | Some existing ->
                match! parseMultipart ctx with
                | Error (status, msg) ->
                    do! writeError ctx status msg
                | Ok (rawTitle, _overwrite, zip) ->
                    // swap endpoint — overwrite 무관 (기존 id 명시 교체). overwrite 필드는 무시.
                    let stagingId = Guid.NewGuid().ToString("D")
                    let stagingPath = Path.Combine(Storage.stagingDir storageRoot, stagingId)
                    // review IC-2 (swap path 변형): title 은 첫 upload 시점에 고정 — swap 은 payload (zip 안 source/ + .lighthouse-kb/) 만 교체.
                    // 새 rawTitle 입력은 (a) audit 진단 hint 로만 sanitize 후 박제, (b) 디렉토리/meta SSOT 는 existing.DisplayName.
                    // title rename 은 별도 endpoint (PUT /collections/{id}) 책임 — Phase S7 옵션. 본 phase 의 swap = payload-only.
                    let titleHint = ZipImport.sanitizeTitle rawTitle
                    let title = existing.DisplayName   // SSOT = registry. swap 이 dir/meta 의 title 을 변경하지 않음.
                    Log.audit.Info(sprintf "collection payload swap 시작 — id=%s title=%s hint=%s by=%s size=%d"
                        id title titleHint user zip.Length)

                    try
                        Directory.CreateDirectory stagingPath |> ignore
                        use zipStream = zip.OpenReadStream()
                        ZipImport.extractAll zipStream stagingPath zip.Length cfg.ZipBombRatioLimit |> ignore

                        let clientMeta = MetaJsonIO.load stagingPath
                        // payload swap 도 id 는 server 가 *기존* id 유지 — client meta 의 id 무시.
                        // Title 은 existing 그대로 (registry SSOT) — client meta.Title 도 overwrite.
                        let storageRelPath = sprintf "Collections\\%s" (ZipImport.collectionDirName id title)
                        let serverMeta =
                            MetaJsonIO.stampServerFields id user storageRelPath { clientMeta with Title = title }
                        MetaJsonIO.save stagingPath serverMeta

                        let probe = Ds2.LightHouse.KnowledgeBase.probeIndexerVersionDetailed stagingPath
                        let gate = ZipImport.evaluateIndexerVersionGate probe cfg.IndexerVersionRange.Min cfg.IndexerVersionRange.Max
                        // s6-r43: postCollections 와 동일 SSOT (`processStagingExtractGate`) — swap label 박제.
                        let! compatible = processStagingExtractGate ctx storageRoot stagingId cfg.IndexerVersionRange gate id " (swap)"
                        if compatible then
                            // existing.DisplayName 으로 swap 대상 폴더 이름 산출 — IC-2 fix 후 title=existing.DisplayName 라 동일 결과.
                            let target = ZipImport.swapCollectionPayload storageRoot stagingPath id title
                            let entry = MetaJsonRegistry.toRegistryEntry serverMeta
                            do! Registry.upsertAsync storageRoot entry
                            notifier.OnPayloadSwapped id
                            Log.audit.Info(sprintf "collection payload swapped — id=%s by=%s target=%s" id user target)
                            bus.Publish(ServerEvent.collectionUpdated id)  // D-S7-2 s6-r27
                            do! writeJson ctx 200 {| id = id; storageRelPath = entry.StorageRelPath |}
                    with
                    | SanitizeException err ->
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        do! writeError ctx 400 (sprintf "zip sanitize 실패: %A" err)
                    | :? FileNotFoundException as ex ->
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        do! writeError ctx 400 (sprintf "zip 구조 결함 — %s 누락" (Path.GetFileName ex.FileName))
                    | :? InvalidDataException as ex ->
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        do! writeError ctx 400 (sprintf "zip 구조 결함: %s" ex.Message)
                    | ex ->
                        Log.service.Error(sprintf "collection payload swap 실패 — id=%s ex=%s" id ex.Message)
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        do! writeError ctx 500 "internal error"
        } :> Task

    /// DELETE /collections/{id} — registry 제거 + Collections\<dir> purge + active session detach 신호.
    /// bus (s6-r27, D-S7-2) — registry 제거 성공 시 `collection-deleted` event publish.
    let private deleteCollection
        (cfg: ServiceConfig)
        (storageRoot: string)
        (notifier: ICollectionLifecycleNotifier)
        (bus: EventBus)
        (id: string)
        (ctx: HttpContext)
        : Task =
        task {
            let user = userIdentityOf ctx
            match Registry.tryFindById storageRoot id with
            | None ->
                do! writeError ctx 404 (sprintf "collection 미존재 — id=%s" id)
            | Some entry ->
                // **s6-r66 D-S7-4** — Hidden / ReadOnly 모두 mutation reject. 404 / 403 분기.
                match MultiTenantPolicy.evaluate cfg.MultiTenant.Mode user entry with
                | MultiTenantPolicy.AccessDecision.Hidden ->
                    Log.audit.Warn(sprintf "DELETE /collections/%s: acl reject (hidden) — user=%s mode=%s" id user cfg.MultiTenant.Mode)
                    do! writeError ctx 404 (sprintf "collection 미존재 — id=%s" id)
                | MultiTenantPolicy.AccessDecision.ReadOnly ->
                    Log.audit.Warn(sprintf "DELETE /collections/%s: acl reject (readOnly) — user=%s mode=%s" id user cfg.MultiTenant.Mode)
                    do! writeError ctx 403 (sprintf "read-only collection — id=%s" id)
                | MultiTenantPolicy.AccessDecision.Allow ->
                    // 순서: 1) active session detach 신호 (Phase S3 시점에 실 detach) → 2) registry 제거 → 3) 디스크 purge
                    notifier.OnDeleted id
                    let! _ = Registry.removeAsync storageRoot id
                    ZipImport.purgeCollection storageRoot id entry.DisplayName
                    Log.audit.Info(sprintf "collection deleted — id=%s by=%s" id user)
                    bus.Publish(ServerEvent.collectionDeleted id)  // D-S7-2 s6-r27
                    ctx.Response.StatusCode <- 204
        } :> Task

    /// DELETE /uploads/{stagingId} — client cancel hook.
    let private deleteUpload (storageRoot: string) (stagingId: string) (ctx: HttpContext) : Task =
        task {
            let removed = StagingSweep.removeStaging storageRoot stagingId
            if removed then ctx.Response.StatusCode <- 204
            else
                do! writeError ctx 404 (sprintf "staging 미존재 — id=%s" stagingId)
        } :> Task

    /// 인증 통과 endpoint 등록. AuthMiddleware 뒤에 매핑.
    /// bus (s6-r27, D-S7-2) — collection mutation event publisher. DI singleton 으로 caller 가 전달.
    let map
        (app: IEndpointRouteBuilder)
        (cfg: ServiceConfig)
        (notifier: ICollectionLifecycleNotifier)
        (bus: EventBus)
        =
        let storageRoot = Config.expandEnv cfg.StorageRoot

        app.MapPost("/collections", RequestDelegate(postCollections cfg storageRoot notifier bus)) |> ignore
        app.MapGet("/collections", RequestDelegate(getCollectionsList cfg storageRoot)) |> ignore
        app.MapGet("/collections/{id}/status", RequestDelegate(fun ctx ->
            let id = ctx.Request.RouteValues.["id"] |> string
            getCollectionStatus cfg storageRoot id ctx)) |> ignore
        app.MapPost("/collections/{id}/payload", RequestDelegate(fun ctx ->
            let id = ctx.Request.RouteValues.["id"] |> string
            postCollectionPayload cfg storageRoot notifier bus id ctx)) |> ignore
        app.MapDelete("/collections/{id}", RequestDelegate(fun ctx ->
            let id = ctx.Request.RouteValues.["id"] |> string
            deleteCollection cfg storageRoot notifier bus id ctx)) |> ignore
        app.MapDelete("/uploads/{stagingId}", RequestDelegate(fun ctx ->
            let stagingId = ctx.Request.RouteValues.["stagingId"] |> string
            deleteUpload storageRoot stagingId ctx)) |> ignore
