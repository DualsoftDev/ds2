namespace Ds2.LightHouseService

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.DependencyInjection

/// Phase S2 collection 관리 endpoint (todo-lighthouse-kb-server.md §3.9 / §4.2 Phase S2).
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

    /// review IM-2 (3/7 reviewer): hot-path response — module-level singleton.
    /// 매 endpoint 호출마다 신규 JsonSerializerOptions allocation 방지.
    let private jsonResponseOpts = JsonSerializerOptions(WriteIndented = false)
    let private jsonOptions () = jsonResponseOpts

    /// HttpContext.Items 의 X-User-Identity (AuthMiddleware 가 박제).
    let private userIdentityOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue AuthMiddleware.UserIdentityItemKey with
        | true, v when not (isNull v) -> string v
        | _ -> "unknown"

    /// multipart form 의 "title" 필드 + "zip" 파일 추출.
    /// title 누락 시 400. zip 누락 시 400.
    let private parseMultipart (ctx: HttpContext) : Task<Result<string * IFormFile, int * string>> = task {
        if not (ctx.Request.HasFormContentType) then
            return Error (415, "multipart/form-data 필수")
        else
            let! form = ctx.Request.ReadFormAsync()
            let title =
                match form.TryGetValue "title" with
                | true, v -> v.ToString()
                | _ -> ""
            if String.IsNullOrWhiteSpace title then
                return Error (400, "title 필드 필수")
            else
                if form.Files.Count = 0 then
                    return Error (400, "zip 파일 필드 필수")
                else
                    let zip = form.Files.[0]
                    return Ok (title.Trim(), zip)
    }

    let private writeJson (ctx: HttpContext) (status: int) (body: obj) : Task =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        let json = JsonSerializer.Serialize(body, jsonOptions())
        ctx.Response.WriteAsync json

    let private writeError (ctx: HttpContext) (status: int) (message: string) : Task =
        writeJson ctx status {| error = message |}

    /// POST /collections — multipart zip + title → guid 발급 + 전개 + IndexerVersion gate + 등록.
    /// notifier 는 본 endpoint 에서 미사용 — payload swap / delete 가 호출. 신규 등록은 detach 불요.
    /// bus (s6-r27, D-S7-2) — Compatible 분기 성공 시 `collection-added` event publish.
    let private postCollections
        (cfg: ServiceConfig)
        (storageRoot: string)
        (_notifier: ICollectionLifecycleNotifier)
        (bus: EventBus)
        (ctx: HttpContext)
        : Task =
        task {
            let user = userIdentityOf ctx
            match! parseMultipart ctx with
            | Error (status, msg) ->
                do! writeError ctx status msg
            | Ok (rawTitle, zip) ->
                // server 가 guid 발급 (D3 / CR3)
                let collectionId = Guid.NewGuid().ToString("D")
                let stagingId = collectionId
                let stagingPath = Path.Combine(Storage.stagingDir storageRoot, stagingId)

                // review IC-2 (3/7 reviewer): title sanitize SSOT 일원화.
                // parseMultipart 직후 1회 산출 → meta.Title / Registry.DisplayName / storageRelPath / 모든 로그 일관.
                // raw title 의 unicode bidi / control char / CR-LF 가 audit log 를 spoofing 하는 위험 차단 + swap path 의 path drift 차단.
                let title = ZipImport.sanitizeTitle rawTitle

                Log.audit.Info(sprintf "collection upload 시작 — id=%s title=%s by=%s size=%d"
                    collectionId title user zip.Length)

                try
                    Directory.CreateDirectory stagingPath |> ignore
                    // zip stream 을 staging 에 직접 extract — sanitize + bomb 가드.
                    use zipStream = zip.OpenReadStream()
                    let decompressed =
                        ZipImport.extractAll zipStream stagingPath zip.Length cfg.ZipBombRatioLimit
                    Log.service.Info(sprintf "zip extracted — collection=%s compressed=%d decompressed=%d"
                        collectionId zip.Length decompressed)

                    // meta.json 검증 + server 필드 stamp.
                    // safeTitle (`title`) 로 clientMeta.Title 도 overwrite — Registry / 디렉토리명과 일관.
                    let clientMeta = MetaJson.load stagingPath
                    let storageRelPath = sprintf "Collections\\%s" (ZipImport.collectionDirName collectionId title)
                    let serverMeta =
                        MetaJson.stampServerFields collectionId user storageRelPath { clientMeta with Title = title }
                    MetaJson.save stagingPath serverMeta

                    // IndexerVersion gate (§3.12)
                    let clientVer = ZipImport.probeIndexerVersion stagingPath
                    let gate = ZipImport.evaluateIndexerVersionGate clientVer cfg.IndexerVersionRange.Min cfg.IndexerVersionRange.Max
                    match gate with
                    | IndexerVersionGateResult.Compatible ->
                        // atomic move → Collections\<guid>-<sanitized>\
                        let target = ZipImport.moveStagingToCollection storageRoot stagingPath collectionId title
                        let entry = MetaJson.toRegistryEntry serverMeta
                        do! Registry.upsertAsync storageRoot entry
                        Log.audit.Info(sprintf "collection registered — id=%s by=%s target=%s" collectionId user target)
                        bus.Publish(ServerEvent.collectionAdded collectionId)  // D-S7-2 s6-r27
                        do! writeJson ctx 201 {| id = collectionId; storageRelPath = entry.StorageRelPath |}
                    | IndexerVersionGateResult.TooLow(v, m) ->
                        Log.audit.Warn(sprintf "indexerVersion gate too-low — id=%s client=%s hostMin=%s" collectionId v m)
                        // staging 정리
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        do! writeJson ctx 415 {| error = "indexerVersion too low"; clientVersion = v; hostingRange = {| min = cfg.IndexerVersionRange.Min; max = cfg.IndexerVersionRange.Max |}; suggestedAction = "client Ds2.LightHouse lib 업그레이드 후 재색인 / 재업로드" |}
                    | IndexerVersionGateResult.TooHigh(v, m) ->
                        Log.audit.Warn(sprintf "indexerVersion gate too-high — id=%s client=%s hostMax=%s" collectionId v m)
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        // suggestedAction: 두 회복 경로 모두 안내 (P5).
                        // (a) host 측: Ds2.LightHouseService 업그레이드 (IndexerVersionRange.Max 확대)
                        // (b) client 측: Ds2.LightHouse lib 다운그레이드 후 재색인 / 재업로드
                        do! writeJson ctx 415 {| error = "indexerVersion too high"; clientVersion = v; hostingRange = {| min = cfg.IndexerVersionRange.Min; max = cfg.IndexerVersionRange.Max |}; suggestedAction = "service 업그레이드 또는 client Ds2.LightHouse lib 다운그레이드 후 재색인 / 재업로드" |}
                    | IndexerVersionGateResult.Missing reason ->
                        Log.audit.Warn(sprintf "indexerVersion gate missing — id=%s reason=%s" collectionId reason)
                        StagingSweep.removeStaging storageRoot stagingId |> ignore
                        do! writeError ctx 400 (sprintf "indexerVersion 미존재 — %s" reason)
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

    /// GET /collections — registry list (T1 flat).
    let private getCollectionsList (storageRoot: string) (ctx: HttpContext) : Task =
        task {
            let entries = Registry.listSnapshot storageRoot
            do! writeJson ctx 200 {|
                schemaVersion = RegistrySchema.Current
                collections = entries
            |}
        } :> Task

    /// GET /collections/{id}/status
    let private getCollectionStatus (storageRoot: string) (id: string) (ctx: HttpContext) : Task =
        task {
            match Registry.tryFindById storageRoot id with
            | None ->
                do! writeError ctx 404 (sprintf "collection 미존재 — id=%s" id)
            | Some entry ->
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
            | Some existing ->
                match! parseMultipart ctx with
                | Error (status, msg) ->
                    do! writeError ctx status msg
                | Ok (rawTitle, zip) ->
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

                        let clientMeta = MetaJson.load stagingPath
                        // payload swap 도 id 는 server 가 *기존* id 유지 — client meta 의 id 무시.
                        // Title 은 existing 그대로 (registry SSOT) — client meta.Title 도 overwrite.
                        let storageRelPath = sprintf "Collections\\%s" (ZipImport.collectionDirName id title)
                        let serverMeta =
                            MetaJson.stampServerFields id user storageRelPath { clientMeta with Title = title }
                        MetaJson.save stagingPath serverMeta

                        let clientVer = ZipImport.probeIndexerVersion stagingPath
                        let gate = ZipImport.evaluateIndexerVersionGate clientVer cfg.IndexerVersionRange.Min cfg.IndexerVersionRange.Max
                        match gate with
                        | IndexerVersionGateResult.Compatible ->
                            // existing.DisplayName 으로 swap 대상 폴더 이름 산출 — IC-2 fix 후 title=existing.DisplayName 라 동일 결과.
                            let target = ZipImport.swapCollectionPayload storageRoot stagingPath id title
                            let entry = MetaJson.toRegistryEntry serverMeta
                            do! Registry.upsertAsync storageRoot entry
                            notifier.OnPayloadSwapped id
                            Log.audit.Info(sprintf "collection payload swapped — id=%s by=%s target=%s" id user target)
                            bus.Publish(ServerEvent.collectionUpdated id)  // D-S7-2 s6-r27
                            do! writeJson ctx 200 {| id = id; storageRelPath = entry.StorageRelPath |}
                        | IndexerVersionGateResult.TooLow(v, m) ->
                            // s6-r7 (M1): swap 경로도 postCollections 와 동일 SSOT 박제 — hostingRange + suggestedAction.
                            Log.audit.Warn(sprintf "indexerVersion gate too-low (swap) — id=%s client=%s hostMin=%s" id v m)
                            StagingSweep.removeStaging storageRoot stagingId |> ignore
                            do! writeJson ctx 415 {| error = "indexerVersion too low"; clientVersion = v; hostingRange = {| min = cfg.IndexerVersionRange.Min; max = cfg.IndexerVersionRange.Max |}; suggestedAction = "client Ds2.LightHouse lib 업그레이드 후 재색인 / 재업로드" |}
                        | IndexerVersionGateResult.TooHigh(v, m) ->
                            // s6-r7 (M1): postCollections 의 P5 정정 정합 — 양 회복 옵션 박제.
                            Log.audit.Warn(sprintf "indexerVersion gate too-high (swap) — id=%s client=%s hostMax=%s" id v m)
                            StagingSweep.removeStaging storageRoot stagingId |> ignore
                            do! writeJson ctx 415 {| error = "indexerVersion too high"; clientVersion = v; hostingRange = {| min = cfg.IndexerVersionRange.Min; max = cfg.IndexerVersionRange.Max |}; suggestedAction = "service 업그레이드 또는 client Ds2.LightHouse lib 다운그레이드 후 재색인 / 재업로드" |}
                        | IndexerVersionGateResult.Missing reason ->
                            StagingSweep.removeStaging storageRoot stagingId |> ignore
                            do! writeError ctx 400 (sprintf "indexerVersion 미존재 — %s" reason)
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
        app.MapGet("/collections", RequestDelegate(getCollectionsList storageRoot)) |> ignore
        app.MapGet("/collections/{id}/status", RequestDelegate(fun ctx ->
            let id = ctx.Request.RouteValues.["id"] |> string
            getCollectionStatus storageRoot id ctx)) |> ignore
        app.MapPost("/collections/{id}/payload", RequestDelegate(fun ctx ->
            let id = ctx.Request.RouteValues.["id"] |> string
            postCollectionPayload cfg storageRoot notifier bus id ctx)) |> ignore
        app.MapDelete("/collections/{id}", RequestDelegate(fun ctx ->
            let id = ctx.Request.RouteValues.["id"] |> string
            deleteCollection storageRoot notifier bus id ctx)) |> ignore
        app.MapDelete("/uploads/{stagingId}", RequestDelegate(fun ctx ->
            let stagingId = ctx.Request.RouteValues.["stagingId"] |> string
            deleteUpload storageRoot stagingId ctx)) |> ignore
