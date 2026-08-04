namespace Ds2.Backend

open Microsoft.AspNetCore.SignalR
open System
open System.Net
open System.Net.Sockets
open System.Threading.Tasks
open Ds2.Backend.Common
open Ds2.Backend.Plc

module RuntimeHubDefaults =
    let emptyIdentity : RuntimeSessionIdentity =
        {
            SessionId = ""
            ModelHash = ""
            Generation = 0
            Mode = ""
        }

    let identityFromEnvelope (envelope: RuntimeCommandEnvelope) : RuntimeSessionIdentity =
        if isNull (box envelope) then
            emptyIdentity
        else
            {
                SessionId = envelope.SessionId
                ModelHash = envelope.ModelHash
                Generation = envelope.Generation
                Mode = envelope.Mode
            }

    /// Server-internal command envelope — 현재 세션 identity 를 그대로 복사해 stale guard 를 통과시킨다.
    /// PLC scan 등 server-origin IO 주입에 사용. Null/empty 세션이면 빈 envelope (Null 세션은 어차피 no-op).
    let selfEnvelope (identity: RuntimeSessionIdentity) : RuntimeCommandEnvelope =
        if isNull (box identity) then
            { SessionId = ""; ModelHash = ""; Generation = 0; Mode = ""; CommandId = "" }
        else
            { SessionId = identity.SessionId
              ModelHash = identity.ModelHash
              Generation = identity.Generation
              Mode = identity.Mode
              CommandId = "" }

    let emptyGuidStatus (id: string) : RuntimeGuidStatus =
        {
            Id = if isNull id then "" else id
            StatusName = ""
            StatusValue = 0
        }

    let emptyFlowTag (id: string) : RuntimeGuidFlowTag =
        {
            Id = if isNull id then "" else id
            FlowTagName = ""
            FlowTagValue = 0
        }

    let emptySnapshot (envelope: RuntimeCommandEnvelope) : RuntimeStateSnapshot =
        let identity = identityFromEnvelope envelope
        {
            SessionId = identity.SessionId
            ModelHash = identity.ModelHash
            Generation = identity.Generation
            Mode = identity.Mode
            StatusName = ""
            StatusValue = 0
            ClockMs = 0L
            CurrentTimeMs = 0L
            NextEventTimeMs = Nullable<int64>()
            WorkStates = [||]
            CallStates = [||]
            FlowStates = [||]
            IOValues = [||]
            HasStartableWork = false
            HasActiveDuration = false
            IsHomingPhase = false
            TimestampUtc = DateTime.UtcNow
        }

    let emptyIndexProjection (envelope: RuntimeCommandEnvelope) : RuntimeIndexProjection =
        let identity = identityFromEnvelope envelope
        {
            SessionId = identity.SessionId
            ModelHash = identity.ModelHash
            Generation = identity.Generation
            Mode = identity.Mode
            WorkNames = [||]
            WorkSystemNames = [||]
            WorkFlowGuids = [||]
            CallWorkGuids = [||]
            WorkCallGuids = [||]
            TokenSourceGuids = [||]
            TokenSinkGuids = [||]
        }

    let emptyIOMapProjection (envelope: RuntimeCommandEnvelope) : RuntimeIOMapProjection =
        let identity = identityFromEnvelope envelope
        {
            SessionId = identity.SessionId
            ModelHash = identity.ModelHash
            Generation = identity.Generation
            Mode = identity.Mode
            OutAddresses = [||]
            InAddresses = [||]
            Mappings = [||]
        }

/// Hub 수신값을 Agent 로컬 PLC 게이트웨이에 전달할지 결정하는 순수 정책.
/// 위임 스캔에서는 Pi5가 이미 현장 PLC에서 읽은 관측값을 보내므로 Agent가 다시 PLC로
/// echo하면 안 된다. Control/VirtualPlant처럼 Agent가 PLC owner인 모드에서만 forward한다.
[<RequireQualifiedAccess>]
module SignalHubWritePolicy =
    let shouldForwardToPlc
            (delegatedScan: bool)
            (gatewayEnabled: bool)
            (address: string)
            (source: string) =
        not delegatedScan
        && gatewayEnabled
        && not (String.IsNullOrWhiteSpace address)
        && not (String.Equals(source, HubSource.Plc, StringComparison.OrdinalIgnoreCase))
        && not (String.Equals(source, HubSource.Resync, StringComparison.OrdinalIgnoreCase))

