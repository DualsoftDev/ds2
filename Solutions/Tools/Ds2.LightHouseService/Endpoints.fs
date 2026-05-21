namespace Ds2.LightHouseService

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing

/// 인증 무관 endpoint (health probe). middleware 진입 전에 매핑.
///
/// Phase S2 진입 후 `GET /collections` 는 CollectionEndpoints.map 으로 이동 — 본 모듈은 public health 만 책임.
/// review IM-3 (3/7 reviewer): 이전 `getCollectionsStub` (Phase S1 의 빈 stub) 제거 — CollectionEndpoints 가 SSOT.
[<RequireQualifiedAccess>]
module Endpoints =

    /// `GET /healthz` — Kestrel 가동 확인용. 인증 무관 (middleware 진입 전에 매핑).
    let getHealth (ctx: HttpContext) : System.Threading.Tasks.Task =
        task {
            ctx.Response.StatusCode <- 200
            ctx.Response.ContentType <- "text/plain; charset=utf-8"
            do! ctx.Response.WriteAsync "ok"
        } :> _

    /// 인증 무관 endpoint 등록 — health probe 등.
    let mapPublic (app: IEndpointRouteBuilder) =
        app.MapGet("/healthz", RequestDelegate(getHealth)) |> ignore
