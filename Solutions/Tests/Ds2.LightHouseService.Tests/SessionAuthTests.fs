module Ds2.LightHouseService.Tests.SessionAuthTests

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Xunit
open Ds2.LightHouseService

/// SessionAuth 미들웨어 — todo-lighthouse-kb-server.md §3.8 + CR6 L3 client 자동 회복의 server-side 신호 (401).
///
/// MCP HTTP transport (`/mcp` 하위) 에만 적용. session 발급 endpoint (POST/DELETE /sessions) 는 본 미들웨어 통과 안 함.

let private fakeResolver () : AttachmentResolver =
    {
        Resolve = fun _ ->
            { AcceptedIds = [||]; Paths = [||]; UnknownIds = [||]; UnindexableIds = [||] }
    }

let private mkRegistry () : ISessionRegistry =
    SessionRegistry(fakeResolver ()) :> ISessionRegistry

let private newCtx (path: string) (headers: (string * string) list) : DefaultHttpContext =
    let ctx = DefaultHttpContext()
    ctx.Request.Path <- PathString path
    for (k, v) in headers do
        ctx.Request.Headers.[k] <- Microsoft.Extensions.Primitives.StringValues v
    ctx.Response.Body <- new IO.MemoryStream()
    ctx

let private runMiddleware (registry: ISessionRegistry) (ctx: HttpContext) : Task<bool> =
    let nextCalled = ref false
    let next = Func<Task>(fun () ->
        nextCalled := true
        Task.CompletedTask)
    task {
        let mw = SessionAuth.middleware registry
        do! mw.Invoke(ctx, next)
        return !nextCalled
    }


[<Fact>]
let ``X-LightHouse-Session 헤더 누락 → 401 + next 미호출 (L3 client 회복 trigger)`` () = task {
    let reg = mkRegistry ()
    let ctx = newCtx "/mcp/sse" []
    let! nextCalled = runMiddleware reg ctx
    Assert.False(nextCalled)
    Assert.Equal(401, ctx.Response.StatusCode)
}


[<Fact>]
let ``X-LightHouse-Session 공백만 → 401`` () = task {
    let reg = mkRegistry ()
    let ctx = newCtx "/mcp/sse" [ "X-LightHouse-Session", "   " ]
    let! nextCalled = runMiddleware reg ctx
    Assert.False(nextCalled)
    Assert.Equal(401, ctx.Response.StatusCode)
}


[<Fact>]
let ``unknown token → 401 (service restart / idle TTL 만료 모두 동일 분기, L3 회복)`` () = task {
    let reg = mkRegistry ()
    let ctx = newCtx "/mcp/sse" [ "X-LightHouse-Session", "deadbeef" ]
    let! nextCalled = runMiddleware reg ctx
    Assert.False(nextCalled)
    Assert.Equal(401, ctx.Response.StatusCode)
}


[<Fact>]
let ``valid token → next 호출 + HttpContext.Items 에 SessionState 박제`` () = task {
    let reg = mkRegistry ()
    let r = reg.CreateSession([||], "alice")  // 빈 active 셋 OK (lazy)
    let ctx = newCtx "/mcp/sse" [ "X-LightHouse-Session", r.Token ]
    let! nextCalled = runMiddleware reg ctx
    Assert.True(nextCalled)
    let stored = ctx.Items.[SessionAuth.SessionItemKey]
    Assert.NotNull(stored)
    let s = stored :?> SessionState
    Assert.Equal(r.Token, s.Token)
}


[<Fact>]
let ``Delete 후 같은 token → 401 (CR6 L3 회복 시나리오)`` () = task {
    let reg = mkRegistry ()
    let r = reg.CreateSession([||], "alice")
    Assert.True(reg.Delete r.Token)
    let ctx = newCtx "/mcp/sse" [ "X-LightHouse-Session", r.Token ]
    let! nextCalled = runMiddleware reg ctx
    Assert.False(nextCalled)
    Assert.Equal(401, ctx.Response.StatusCode)
}
