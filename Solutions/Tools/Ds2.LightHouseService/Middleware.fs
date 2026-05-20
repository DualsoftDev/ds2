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

    /// **s6-r70 review C-3** — cert subject CN 추출. `CN=name, OU=...` 형식의 첫 RDN 만 파싱.
    /// distinguished name 안에 `,` 가 escape 되어 있으면 (`\,`) 그대로 보존. 단순 split — 본 사용 path 는
    /// "CN=username" prefix 만 검증.
    let private extractCommonName (subject: string) : string =
        if String.IsNullOrEmpty subject then ""
        else
            // RDN 들이 `,` 로 구분. CN= 시작 RDN 식별.
            let rdns = subject.Split(',') |> Array.map (fun s -> s.Trim())
            match rdns |> Array.tryFind (fun s -> s.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) with
            | Some rdn -> rdn.Substring(3).Trim()
            | None -> ""

    /// **s6-r70 review C-3** — mTLS subject CN ↔ X-User-Identity 강제 검증. mtls.mode != "off" 시점 진입.
    /// mismatch 시 false (caller 401). subject CN 빈 값 또는 cert 미박제 = false.
    /// case-insensitive ordinal 비교 (Windows username 정합).
    let private verifyMtlsIdentity (ctx: HttpContext) (claimedUser: string) : bool =
        let cert = ctx.Connection.ClientCertificate
        if isNull cert then false
        else
            let cn = extractCommonName cert.Subject
            if String.IsNullOrEmpty cn then false
            else String.Equals(cn, claimedUser, StringComparison.OrdinalIgnoreCase)

    /// middleware 본체 — DI 컨테이너 등록 시 `app.Use(authMiddleware cfg expectedPsk)` 형태로 wire.
    ///
    /// ASP.NET Core 9 표준 시그니처 `Func<HttpContext, Func<Task>, Task>` (review C1).
    /// 401 응답 시 body 미반환 (정보 leak 방지). audit log 만 박제.
    /// `/healthz` 등 public path 는 인증 skip + next 직접 호출.
    ///
    /// **s6-r70 review C-3**: cfg 인자 추가. mtls.mode != "off" 시 client cert subject CN ↔ X-User-Identity
    /// 강제 검증 — T2/T3 격리 정책의 fundamental 약점 (X-User-Identity 위변조) 해소.
    let middleware (cfg: ServiceConfig) (expectedPsk: string) : Func<HttpContext, Func<Task>, Task> =
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
                        Log.audit.Warn(sprintf "auth: Bearer 헤더 누락 — path=%s remoteIp=%s" (Log.sanitizeForLog path) (Log.sanitizeForLog remoteIp))
                        ctx.Response.StatusCode <- 401
                        do! ctx.Response.CompleteAsync()
                    | Some psk when not (compareBearerSecret expectedPsk psk) ->
                        Log.audit.Warn(sprintf "auth: PSK 불일치 — path=%s remoteIp=%s" (Log.sanitizeForLog path) (Log.sanitizeForLog remoteIp))
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
                            Log.audit.Warn(sprintf "auth: X-User-Identity 누락/다중 — path=%s remoteIp=%s" (Log.sanitizeForLog path) (Log.sanitizeForLog remoteIp))
                            ctx.Response.StatusCode <- 401
                            do! ctx.Response.CompleteAsync()
                        // **s6-r70 review C-3** — mTLS 활성 시 cert subject CN ↔ X-User-Identity 강제. mismatch=401.
                        elif cfg.Mtls.Mode <> MtlsMode.Off && not (verifyMtlsIdentity ctx userIdentity) then
                            let cnRaw =
                                let cert = ctx.Connection.ClientCertificate
                                if isNull cert then "(no-cert)" else extractCommonName cert.Subject
                            Log.audit.Warn(
                                sprintf "auth: mTLS subject CN ↔ X-User-Identity mismatch — path=%s claimed=%s cn=%s remoteIp=%s"
                                    (Log.sanitizeForLog path) (Log.sanitizeForLog userIdentity)
                                    (Log.sanitizeForLog cnRaw) (Log.sanitizeForLog remoteIp))
                            ctx.Response.StatusCode <- 401
                            do! ctx.Response.CompleteAsync()
                        else
                            ctx.Items.[UserIdentityItemKey] <- box userIdentity
                            do! next.Invoke()
            } :> Task)


/// Phase S3 session 검증 미들웨어 (todo-lighthouse-kb-server.md §3.8 / CR6 L3).
///
/// MCP HTTP transport (`/mcp` prefix) 호출에만 적용 — `app.UseWhen(path startsWith /mcp, ...)`.
/// session 관리 endpoint (POST/DELETE /sessions) 는 본 미들웨어 통과 안 함 (token 발급 자체가 본 endpoint).
///
/// 동작:
/// 1. `X-LightHouse-Session` 헤더 추출 → SessionRegistry.TryGet
/// 2. 누락 / NotFound → 401 + audit warn. **client (LightHouseClient) 가 401 받으면 자동 재발급 + 1회 retry (L3)**.
/// 3. Active → `SessionState` 를 `HttpContext.Items` 에 박제 (AttachmentTools 가 lookup 회피)
///
/// 401 응답은 body 없이 — client 의 L3 자동 회복은 status code 만 보면 됨.
[<RequireQualifiedAccess>]
module SessionAuth =

    /// `HttpContext.Items` 의 SessionState key — AttachmentTools 가 본 key 로 active session 얻음.
    [<Literal>]
    let SessionItemKey = "Ds2.LightHouseService.Session"

    /// 미들웨어 본체. caller (`Program.fs`) 가 `app.UseWhen` 으로 MCP path prefix 에만 적용.
    let middleware (registry: ISessionRegistry) : Func<HttpContext, Func<Task>, Task> =
        Func<HttpContext, Func<Task>, Task>(fun (ctx: HttpContext) (next: Func<Task>) ->
            task {
                let token =
                    match ctx.Request.Headers.TryGetValue AuthMiddleware.SessionHeader with
                    | true, v when v.Count = 1 ->
                        let s = v.[0]
                        if String.IsNullOrWhiteSpace s then "" else s.Trim()
                    | _ -> ""
                let path = ctx.Request.Path.Value
                let remoteIp = ctx.Connection.RemoteIpAddress |> string

                if String.IsNullOrEmpty token then
                    Log.audit.Warn(sprintf "session-auth: X-LightHouse-Session 헤더 누락 — path=%s remoteIp=%s"
                        (Log.sanitizeForLog path) (Log.sanitizeForLog remoteIp))
                    ctx.Response.StatusCode <- 401
                    do! ctx.Response.CompleteAsync()
                else
                    match registry.TryGet token with
                    | SessionLookup.NotFound ->
                        // review IM-11: token fingerprint 만 박제 (평문 박제 시 log leak 위험).
                        Log.audit.Warn(sprintf "session-auth: token unknown — path=%s remoteIp=%s token=%s"
                            (Log.sanitizeForLog path) (Log.sanitizeForLog remoteIp) (Log.tokenFingerprint token))
                        ctx.Response.StatusCode <- 401
                        do! ctx.Response.CompleteAsync()
                    | SessionLookup.Active state ->
                        ctx.Items.[SessionItemKey] <- box state
                        do! next.Invoke()
            } :> Task)
