namespace Ds2.LightHouseService

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Ds2.LightHouse.Protocol

/// **Phase S7 D-S7-4 admin endpoint (B-S7-4, s6-r71+)** — multi-tenant T2/T3 실 활용 path 의 admin 측 API.
///
/// 본 module 의 endpoint 2 종 (todo-lighthouse-kb-server.md §3.6 multi-tenant 정합):
/// - `POST /admin/collections/{id}/owner` — body `{ "user": "kwak@dualsoft.com" }` → `CollectionEntry.ImportedBy` 변경
///   (T2 mode owner 이전 path — 본 endpoint 호출 후 `MultiTenantPolicy.evaluate` 의 ImportedBy filter 결과가 즉시 갱신).
/// - `PUT /admin/collections/{id}/acl` — body `{ "users": ["u1","u2"], "readOnly": false }` → `CollectionEntry.Acl` 갱신
///   (T3 mode acl 편집 path — null/빈 users = 전체 공개 default 정합).
///
/// **권한 모델 (본 phase scope)**: AuthMiddleware 의 PSK + `X-User-Identity` 통과한 모든 user 가 호출 가능 — 즉 본
/// service 의 single trust pool (PSK 보유자 = admin 권한). 별도 admin-only ACL 분리는 다음 phase 박제 의무
/// (config 의 `adminUsers: string[]` SSOT + `requireAdmin` helper, M9/K6 보안 sweep 후속).
///
/// **audit log 박제 의무** — 모든 admin 변경은 `Log.audit.Info` 박제 (보안 추적 + retention 365 일).
/// caller identity = `AuthMiddleware.UserIdentityItemKey` 의 `ctx.Items` 값.
[<RequireQualifiedAccess>]
module AdminEndpoints =

    // **CLIMutable + 비-private** — System.Text.Json reflection 이 mutable property 인식 의무
    // (cli `Packager.MetaDto` 동일 패턴, private type 은 System.Text.Json 가 모든 field default 로 직렬화 회귀).
    [<CLIMutable; NoComparison; NoEquality>]
    type OwnerBody = {
        [<JsonPropertyName("user")>] User: string
    }

    [<CLIMutable; NoComparison; NoEquality>]
    type AclBody = {
        [<JsonPropertyName("users")>] Users: string array
        [<JsonPropertyName("readOnly")>] ReadOnly: bool
    }

    /// module-level singleton — System.Text.Json 의 `JsonSerializerOptions` 는 frozen 후 thread-safe + reflection
    /// cache instance-bound. 매 호출 재생성 시 cache miss 누적 (자가 검열 M-1 정합).
    let private jsonOpts = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    let private writeJson (resp: HttpResponse) (status: int) (payload: obj) : Task = task {
        resp.StatusCode <- status
        resp.ContentType <- MimeTypes.Json
        // 자가 검열 M-2: options 명시 — 응답 일관성 (CollectionEndpoints / UploadsEndpoint 의 PropertyNamingPolicy 정합).
        let json = JsonSerializer.Serialize(payload, jsonOpts)
        do! resp.WriteAsync(json)
    }

    let private writeErr (resp: HttpResponse) (status: int) (msg: string) : Task =
        writeJson resp status (box {| error = msg |})

    let private callerIdentity (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue(AuthMiddleware.UserIdentityItemKey) with
        | true, (:? string as s) when not (String.IsNullOrEmpty s) -> s
        | _ -> "unknown"

    let private readBody (ctx: HttpContext) : Task<string> = task {
        use reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8)
        return! reader.ReadToEndAsync()
    }

    let map (app: IEndpointRouteBuilder) (storageRoot: string) : unit =
        // POST /admin/collections/{id}/owner — body { "user": "..." } → ImportedBy stamp 갱신.
        app.MapPost("/admin/collections/{id}/owner",
            Func<HttpContext, string, Task>(fun ctx id ->
                task {
                    let! body = readBody ctx
                    let parsed =
                        try
                            let p = JsonSerializer.Deserialize<OwnerBody>(body, jsonOpts)
                            if obj.ReferenceEquals(p, null) then None else Some p
                        with _ -> None
                    match parsed with
                    | None ->
                        do! writeErr ctx.Response 400 "body 파싱 실패 — JSON { \"user\": \"...\" } 형식 필요"
                    | Some p when String.IsNullOrWhiteSpace p.User ->
                        do! writeErr ctx.Response 400 "body.user 필수"
                    | Some p ->
                        match Registry.tryFindById storageRoot id with
                        | None ->
                            do! writeErr ctx.Response 404 (sprintf "collection not found — id=%s" id)
                        | Some entry ->
                            let newOwner = p.User.Trim()
                            let updated = { entry with ImportedBy = newOwner }
                            do! Registry.upsertAsync storageRoot updated
                            Log.audit.Info(
                                sprintf "admin.owner: id=%s prev=%s next=%s caller=%s"
                                    id entry.ImportedBy newOwner (callerIdentity ctx))
                            do! writeJson ctx.Response 200 (box {| id = id; importedBy = newOwner |})
                } :> Task)) |> ignore

        // PUT /admin/collections/{id}/acl — body { "users": [...], "readOnly": bool } → Acl 갱신.
        app.MapPut("/admin/collections/{id}/acl",
            Func<HttpContext, string, Task>(fun ctx id ->
                task {
                    let! body = readBody ctx
                    let parsed =
                        try
                            let p = JsonSerializer.Deserialize<AclBody>(body, jsonOpts)
                            if obj.ReferenceEquals(p, null) then None else Some p
                        with _ -> None
                    match parsed with
                    | None ->
                        do! writeErr ctx.Response 400 "body 파싱 실패 — JSON { \"users\": [...], \"readOnly\": bool } 형식 필요"
                    | Some p ->
                        match Registry.tryFindById storageRoot id with
                        | None ->
                            do! writeErr ctx.Response 404 (sprintf "collection not found — id=%s" id)
                        | Some entry ->
                            let users = if isNull p.Users then Array.empty else p.Users
                            let newAcl : CollectionAcl = { Users = users; ReadOnly = p.ReadOnly }
                            let updated = { entry with Acl = newAcl }
                            do! Registry.upsertAsync storageRoot updated
                            Log.audit.Info(
                                sprintf "admin.acl: id=%s users=%d readOnly=%b caller=%s"
                                    id users.Length p.ReadOnly (callerIdentity ctx))
                            do! writeJson ctx.Response 200 (box {| id = id; acl = newAcl |})
                } :> Task)) |> ignore
