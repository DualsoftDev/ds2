namespace Ds2.Backend

open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
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
            (configureBuilder: WebApplicationBuilder -> unit) =
        SignalHub.ClearTagCache()
        SignalHub.SetReadOnly(readOnly)
        // Monitoring(read-only) 은 초기 동기 PLC 스캔 생략 — PLC 응답 지연이 UI 를 freeze 시키는 문제 차단.
        // Control 은 원위치 추론용 cache populate 가 필요하므로 기존 동작 유지 (false).
        PlcScanService.SetSkipInitialScan(readOnly)

        let builder = WebApplication.CreateBuilder()
        configureBuilder builder
        builder.Services.AddSignalR() |> ignore

        let cfg = plcConfig |> Option.defaultValue emptyConfig
        builder.Services.AddSingleton<PlcGatewayConfig>(cfg) |> ignore
        builder.Services.AddSingleton<IPlcGateway, PlcGateway>() |> ignore
        builder.Services.AddSingleton<IPlcHubBroadcaster, SignalHubBroadcaster>() |> ignore
        builder.Services.AddHostedService<PlcScanService>() |> ignore

        let app = builder.Build()
        app.Urls.Add($"http://localhost:{port}")
        app.MapHub<SignalHub>(hubPath) |> ignore
        app.StartAsync() |> Async.AwaitTask |> Async.RunSynchronously
        app

    /// BackendHost 시작.
    /// - port: SignalR Hub 포트. None 이면 5050.
    /// - plcConfig: 실 PLC 연동 설정. None 이면 PLC 게이트웨이 등록만 하고 idle.
    /// - readOnly: true 면 SignalHub 가 클라이언트 WriteTag/WriteTags 를 거부 — Monitoring 모드용.
    let startWithPlc (port: int option) (plcConfig: PlcGatewayConfig option) (readOnly: bool) =
        let p = port |> Option.defaultValue defaultPort
        bootstrap p plcConfig readOnly (fun _ -> ())

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
    let startWithBuilderConfig
            (port: int)
            (plcConfig: PlcGatewayConfig)
            (readOnly: bool)
            (configureBuilder: Action<WebApplicationBuilder>) =
        bootstrap port (Some plcConfig) readOnly (fun b -> configureBuilder.Invoke(b))

    let stop (app: WebApplication) =
        SignalHub.ClearTagCache()
        // 기본 ShutdownTimeout(30s) 을 다 기다리는 wedge 관찰됨 — Kestrel 의 active SignalR
        // client drain 또는 hosted service 종료 대기가 원인. Promaker.Agent 의 restart cycle 이
        // 매번 30s 씩 hang 되어 DSPilot 에서 새 PLC 설정이 늦게 반영되는 race 의 진입점이 됨.
        // 짧은 timeout 으로 강제 — graceful 실패 시 dispose 가 어차피 자원 해제.
        let stopTimeout = TimeSpan.FromSeconds 5.0
        app.StopAsync(stopTimeout) |> Async.AwaitTask |> Async.RunSynchronously
        (app :> IDisposable).Dispose()
