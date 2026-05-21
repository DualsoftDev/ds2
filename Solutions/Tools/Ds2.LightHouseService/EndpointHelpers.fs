namespace Ds2.LightHouseService

open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

/// **C-15 (s6-r79, K4 잔여 SSOT 추출)** — endpoint helper SSOT.
///
/// 이전 박제 (s6-r78 까지): 6 endpoint (AdminEndpoints / CollectionEndpoints / FileServing / SessionEndpoints /
/// EventsEndpoint / UploadsEndpoint) 가 각자 `userIdentityOf` / `writeJson` / `writeError` / `jsonOpts` 박제 —
/// drift (ContentType / WriteIndented / DefaultIgnoreCondition / Warn 박제 유무) 가 누적되어 cross-endpoint
/// 응답 일관성 저하 + 보수성 저하.
///
/// 본 module = 단일 SSOT. caller 가 `EndpointHelpers.writeJson` / `writeError` / `userIdentityOf` 직접 호출.
/// JSON 직렬화 options 가 caller 별 다양성 필요 시 `writeJsonOpts` overload + custom options.
[<RequireQualifiedAccess>]
module EndpointHelpers =

    /// 기본 JSON 직렬화 options — 6 endpoint 응답 공통 박제.
    /// caller 별 다양성 박제 (예: UploadsEndpoint 의 meta.json WriteIndented=true) 는 별 instance 유지.
    let DefaultJsonOpts : JsonSerializerOptions =
        JsonSerializerOptions(
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never)

    /// HttpContext.Items 의 X-User-Identity (AuthMiddleware 가 박제).
    /// **review S4-m3 / FileServing 정합**: "unknown" fallback 도달 자체가 invariant 위반 — AuthMiddleware 가
    /// 통과시켰다면 `UserIdentityItemKey` 박제 의무. 도달 시 `Log.audit.Warn` 박제 (storm 방지: caller 가 단일
    /// endpoint 안에서 1회만 호출, 매 endpoint 통상 1 call).
    let userIdentityOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue AuthMiddleware.UserIdentityItemKey with
        | true, v when not (isNull v) -> string v
        | _ ->
            Log.audit.Warn "X-User-Identity invariant 위반 — AuthMiddleware 통과 후 UserIdentityItemKey 부재"
            "unknown"

    /// `application/json; charset=utf-8` + status + Serialize(body, DefaultJsonOpts) + WriteAsync.
    /// 6 endpoint 응답의 단일 SSOT.
    let writeJson (ctx: HttpContext) (status: int) (body: obj) : Task =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        let json = JsonSerializer.Serialize(body, DefaultJsonOpts)
        ctx.Response.WriteAsync json

    /// caller-supplied options 박제 — 별 박제 필요 시 (예: file write 의 WriteIndented=true 등).
    let writeJsonOpts (ctx: HttpContext) (status: int) (body: obj) (opts: JsonSerializerOptions) : Task =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        let json = JsonSerializer.Serialize(body, opts)
        ctx.Response.WriteAsync json

    /// `{ "error": "..." }` 응답 SSOT. writeJson 통과.
    let writeError (ctx: HttpContext) (status: int) (message: string) : Task =
        writeJson ctx status {| error = message |}

    /// **B-S7-4 (s6-r80)** — admin-only ACL 검증.
    /// `cfg.AdminUsers` 가 null / 빈 array 면 backward-compat (single trust pool, 모든 user = admin, 기존 동작).
    /// 비어 있지 않으면 X-User-Identity (case-insensitive ordinal 비교, Windows username 정합) 박제 여부 검사.
    /// 통과 시 true, 거부 시 false (caller 가 호출 후 403 응답 의무).
    let requireAdmin (cfg: ServiceConfig) (ctx: HttpContext) : bool =
        let admins =
            if isNull cfg.AdminUsers then Array.empty
            else cfg.AdminUsers
        if admins.Length = 0 then true   // backward-compat — single trust pool
        else
            let user = userIdentityOf ctx
            admins
            |> Array.exists (fun a ->
                not (System.String.IsNullOrWhiteSpace a)
                && System.String.Equals(a.Trim(), user, System.StringComparison.OrdinalIgnoreCase))
