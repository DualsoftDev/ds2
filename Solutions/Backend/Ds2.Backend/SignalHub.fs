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

        member _.BroadcastPlcConnectionStatus(status: PlcConnectionStatus) =
            // 캐시도 갱신 — 신규 클라이언트가 OnConnectedAsync 단계에서 최신 스냅샷을 수신.
            SignalHub.UpdatePlcStatusCache(status)
            hubContext.Clients.All.SendAsync(HubMethod.OnPlcConnectionStatus, status)

and SignalHub(gateway: IPlcGateway) =
    inherit Hub()

    static let log = log4net.LogManager.GetLogger("SignalHub")
    /// Tag 값 캐시: 마지막 WriteTag 값을 기억해서 Control 재접속/재시작 시 QueryTag로 복원.
    /// PLC scan service 의 broadcast 도 이 캐시를 갱신해 둠.
    static let tagCache = System.Collections.Concurrent.ConcurrentDictionary<string, string>()

    /// 어댑터별 PLC 연결 상태 스냅샷 — broadcaster 가 갱신, 신규 client 가 OnConnectedAsync 에서 캐스트로 수신.
    /// PlcGateway 자체도 동일 상태를 갖지만 broadcaster 캐시를 두면 Hub bootstrap 직후 첫 connect 시도 전
    /// (gateway 가 아직 빈 상태) 클라이언트가 들어와도 일관된 응답이 가능하다.
    static let plcStatusCache =
        System.Collections.Concurrent.ConcurrentDictionary<string, PlcConnectionStatus>()

    /// Monitoring 모드 read-only flag. true 면 클라이언트 WriteTag/WriteTags 가 no-op.
    /// PlcScanService 의 PLC→Hub broadcast 는 영향 없음 (broadcaster 가 직접 SendAsync).
    static let mutable readOnlyMode = false

    static member ClearTagCache() =
        tagCache.Clear()
        plcStatusCache.Clear()

    /// PlcScanService broadcaster 가 캐시를 직접 갱신하기 위한 internal 진입점.
    static member internal UpdateTagCache(address: string, value: string) =
        tagCache.[address] <- value

    /// SignalHubBroadcaster.BroadcastPlcConnectionStatus 가 캐시를 갱신하기 위한 internal 진입점.
    static member internal UpdatePlcStatusCache(status: PlcConnectionStatus) =
        plcStatusCache.[status.Name] <- status

    /// Monitoring 모드 진입 시 host bootstrap 에서 true 로 set — 클라이언트 write 차단.
    static member SetReadOnly(value: bool) =
        readOnlyMode <- value

    static member IsReadOnly = readOnlyMode

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
        if readOnlyMode then
            log.Debug($"WriteTag suppressed (read-only): {address}={value} source={source}")
            Task.CompletedTask
        else
            log.Debug($"WriteTag: {address}={value} source={source}")
            tagCache.[address] <- value
            this.ForwardToPlc(address, value, source)
            this.Clients.All.SendAsync(HubMethod.OnTagChanged, address, value, source)

    /// Batch 송신 — 여러 태그 변경을 한 프레임으로 받아 한 프레임으로 fan-out.
    /// Per-tag WriteTag 호출 대비 SignalR 프레임 수 / 직렬화 비용 감소.
    member this.WriteTags(items: TagWrite[]) : Task =
        if readOnlyMode then
            let cnt = if isNull items then 0 else items.Length
            log.Debug($"WriteTags suppressed (read-only): count={cnt}")
            Task.CompletedTask
        elif isNull items || items.Length = 0 then
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

    /// 신규 클라이언트가 붙는 즉시 현재 알려진 모든 PLC 어댑터 상태를 caller 전용으로 송출.
    /// gateway snapshot 을 우선 사용하고, plcStatusCache 만 있고 gateway 가 빈 케이스(예: Control idle host)
    /// 도 함께 union — 이로써 DSPilot 이 부팅 후 처음 붙어도 "PLC 통신 실패" 배너를 즉시 그릴 수 있다.
    override this.OnConnectedAsync() =
        // base.OnConnectedAsync() 호출은 override 본체 직접 위치에서만 허용 — task { } closure 내부에서
        // 호출하면 FS0405 ("base 멤버 capture 불가"). 호출 결과 Task 를 먼저 만들고 task 내에서 await.
        let baseTask = base.OnConnectedAsync()
        task {
            do! baseTask
            try
                let fromGateway = gateway.GetConnectionStatuses() |> List.map (fun s -> s.Name, s)
                let merged =
                    let m = System.Collections.Generic.Dictionary<string, PlcConnectionStatus>()
                    for kv in plcStatusCache do m.[kv.Key] <- kv.Value
                    for (k, v) in fromGateway do m.[k] <- v
                    m.Values
                for s in merged do
                    try
                        do! this.Clients.Caller.SendAsync(HubMethod.OnPlcConnectionStatus, s)
                    with ex ->
                        log.Debug($"OnConnectedAsync PlcStatus send {s.Name}: {ex.Message}")
            with ex ->
                log.Warn($"OnConnectedAsync PlcStatus snapshot threw: {ex.Message}")
        } :> Task
