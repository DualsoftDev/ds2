module Ds2.LightHouseService.Tests.EndpointHelpersTests

open System
open Microsoft.AspNetCore.Http
open Xunit
open Ds2.LightHouseService

/// **C-15 + B-S7-4 + B PR M-3 (s6-r79~r80)** — EndpointHelpers / AdminEndpoints 의 SSOT helper 단위 fact.
///
/// scope: userIdentityOf invariant 박제 / requireAdmin 의 backward-compat / case-insensitive 비교 /
/// normalizeAclUsers 의 whitespace trim + empty filter + 중복 제거.

let private newCtx (userIdentity: string option) : DefaultHttpContext =
    let ctx = DefaultHttpContext()
    ctx.Response.Body <- new IO.MemoryStream()
    match userIdentity with
    | Some s -> ctx.Items.[AuthMiddleware.UserIdentityItemKey] <- box s
    | None -> ()
    ctx

// **N-M5 (s6-r90)** — ServiceConfigBuilder.defaultConfig + with AdminUsers 박제.
let private cfgWithAdmins (admins: string array) : ServiceConfig =
    { ServiceConfigBuilder.defaultConfig "https://127.0.0.1:0" "" with AdminUsers = admins }

// ─── userIdentityOf — invariant Warn 박제 + 정상 path ──────────────

[<Fact>]
let ``userIdentityOf — Items 에 박제된 user 반환`` () =
    let ctx = newCtx (Some "kwak@dualsoft.com")
    Assert.Equal("kwak@dualsoft.com", EndpointHelpers.userIdentityOf ctx)

[<Fact>]
let ``userIdentityOf — Items 미박제 시 "unknown" 반환 (invariant Warn 박제)`` () =
    let ctx = newCtx None
    Assert.Equal("unknown", EndpointHelpers.userIdentityOf ctx)

// ─── B-S7-4 requireAdmin — adminUsers null/빈/박제 분기 ──────────────

[<Fact>]
let ``requireAdmin — AdminUsers null = backward-compat (single trust pool, 모든 user 통과)`` () =
    let cfg = cfgWithAdmins null
    let ctx = newCtx (Some "anyone@x.com")
    Assert.True(EndpointHelpers.requireAdmin cfg ctx)

[<Fact>]
let ``requireAdmin — AdminUsers 빈 array = backward-compat (모든 user 통과)`` () =
    let cfg = cfgWithAdmins Array.empty
    let ctx = newCtx (Some "anyone@x.com")
    Assert.True(EndpointHelpers.requireAdmin cfg ctx)

[<Fact>]
let ``requireAdmin — AdminUsers 박제 + caller 일치 (case-insensitive ordinal)`` () =
    let cfg = cfgWithAdmins [| "kwak@dualsoft.com"; "admin@x.com" |]
    let ctx1 = newCtx (Some "kwak@dualsoft.com")
    let ctx2 = newCtx (Some "KWAK@DUALSOFT.COM")  // case-insensitive
    Assert.True(EndpointHelpers.requireAdmin cfg ctx1)
    Assert.True(EndpointHelpers.requireAdmin cfg ctx2)

[<Fact>]
let ``requireAdmin — AdminUsers 박제 + caller 불일치 거부`` () =
    let cfg = cfgWithAdmins [| "kwak@dualsoft.com" |]
    let ctx = newCtx (Some "attacker@evil.com")
    Assert.False(EndpointHelpers.requireAdmin cfg ctx)

[<Fact>]
let ``requireAdmin — AdminUsers 박제 + caller invariant 위반 ("unknown") 거부`` () =
    let cfg = cfgWithAdmins [| "kwak@dualsoft.com" |]
    let ctx = newCtx None  // Items 미박제 → userIdentityOf="unknown"
    Assert.False(EndpointHelpers.requireAdmin cfg ctx)

[<Fact>]
let ``requireAdmin — AdminUsers 안 whitespace trim 후 비교`` () =
    let cfg = cfgWithAdmins [| "  kwak@dualsoft.com  " |]   // 외부 공백
    let ctx = newCtx (Some "kwak@dualsoft.com")
    Assert.True(EndpointHelpers.requireAdmin cfg ctx)

// ─── B PR M-3 normalizeAclUsers — whitespace trim + empty filter + 중복 제거 ──────────────

[<Fact>]
let ``normalizeAclUsers — null safe 빈 array`` () =
    Assert.Equal<string array>(Array.empty, AdminEndpoints.normalizeAclUsers null)

[<Fact>]
let ``normalizeAclUsers — whitespace trim + empty filter`` () =
    let raw = [| "kwak@x.com"; "  alice@x.com  "; ""; "   "; null |]
    let result = AdminEndpoints.normalizeAclUsers raw
    Assert.Equal<string array>([| "kwak@x.com"; "alice@x.com" |], result)

[<Fact>]
let ``normalizeAclUsers — 중복 제거 (case-insensitive 첫 박제 보존)`` () =
    let raw = [| "Kwak@X.com"; "kwak@x.com"; "KWAK@X.COM"; "bob@y.com" |]
    let result = AdminEndpoints.normalizeAclUsers raw
    // 첫 박제 보존 — case 유지.
    Assert.Equal<string array>([| "Kwak@X.com"; "bob@y.com" |], result)

[<Fact>]
let ``normalizeAclUsers — trim 후 중복 제거 통합`` () =
    let raw = [| "  alice@x.com  "; "alice@x.com"; "Alice@X.com" |]
    let result = AdminEndpoints.normalizeAclUsers raw
    Assert.Single(result) |> ignore
    Assert.Equal("alice@x.com", result.[0])
