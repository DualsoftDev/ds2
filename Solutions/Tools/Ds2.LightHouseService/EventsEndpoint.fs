namespace Ds2.LightHouseService

open System
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.DependencyInjection
open Ds2.LightHouse.Protocol

/// Phase S7 D-S7-2 — `GET /events` SSE endpoint (todo-lighthouse-kb-server.md §3.7, s6-r27).
///
/// 동작:
/// - **인증**: AuthMiddleware (Bearer PSK + X-User-Identity) 가 본 endpoint 진입 전 강제. 401 분기는 middleware 책임.
/// - **transport**: text/event-stream (RFC 6202 / EventSource API 호환). 단순 ndjson `data: {json}\n\n` 형식.
/// - **lifecycle**: `EventBus.Subscribe()` 로 per-request Channel reader 발급 → loop 안에서 reader 가 새 event
///   읽을 때마다 stream 에 write + flush. client disconnect 시 `HttpContext.RequestAborted` token 이 signal.
/// - **keepalive**: 30s heartbeat — proxy/firewall 의 idle disconnect 회피. `keepalive` 별 event type 으로 emit
///   (subscriber 가 last-seen 시각 갱신용).
/// - **content type**: `text/event-stream; charset=utf-8` + `Cache-Control: no-cache` + `X-Accel-Buffering: no`
///   (nginx 등 reverse proxy 가 buffering 안 함). HTTP/1.1 chunked transfer-encoding 은 Kestrel 자동.
[<RequireQualifiedAccess>]
module EventsEndpoint =

    /// keepalive heartbeat 간격. 30s = proxy 의 통상 idle timeout (60s) 의 절반.
    [<Literal>]
    let private KeepaliveSeconds = 30

    /// SSE 응답 헤더 박제. ResponseStartedTimeout (Kestrel default 30s) 이전에 모두 set 의무 — first write 전.
    let private setSseHeaders (ctx: HttpContext) : unit =
        ctx.Response.StatusCode <- 200
        ctx.Response.ContentType <- "text/event-stream; charset=utf-8"
        ctx.Response.Headers.["Cache-Control"] <- "no-cache"
        ctx.Response.Headers.["X-Accel-Buffering"] <- "no"
        ctx.Response.Headers.["Connection"] <- "keep-alive"

    let private jsonOpts = JsonSerializerOptions(WriteIndented = false)

    /// 단일 event → SSE wire format. `data: {json}\n\n` (EventSource API 정합).
    ///
    /// **s6-r56 ⑲ (외부 --review) — client-write keepalive 강화**: client disconnect 검출 정합.
    /// `WriteAsync` / `FlushAsync` 가 `OperationCanceledException` (RequestAborted) 외에
    /// `IOException` (socket close) / `ObjectDisposedException` 도 throw 가능. 모두 graceful exit 의무.
    let private writeEvent (ctx: HttpContext) (evt: ServerEvent) : Task =
        task {
            let json = JsonSerializer.Serialize(evt, jsonOpts)
            let line = sprintf "data: %s\n\n" json
            let bytes = Encoding.UTF8.GetBytes line
            do! ctx.Response.Body.WriteAsync(ReadOnlyMemory<byte>(bytes), ctx.RequestAborted)
            do! ctx.Response.Body.FlushAsync ctx.RequestAborted
        } :> Task

    /// **s6-r56 ⑲ — client disconnect 검출 보조**. write 실패 (OCE / IOException /
    /// ObjectDisposedException) 시 false 반환. 다른 exception 은 재throw.
    let private tryWriteEvent (ctx: HttpContext) (evt: ServerEvent) : Task<bool> =
        task {
            try
                do! writeEvent ctx evt
                return true
            with
            | :? OperationCanceledException -> return false
            | :? System.IO.IOException -> return false  // socket close mid-write
            | :? ObjectDisposedException -> return false  // response stream disposed
        }

    /// **R5-M1 (s6-r72+ external review hotfix)** — SSE Response.Body 단일 writer 직렬화.
    /// keepalive task + reader loop 가 동시에 `ctx.Response.Body.WriteAsync` 호출 시 ASP.NET Core 의
    /// chunked transfer-encoding 손상 (single-writer assumption 위반) → client 측 SSE parse 실패 회귀.
    /// per-context SemaphoreSlim 으로 write 직렬화. lock acquire 자체가 cancel (RequestAborted) 시 false 반환
    /// (정상 disconnect path). lock release 는 finally 안 sync — task CE 의 finally 가 sync block 지원.
    let private tryWriteEventSerialized
        (writeLock: SemaphoreSlim) (ctx: HttpContext) (evt: ServerEvent) : Task<bool> =
        task {
            let mutable lockAcquired = false
            try
                try
                    do! writeLock.WaitAsync ctx.RequestAborted
                    lockAcquired <- true
                    return! tryWriteEvent ctx evt
                with :? OperationCanceledException -> return false
            finally
                if lockAcquired then writeLock.Release() |> ignore
        }

    /// **s6-r70 review C-2** — multi-tenant SSE tenant filter helper. T2/T3 모드 시 각 event 의 CollectionId 가
    /// 본 caller user 에게 visible 한지 검증. T1 모드 / `keepalive` / `upload-progress` 는 통과 (filter 대상 외).
    ///
    /// upload-progress 의 CollectionId 는 *uploadId* (registry 미존재) — owner-only routing 은 별 phase backlog
    /// (UploadsEndpoint.publish 가 owner 표시 의무).
    let private isEventVisibleTo (cfg: ServiceConfig) (storageRoot: string) (user: string) (evt: ServerEvent) : bool =
        if cfg.MultiTenant.Mode = MultiTenantMode.T1 then true
        elif String.IsNullOrEmpty evt.CollectionId then true
        elif evt.Event = ServerEventNames.Keepalive then true
        elif evt.Event = ServerEventNames.UploadProgress then true   // backlog: owner-only routing 별 phase
        else
            match Registry.tryFindById storageRoot evt.CollectionId with
            | None -> true   // collection-deleted 등 registry 에서 이미 제거 — leak 차단 의미 0, 통과 (race)
            | Some entry ->
                match MultiTenantPolicy.evaluate cfg.MultiTenant.Mode user entry with
                | MultiTenantPolicy.AccessDecision.Hidden -> false
                | _ -> true

    /// HttpContext.Items 의 X-User-Identity (AuthMiddleware 박제).
    let private userIdentityOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue AuthMiddleware.UserIdentityItemKey with
        | true, v when not (isNull v) -> string v
        | _ -> "unknown"

    /// `GET /events` handler. 인증은 middleware 가 처리.
    ///
    /// **s6-r56 ⑲ — client-write keepalive 강화**: keepalive 가 EventBus fan-out 대신 *direct write*.
    /// 박제 의도: (i) multi-instance 시 다른 subscriber 의 keepalive 와 무관 — 각 SSE 가 자체 keepalive timer.
    /// (ii) EventBus drop-oldest 정책에 keepalive 가 차지하는 capacity 절감. (iii) self-receive 의 lazy
    /// fan-out cost 절감 (N subscribers × 30s 마다 N publish → N timer × N self-write 만).
    ///
    /// **s6-r70 review C-2**: cfg + storageRoot 인자 추가. reader loop 안 evt 마다 isEventVisibleTo 호출.
    let private handle (cfg: ServiceConfig) (storageRoot: string) (bus: EventBus) (ctx: HttpContext) : Task =
        task {
            setSseHeaders ctx
            let user = userIdentityOf ctx
            // **R5-M1 (s6-r72+ external review hotfix)** — per-context write lock (SSE single-writer 보장).
            // keepalive task + reader loop 가 동시 write 시 chunk encoding 손상 → client SSE parse 실패 회귀 차단.
            use writeLock = new SemaphoreSlim(1, 1)
            // m2 (s6-r27 자가 검열 review) — Subscribe 를 first-write 보다 먼저.
            // 이전 순서 (first keepalive write → Subscribe) 는 µs window race — 그 사이 publish 된 event 가 본
            // subscriber 의 channel 에 들어가지 못함. Subscribe 후 first keepalive write 면 race window 0.
            // **R5-M2 (s6-r76)** — lifecycle / progress channel 분리. 두 reader 합산 송신 (우선순위 = lifecycle).
            let id, lifecycleReader, progressReader = bus.Subscribe()
            // first write — single-thread phase (keepalive task / reader loop 시작 전) 라 lock 외부 호출 OK.
            // client 가 즉시 connected 확인 (EventSource readyState=OPEN 진입). 실패 시 즉시 종료.
            let! firstOk = tryWriteEventSerialized writeLock ctx (ServerEvent.keepalive())
            try
                if firstOk then
                    use keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource ctx.RequestAborted
                    // **s6-r56 ⑲** — per-subscriber direct keepalive timer (EventBus fan-out 폐기).
                    // disconnect 시 write 실패 (false) → loop 종료 → keepalive task cancel.
                    let disconnectSignal = TaskCompletionSource<bool>()
                    let keepaliveLoop () = task {
                        let mutable alive = true
                        while alive && not keepaliveCts.IsCancellationRequested do
                            try
                                do! Task.Delay(TimeSpan.FromSeconds(float KeepaliveSeconds), keepaliveCts.Token)
                                if not keepaliveCts.IsCancellationRequested then
                                    let! ok = tryWriteEventSerialized writeLock ctx (ServerEvent.keepalive())
                                    if not ok then
                                        alive <- false
                                        disconnectSignal.TrySetResult true |> ignore
                            with
                            | :? OperationCanceledException -> alive <- false
                    }
                    let keepaliveTask = keepaliveLoop()

                    // reader loop — disconnect 시 WaitToReadAsync false / OCE / write 실패 모두 graceful 종료.
                    // **R5-M2 (s6-r76)** — lifecycle / progress 2 reader 합산. 매 wakeup 마다 lifecycle 먼저 drain
                    // 후 progress drain (우선순위 = lifecycle, 같은 burst 안에서도 lifecycle 이 먼저 client 도달).
                    try
                        let mutable continueLoop = true
                        let drainReader (r: ChannelReader<ServerEvent>) = task {
                            let mutable evt = Unchecked.defaultof<ServerEvent>
                            while continueLoop && r.TryRead &evt do
                                // **s6-r70 review C-2** — T2/T3 모드 시 tenant filter. Hidden = skip (caller 가 안 받음).
                                if isEventVisibleTo cfg storageRoot user evt then
                                    let! ok = tryWriteEventSerialized writeLock ctx evt
                                    if not ok then continueLoop <- false
                        }
                        let mutable lifecycleWait = lifecycleReader.WaitToReadAsync(ctx.RequestAborted).AsTask()
                        let mutable progressWait = progressReader.WaitToReadAsync(ctx.RequestAborted).AsTask()
                        while continueLoop do
                            let! _ = Task.WhenAny(lifecycleWait, progressWait)
                            // lifecycle 먼저 drain (우선순위) — progress ready 였어도 함께 drain.
                            do! drainReader lifecycleReader
                            do! drainReader progressReader
                            // **R5-M2 review M1 patch (sub-agent)** — close 판정 race window 보호. 양 channel
                            // 모두 closed (Unsubscribe 의 양 TryComplete 후) 면 종료. 한 쪽만 close 인 µs window
                            // 에서는 살아있는 쪽만 reassign — closed 쪽 재호출 시 즉시 false → busy spin 차단.
                            let lcDone = lifecycleWait.IsCompletedSuccessfully && not lifecycleWait.Result
                            let prDone = progressWait.IsCompletedSuccessfully && not progressWait.Result
                            if lcDone && prDone then
                                continueLoop <- false
                            else
                                if not lcDone && lifecycleWait.IsCompleted then
                                    lifecycleWait <- lifecycleReader.WaitToReadAsync(ctx.RequestAborted).AsTask()
                                if not prDone && progressWait.IsCompleted then
                                    progressWait <- progressReader.WaitToReadAsync(ctx.RequestAborted).AsTask()
                            // keepalive timer 가 disconnect 검출했으면 loop 종료 (background path 정합)
                            if disconnectSignal.Task.IsCompleted then continueLoop <- false
                    with
                    | :? OperationCanceledException -> ()  // client disconnect — 정상 종료

                    keepaliveCts.Cancel()
                    try do! keepaliveTask
                    with :? OperationCanceledException -> ()
            finally
                bus.Unsubscribe id
        } :> Task

    // ── D-S7-2c (s6-r32) — POST /events/caption-progress ─────────────────────────
    // Phase 2 vision caption indexing 진행률을 client (Promaker / lighthouse-cli) 가 server 로 publish.
    // server 는 검증 후 EventBus 로 fan-out — 다른 client (예: codex / 별 Promaker instance) 가 본 SSE 로 수신.

    /// POST body schema — caption-progress publish.
    [<CLIMutable>]
    type CaptionProgressRequest =
        { [<System.Text.Json.Serialization.JsonPropertyName("collectionId")>]
          CollectionId: string
          [<System.Text.Json.Serialization.JsonPropertyName("progress")>]
          Progress: int
          [<System.Text.Json.Serialization.JsonPropertyName("message")>]
          Message: string }

    let private requestJsonOpts =
        let o = JsonSerializerOptions()
        o.PropertyNameCaseInsensitive <- true
        o

    let private writeError (ctx: HttpContext) (status: int) (message: string) : Task =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        let json = JsonSerializer.Serialize({| error = message |})
        ctx.Response.WriteAsync json

    /// POST /events/caption-progress — body 검증 후 `caption-progress` event publish + 204.
    /// 검증: collectionId 빈 값 → 400, progress 0~100 외 → 400. body json 결함 → 400.
    /// message 는 선택 (빈 값 / null 허용).
    let private postCaptionProgress (bus: EventBus) (ctx: HttpContext) : Task =
        task {
            let mutable body = Unchecked.defaultof<CaptionProgressRequest>
            let mutable parseFail = false
            try
                let! parsed =
                    JsonSerializer.DeserializeAsync<CaptionProgressRequest>(
                        ctx.Request.Body, requestJsonOpts, ctx.RequestAborted).AsTask()
                body <- parsed
            with
            | :? JsonException -> parseFail <- true
            | :? System.IO.InvalidDataException -> parseFail <- true

            if parseFail || isNull (box body) then
                do! writeError ctx 400 "request body JSON 결함"
            elif System.String.IsNullOrWhiteSpace body.CollectionId then
                do! writeError ctx 400 "collectionId 필수"
            elif body.Progress < 0 || body.Progress > 100 then
                do! writeError ctx 400 "progress 는 0~100 범위"
            else
                let msg = if isNull body.Message then "" else body.Message
                bus.Publish(ServerEvent.captionProgress body.CollectionId body.Progress msg)
                ctx.Response.StatusCode <- 204
        } :> Task

    /// `GET /events` + `POST /events/caption-progress` endpoint 등록. AuthMiddleware 뒤에 매핑.
    /// **s6-r70 review C-2** — cfg + storageRoot 인자 추가 (multi-tenant filter).
    let map (app: IEndpointRouteBuilder) (cfg: ServiceConfig) (storageRoot: string) (bus: EventBus) =
        app.MapGet("/events", RequestDelegate(handle cfg storageRoot bus)) |> ignore
        app.MapPost("/events/caption-progress",
            RequestDelegate(postCaptionProgress bus)) |> ignore
