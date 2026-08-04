namespace Ds2.Backend

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Hosting
open Ds2.Backend.Common
open Ds2.Backend.Plc

module BackendHost =

    let private defaultPort = 5050
    let private hubPath = "/hub/signal"

    let getHubUrl (port: int) = $"http://localhost:{port}{hubPath}"

    /// Empty(=PLC 미사용) 게이트웨이용 sentinel — PlcGateway 자체가 connections=[] 면 IsEnabled=false 가 되어
    /// scan service 는 idle, SignalHub.ForwardToPlc 분기는 no-op.
    let private emptyConfig : PlcGatewayConfig = { Connections = [] }

    /// 모든 entry point 가 공유하는 호스트 부트스트랩 — 빌더 생성, DI 등록, Hub map, StartAsync.
    /// configureBuilder: 빌더 생성 직후 호출되는 hook. Promaker.Agent 가 Host.UseWindowsService() 주입에 사용.
    let private bootstrap
            (port: int)
            (plcConfig: PlcGatewayConfig option)
            (readOnly: bool)
            (delegated: bool)
            (configureBuilder: WebApplicationBuilder -> unit)
            (configureApp: WebApplication -> unit) =
        SignalHub.ClearTagCache()
        SignalHub.SetReadOnly(readOnly)
        // 위임 스캔: Agent 는 PLC 에 직접 안 붙고 Pi5 수집기가 WriteTags push 로 IN 공급(§10.10 ①).
        // → PlcScanService 를 아예 등록하지 않아 connect/scan loop 자체가 없다(모델 IP 무한 접속실패/blackout 차단).
        SignalHub.SetDelegatedScan(delegated)
        // Monitoring(read-only) 은 초기 동기 PLC 스캔 생략 — PLC 응답 지연이 UI 를 freeze 시키는 문제 차단.
        // Control 은 원위치 추론용 cache populate 가 필요하므로 기존 동작 유지 (false).
        PlcScanService.SetSkipInitialScan(readOnly)

        let builder = WebApplication.CreateBuilder()
        configureBuilder builder
        // Pi5 collector flushes PLC changes in batches. The ASP.NET Core SignalR default
        // receive limit is 32 KiB, which is too small for a few hundred TagWrite records
        // and makes the server close the connection before SignalHub.WriteTags is invoked.
        // Keep a finite ceiling, but size it for collector backlog chunks.
        builder.Services.AddSignalR(fun options ->
            options.MaximumReceiveMessageSize <- Nullable(1024L * 1024L)
            // DSPilot 구독자(HubSubscriberService)는 KeepAliveInterval=2분 — 수신 전용이라 ping 외엔
            // 보낼 게 없는데, 기본 ClientTimeoutInterval(30s)은 "30초 무수신 = 절단"이라 조용한
            // 구독자가 31초마다 강제 절단됐다(실측 464회/4h, 부하 무관 정주기). 재연결 틈에
            // 팬아웃이 유실되고(이 홉은 재전송 없음), Pi5 backlog replay 버스트가 그 틈에 걸리면
            // 뭉텅이로 증발해 그래프/사이클이 깨진다. 클라이언트 ping 주기(2분)의 2배 이상으로 완화.
            options.ClientTimeoutInterval <- Nullable(TimeSpan.FromMinutes 5.0))
        |> ignore

        let cfg = plcConfig |> Option.defaultValue emptyConfig
        builder.Services.AddSingleton<PlcGatewayConfig>(cfg) |> ignore
        // TryAdd: configureBuilder 가 IPlcGateway 인스턴스를 먼저 주입하면(Control 호스팅: engine writeTag 와
        // 같은 gateway 를 공유해야 함) 그것을 쓰고, 없으면 기본 PlcGateway 를 DI 가 생성한다.
        builder.Services.TryAddSingleton<IPlcGateway, PlcGateway>()
        builder.Services.AddSingleton<IPlcHubBroadcaster, SignalHubBroadcaster>() |> ignore
        builder.Services.TryAddSingleton<IRuntimeHubSession, NullRuntimeHubSession>()
        // 위임이 아닐 때만 직접 스캔 서비스 등록 — 위임이면 Agent 가 PLC 에 접속하지 않는다.
        if not delegated then
            builder.Services.AddHostedService<PlcScanService>() |> ignore

        let app = builder.Build()
        configureApp app
        // 위임(분리) 모드는 원격 Pi5 가 WG 너머로 이 Hub 에 붙어야 하므로 모든 인터페이스에 bind.
        // 직접(올인원)은 로컬 전용 유지 — 외부 노출 없음, 회귀 0.
        let bindHost =
            if delegated then
                match Environment.GetEnvironmentVariable "DS2_AGENT_HUB_BIND_HOST" with
                | null | "" -> "0.0.0.0"
                | value -> value.Trim()
            else "localhost"
        let scheme =
            if delegated then
                match Environment.GetEnvironmentVariable "DS2_AGENT_HUB_SCHEME" with
                | value when String.Equals(value, "https", StringComparison.OrdinalIgnoreCase) -> "https"
                | _ -> "http"
            else "http"
        let privateHttp = delegated && scheme = "http"
        if privateHttp then
            match Environment.GetEnvironmentVariable "DS2_AGENT_HUB_ALLOW_PRIVATE_HTTP" with
            | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> ()
            | _ ->
                invalidOp
                    "Delegated Hub HTTP requires DS2_AGENT_HUB_ALLOW_PRIVATE_HTTP=true and is restricted to private peers."
        app.Urls.Add($"{scheme}://{bindHost}:{port}")
        if privateHttp then
            app.Use(Func<HttpContext, RequestDelegate, Task>(fun context next ->
                task {
                    if SignalHubConnectionPolicy.isPrivateOrLoopbackAddress context.Connection.RemoteIpAddress then
                        do! next.Invoke(context)
                    else
                        context.Response.StatusCode <- StatusCodes.Status403Forbidden
                        context.Response.ContentType <- "application/json"
                        do! context.Response.WriteAsync("{\"error\":\"private network required\"}")
                } :> Task))
            |> ignore
        app.MapHub<SignalHub>(hubPath) |> ignore
        app.StartAsync() |> Async.AwaitTask |> Async.RunSynchronously
        app

    /// BackendHost 시작.
    /// - port: SignalR Hub 포트. None 이면 5050.
    /// - plcConfig: 실 PLC 연동 설정. None 이면 PLC 게이트웨이 등록만 하고 idle.
    /// - readOnly: true 면 SignalHub 가 클라이언트 WriteTag/WriteTags 를 거부 — Monitoring 모드용.
    let startWithPlc (port: int option) (plcConfig: PlcGatewayConfig option) (readOnly: bool) =
        let p = port |> Option.defaultValue defaultPort
        bootstrap p plcConfig readOnly false (fun _ -> ()) (fun _ -> ())

    /// 기존 호출자 호환 entry — PLC 미연결 모드.
    let start (port: int option) =
        startWithPlc port None false

    /// C# 호출 편의용 — int / config 를 unwrap 형태로 받는다 (Control 모드: read/write).
    let startWithPlcConfig (port: int) (plcConfig: PlcGatewayConfig) =
        startWithPlc (Some port) (Some plcConfig) false

    /// Monitoring 모드용 — PLC 스캔만 하고 클라이언트 write 는 거부.
    let startWithPlcConfigReadOnly (port: int) (plcConfig: PlcGatewayConfig) =
        startWithPlc (Some port) (Some plcConfig) true

    /// Promaker.Agent 등 호스트 lifecycle 을 커스터마이즈해야 하는 호출자용 entry.
    /// configureBuilder 에서 Host.UseWindowsService() 등을 주입할 수 있다.
    /// C# 에서 람다 그대로 전달 가능 — Action<WebApplicationBuilder>.
    /// delegated: true 면 위임 스캔(§10.10 ①) — PlcScanService 미등록(Agent 가 PLC 직접 접속 안 함),
    ///            Pi5 수집기의 WriteTags 만 IN 소스. false 면 기존 직접 스캔(회귀 0).
    let startWithBuilderConfig
            (port: int)
            (plcConfig: PlcGatewayConfig)
            (readOnly: bool)
            (delegated: bool)
            (configureBuilder: Action<WebApplicationBuilder>) =
        bootstrap port (Some plcConfig) readOnly delegated (fun b -> configureBuilder.Invoke(b)) (fun _ -> ())

    /// Agent extension point for routes that must be mapped after Build but
    /// before StartAsync (for example AID HTTP webhook ingress).
    let startWithBuilderAndAppConfig
            (port: int)
            (plcConfig: PlcGatewayConfig)
            (readOnly: bool)
            (delegated: bool)
            (configureBuilder: Action<WebApplicationBuilder>)
            (configureApp: Action<WebApplication>) =
        bootstrap port (Some plcConfig) readOnly delegated
            (fun b -> configureBuilder.Invoke(b))
            (fun app -> configureApp.Invoke(app))

    let stop (app: WebApplication) =
        SignalHub.ClearTagCache()
        // 기본 ShutdownTimeout(30s) 을 다 기다리는 wedge 관찰됨 — Kestrel 의 active SignalR
        // client drain 또는 hosted service 종료 대기가 원인. Promaker.Agent 의 restart cycle 이
        // 매번 30s 씩 hang 되어 DSPilot 에서 새 PLC 설정이 늦게 반영되는 race 의 진입점이 됨.
        // 짧은 timeout 으로 강제 — graceful 실패 시 dispose 가 어차피 자원 해제.
        let stopTimeout = TimeSpan.FromSeconds 5.0
        app.StopAsync(stopTimeout) |> Async.AwaitTask |> Async.RunSynchronously
        (app :> IDisposable).Dispose()
