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

        member _.BroadcastPlcConnectionStatus(status: PlcConnectionStatus) =
            // 캐시도 갱신 — 신규 클라이언트가 OnConnectedAsync 단계에서 최신 스냅샷을 수신.
            SignalHub.UpdatePlcStatusCache(status)
            hubContext.Clients.All.SendAsync(HubMethod.OnPlcConnectionStatus, status)

        member _.BroadcastAbnormal(payload: AbnormalPayload) =
            hubContext.Clients.All.SendAsync(HubMethod.OnAbnormal, payload)

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
