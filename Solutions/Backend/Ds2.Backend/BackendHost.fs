namespace Ds2.Backend

open System
open Microsoft.AspNetCore.Builder
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
            (configureBuilder: WebApplicationBuilder -> unit) =
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
            options.MaximumReceiveMessageSize <- Nullable(1024L * 1024L))
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
        // 위임(분리) 모드는 원격 Pi5 가 WG 너머로 이 Hub 에 붙어야 하므로 모든 인터페이스에 bind.
        // 직접(올인원)은 로컬 전용 유지 — 외부 노출 없음, 회귀 0.
        let bindHost = if delegated then "0.0.0.0" else "localhost"
        app.Urls.Add($"http://{bindHost}:{port}")
        app.MapHub<SignalHub>(hubPath) |> ignore
        app.StartAsync() |> Async.AwaitTask |> Async.RunSynchronously
        app

    /// BackendHost 시작.
    /// - port: SignalR Hub 포트. None 이면 5050.
    /// - plcConfig: 실 PLC 연동 설정. None 이면 PLC 게이트웨이 등록만 하고 idle.
    /// - readOnly: true 면 SignalHub 가 클라이언트 WriteTag/WriteTags 를 거부 — Monitoring 모드용.
    let startWithPlc (port: int option) (plcConfig: PlcGatewayConfig option) (readOnly: bool) =
        let p = port |> Option.defaultValue defaultPort
        bootstrap p plcConfig readOnly false (fun _ -> ())

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
        bootstrap port (Some plcConfig) readOnly delegated (fun b -> configureBuilder.Invoke(b))

    let stop (app: WebApplication) =
        SignalHub.ClearTagCache()
        // 기본 ShutdownTimeout(30s) 을 다 기다리는 wedge 관찰됨 — Kestrel 의 active SignalR
        // client drain 또는 hosted service 종료 대기가 원인. Promaker.Agent 의 restart cycle 이
        // 매번 30s 씩 hang 되어 DSPilot 에서 새 PLC 설정이 늦게 반영되는 race 의 진입점이 됨.
        // 짧은 timeout 으로 강제 — graceful 실패 시 dispose 가 어차피 자원 해제.
        let stopTimeout = TimeSpan.FromSeconds 5.0
        app.StopAsync(stopTimeout) |> Async.AwaitTask |> Async.RunSynchronously
        (app :> IDisposable).Dispose()
