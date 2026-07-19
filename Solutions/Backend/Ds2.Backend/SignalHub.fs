namespace Ds2.Backend

open Microsoft.AspNetCore.SignalR
open System
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
                    changes
                    |> List.map (fun change ->
                        { Address = change.HubAddress
                          Value = change.Value
                          Source = change.Source
                          OriginTsMs = change.OriginTsMs })
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

and SignalHub(gateway: IPlcGateway, runtimeSession: IRuntimeHubSession) =
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

    /// 자동 duration 정합 현재 상태 캐시 — 클라이언트(DSPilot)가 연결 직후 GetAutoCalibrate 로 pull.
    /// SetAutoCalibrate(토글) + InitAutoCalibrate(Agent 시작 시 저장값 복원)가 갱신. SSOT 는 엔진이지만
    /// hub 가 캐시를 둬 신규 클라이언트가 broadcast 를 놓쳐도 일관된 현재값을 받게 한다(스캔주기와 동형).
    static let mutable autoCalibrateState = true

    /// Agent 시작 시 저장값(PlcConnection.json)으로 hub 캐시 초기화 — broadcast 없이 캐시만.
    static member InitAutoCalibrate(on: bool) = autoCalibrateState <- on

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

    /// 스캔 주기 영속화 훅 — 호스트(Promaker.Agent 등)가 PlcConnection.json 기록 람다를 주입.
    /// null 이면 라이브 적용만 (재시작 시 파일값으로 복귀). readOnlyMode 와 무관 — 설정이지 태그 쓰기가 아님.
    static member val PersistScanIntervalMs : Action<int> = null with get, set

    /// 자동 duration 정합 ON/OFF 영속화 훅 — 스캔주기와 동형. 호스트가 PlcConnection.json 기록 람다 주입.
    /// OFF 상태가 재시작 후에도 유지되게 한다(정지 시 AASX 반영→OFF 의 결과 보존).
    static member val PersistAutoCalibrate : Action<bool> = null with get, set

    /// 원격 수집 클라이언트(Pi5 엣지 수집기)의 단말 신원 검증 훅 — 호스트(Promaker.Agent)가 주입.
    /// 시그니처: deviceId → 등록된 단말이면 true. deviceId = RPi 하드웨어 시리얼(device.device_id),
    /// Agent 가 cloudinit 화이트리스트에 있는지 단순 membership 대조. device_id 는 비밀이 아닌 등록 화이트리스트.
    /// **null(미설정)이면 검증 생략** — localhost 올인원/로컬 개발은 인증 불필요(기존 동작 유지, 회귀 0).
    /// 설정돼 있고 미등록이면 OnConnectedAsync 가 연결을 Abort. Pi5 는 X-Device-Id 헤더로 시리얼을 싣는다
    /// (HubClientPusher 와 헤더 계약 일치). provision_token 은 부트스트랩 전용이라 상시 인증에서 제외.
    static member val ValidateDeviceCredential : Func<string, bool> = null with get, set

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
        log.Info("Health baseline freeze requested — broadcasting to all clients")
        this.Clients.All.SendAsync(HubMethod.OnHealthBaselineFreeze)

    /// 스캔 생존 heartbeat 리포트 (Client → Server). 수집 주체가 분리(Pi5 엣지 수집기)일 때 Pi5 가
    /// 클라이언트로서 호출 → Hub 가 OnScanHeartbeat 를 fan-out. 올인원의 BroadcastScanHeartbeat(server-origin)
    /// 와 최종 fan-out 지점(OnScanHeartbeat, Clients.All)이 동일하므로 소비자(Promaker/DSPilot)는 무영향.
    /// Clients.All 사용(BroadcastScanHeartbeat 와 동형) — Pi5 는 OnScanHeartbeat 를 구독하지 않아 자기 echo 무해.
    member this.ReportScanHeartbeat() : Task =
        this.Clients.All.SendAsync(HubMethod.OnScanHeartbeat)

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

    member private this.RejectRuntimeCommand(envelope: RuntimeCommandEnvelope, reason: string) =
        let payload = RuntimeCommandRejected.fromEnvelope reason envelope
        this.Clients.Caller.SendAsync(HubMethod.OnRuntimeCommandRejected, payload)

    member private this.ExecuteRuntimeCommand(envelope: RuntimeCommandEnvelope, execute: unit -> Task) : Task =
        task {
            match RuntimeSessionContract.tryRejectCommand runtimeSession.CurrentIdentity envelope with
            | Some reason ->
                do! this.RejectRuntimeCommand(envelope, reason)
            | None ->
                do! execute()
        } :> Task

    member private this.QueryRuntime(envelope: RuntimeCommandEnvelope, fallback: 'T, query: unit -> Task<'T>) : Task<'T> =
        task {
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
        if readOnlyMode then
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
                    // client write IN 도 engine 으로 forward (WriteTag 와 동일 이유 — VP IN echo 가 engine 에 반영).
                    let injectCmd : RuntimeIOAddressCommand =
                        { Envelope = RuntimeHubDefaults.selfEnvelope runtimeSession.CurrentIdentity
                          Address = it.Address; Value = it.Value }
                    runtimeSession.InjectIOValueByAddressAsync(injectCmd) |> ignore
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
            // 원격 수집 클라이언트 단말 신원 검증 (hook 미설정=localhost 올인원/로컬이면 생략).
            // 미등록 시 연결 Abort — 이후 snapshot 송출 스킵. Hub 앞단(연결 진입)에서 걸러 무효 연결을 조기 차단.
            // deviceId = RPi 시리얼(X-Device-Id) → 화이트리스트 membership 대조(단순). Bearer 토큰 대조 없음.
            let validator = SignalHub.ValidateDeviceCredential
            if not (isNull validator) then
                let mutable ok = false
                try
                    let http = this.Context.GetHttpContext()
                    if not (isNull http) then
                        let deviceId = http.Request.Headers.["X-Device-Id"].ToString()
                        ok <- validator.Invoke(deviceId)
                with ex ->
                    log.Warn($"Device credential validation threw: {ex.Message}")
                    ok <- false
                if not ok then
                    log.Warn($"SignalR connection rejected — unregistered device (connId={this.Context.ConnectionId})")
                    this.Context.Abort()
                    return ()
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
