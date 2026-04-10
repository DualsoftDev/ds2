namespace Ds2.Backend

open Microsoft.AspNetCore.SignalR
open System.Threading.Tasks
open Ds2.Backend.Common

type SignalHub() =
    inherit Hub()

    static let log = log4net.LogManager.GetLogger("SignalHub")

    member this.WriteTag(address: string, value: string, source: string) : Task =
        log.Debug($"WriteTag: {address}={value} source={source}")
        this.Clients.All.SendAsync(HubMethod.OnTagChanged, address, value, source)

    member this.SubscribeTag(address: string) : Task =
        this.Groups.AddToGroupAsync(this.Context.ConnectionId, address)

    member this.UnsubscribeTag(address: string) : Task =
        this.Groups.RemoveFromGroupAsync(this.Context.ConnectionId, address)
