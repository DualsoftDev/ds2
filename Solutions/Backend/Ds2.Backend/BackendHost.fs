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

    /// BackendHost 시작.
    /// - port: SignalR Hub 포트. None 이면 5050.
    /// - plcConfig: 실 PLC 연동 설정. None 이면 PLC 게이트웨이 등록만 하고 idle.
    /// - readOnly: true 면 SignalHub 가 클라이언트 WriteTag/WriteTags 를 거부 — Monitoring 모드용.
    let startWithPlc (port: int option) (plcConfig: PlcGatewayConfig option) (readOnly: bool) =
        let p = port |> Option.defaultValue defaultPort
        SignalHub.ClearTagCache()
        SignalHub.SetReadOnly(readOnly)
        // Monitoring(read-only) 은 초기 동기 PLC 스캔 생략 — PLC 응답 지연이 UI 를 freeze 시키는 문제 차단.
        // Control 은 원위치 추론용 cache populate 가 필요하므로 기존 동작 유지 (false).
        PlcScanService.SetSkipInitialScan(readOnly)

        let builder = WebApplication.CreateBuilder()
        builder.Services.AddSignalR() |> ignore

        let cfg = plcConfig |> Option.defaultValue emptyConfig
        builder.Services.AddSingleton<PlcGatewayConfig>(cfg) |> ignore
        builder.Services.AddSingleton<IPlcGateway, PlcGateway>() |> ignore
        builder.Services.AddSingleton<IPlcHubBroadcaster, SignalHubBroadcaster>() |> ignore
        builder.Services.AddHostedService<PlcScanService>() |> ignore

        let app = builder.Build()
        app.Urls.Add($"http://localhost:{p}")
        app.MapHub<SignalHub>(hubPath) |> ignore
        app.StartAsync() |> Async.AwaitTask |> Async.RunSynchronously
        app

    /// 기존 호출자 호환 entry — PLC 미연결 모드.
    let start (port: int option) =
        startWithPlc port None false

    /// C# 호출 편의용 — int / config 를 unwrap 형태로 받는다 (Control 모드: read/write).
    let startWithPlcConfig (port: int) (plcConfig: PlcGatewayConfig) =
        startWithPlc (Some port) (Some plcConfig) false

    /// Monitoring 모드용 — PLC 스캔만 하고 클라이언트 write 는 거부.
    let startWithPlcConfigReadOnly (port: int) (plcConfig: PlcGatewayConfig) =
        startWithPlc (Some port) (Some plcConfig) true

    let stop (app: WebApplication) =
        SignalHub.ClearTagCache()
        app.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously
        (app :> IDisposable).Dispose()
