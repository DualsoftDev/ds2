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

    /// review IM-2 (3/7 reviewer): hot-path module-level singleton.
    let private jsonResponseOpts =
        JsonSerializerOptions(
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never)
    let private jsonOptions () = jsonResponseOpts

    let private userIdentityOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue AuthMiddleware.UserIdentityItemKey with
        | true, v when not (isNull v) -> string v
        | _ -> "unknown"

    let private writeJson (ctx: HttpContext) (status: int) (body: obj) : Task =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        let json = JsonSerializer.Serialize(body, jsonOptions())
        ctx.Response.WriteAsync json

    let private writeError (ctx: HttpContext) (status: int) (message: string) : Task =
        writeJson ctx status {| error = message |}

    /// `POST /sessions` — collectionIds validate (Registry 부분집합) + ATTACH limit 가드 + token 발급.
    /// unknownIds / unindexableIds 응답 동봉 (§3.8 Q4 lazy sync).
    let private postSession (registry: ISessionRegistry) (ctx: HttpContext) : Task =
        task {
            let user = userIdentityOf ctx
            // 요청 body 가 application/json 가정. content-type 누락 시에도 stream parse 시도.
            let! payload =
                task {
                    try
                        let! req = JsonSerializer.DeserializeAsync<CreateSessionRequest>(ctx.Request.Body, jsonOptions())
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
                try
                    let result = registry.CreateSession(ids, user)
                    do! writeJson ctx 201 {|
                        token = result.Token
                        acceptedCollectionIds = result.AcceptedCollectionIds
                        unknownIds = result.UnknownIds
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
    let map (app: IEndpointRouteBuilder) (registry: ISessionRegistry) =
        app.MapPost("/sessions", RequestDelegate(postSession registry)) |> ignore
        app.MapDelete("/sessions/{token}", RequestDelegate(fun ctx ->
            let token = ctx.Request.RouteValues.["token"] |> string
            deleteSession registry token ctx)) |> ignore