/// 단말 화이트리스트가 켜진 Hub의 연결 허용 정책.
/// 같은 인스턴스의 DSPilot 같은 loopback 클라이언트는 헤더 없이 허용하되, 원격(Pi5 포함)은
/// 반드시 X-Device-Id를 제시해 화이트리스트를 통과해야 한다. 로컬이라도 헤더를 제시했다면 검증한다.
[<RequireQualifiedAccess>]
module SignalHubConnectionPolicy =
    /// Plaintext delegated ingress is limited to loopback, RFC1918 IPv4,
    /// link-local addresses, and IPv6 ULA (fc00::/7).
    let isPrivateOrLoopbackAddress (address: IPAddress) =
        if isNull address then false
        elif IPAddress.IsLoopback address then true
        else
            let normalized =
                if address.IsIPv4MappedToIPv6 then address.MapToIPv4()
                else address
            let bytes = normalized.GetAddressBytes()
            match normalized.AddressFamily with
            | AddressFamily.InterNetwork ->
                bytes.[0] = 10uy
                || (bytes.[0] = 172uy && bytes.[1] >= 16uy && bytes.[1] <= 31uy)
                || (bytes.[0] = 192uy && bytes.[1] = 168uy)
                || (bytes.[0] = 169uy && bytes.[1] = 254uy)
            | AddressFamily.InterNetworkV6 ->
                normalized.IsIPv6LinkLocal || (bytes.[0] &&& 0xFEuy) = 0xFCuy
            | _ -> false

    let isAllowed
            (validatorConfigured: bool)
            (isLoopback: bool)
            (hasDeviceId: bool)
            (credentialValid: bool) =
        not validatorConfigured
        || (isLoopback && not hasDeviceId)
        || credentialValid

    /// 등록된 원격 수집 단말은 현장 관측 push만 수행할 수 있다.
    let isRemoteMethodAllowed methodName =
        match methodName with
        | "WriteTags" | "ReportScanHeartbeat" | "ReportPlcConnectionStatus" -> true
        | _ -> false

type NullRuntimeHubSession() =
    interface IRuntimeHubSession with
        member _.CurrentIdentity = RuntimeHubDefaults.emptyIdentity
        member _.StartAsync _ = Task.CompletedTask
        member _.PauseAsync _ = Task.CompletedTask
        member _.ResumeAsync _ = Task.CompletedTask
        member _.StopAsync _ = Task.CompletedTask
        member _.ResetAsync _ = Task.CompletedTask
        member _.ApplyInitialStatesAsync _ = Task.CompletedTask
        member _.StepAsync _ = Task.CompletedTask
        member _.CanAdvanceStepAsync _ = Task.FromResult(false)
        member _.StepWithSourcePrimingAsync _ = Task.CompletedTask
        member _.BeginStepBatchAsync _ = Task.CompletedTask
        member _.IsStepBatchActiveAsync _ = Task.FromResult(false)
        member _.EndStepAsync _ = Task.CompletedTask
        member _.AdvanceSimulationToAsync _ = Task.CompletedTask
        member _.ForceWorkStateAsync _ = Task.CompletedTask
        member _.ForceCallStateAsync _ = Task.CompletedTask
        member _.TryForceWorkStateIfGoingAsync _ = Task.FromResult(false)
        member _.TryForceWorkStateIfReadyAsync _ = Task.FromResult(false)
        member _.GetWorkStateAsync command =
            let id = if isNull (box command) then "" else command.WorkId
            Task.FromResult(RuntimeHubDefaults.emptyGuidStatus id)
        member _.GetCallStateAsync command =
            let id = if isNull (box command) then "" else command.CallId
            Task.FromResult(RuntimeHubDefaults.emptyGuidStatus id)
        member _.GetFlowStateAsync command =
            let id = if isNull (box command) then "" else command.FlowTagName
            Task.FromResult(RuntimeHubDefaults.emptyFlowTag id)
        member _.SeedTokenAsync _ = Task.CompletedTask
        member _.StartSourceWorkAsync _ = Task.CompletedTask
        member _.DiscardTokenAsync _ = Task.CompletedTask
        member _.InjectIOValueAsync _ = Task.CompletedTask
        member _.InjectIOValueByAddressAsync _ = Task.CompletedTask
        member _.InjectIOValuesByAddressAsync _ = Task.CompletedTask
        member _.SetAllFlowStatesAsync _ = Task.CompletedTask
        member _.ReloadConnectionsAsync _ = Task.CompletedTask
        member _.ReloadDurationsAsync _ = Task.CompletedTask
        member _.StartWithHomingPhaseAsync _ = Task.CompletedTask
        member _.GetWorkTokenAsync _ = Task.FromResult(0)
        member _.GetTokenOriginAsync _ = Task.FromResult("")
        member _.GetSnapshotAsync envelope =
            Task.FromResult(RuntimeHubDefaults.emptySnapshot envelope)
        member _.GetIndexProjectionAsync envelope =
            Task.FromResult(RuntimeHubDefaults.emptyIndexProjection envelope)
        member _.GetIOMapProjectionAsync envelope =
            Task.FromResult(RuntimeHubDefaults.emptyIOMapProjection envelope)
        member _.NotifyPlcConnectionAsync _ = Task.CompletedTask
        member _.SetAutoCalibrate _ = ()

