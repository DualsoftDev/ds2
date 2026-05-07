namespace Ds2.Backend

open Microsoft.AspNetCore.SignalR
open System.Threading.Tasks
open Ds2.Backend.Common

type SignalHub() =
    inherit Hub()

    static let log = log4net.LogManager.GetLogger("SignalHub")
    /// Tag 값 캐시: 마지막 WriteTag 값을 기억해서 Control 재접속/재시작 시 QueryTag로 복원
    static let tagCache = System.Collections.Concurrent.ConcurrentDictionary<string, string>()

    static member ClearTagCache() =
        tagCache.Clear()

    member this.WriteTag(address: string, value: string, source: string) : Task =
        log.Debug($"WriteTag: {address}={value} source={source}")
        tagCache.[address] <- value
        this.Clients.All.SendAsync(HubMethod.OnTagChanged, address, value, source)

    /// Batch 송신 — 여러 태그 변경을 한 프레임으로 받아 한 프레임으로 fan-out.
    /// Per-tag WriteTag 호출 대비 SignalR 프레임 수 / 직렬화 비용 감소.
    member this.WriteTags(items: TagWrite[]) : Task =
        if isNull items || items.Length = 0 then
            Task.CompletedTask
        else
            for it in items do
                if not (isNull it.Address) then
                    tagCache.[it.Address] <- it.Value
            log.Debug($"WriteTags: count={items.Length}")
            this.Clients.All.SendAsync(HubMethod.OnTagsChanged, items)

    /// 현재 Tag 값 조회 — 캐시에 없으면 빈 문자열
    member _.QueryTag(address: string) : Task<string> =
        match tagCache.TryGetValue(address) with
        | true, v -> Task.FromResult(v)
        | _ -> Task.FromResult("")

    member this.SubscribeTag(address: string) : Task =
        this.Groups.AddToGroupAsync(this.Context.ConnectionId, address)

    member this.UnsubscribeTag(address: string) : Task =
        this.Groups.RemoveFromGroupAsync(this.Context.ConnectionId, address)
