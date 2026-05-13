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
    let startWithPlc (port: int option) (plcConfig: PlcGatewayConfig option) =
        let p = port |> Option.defaultValue defaultPort
        SignalHub.ClearTagCache()

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
        startWithPlc port None

    /// C# 호출 편의용 — int / config 를 unwrap 형태로 받는다.
    let startWithPlcConfig (port: int) (plcConfig: PlcGatewayConfig) =
        startWithPlc (Some port) (Some plcConfig)

    let stop (app: WebApplication) =
        SignalHub.ClearTagCache()
        app.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously
        (app :> IDisposable).Dispose()