/// PlcScanService 가 외부 PLC 변화 → 모든 클라이언트로 OnTagChanged 송출 시 사용하는 broadcaster.
/// SignalHub 인스턴스는 connection 단위로 transient 라 broadcaster 만 별도 DI 로 노출.
type SignalHubBroadcaster(hubContext: IHubContext<SignalHub>, runtimeSession: IRuntimeHubSession) =
    interface IPlcHubBroadcaster with
        member _.BroadcastTagChanged(address, value, source) =
            // Hub.WriteTag 와 동일하게 캐시도 갱신 — Control 부팅 싱크 시 QueryTag 가 최신값 반환하도록.
            SignalHub.UpdateTagCache(address, value)
            // Agent 단일 호스팅: PLC IN 값을 server engine 으로도 forward.
            // self-session envelope 이라 stale guard 통과. Null 세션(엔진 없음)이면 InjectIO 가 no-op.
            let cmd : RuntimeIOAddressCommand =
                { Envelope = RuntimeHubDefaults.selfEnvelope runtimeSession.CurrentIdentity
                  Address = address
                  Value = value }
            runtimeSession.InjectIOValueByAddressAsync(cmd).GetAwaiter().GetResult()
            hubContext.Clients.All.SendAsync(HubMethod.OnTagChanged, address, value, source)

        member _.BroadcastTagsChanged(changes) =
            if List.isEmpty changes then
                Task.CompletedTask
            else
                let items =
                    // WallClockMs=0 — 직접 스캔 경로는 store-and-forward 가 없어 replay 가 애초에 없다.
                    // ★송신측 UtcNow 를 각인하면 안 된다: 이 경로의 송신자는 Agent 프로세스이고 DSPilot 이
                    // 다른 PC 일 수 있는데, 그러면 plcTagLog 만 Agent 시계로 찍히고 사이클 이력·정지 이벤트·
                    // 심박은 DSPilot 시계로 남아 두 시계 차만큼 구간 경계가 어긋난다(NTP 미설정 사내망에서
                    // 실제로 벌어진다). 0 을 보내 수신측 도착시각 폴백을 타면 전 테이블이 단일 시계를 유지한다
                    // — 스캔≈broadcast≈도착(ms 급)이라 정확도 손실도 없다. Promaker 로컬 송신부(HubTagBatchSender)
                    // 와 같은 규약이다. (Pi5 위임 경로는 수집기가 event_log.wall_clock_ms 를 실어 보낸다.)
                    changes
                    |> List.map (fun change ->
                        { Address = change.HubAddress
                          Value = change.Value
                          Source = change.Source
                          OriginTsMs = change.OriginTsMs
                          WallClockMs = 0L })
                    |> List.toArray

                for item in items do
                    SignalHub.UpdateTagCache(item.Address, item.Value)

                let cmd : RuntimeIOAddressBatchCommand =
                    { Envelope = RuntimeHubDefaults.selfEnvelope runtimeSession.CurrentIdentity
                      Items = items }
                runtimeSession.InjectIOValuesByAddressAsync(cmd).GetAwaiter().GetResult()
                hubContext.Clients.All.SendAsync(HubMethod.OnTagsChanged, items)

        member _.BroadcastPlcConnectionStatus(status: PlcConnectionStatus) =
            // 캐시도 갱신 — 신규 클라이언트가 OnConnectedAsync 단계에서 최신 스냅샷을 수신.
            SignalHub.UpdatePlcStatusCache(status)
            // server engine 에 in-proc 우선 통지 — SignalR 클라이언트보다 먼저 엔진이 통신 blackout
            // (이상감지 억제 + 관측 무효화)에 진입한다. Null 세션이면 no-op.
            runtimeSession.NotifyPlcConnectionAsync(status).GetAwaiter().GetResult()
            hubContext.Clients.All.SendAsync(HubMethod.OnPlcConnectionStatus, status)

        member _.BroadcastAbnormal(payload: AbnormalPayload) =
            hubContext.Clients.All.SendAsync(HubMethod.OnAbnormal, payload)

        member _.BroadcastScanHeartbeat() =
            hubContext.Clients.All.SendAsync(HubMethod.OnScanHeartbeat)

