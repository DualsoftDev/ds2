namespace Ds2.LightHouseService

open System
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.DependencyInjection

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

    /// `GET /events` handler. 인증은 middleware 가 처리.
    ///
    /// **s6-r56 ⑲ — client-write keepalive 강화**: keepalive 가 EventBus fan-out 대신 *direct write*.
    /// 박제 의도: (i) multi-instance 시 다른 subscriber 의 keepalive 와 무관 — 각 SSE 가 자체 keepalive timer.
    /// (ii) EventBus drop-oldest 정책에 keepalive 가 차지하는 capacity 절감. (iii) self-receive 의 lazy
    /// fan-out cost 절감 (N subscribers × 30s 마다 N publish → N timer × N self-write 만).
    let private handle (bus: EventBus) (ctx: HttpContext) : Task =
        task {
            setSseHeaders ctx
            // m2 (s6-r27 자가 검열 review) — Subscribe 를 first-write 보다 먼저.
            // 이전 순서 (first keepalive write → Subscribe) 는 µs window race — 그 사이 publish 된 event 가 본
            // subscriber 의 channel 에 들어가지 못함. Subscribe 후 first keepalive write 면 race window 0.
            let id, reader = bus.Subscribe()
            // first write — client 가 즉시 connected 확인 (EventSource readyState=OPEN 진입). 실패 시 즉시 종료.
            let! firstOk = tryWriteEvent ctx (ServerEvent.keepalive())
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
                                    let! ok = tryWriteEvent ctx (ServerEvent.keepalive())
                                    if not ok then
                                        alive <- false
                                        disconnectSignal.TrySetResult true |> ignore
                            with
                            | :? OperationCanceledException -> alive <- false
                    }
                    let keepaliveTask = keepaliveLoop()

                    // reader loop — disconnect 시 WaitToReadAsync false / OCE / write 실패 모두 graceful 종료.
                    try
                        let mutable continueLoop = true
                        while continueLoop do
                            let! hasMore = reader.WaitToReadAsync ctx.RequestAborted
                            if not hasMore then
                                continueLoop <- false
                            else
                                let mutable evt = Unchecked.defaultof<ServerEvent>
                                while continueLoop && reader.TryRead &evt do
                                    let! ok = tryWriteEvent ctx evt
                                    if not ok then continueLoop <- false
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
    let map (app: IEndpointRouteBuilder) (bus: EventBus) =
        app.MapGet("/events", RequestDelegate(handle bus)) |> ignore
        app.MapPost("/events/caption-progress",
            RequestDelegate(postCaptionProgress bus)) |> ignore
