namespace Ds2.LightHouseService

open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing

/// Phase S1 의 최소 endpoint — `GET /collections` 가 빈 registry (=`{"collections":[]}`) 반환.
///
/// Phase S2 진입 시 본 모듈을 collection 관리 API 전체 (POST/GET/DELETE) 로 확장.
/// 본 endpoint 는 AuthMiddleware 통과 후 진입 — 인증 안 된 호출은 401 으로 차단됨.
[<RequireQualifiedAccess>]
module Endpoints =

    /// Phase S2 진입 전 stub. registry.json 미존재 라 빈 결과 반환.
    /// `application/json; charset=utf-8` 명시 — codex CLI 등 strict client 대비.
    let getCollectionsStub (ctx: HttpContext) : System.Threading.Tasks.Task =
        task {
            let payload =
                JsonSerializer.Serialize(
                    {| schemaVersion = 1; collections = ([||]: obj array) |},
                    JsonSerializerOptions(WriteIndented = false))
            ctx.Response.StatusCode <- 200
            ctx.Response.ContentType <- "application/json; charset=utf-8"
            do! ctx.Response.WriteAsync payload
        } :> _

    /// `GET /healthz` — Kestrel 가동 확인용. 인증 무관 (middleware 진입 전에 매핑).
    let getHealth (ctx: HttpContext) : System.Threading.Tasks.Task =
        task {
            ctx.Response.StatusCode <- 200
            ctx.Response.ContentType <- "text/plain; charset=utf-8"
            do! ctx.Response.WriteAsync "ok"
        } :> _

    /// 인증 무관 endpoint 등록 — health probe 등. middleware 진입 전에 매핑.
    /// Phase S2 진입 후 `GET /collections` 는 CollectionEndpoints.map 으로 이동 — 본 모듈은 public health 만 책임.
    let mapPublic (app: IEndpointRouteBuilder) =
        app.MapGet("/healthz", RequestDelegate(getHealth)) |> ignore
