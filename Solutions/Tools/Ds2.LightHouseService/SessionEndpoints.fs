namespace Ds2.LightHouseService

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing

/// Phase S3 session endpoint (todo-lighthouse-kb-server.md §3.8 / §3.9 / §4.2 Phase S3).
///
/// 포함:
/// - POST /sessions  — `{ collectionIds: [...] }` → `{token, acceptedCollectionIds, unknownIds, unindexableIds}`
/// - DELETE /sessions/{token} — 명시 해제 (L2-1)
///
/// 본 endpoint 는 AuthMiddleware 통과 후 진입 — `X-User-Identity` 헤더 박제 의무 (audit log 의 user 기록).
[<RequireQualifiedAccess>]
module SessionEndpoints =

    /// `POST /sessions` 요청 body schema.
    [<NoComparison; NoEquality>]
    type CreateSessionRequest = {
        [<JsonPropertyName("collectionIds")>] CollectionIds: string array
    }

    // **C-15 (s6-r79)** — 5 helper 폐기 → `EndpointHelpers` SSOT 통과 alias. signature 정합 (PropertyNameCaseInsensitive=true
    // + WriteIndented=false + DefaultIgnoreCondition.Never 박제 일치). caller 변경 0.
    let private userIdentityOf = EndpointHelpers.userIdentityOf
    let private writeJson = EndpointHelpers.writeJson
    let private writeError = EndpointHelpers.writeError

    /// `POST /sessions` — collectionIds validate (Registry 부분집합) + ATTACH limit 가드 + token 발급.
    /// unknownIds / unindexableIds 응답 동봉 (§3.8 Q4 lazy sync).
    ///
    /// **s6-r66 D-S7-4**: T2/T3 모드 시 acl reject (Hidden) 한 id 는 unknownIds 에 합산 (acl reject ↔ 실제 미존재
    /// 구분 안 함, 정보 leak 차단). ReadOnly 는 검색 허용이라 visible 셋 포함. cfg + storageRoot 인자 추가 (filter
    /// SSOT 호출 의무).
    let private postSession
            (cfg: ServiceConfig)
            (storageRoot: string)
            (registry: ISessionRegistry)
            (ctx: HttpContext) : Task =
        task {
            let user = userIdentityOf ctx
            // 요청 body 가 application/json 가정. content-type 누락 시에도 stream parse 시도.
            let! payload =
                task {
                    try
                        let! req = JsonSerializer.DeserializeAsync<CreateSessionRequest>(ctx.Request.Body, EndpointHelpers.DefaultJsonOpts)
                        return Ok req
                    with
                    | :? JsonException as ex -> return Error ex.Message
                }
            match payload with
            | Error msg ->
                do! writeError ctx 400 (sprintf "request body JSON 파싱 실패: %s" msg)
            | Ok req when isNull (box req) ->
                do! writeError ctx 400 "request body 비어있음"
            | Ok req ->
                let ids = if isNull req.CollectionIds then [||] else req.CollectionIds
                // **s6-r66 D-S7-4** — multi-tenant pre-filter. Hidden id 는 visible 셋 제외 + unknown 합산.
                // T1 모드 시 본 helper 가 entries 전체 Allow 박제라 ids 전부 통과 (회귀 0).
                let snapshot = Registry.listSnapshot storageRoot
                let byId = snapshot |> Array.map (fun e -> e.Id, e) |> Map.ofArray
                let visibleIds, aclHidden =
                    ids
                    |> Array.partition (fun id ->
                        match Map.tryFind id byId with
                        | None -> true  // 실제 미존재 — resolver 가 unknown 으로 분류 (그대로 통과)
                        | Some e ->
                            match MultiTenantPolicy.evaluate cfg.MultiTenant.Mode user e with
                            | MultiTenantPolicy.AccessDecision.Hidden -> false
                            | _ -> true)
                if aclHidden.Length > 0 then
                    Log.audit.Warn(
                        sprintf "POST /sessions: acl hidden %d ids — user=%s mode=%s"
                            aclHidden.Length user cfg.MultiTenant.Mode)
                try
                    let result = registry.CreateSession(visibleIds, user)
                    // acl hidden 도 unknown 으로 client 에게 응답 (구분 안 함).
                    let unknownMerged = Array.append result.UnknownIds aclHidden
                    do! writeJson ctx 201 {|
                        token = result.Token
                        acceptedCollectionIds = result.AcceptedCollectionIds
                        unknownIds = unknownMerged
                        unindexableIds = result.UnindexableIds
                    |}
                with
                | :? InvalidOperationException as ex ->
                    // ATTACH limit 초과 등 — 400 client 결함.
                    Log.audit.Warn(sprintf "POST /sessions reject — user=%s reason=%s" user ex.Message)
                    do! writeError ctx 400 ex.Message
        } :> Task

    /// `DELETE /sessions/{token}` — 명시 해제 (chat panel close / process exit).
    let private deleteSession (registry: ISessionRegistry) (token: string) (ctx: HttpContext) : Task =
        task {
            if registry.Delete token then
                ctx.Response.StatusCode <- 204
            else
                do! writeError ctx 404 (sprintf "session 미존재 — token=%s" token)
        } :> Task

    /// 인증 통과 endpoint 등록. AuthMiddleware 뒤에 매핑.
    /// **s6-r66 D-S7-4** — cfg + storageRoot 인자 추가 (multi-tenant filter).
    let map (app: IEndpointRouteBuilder) (cfg: ServiceConfig) (registry: ISessionRegistry) =
        let storageRoot = Config.expandEnv cfg.StorageRoot
        app.MapPost("/sessions", RequestDelegate(postSession cfg storageRoot registry)) |> ignore
        app.MapDelete("/sessions/{token}", RequestDelegate(fun ctx ->
            let token = ctx.Request.RouteValues.["token"] |> string
            deleteSession registry token ctx)) |> ignore
