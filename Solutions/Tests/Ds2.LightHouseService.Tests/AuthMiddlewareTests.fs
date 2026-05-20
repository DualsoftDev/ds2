module Ds2.LightHouseService.Tests.AuthMiddlewareTests

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Xunit
open Ds2.LightHouseService

/// Phase S1 DoD 핵심 — "PSK 인증 middleware 가 빈 GET /collections 200 응답".
/// C1 (시그니처 mismatch) + C3 (unit test 부재) 자가 검열 결과 본 파일 신설.
///
/// DefaultHttpContext + middleware 직접 invoke 패턴 — WebApplicationFactory 미사용 (Phase S5 영역).

let private newCtx (path: string) (headers: (string * string) list) : DefaultHttpContext =
    let ctx = DefaultHttpContext()
    ctx.Request.Path <- PathString path
    for (k, v) in headers do
        ctx.Request.Headers.[k] <- Microsoft.Extensions.Primitives.StringValues v
    ctx.Response.Body <- new IO.MemoryStream()
    ctx

/// **s6-r70 review C-3** — middleware 시그니처 cfg 추가. AuthMiddlewareTests 는 mtls.mode="off" (현행) 만 검증
/// (mTLS subject ↔ X-User-Identity 의 cfg 분기 fact 는 MtlsRoundTripTests IT 가 별도 박제).
let private testCfg : ServiceConfig =
    {
        SchemaVersion = ConfigSchema.Current
        ListenUrl = "https://127.0.0.1:0"
        TlsCertPath = ""
        TlsCertPasswordEncrypted = ""
        PreSharedKeyEncrypted = ""
        StorageRoot = ""
        MaxUploadBytes = 10737418240L
        ZipBombRatioLimit = 50
        SessionIdleTtlMinutes = 240
        StagingSweepIntervalMinutes = 10
        LogRetentionDays = 30
        LogMaxSizeMB = 100
        AuditRetentionDays = 365
        IndexerVersionRange = { Min = "1.0.0"; Max = "2.99.99" }
        Embedding = { Enabled = false; BaseUrl = ""; Model = ""; Dimension = 1024 }
        Mtls = { Mode = MtlsMode.Off; AllowedThumbprints = Array.empty }
        MultiTenant = { Mode = MultiTenantMode.T1 }
    }

let private runMiddleware (psk: string) (ctx: HttpContext) : Task<bool> =
    let nextCalled = ref false
    let next = Func<Task>(fun () ->
        nextCalled := true
        Task.CompletedTask)
    task {
        let mw = AuthMiddleware.middleware testCfg psk
        do! mw.Invoke(ctx, next)
        return !nextCalled
    }

[<Fact>]
let ``정상 Bearer + X-User-Identity → next 호출 + 200`` () = task {
    let ctx = newCtx "/collections" [
        "Authorization", "Bearer test-psk"
        "X-User-Identity", "alice@example.com"
    ]
    let! nextCalled = runMiddleware "test-psk" ctx
    Assert.True(nextCalled, "next 가 호출되어야 함")
    // ctx.Response.StatusCode 는 default 200 — middleware 가 별도 갱신 X
    Assert.Equal(200, ctx.Response.StatusCode)
    // userIdentity HttpContext.Items 박제
    Assert.Equal(box "alice@example.com", ctx.Items.[AuthMiddleware.UserIdentityItemKey])
}

[<Fact>]
let ``Authorization 헤더 없음 → 401 + next 미호출`` () = task {
    let ctx = newCtx "/collections" []
    let! nextCalled = runMiddleware "test-psk" ctx
    Assert.False(nextCalled)
    Assert.Equal(401, ctx.Response.StatusCode)
}

[<Fact>]
let ``PSK 불일치 → 401 + next 미호출`` () = task {
    let ctx = newCtx "/collections" [
        "Authorization", "Bearer wrong-psk"
        "X-User-Identity", "alice"
    ]
    let! nextCalled = runMiddleware "test-psk" ctx
    Assert.False(nextCalled)
    Assert.Equal(401, ctx.Response.StatusCode)
}

[<Fact>]
let ``Bearer scheme 케이스 비교 — bearer 소문자 통과 (RFC 7235 §2.1 case-insensitive)`` () = task {
    let ctx = newCtx "/collections" [
        "Authorization", "bearer test-psk"
        "X-User-Identity", "alice"
    ]
    let! nextCalled = runMiddleware "test-psk" ctx
    Assert.True(nextCalled)
}

[<Fact>]
let ``X-User-Identity 헤더 누락 → 401 + next 미호출 (CR4 M11)`` () = task {
    let ctx = newCtx "/collections" [
        "Authorization", "Bearer test-psk"
    ]
    let! nextCalled = runMiddleware "test-psk" ctx
    Assert.False(nextCalled)
    Assert.Equal(401, ctx.Response.StatusCode)
}

[<Fact>]
let ``X-User-Identity 헤더 공백만 → 401`` () = task {
    let ctx = newCtx "/collections" [
        "Authorization", "Bearer test-psk"
        "X-User-Identity", "   "
    ]
    let! nextCalled = runMiddleware "test-psk" ctx
    Assert.False(nextCalled)
    Assert.Equal(401, ctx.Response.StatusCode)
}

[<Fact>]
let ``Bearer 형식 위반 (prefix 없음) → 401`` () = task {
    let ctx = newCtx "/collections" [
        "Authorization", "test-psk"
        "X-User-Identity", "alice"
    ]
    let! nextCalled = runMiddleware "test-psk" ctx
    Assert.False(nextCalled)
}

[<Fact>]
let ``X-User-Identity 다중 값 거부 → 401 (review m2)`` () = task {
    let ctx = DefaultHttpContext()
    ctx.Request.Path <- PathString "/collections"
    ctx.Request.Headers.["Authorization"] <- Microsoft.Extensions.Primitives.StringValues "Bearer test-psk"
    ctx.Request.Headers.["X-User-Identity"] <-
        Microsoft.Extensions.Primitives.StringValues [| "kwak"; "attacker@evil.com" |]
    ctx.Response.Body <- new IO.MemoryStream()
    let! nextCalled = runMiddleware "test-psk" ctx
    Assert.False(nextCalled, "다중 X-User-Identity 거부")
    Assert.Equal(401, ctx.Response.StatusCode)
}

[<Fact>]
let ``public path /healthz → 인증 skip + next 호출 (review M8)`` () = task {
    let ctx = newCtx "/healthz" []
    let! nextCalled = runMiddleware "test-psk" ctx
    Assert.True(nextCalled, "/healthz 는 auth skip 후 next 호출")
}

[<Fact>]
let ``public path /healthz — Bearer 있어도 그대로 통과 (skip 우선)`` () = task {
    let ctx = newCtx "/healthz" [ "Authorization", "Bearer wrong" ]
    let! nextCalled = runMiddleware "test-psk" ctx
    Assert.True(nextCalled)
}
