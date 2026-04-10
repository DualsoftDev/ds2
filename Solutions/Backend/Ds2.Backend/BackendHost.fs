namespace Ds2.Backend

open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection

module BackendHost =

    let private defaultPort = 5050
    let private hubPath = "/hub/signal"

    let getHubUrl (port: int) = $"http://localhost:{port}{hubPath}"

    let start (port: int option) =
        let p = port |> Option.defaultValue defaultPort
        let builder = WebApplication.CreateBuilder()
        builder.Services.AddSignalR() |> ignore

        let app = builder.Build()
        app.Urls.Add($"http://localhost:{p}")
        app.MapHub<SignalHub>(hubPath) |> ignore
        app.StartAsync() |> Async.AwaitTask |> Async.RunSynchronously
        app

    let stop (app: WebApplication) =
        app.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously
        (app :> IDisposable).Dispose()
