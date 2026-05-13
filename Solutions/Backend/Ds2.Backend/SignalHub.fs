namespace Ds2.Backend

open Microsoft.AspNetCore.SignalR
open System.Threading.Tasks
open Ds2.Backend.Common
open Ds2.Backend.Plc

/// PlcScanService 가 외부 PLC 변화 → 모든 클라이언트로 OnTagChanged 송출 시 사용하는 broadcaster.
/// SignalHub 인스턴스는 connection 단위로 transient 라 broadcaster 만 별도 DI 로 노출.
type SignalHubBroadcaster(hubContext: IHubContext<SignalHub>) =
    interface IPlcHubBroadcaster with
        member _.BroadcastTagChanged(address, value, source) =
            // Hub.WriteTag 와 동일하게 캐시도 갱신 — Control 부팅 싱크 시 QueryTag 가 최신값 반환하도록.
            SignalHub.UpdateTagCache(address, value)
            hubContext.Clients.All.SendAsync(HubMethod.OnTagChanged, address, value, source)

and SignalHub(gateway: IPlcGateway) =
    inherit Hub()

    static let log = log4net.LogManager.GetLogger("SignalHub")
    /// Tag 값 캐시: 마지막 WriteTag 값을 기억해서 Control 재접속/재시작 시 QueryTag로 복원.
    /// PLC scan service 의 broadcast 도 이 캐시를 갱신해 둠.
    static let tagCache = System.Collections.Concurrent.ConcurrentDictionary<string, string>()

    static member ClearTagCache() =
        tagCache.Clear()

    /// PlcScanService broadcaster 가 캐시를 직접 갱신하기 위한 internal 진입점.
    static member internal UpdateTagCache(address: string, value: string) =
        tagCache.[address] <- value

    /// PLC 게이트웨이로 위임 — fire-and-forget.
    /// source = "plc" 인 경우(=PLC 가 우리에게 알려준 변화)는 다시 PLC 로 echo 하지 않는다.
    member private _.ForwardToPlc(address: string, value: string, source: string) =
        if isNull address || not gateway.IsEnabled then ()
        elif source = HubSource.Plc then ()  // self-echo 차단
        else
            log.Debug($"ForwardToPlc: {address}={value} source={source}")
            task {
                try
                    let! ok = gateway.WriteAsync(address, value)
                    if not ok then
                        // PlcGateway 가 이미 사유를 Warn 으로 로그함 — 여기선 추가 noise 없이 종료.
                        ()
                with ex ->
                    log.Warn($"PLC write threw for {address}={value}: {ex.Message}")
            } |> ignore

    member this.WriteTag(address: string, value: string, source: string) : Task =
        log.Debug($"WriteTag: {address}={value} source={source}")
        tagCache.[address] <- value
        this.ForwardToPlc(address, value, source)
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
                    this.ForwardToPlc(it.Address, it.Value, it.Source)
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
