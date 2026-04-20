namespace Ds2.Backend

open Microsoft.AspNetCore.SignalR
open System.Threading.Tasks
open Ds2.Backend.Common

type SignalHub() =
    inherit Hub()

    static let log = log4net.LogManager.GetLogger("SignalHub")
    /// Tag 값 캐시: 마지막 WriteTag 값을 기억해서 Control 재접속/재시작 시 QueryTag로 복원
    static let tagCache = System.Collections.Concurrent.ConcurrentDictionary<string, string>()

    member this.WriteTag(address: string, value: string, source: string) : Task =
        log.Debug($"WriteTag: {address}={value} source={source}")
        tagCache.[address] <- value
        this.Clients.All.SendAsync(HubMethod.OnTagChanged, address, value, source)

    /// 현재 Tag 값 조회 — 캐시에 없으면 빈 문자열
    member _.QueryTag(address: string) : Task<string> =
        match tagCache.TryGetValue(address) with
        | true, v -> Task.FromResult(v)
        | _ -> Task.FromResult("")

    member this.SubscribeTag(address: string) : Task =
        this.Groups.AddToGroupAsync(this.Context.ConnectionId, address)

    member this.UnsubscribeTag(address: string) : Task =
        this.Groups.RemoveFromGroupAsync(this.Context.ConnectionId, address)
