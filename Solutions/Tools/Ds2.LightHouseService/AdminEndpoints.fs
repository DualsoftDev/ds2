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
/// 본 module 의 endpoint 2 종 (done-lighthouse-kb-server.md §3.6 multi-tenant 정합):
/// - `POST /admin/collections/{id}/owner` — body `{ "user": "kwak@dualsoft.com" }` → `CollectionEntry.ImportedBy` 변경
///   (T2 mode owner 이전 path — 본 endpoint 호출 후 `MultiTenantPolicy.evaluate` 의 ImportedBy filter 결과가 즉시 갱신).
/// - `PUT /admin/collections/{id}/acl` — body `{ "users": ["u1","u2"], "readOnly": false }` → `CollectionEntry.Acl` 갱신
///   (T3 mode acl 편집 path — null/빈 users = 전체 공개 default 정합).
///
/// **권한 모델 (s6-r80 B-S7-4 admin-only ACL 분리)**: AuthMiddleware 의 PSK + `X-User-Identity` 통과한 user 중
/// `config.adminUsers` list 안 user 만 호출 가능 (case-insensitive ordinal 비교). `adminUsers` null/빈 array
/// = backward-compat (single trust pool, 기존 동작 유지). `EndpointHelpers.requireAdmin` 통과 후 403 분기.
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

    // **C-15 (s6-r79) + B20 Maj-5 (s6-r84, 15-reviewer)** — `EndpointHelpers` SSOT 통과.
    // 다른 5 endpoint module 의 alias 패턴 정합 (K4 잔여 cosmetic).
    let private userIdentityOf = EndpointHelpers.userIdentityOf
    let private writeJson = EndpointHelpers.writeJson
    let private writeError = EndpointHelpers.writeError
    let private requireAdmin = EndpointHelpers.requireAdmin

    let private readBody (ctx: HttpContext) : Task<string> = task {
        use reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8)
        return! reader.ReadToEndAsync()
    }

    /// **B PR M-3 (s6-r80)** — acl users element 정규화. whitespace trim + empty filter + 중복 제거
    /// (case-insensitive ordinal, 첫 박제 보존). null/빈 array safe. defense-in-depth — client (admin)
    /// 의 raw input 결함 흡수 + Acl.Users SSOT 정합 (storage 안 일관 박제).
    /// public — unit fact 박제 의무 (`Ds2.LightHouseService.Tests.EndpointHelpersTests`).
    let normalizeAclUsers (raw: string array) : string array =
        if isNull raw then Array.empty
        else
            let seen = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            raw
            |> Array.choose (fun s ->
                if String.IsNullOrWhiteSpace s then None
                else Some (s.Trim()))
            |> Array.filter (fun s -> seen.Add s)

    // **B13 (s6-r89, 15-reviewer Major)** — atomic update path. handleOwner / handleAcl 가 Registry.updateByIdAsync
    // (lock 안 read-modify-write) 통과 — 동시 admin 호출 시 last-writer-wins lost update 차단.
    // **B14** — handleAcl 가 notifier.OnAclChanged 호출 → SessionRegistry 가 affected session KB 폐기 + acl 재검증.

    let private handleOwner (cfg: ServiceConfig) (storageRoot: string) (ctx: HttpContext) (id: string) : Task =
        task {
            if not (requireAdmin cfg ctx) then
                Log.audit.Warn(
                    sprintf "admin.owner: 권한 거부 — caller=%s id=%s"
                        (userIdentityOf ctx) id)
                do! writeError ctx 403 "admin 권한 필요"
            else
                let! body = readBody ctx
                let parsed =
                    try
                        let p = JsonSerializer.Deserialize<OwnerBody>(body, EndpointHelpers.DefaultJsonOpts)
                        if obj.ReferenceEquals(p, null) then None else Some p
                    with _ -> None
                match parsed with
                | None ->
                    do! writeError ctx 400 "body 파싱 실패 — JSON { \"user\": \"...\" } 형식 필요"
                | Some p when String.IsNullOrWhiteSpace p.User ->
                    do! writeError ctx 400 "body.user 필수"
                | Some p ->
                    let newOwner = p.User.Trim()
                    let mutable prevOwner = ""
                    // **B13** — atomic mutate (lock 안 read+modify+save).
                    let! result =
                        Registry.updateByIdAsync storageRoot id (fun entry ->
                            prevOwner <- entry.ImportedBy
                            { entry with ImportedBy = newOwner })
                    match result with
                    | None ->
                        do! writeError ctx 404 (sprintf "collection not found — id=%s" id)
                    | Some _ ->
                        Log.audit.Info(
                            sprintf "admin.owner: id=%s prev=%s next=%s caller=%s"
                                id prevOwner newOwner (userIdentityOf ctx))
                        do! writeJson ctx 200 (box {| id = id; importedBy = newOwner |})
        } :> Task

    let private handleAcl
        (cfg: ServiceConfig)
        (storageRoot: string)
        (notifier: ICollectionLifecycleNotifier)
        (ctx: HttpContext)
        (id: string) : Task =
        task {
            if not (requireAdmin cfg ctx) then
                Log.audit.Warn(
                    sprintf "admin.acl: 권한 거부 — caller=%s id=%s"
                        (userIdentityOf ctx) id)
                do! writeError ctx 403 "admin 권한 필요"
            else
                let! body = readBody ctx
                let parsed =
                    try
                        let p = JsonSerializer.Deserialize<AclBody>(body, EndpointHelpers.DefaultJsonOpts)
                        if obj.ReferenceEquals(p, null) then None else Some p
                    with _ -> None
                match parsed with
                | None ->
                    do! writeError ctx 400 "body 파싱 실패 — JSON { \"users\": [...], \"readOnly\": bool } 형식 필요"
                | Some p ->
                    // **B PR M-3 (s6-r80)** — users element 정규화 (whitespace trim + empty filter + dedup).
                    let users = normalizeAclUsers p.Users
                    let newAcl : CollectionAcl = { Users = users; ReadOnly = p.ReadOnly }
                    // **B13** — atomic update (lock 안 read-modify-write).
                    let! result =
                        Registry.updateByIdAsync storageRoot id (fun entry ->
                            { entry with Acl = newAcl })
                    match result with
                    | None ->
                        do! writeError ctx 404 (sprintf "collection not found — id=%s" id)
                    | Some _ ->
                        // **B14** — acl 변경 → SessionRegistry KB 폐기 + 재검증 trigger.
                        notifier.OnAclChanged id
                        Log.audit.Info(
                            sprintf "admin.acl: id=%s users=%d readOnly=%b caller=%s"
                                id users.Length p.ReadOnly (userIdentityOf ctx))
                        do! writeJson ctx 200 (box {| id = id; acl = newAcl |})
        } :> Task

    let map
        (cfg: ServiceConfig)
        (notifier: ICollectionLifecycleNotifier)
        (app: IEndpointRouteBuilder)
        (storageRoot: string) : unit =
        app.MapPost("/admin/collections/{id}/owner",
            Func<HttpContext, string, Task>(fun ctx id -> handleOwner cfg storageRoot ctx id)) |> ignore
        app.MapPut("/admin/collections/{id}/acl",
            Func<HttpContext, string, Task>(fun ctx id -> handleAcl cfg storageRoot notifier ctx id)) |> ignore
