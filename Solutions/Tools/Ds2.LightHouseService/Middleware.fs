namespace Ds2.LightHouseService

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

/// 인증 미들웨어 SSOT (todo-lighthouse-kb-server.md §3.7).
///
/// 1차: `Authorization: Bearer <PSK>` fixed-time compare (timing attack 방어)
/// 2차: `X-User-Identity: <user>` 헤더 의무 (CR4 / M11 — audit 추적용)
///
/// /healthz 등 health endpoint 는 본 middleware 통과 안 함 — Program.fs 에서 매핑 순서로 회피.
[<RequireQualifiedAccess>]
module AuthMiddleware =

    [<Literal>]
    let AuthorizationHeader = "Authorization"

    [<Literal>]
    let UserIdentityHeader = "X-User-Identity"

    [<Literal>]
    let SessionHeader = "X-LightHouse-Session"

    /// HttpContext.Items 의 사용자 식별 key — endpoint 가 audit log 에 박을 때 사용.
    [<Literal>]
    let UserIdentityItemKey = "Ds2.LightHouseService.UserIdentity"

    /// fixed-time byte 비교 — `CryptographicOperations.FixedTimeEquals` 의 BCL contract:
    ///   "The lengths of left and right are compared in constant time. If the lengths are
    ///    different, the method returns false without examining any data."
    /// 즉 길이 mismatch 도 const-time false. buffer normalize 불요 (review C2).
    let private compareBearerSecret (expected: string) (provided: string) : bool =
        let eBytes = Encoding.UTF8.GetBytes expected
        let pBytes = Encoding.UTF8.GetBytes provided
        CryptographicOperations.FixedTimeEquals(ReadOnlySpan(eBytes), ReadOnlySpan(pBytes))

    /// `Authorization` 헤더 → Bearer scheme + PSK 추출. format 위반 None.
    let private extractBearer (header: string) : string option =
        if String.IsNullOrWhiteSpace header then None
        else
            let prefix = "Bearer "
            if header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                let token = header.Substring(prefix.Length).Trim()
                if String.IsNullOrEmpty token then None else Some token
            else None

    /// path 가 public skip 대상인지 — `/healthz` 등 인증 무관 endpoint (review M8).
    let private isPublicPath (path: PathString) : bool =
        path.StartsWithSegments(PathString "/healthz")

    /// middleware 본체 — DI 컨테이너 등록 시 `app.Use(authMiddleware expectedPsk)` 형태로 wire.
    ///
    /// ASP.NET Core 9 표준 시그니처 `Func<HttpContext, Func<Task>, Task>` (review C1).
    /// 401 응답 시 body 미반환 (정보 leak 방지). audit log 만 박제.
    /// `/healthz` 등 public path 는 인증 skip + next 직접 호출.
    let middleware (expectedPsk: string) : Func<HttpContext, Func<Task>, Task> =
        Func<HttpContext, Func<Task>, Task>(fun (ctx: HttpContext) (next: Func<Task>) ->
            task {
                if isPublicPath ctx.Request.Path then
                    do! next.Invoke()
                else
                    let authHeader =
                        match ctx.Request.Headers.TryGetValue AuthorizationHeader with
                        | true, v -> v.ToString()
                        | _ -> ""
                    let remoteIp = ctx.Connection.RemoteIpAddress |> string
                    let path = ctx.Request.Path.Value

                    match extractBearer authHeader with
                    | None ->
                        Log.audit.Warn(sprintf "auth: Bearer 헤더 누락 — path=%s remoteIp=%s" path remoteIp)
                        ctx.Response.StatusCode <- 401
                        do! ctx.Response.CompleteAsync()
                    | Some psk when not (compareBearerSecret expectedPsk psk) ->
                        Log.audit.Warn(sprintf "auth: PSK 불일치 — path=%s remoteIp=%s" path remoteIp)
                        ctx.Response.StatusCode <- 401
                        do! ctx.Response.CompleteAsync()
                    | Some _ ->
                        // 2차 — X-User-Identity 헤더 의무 (CR4 / M11).
                        // multi-value 거부 (review m2) — `kwak, attacker@evil.com` 형태로 두 값 보내면 401.
                        // StringValues.ToString() 이 `,` join 하므로 .Count 로 명시 단일 검증.
                        let userIdentity =
                            match ctx.Request.Headers.TryGetValue UserIdentityHeader with
                            | true, v when v.Count = 1 ->
                                let s = v.[0]
                                if String.IsNullOrWhiteSpace s then "" else s.Trim()
                            | _ -> ""
                        if String.IsNullOrEmpty userIdentity then
                            Log.audit.Warn(sprintf "auth: X-User-Identity 누락/다중 — path=%s remoteIp=%s" path remoteIp)
                            ctx.Response.StatusCode <- 401
                            do! ctx.Response.CompleteAsync()
                        else
                            ctx.Items.[UserIdentityItemKey] <- box userIdentity
                            do! next.Invoke()
            } :> Task)