and SignalHub(
        gateway: IPlcGateway,
        runtimeSession: IRuntimeHubSession,
        gatewayConfig: PlcGatewayConfig) =
    inherit Hub()

    static let log = log4net.LogManager.GetLogger("SignalHub")
    let allowedCollectorTags =
        let result = System.Collections.Generic.Dictionary<string, PlcTagDef>(StringComparer.OrdinalIgnoreCase)
        for connection in gatewayConfig.Connections do
            for tag in connection.Tags do
                if not (result.ContainsKey tag.HubAddress) then result.[tag.HubAddress] <- tag
        result
    let allowedCollectorConnections =
        let result = System.Collections.Generic.Dictionary<string, PlcConnectionConfig>(StringComparer.OrdinalIgnoreCase)
        for connection in gatewayConfig.Connections do
            if not (result.ContainsKey connection.Name) then result.[connection.Name] <- connection
        result
    let validRemoteCollectorItem (item: TagWrite) =
        if isNull item.Address || isNull item.Value || isNull item.Source
           || item.Address.Length > 1024 || item.Value.Length > 65_536
           || (not (item.Source.Equals(HubSource.Plc, StringComparison.OrdinalIgnoreCase))
               && not (item.Source.Equals(HubSource.Resync, StringComparison.OrdinalIgnoreCase))) then false
        else
            match allowedCollectorTags.TryGetValue item.Address with
            | true, tag -> PlcValueIo.canParseTagValue tag item.Value
            | _ -> false
    /// Tag 값 캐시: 마지막 WriteTag 값을 기억해서 Control 재접속/재시작 시 QueryTag로 복원.
    /// PLC scan service 의 broadcast 도 이 캐시를 갱신해 둠.
    static let tagCache = System.Collections.Concurrent.ConcurrentDictionary<string, string>()

    /// 어댑터별 PLC 연결 상태 스냅샷 — broadcaster 가 갱신, 신규 client 가 OnConnectedAsync 에서 캐스트로 수신.
    /// PlcGateway 자체도 동일 상태를 갖지만 broadcaster 캐시를 두면 Hub bootstrap 직후 첫 connect 시도 전
    /// (gateway 가 아직 빈 상태) 클라이언트가 들어와도 일관된 응답이 가능하다.
    static let plcStatusCache =
        System.Collections.Concurrent.ConcurrentDictionary<string, PlcConnectionStatus>()

    /// 마지막 수집기 config push (분리 아키텍처) — 신규 client(Pi5)가 OnConnectedAsync 에서 caller 로 받아
    /// Agent 활성 broadcast 를 놓쳐도 현재 config 를 확보한다(PlcConnectionStatus 캐시와 동형).
    /// null 이면 아직 config 가 조립/push 되지 않음(전송 생략).
    static let mutable collectorConfigCache : CollectorConfigPayload = Unchecked.defaultof<CollectorConfigPayload>

    /// Monitoring 모드 read-only flag. true 면 클라이언트 WriteTag/WriteTags 가 no-op.
    /// PlcScanService 의 PLC→Hub broadcast 는 영향 없음 (broadcaster 가 직접 SendAsync).
    static let mutable readOnlyMode = false

    /// 위임 스캔 모드 — Agent 가 PLC 에 직접 안 붙고(§10.10 ①: PlcScanService off) 분리된 Pi5 수집기가
    /// 스캔→WriteTags push 로 IN 을 공급한다. readOnlyMode(Monitoring)여도 Pi5 의 WriteTags 는 수용해야
    /// 엔진이 IN 을 받아 구동되므로, 이 플래그가 켜지면 WriteTags 의 read-only 억제를 예외 처리한다.
    /// (UI 클라이언트 write 는 여전히 억제 — Monitoring UI 는 수신 전용이라 WriteTags 를 호출하지 않는다.)
    static let mutable delegatedScanMode = false

    /// 자동 duration 정합 현재 상태 캐시 — 클라이언트(DSPilot)가 연결 직후 GetAutoCalibrate 로 pull.
    /// SetAutoCalibrate(토글) + InitAutoCalibrate(Agent 시작 시 저장값 복원)가 갱신. SSOT 는 엔진이지만
    /// hub 가 캐시를 둬 신규 클라이언트가 broadcast 를 놓쳐도 일관된 현재값을 받게 한다(스캔주기와 동형).
    static let mutable autoCalibrateState = true

    /// Agent 시작 시 저장값(PlcConnection.json)으로 hub 캐시 초기화 — broadcast 없이 캐시만.
    static member InitAutoCalibrate(on: bool) = autoCalibrateState <- on

    static member ClearTagCache() =
        tagCache.Clear()
        plcStatusCache.Clear()
        collectorConfigCache <- Unchecked.defaultof<CollectorConfigPayload>

    /// Agent 가 조립한 수집기 config 를 캐시 — broadcast 직후 호출해 신규 client 가 OnConnectedAsync 로 받게 한다.
    static member UpdateCollectorConfigCache(payload: CollectorConfigPayload) =
        collectorConfigCache <- payload

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

    /// 위임 스캔 모드 set — host bootstrap 에서 "실제 PLC 연결" 안 함(위임)일 때 true.
    /// true 면 PlcScanService 가 등록되지 않아(Agent 직접 스캔 off) Pi5 수집기가 유일한 IN 소스가 된다.
    static member SetDelegatedScan(value: bool) =
        delegatedScanMode <- value

    static member IsDelegatedScan = delegatedScanMode

    /// 스캔 주기 영속화 훅 — 호스트(Promaker.Agent 등)가 PlcConnection.json 기록 람다를 주입.
    /// null 이면 라이브 적용만 (재시작 시 파일값으로 복귀). readOnlyMode 와 무관 — 설정이지 태그 쓰기가 아님.
    static member val PersistScanIntervalMs : Action<int> = null with get, set

    /// 자동 duration 정합 ON/OFF 영속화 훅 — 스캔주기와 동형. 호스트가 PlcConnection.json 기록 람다 주입.
    /// OFF 상태가 재시작 후에도 유지되게 한다(정지 시 AASX 반영→OFF 의 결과 보존).
    static member val PersistAutoCalibrate : Action<bool> = null with get, set

    /// 원격 수집 클라이언트(Pi5 엣지 수집기)의 단말 신원 검증 훅 — 호스트(Promaker.Agent)가 주입.
    /// 시그니처: deviceId, credential → 등록된 단말 자격 증명이면 true.
    /// **null(미설정)이면 검증 생략** — localhost 올인원/로컬 개발은 인증 불필요(기존 동작 유지, 회귀 0).
    /// 설정돼 있고 미등록이면 OnConnectedAsync 가 연결을 Abort. Pi5 는 X-Device-Id 헤더로 시리얼을 싣는다
    /// (HubClientPusher 와 헤더 계약 일치). provision_token 은 부트스트랩 전용이라 상시 인증에서 제외.
    static member val ValidateDeviceCredential : Func<string, string, bool> = null with get, set

    member private this.IsRemoteCaller =
        let http = this.Context.GetHttpContext()
        if isNull http || isNull http.Connection.RemoteIpAddress then true
        else not (System.Net.IPAddress.IsLoopback(http.Connection.RemoteIpAddress))

    member private this.LocalOnly(methodName: string) =
        if not this.IsRemoteCaller then true
        else
            log.Warn($"Remote SignalR method rejected: method={methodName} connId={this.Context.ConnectionId}")
            false

    member private this.LocalOnlyFailure(methodName: string) : Task =
        Task.FromException(HubException($"'{methodName}' is restricted to local Agent clients."))

    /// 현재 유효 스캔 주기(ms) — override 우선, 없으면 config 의 최소 주기.
    member _.GetScanIntervalMs() : Task<int> =
        let ms =
            match gateway.ScanIntervalOverrideMs with
            | Some v -> v
            | None ->
                gateway.MinScanInterval
                |> Option.map (fun t -> int t.TotalMilliseconds)
                |> Option.defaultValue 100
        Task.FromResult ms

    /// 스캔 주기 변경 (10~500ms clamp) — 게이트웨이 라이브 적용 + 영속화 + 전체 클라이언트 동기화.
    /// Promaker/DSPilot 슬라이더가 호출. 재시작 없음 — scan loop 가 다음 iteration 부터 새 주기 사용.
    member this.SetScanIntervalMs(ms: int) : Task =
        if not (this.LocalOnly("SetScanIntervalMs")) then
            this.LocalOnlyFailure("SetScanIntervalMs")
        else
            let clamped = max 10 (min 500 ms)
            gateway.ScanIntervalOverrideMs <- Some clamped
            let persist = SignalHub.PersistScanIntervalMs
            if not (isNull persist) then
                try persist.Invoke(clamped)
                with ex -> log.Warn($"PersistScanIntervalMs threw: {ex.Message}")
            log.Info($"Scan interval set to {clamped}ms (live, requested={ms})")
            this.Clients.All.SendAsync(HubMethod.OnScanIntervalChanged, clamped)

    /// 자동 duration 정합 ON/OFF — server engine(abnormal 어댑터)에 즉시 적용 + 전 클라이언트 동기화.
    /// ON=실측 학습값 기준 판정, OFF=모델(AASX 확정값) 기준. 정지 시 "AASX 반영" 선택하면 OFF 로 전환.
    /// 현재 자동 duration 정합 상태 — 클라이언트 연결 직후 pull(스캔주기 GetScanIntervalMs 동형).
    member _.GetAutoCalibrate() : Task<bool> = Task.FromResult autoCalibrateState

    member this.SetAutoCalibrate(on: bool) : Task =
        if not (this.LocalOnly("SetAutoCalibrate")) then
            this.LocalOnlyFailure("SetAutoCalibrate")
        else
            autoCalibrateState <- on
            runtimeSession.SetAutoCalibrate(on)
            let persist = SignalHub.PersistAutoCalibrate
            if not (isNull persist) then
                try persist.Invoke(on)
                with ex -> log.Warn($"PersistAutoCalibrate threw: {ex.Message}")
            log.Info($"AutoCalibrate set to {on} (live, broadcasting + persisted)")
            this.Clients.All.SendAsync(HubMethod.OnAutoCalibrateChanged, on)

    /// 건강 기준선 수동 동결 — 상태 없는 릴레이. duration 학습/기준선은 각 클라이언트(Promaker 등)가
    /// 들고 있으므로 hub 는 동결 명령을 전 클라이언트에 fan-out 만 한다 (스캔 주기 동기화 패턴과 동형).
    /// 호출자 자신도 브로드캐스트를 받아 동결한다 — 동결 경로 일원화.
    member this.FreezeHealthBaseline() : Task =
        if not (this.LocalOnly("FreezeHealthBaseline")) then
            this.LocalOnlyFailure("FreezeHealthBaseline")
        else
            log.Info("Health baseline freeze requested — broadcasting to all clients")
            this.Clients.All.SendAsync(HubMethod.OnHealthBaselineFreeze)

    /// 스캔 생존 heartbeat 리포트 (Client → Server). 수집 주체가 분리(Pi5 엣지 수집기)일 때 Pi5 가
    /// 클라이언트로서 호출 → Hub 가 OnScanHeartbeat 를 fan-out. 올인원의 BroadcastScanHeartbeat(server-origin)
    /// 와 최종 fan-out 지점(OnScanHeartbeat, Clients.All)이 동일하므로 소비자(Promaker/DSPilot)는 무영향.
    /// Clients.All 사용(BroadcastScanHeartbeat 와 동형) — Pi5 는 OnScanHeartbeat 를 구독하지 않아 자기 echo 무해.
    member this.ReportScanHeartbeat() : Task =
        this.Clients.All.SendAsync(HubMethod.OnScanHeartbeat)

    /// Field-side PLC connection status report from the delegated Pi5 collector.
    /// In delegated mode the Agent gateway is intentionally idle, so only this
    /// report represents the real PLC connection. Direct-scan hosts ignore it.
    member this.ReportPlcConnectionStatus(status: PlcConnectionStatus) : Task =
        if not delegatedScanMode
           || isNull (box status)
           || String.IsNullOrWhiteSpace status.Name then
            Task.CompletedTask
        else
            match allowedCollectorConnections.TryGetValue status.Name with
            | false, _ ->
                log.Warn($"ReportPlcConnectionStatus rejected unconfigured connection: {status.Name}")
                Task.CompletedTask
            | true, connection ->
                let lastError =
                    if isNull status.LastError then ""
                    elif status.LastError.Length <= 4096 then status.LastError
                    else status.LastError.Substring(0, 4096)
                let normalized =
                    { Name = connection.Name
                      Vendor = string connection.Vendor
                      IpAddress = connection.IpAddress
                      Port = connection.Port
                      IsConnected = status.IsConnected
                      LastError = lastError
                      FailedAttempts = Math.Clamp(status.FailedAttempts, 0, 1_000_000)
                      AtUtc = DateTime.UtcNow }
                SignalHub.UpdatePlcStatusCache(normalized)
                task {
                    try
                        do! runtimeSession.NotifyPlcConnectionAsync(normalized)
                    with ex ->
                        log.Warn($"ReportPlcConnectionStatus engine notify failed for {normalized.Name}: {ex.Message}")
                    do! this.Clients.All.SendAsync(HubMethod.OnPlcConnectionStatus, normalized)
                } :> Task

    /// PLC 게이트웨이로 위임 — fire-and-forget.
    /// - source=plc/resync: PLC 관측값이므로 self-echo 차단.
    /// - delegatedScanMode: Pi5가 현장 PLC owner이므로 source와 무관하게 Agent 재쓰기 차단.
    member private _.ForwardToPlc(address: string, value: string, source: string) =
        if SignalHubWritePolicy.shouldForwardToPlc delegatedScanMode gateway.IsEnabled address source then
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

    member private this.RejectRuntimeCommand(envelope: RuntimeCommandEnvelope, reason: string) =
        let payload = RuntimeCommandRejected.fromEnvelope reason envelope
        this.Clients.Caller.SendAsync(HubMethod.OnRuntimeCommandRejected, payload)

    member private this.ExecuteRuntimeCommand(envelope: RuntimeCommandEnvelope, execute: unit -> Task) : Task =
        task {
            if not (this.LocalOnly("RuntimeCommand")) then
                do! this.RejectRuntimeCommand(envelope, "remote-forbidden")
            else
                match RuntimeSessionContract.tryRejectCommand runtimeSession.CurrentIdentity envelope with
                | Some reason ->
                    do! this.RejectRuntimeCommand(envelope, reason)
                | None ->
                    do! execute()
        } :> Task

    member private this.QueryRuntime(envelope: RuntimeCommandEnvelope, fallback: 'T, query: unit -> Task<'T>) : Task<'T> =
        task {
            if not (this.LocalOnly("RuntimeQuery")) then
                do! this.RejectRuntimeCommand(envelope, "remote-forbidden")
                return fallback
            else
                match RuntimeSessionContract.tryRejectCommand runtimeSession.CurrentIdentity envelope with
                | Some reason ->
                    do! this.RejectRuntimeCommand(envelope, reason)
                    return fallback
                | None ->
                    return! query()
        }

    member private _.EnvelopeOf(command: RuntimeEmptyCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeStepPolicyCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeStepBatchCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeAdvanceSimulationCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeWorkStateCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeCallStateCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeWorkCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeCallCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeFlowTagCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeIOValueCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeIOAddressCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member private _.EnvelopeOf(command: RuntimeTokenCommand) =
        if isNull (box command) then Unchecked.defaultof<RuntimeCommandEnvelope> else command.Envelope

    member this.RuntimeStart(command: RuntimeEmptyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.StartAsync(command))

    member this.RuntimePause(command: RuntimeEmptyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.PauseAsync(command))

    member this.RuntimeResume(command: RuntimeEmptyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.ResumeAsync(command))

    member this.RuntimeStop(command: RuntimeEmptyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.StopAsync(command))

    member this.RuntimeReset(command: RuntimeEmptyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.ResetAsync(command))

    member this.RuntimeApplyInitialStates(command: RuntimeEmptyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.ApplyInitialStatesAsync(command))

    member this.RuntimeStep(command: RuntimeStepPolicyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.StepAsync(command))

    member this.RuntimeCanAdvanceStep(command: RuntimeStepPolicyCommand) : Task<bool> =
        this.QueryRuntime(this.EnvelopeOf(command), false, fun () -> runtimeSession.CanAdvanceStepAsync(command))

    member this.RuntimeStepWithSourcePriming(command: RuntimeStepPolicyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.StepWithSourcePrimingAsync(command))

    member this.RuntimeBeginStepBatch(command: RuntimeStepBatchCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.BeginStepBatchAsync(command))

    member this.RuntimeIsStepBatchActive(command: RuntimeEmptyCommand) : Task<bool> =
        this.QueryRuntime(this.EnvelopeOf(command), false, fun () -> runtimeSession.IsStepBatchActiveAsync(command))

    member this.RuntimeEndStep(command: RuntimeEmptyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.EndStepAsync(command))

    member this.RuntimeAdvanceSimulationTo(command: RuntimeAdvanceSimulationCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.AdvanceSimulationToAsync(command))

    member this.RuntimeForceWorkState(command: RuntimeWorkStateCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.ForceWorkStateAsync(command))

    member this.RuntimeForceCallState(command: RuntimeCallStateCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.ForceCallStateAsync(command))

    member this.RuntimeTryForceWorkStateIfGoing(command: RuntimeWorkStateCommand) : Task<bool> =
        this.QueryRuntime(this.EnvelopeOf(command), false, fun () -> runtimeSession.TryForceWorkStateIfGoingAsync(command))

    member this.RuntimeTryForceWorkStateIfReady(command: RuntimeWorkStateCommand) : Task<bool> =
        this.QueryRuntime(this.EnvelopeOf(command), false, fun () -> runtimeSession.TryForceWorkStateIfReadyAsync(command))

    member this.RuntimeGetWorkState(command: RuntimeWorkCommand) : Task<RuntimeGuidStatus> =
        let fallback =
            let id = if isNull (box command) then "" else command.WorkId
            RuntimeHubDefaults.emptyGuidStatus id
        this.QueryRuntime(this.EnvelopeOf(command), fallback, fun () -> runtimeSession.GetWorkStateAsync(command))

    member this.RuntimeGetCallState(command: RuntimeCallCommand) : Task<RuntimeGuidStatus> =
        let fallback =
            let id = if isNull (box command) then "" else command.CallId
            RuntimeHubDefaults.emptyGuidStatus id
        this.QueryRuntime(this.EnvelopeOf(command), fallback, fun () -> runtimeSession.GetCallStateAsync(command))

    member this.RuntimeGetFlowState(command: RuntimeFlowTagCommand) : Task<RuntimeGuidFlowTag> =
        let fallback =
            let id = if isNull (box command) then "" else command.FlowTagName
            RuntimeHubDefaults.emptyFlowTag id
        this.QueryRuntime(this.EnvelopeOf(command), fallback, fun () -> runtimeSession.GetFlowStateAsync(command))

    member this.RuntimeSeedToken(command: RuntimeTokenCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.SeedTokenAsync(command))

    member this.RuntimeStartSourceWork(command: RuntimeWorkCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.StartSourceWorkAsync(command))

    member this.RuntimeDiscardToken(command: RuntimeTokenCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.DiscardTokenAsync(command))

    member this.RuntimeInjectIOValue(command: RuntimeIOValueCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.InjectIOValueAsync(command))

    member this.RuntimeInjectIOValueByAddress(command: RuntimeIOAddressCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.InjectIOValueByAddressAsync(command))

    member this.RuntimeSetAllFlowStates(command: RuntimeFlowTagCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.SetAllFlowStatesAsync(command))

    member this.RuntimeReloadConnections(command: RuntimeEmptyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.ReloadConnectionsAsync(command))

    member this.RuntimeReloadDurations(command: RuntimeEmptyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.ReloadDurationsAsync(command))

    member this.RuntimeStartWithHomingPhase(command: RuntimeEmptyCommand) : Task =
        this.ExecuteRuntimeCommand(this.EnvelopeOf(command), fun () -> runtimeSession.StartWithHomingPhaseAsync(command))

    member this.RuntimeGetWorkToken(command: RuntimeWorkCommand) : Task<int> =
        this.QueryRuntime(this.EnvelopeOf(command), 0, fun () -> runtimeSession.GetWorkTokenAsync(command))

    member this.RuntimeGetTokenOrigin(command: RuntimeTokenCommand) : Task<string> =
        this.QueryRuntime(this.EnvelopeOf(command), "", fun () -> runtimeSession.GetTokenOriginAsync(command))

    member this.RuntimeGetSnapshot(envelope: RuntimeCommandEnvelope) : Task<RuntimeStateSnapshot> =
        this.QueryRuntime(envelope, RuntimeHubDefaults.emptySnapshot envelope, fun () -> runtimeSession.GetSnapshotAsync(envelope))

    member this.RuntimeGetIndexProjection(envelope: RuntimeCommandEnvelope) : Task<RuntimeIndexProjection> =
        this.QueryRuntime(envelope, RuntimeHubDefaults.emptyIndexProjection envelope, fun () -> runtimeSession.GetIndexProjectionAsync(envelope))

    member this.RuntimeGetIOMapProjection(envelope: RuntimeCommandEnvelope) : Task<RuntimeIOMapProjection> =
        this.QueryRuntime(envelope, RuntimeHubDefaults.emptyIOMapProjection envelope, fun () -> runtimeSession.GetIOMapProjectionAsync(envelope))

    member this.WriteTag(address: string, value: string, source: string) : Task =
        if not (this.LocalOnly("WriteTag")) then
            this.LocalOnlyFailure("WriteTag")
        elif readOnlyMode then
            log.Debug($"WriteTag suppressed (read-only): {address}={value} source={source}")
            Task.CompletedTask
        else
            log.Debug($"WriteTag: {address}={value} source={source}")
            tagCache.[address] <- value
            this.ForwardToPlc(address, value, source)
            // Agent 단일 호스팅: client write(예: VirtualPlant 의 IN echo)도 server engine 으로 forward.
            // PLC scan(broadcaster)이 PLC IN 을 engine 에 넣듯, 실PLC 없는 VP 경로의 IN 도 engine 에 들어가야 상태가 돈다.
            // self-session envelope → stale guard 통과. Null 세션(engine 없음)이면 no-op. InjectIOValueByAddress 가
            // IN 주소만 처리(ioMap.InAddressToMappings)하므로 OUT write 가 섞여도 무해.
            let injectCmd : RuntimeIOAddressCommand =
                { Envelope = RuntimeHubDefaults.selfEnvelope runtimeSession.CurrentIdentity
                  Address = address; Value = value }
            runtimeSession.InjectIOValueByAddressAsync(injectCmd) |> ignore
            this.Clients.All.SendAsync(HubMethod.OnTagChanged, address, value, source)

    /// Batch 송신 — 여러 태그 변경을 한 프레임으로 받아 한 프레임으로 fan-out.
    /// Per-tag WriteTag 호출 대비 SignalR 프레임 수 / 직렬화 비용 감소.
    member this.WriteTags(items: TagWrite[]) : Task =
        // 위임 스캔 모드에선 read-only 여도 수용 — 분리된 Pi5 수집기의 WriteTags 가 유일한 IN 소스이므로.
        // (Monitoring UI 는 수신 전용이라 WriteTags 를 호출하지 않으니, 위임 모드에서 이 예외로 UI write 가
        //  열려도 실질 위험 없음. 직접 Monitoring(위임 아님)은 기존대로 억제.)
        if readOnlyMode && not delegatedScanMode then
            let cnt = if isNull items then 0 else items.Length
            log.Debug($"WriteTags suppressed (read-only): count={cnt}")
            Task.CompletedTask
        elif isNull items || items.Length = 0 then
            Task.CompletedTask
        elif items.Length > 10_000 then
            Task.FromException(HubException("WriteTags batch exceeds 10000 items."))
        else
            let remote = this.IsRemoteCaller
            let accepted =
                items
                |> Array.filter (fun item ->
                    not (isNull item.Address)
                    && not (isNull item.Value)
                    && not (isNull item.Source)
                    && item.Address.Length <= 1024
                    && item.Value.Length <= 65_536
                    && (not remote || validRemoteCollectorItem item))
            let rejected = items.Length - accepted.Length
            if rejected > 0 then
                log.Warn($"WriteTags rejected invalid/unconfigured items: count={rejected} remote={remote}")
            for it in accepted do
                tagCache.[it.Address] <- it.Value
                this.ForwardToPlc(it.Address, it.Value, it.Source)
                // client write IN 도 engine 으로 forward (WriteTag 와 동일 이유 — VP IN echo 가 engine 에 반영).
                let injectCmd : RuntimeIOAddressCommand =
                    { Envelope = RuntimeHubDefaults.selfEnvelope runtimeSession.CurrentIdentity
                      Address = it.Address; Value = it.Value }
                runtimeSession.InjectIOValueByAddressAsync(injectCmd) |> ignore
            if accepted.Length = 0 then Task.CompletedTask
            else
                log.Debug($"WriteTags: accepted={accepted.Length} received={items.Length}")
                this.Clients.All.SendAsync(HubMethod.OnTagsChanged, accepted)

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
            // 원격 수집 클라이언트 단말 신원 검증. 같은 인스턴스의 DSPilot(loopback)은 헤더 없이 허용하고,
            // 그 외 원격 연결은 X-Device-Id가 화이트리스트를 통과해야 한다. 로컬이라도 헤더를 보냈으면 검증한다.
            // 미등록 시 연결 Abort — 이후 snapshot 송출 스킵. provision_token은 부트스트랩 전용이라 사용하지 않는다.
            let validator = SignalHub.ValidateDeviceCredential
            if not (isNull validator) then
                let mutable ok = false
                try
                    let http = this.Context.GetHttpContext()
                    if not (isNull http) then
                        let deviceId = http.Request.Headers.["X-Device-Id"].ToString()
                        let authorization = http.Request.Headers.Authorization.ToString()
                        let credential =
                            if authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
                                authorization.Substring("Bearer ".Length).Trim()
                            else
                                http.Request.Headers.["X-Device-Credential"].ToString().Trim()
                        let hasDeviceId = not (String.IsNullOrWhiteSpace deviceId)
                        let remoteIp = http.Connection.RemoteIpAddress
                        let isLoopback = not (isNull remoteIp) && System.Net.IPAddress.IsLoopback(remoteIp)
                        let credentialValid =
                            hasDeviceId
                            && not (String.IsNullOrWhiteSpace credential)
                            && validator.Invoke(deviceId, credential)
                        ok <- SignalHubConnectionPolicy.isAllowed true isLoopback hasDeviceId credentialValid
                with ex ->
                    log.Warn($"Device credential validation threw: {ex.Message}")
                    ok <- false
                if not ok then
                    log.Warn($"SignalR connection rejected — unregistered device (connId={this.Context.ConnectionId})")
                    this.Context.Abort()
                    return ()
            try
                // The local gateway is intentionally disconnected in delegated mode.
                // Publishing that snapshot makes DSPilot report a false PLC outage.
                // Use only statuses reported by the Pi5 collector in that mode.
                let fromGateway =
                    if delegatedScanMode then []
                    else gateway.GetConnectionStatuses() |> List.map (fun s -> s.Name, s)
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
            // 수집기 config 스냅샷 — 분리 아키텍처에서 나중에 붙은 Pi5 가 마지막 config 를 즉시 받게 한다.
            try
                if not (isNull (box collectorConfigCache)) then
                    do! this.Clients.Caller.SendAsync(HubMethod.OnCollectorConfig, collectorConfigCache)
            with ex ->
                log.Debug($"OnConnectedAsync CollectorConfig send: {ex.Message}")
            // Runtime session 초기 snapshot — client proxy 가 현재 상태 + identity(SessionId/Generation) 를 동기화.
            // engine 없는 Null 세션(SessionId="")이면 생략 — 무의미한 빈 snapshot 차단.
            try
                let id = runtimeSession.CurrentIdentity
                if not (System.String.IsNullOrWhiteSpace id.SessionId) then
                    let env = RuntimeHubDefaults.selfEnvelope id
                    let! snapshot = runtimeSession.GetSnapshotAsync(env)
                    do! this.Clients.Caller.SendAsync(HubMethod.OnRuntimeSnapshot, snapshot)
            with ex ->
                log.Warn($"OnConnectedAsync Runtime snapshot threw: {ex.Message}")
        } :> Task
