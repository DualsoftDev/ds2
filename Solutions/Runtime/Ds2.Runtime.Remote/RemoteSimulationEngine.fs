namespace Ds2.Runtime.Remote

open System
open Microsoft.AspNetCore.SignalR.Client
open Ds2.Core
open Ds2.Backend.Common
open Ds2.Runtime.Model
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO

/// Agent 가 단일 호스팅하는 server engine 의 client-side 원격 proxy.
/// - 명령: SignalR Send/Invoke (RuntimeCommandEnvelope 동반 — stale guard).
/// - 이벤트: On<Payload> 구독 → 로컬 SimState push-cache 갱신 + 로컬 Event 재발행 (UI 무수정).
/// - State/Status/CurrentTimeMs 등: push-cache (read round-trip 금지 — plan R3/§5-2).
/// - Index/IOMap: client 가 자기 store 로 로컬 build 한 값 (SimIndex 가 Store 를 품어 직렬화 불가 — R3).
///   같은 AASX → 같은 store → 동일 index, ModelHash 로 server 와 동일성 보장.
type RemoteSimulationEngine
    ( hub: HubConnection,
      index: SimIndex,
      ioMap: SignalIOMap,
      identity: RuntimeSessionIdentity ) =

    static let log = log4net.LogManager.GetLogger("RemoteSimulationEngine")

    // ── push-cache 상태 ─────────────────────────────────────────
    let stateLock = obj()
    let mutable state =
        SimState.create index.TickMs index.AllWorkGuids index.AllCallGuids index.AllFlowGuids
    let mutable status = Stopped
    let mutable currentTimeMs = 0L
    let mutable nextEventTimeMs : int64 option = None
    let mutable isHomingPhase = false
    let mutable hasStartableWork = false
    let mutable hasActiveDuration = false
    let mutable speedMultiplier = 1.0
    let mutable timeIgnore = false
    // server identity 의 SessionId/Generation 은 연결 후 첫 snapshot 에서 동기화한다 (client 는 사전에 모름).
    // ModelHash/Mode 는 client 가 로컬 AASX·모드로 재현하므로 생성자 identity 값을 신뢰한다.
    let mutable serverSessionId = identity.SessionId
    let mutable serverGeneration = identity.Generation

    // ── 로컬 이벤트 (server push → 재발행) ───────────────────────
    let workStateChangedEvent = Event<WorkStateChangedArgs>()
    let callStateChangedEvent = Event<CallStateChangedArgs>()
    let simulationStatusChangedEvent = Event<SimulationStatusChangedArgs>()
    let tokenEventEvent = Event<TokenEventArgs>()
    let callTimeoutEvent = Event<CallTimeoutArgs>()
    let homingPhaseCompletedEvent = Event<EventArgs>()
    let abnormalDetectedEvent = Event<AbnormalRecord>()

    // ── 변환 헬퍼 ───────────────────────────────────────────────
    let pg (s: string) = match Guid.TryParse s with | true, g -> g | _ -> Guid.Empty
    let st4 (v: int) = enum<Status4> v
    let flowTagOf (v: int) = enum<FlowTag> v
    let clockOf (ms: int64) = TimeSpan.FromMilliseconds(float ms)
    let simStatusOf (name: string) =
        match name with | "Running" -> Running | "Paused" -> Paused | _ -> Stopped
    let tokenKindOf (name: string) =
        match name with
        | "Seed" -> Seed | "Shift" -> Shift | "Complete" -> Complete
        | "Blocked" -> Blocked | "Discard" -> Discard
        | "BlockedOnHoming" -> BlockedOnHoming | "Conflict" -> Conflict
        | _ -> Shift
    let optGuid (s: string) = if String.IsNullOrEmpty s then None else Some(pg s)
    let optName (s: string) = if String.IsNullOrEmpty s then None else Some s

    // push 수용 판정 — 같은 모델(ModelHash) + 같은 모드면 수용. SessionId/Generation 은 snapshot 으로 동기화.
    let matches (modelHash: string) (mode: string) =
        modelHash = identity.ModelHash && mode = identity.Mode

    // ── command envelope / 빌더 ─────────────────────────────────
    let envelope () : RuntimeCommandEnvelope =
        { SessionId = serverSessionId; ModelHash = identity.ModelHash
          Generation = serverGeneration; Mode = identity.Mode
          CommandId = Guid.NewGuid().ToString("N") }
    let emptyCmd () : RuntimeEmptyCommand = { Envelope = envelope () }
    let stepPolicyCmd (g: Guid) (auto: bool) : RuntimeStepPolicyCommand =
        { Envelope = envelope (); SelectedSourceWorkId = string g; AutoStartSources = auto }
    let workCmd (g: Guid) : RuntimeWorkCommand = { Envelope = envelope (); WorkId = string g }
    let workStateCmd (g: Guid) (s: Status4) : RuntimeWorkStateCommand =
        { Envelope = envelope (); WorkId = string g; StatusName = string s; StatusValue = int s }
    let callStateCmd (g: Guid) (s: Status4) : RuntimeCallStateCommand =
        { Envelope = envelope (); CallId = string g; StatusName = string s; StatusValue = int s }
    let flowTagCmd (t: FlowTag) : RuntimeFlowTagCommand =
        { Envelope = envelope (); FlowTagName = string t; FlowTagValue = int t }
    let ioValueCmd (g: Guid) (v: string) : RuntimeIOValueCommand =
        { Envelope = envelope (); ApiCallId = string g; Value = v }
    let ioAddressCmd (a: string) (v: string) : RuntimeIOAddressCommand =
        { Envelope = envelope (); Address = a; Value = v }
    let tokenCmd (g: Guid) (tv: int) : RuntimeTokenCommand =
        { Envelope = envelope (); WorkId = string g; TokenValue = tv }
    let advanceCmd (t: int64) : RuntimeAdvanceSimulationCommand =
        { Envelope = envelope (); TargetTimeMs = t }
    let stepBatchCmd (batch: Guid[]) : RuntimeStepBatchCommand =
        { Envelope = envelope (); BatchIds = batch |> Array.map string }

    // ── SignalR 송신 (명령 fire-and-forget / 결과형 invoke) ──────
    let send (m: string) (arg: obj) =
        try hub.SendAsync(m, arg) |> ignore
        with ex -> log.Warn(sprintf "%s send 실패: %s" m ex.Message)
    let invokeBool (m: string) (arg: obj) : bool =
        try hub.InvokeAsync<bool>(m, arg).GetAwaiter().GetResult()
        with ex -> log.Warn(sprintf "%s invoke 실패: %s" m ex.Message); false
    let invokeInt (m: string) (arg: obj) : int =
        try hub.InvokeAsync<int>(m, arg).GetAwaiter().GetResult()
        with ex -> log.Warn(sprintf "%s invoke 실패: %s" m ex.Message); -1
    let invokeStrings (m: string) (arg: obj) : string[] =
        try hub.InvokeAsync<string[]>(m, arg).GetAwaiter().GetResult()
        with ex -> log.Warn(sprintf "%s invoke 실패: %s" m ex.Message); [||]

    // ── server push 구독 (생성자) ───────────────────────────────
    do
        hub.On(HubMethod.OnRuntimeSnapshot, Action<RuntimeStateSnapshot>(fun snap ->
            if matches snap.ModelHash snap.Mode then
                serverSessionId <- snap.SessionId
                serverGeneration <- snap.Generation
                lock stateLock (fun () ->
                    let mutable s =
                        SimState.create index.TickMs index.AllWorkGuids index.AllCallGuids index.AllFlowGuids
                    for ws in snap.WorkStates do s <- SimState.setWorkState (pg ws.Id) (st4 ws.StatusValue) s
                    for cs in snap.CallStates do s <- SimState.setCallState (pg cs.Id) (st4 cs.StatusValue) s
                    for fs in snap.FlowStates do s <- SimState.setFlowState (pg fs.Id) (flowTagOf fs.FlowTagValue) s
                    for io in snap.IOValues do s <- SimState.setIOValue (pg io.Id) io.Value s
                    state <- s)
                status <- simStatusOf snap.StatusName
                currentTimeMs <- snap.CurrentTimeMs
                nextEventTimeMs <- (if snap.NextEventTimeMs.HasValue then Some snap.NextEventTimeMs.Value else None)
                isHomingPhase <- snap.IsHomingPhase
                hasStartableWork <- snap.HasStartableWork
                hasActiveDuration <- snap.HasActiveDuration)) |> ignore

        hub.On(HubMethod.OnRuntimeWorkStateChanged, Action<RuntimeWorkStateChangedPayload>(fun p ->
            if matches p.ModelHash p.Mode then
                let g = pg p.WorkId
                lock stateLock (fun () -> state <- SimState.setWorkState g (st4 p.NewStatusValue) state)
                currentTimeMs <- max currentTimeMs p.ClockMs
                workStateChangedEvent.Trigger(
                    { WorkGuid = g; WorkName = p.WorkName
                      PreviousState = st4 p.PreviousStatusValue; NewState = st4 p.NewStatusValue
                      Clock = clockOf p.ClockMs }))) |> ignore

        hub.On(HubMethod.OnRuntimeCallStateChanged, Action<RuntimeCallStateChangedPayload>(fun p ->
            if matches p.ModelHash p.Mode then
                let g = pg p.CallId
                lock stateLock (fun () -> state <- SimState.setCallState g (st4 p.NewStatusValue) state)
                currentTimeMs <- max currentTimeMs p.ClockMs
                callStateChangedEvent.Trigger(
                    { CallGuid = g; CallName = p.CallName
                      PreviousState = st4 p.PreviousStatusValue; NewState = st4 p.NewStatusValue
                      IsSkipped = p.IsSkipped; Clock = clockOf p.ClockMs }))) |> ignore

        hub.On(HubMethod.OnRuntimeStatusChanged, Action<RuntimeStatusChangedPayload>(fun p ->
            if matches p.ModelHash p.Mode then
                let prev = simStatusOf p.PreviousStatusName
                let next = simStatusOf p.NewStatusName
                status <- next
                simulationStatusChangedEvent.Trigger({ PreviousStatus = prev; NewStatus = next }))) |> ignore

        hub.On(HubMethod.OnRuntimeTokenEvent, Action<RuntimeTokenEventPayload>(fun p ->
            if matches p.ModelHash p.Mode then
                currentTimeMs <- max currentTimeMs p.ClockMs
                tokenEventEvent.Trigger(
                    { Kind = tokenKindOf p.KindName; Token = IntToken p.TokenValue
                      WorkGuid = pg p.WorkId; WorkName = p.WorkName
                      TargetWorkGuid = optGuid p.TargetWorkId; TargetWorkName = optName p.TargetWorkName
                      Clock = clockOf p.ClockMs }))) |> ignore

        hub.On(HubMethod.OnRuntimeCallTimeout, Action<RuntimeCallTimeoutPayload>(fun p ->
            if matches p.ModelHash p.Mode then
                callTimeoutEvent.Trigger(
                    { CallGuid = pg p.CallId; CallName = p.CallName
                      TimeoutMs = p.TimeoutMs; Clock = clockOf p.ClockMs }))) |> ignore

        hub.On(HubMethod.OnRuntimeHomingPhaseCompleted, Action<RuntimeHomingPhaseCompletedPayload>(fun p ->
            if matches p.ModelHash p.Mode then
                isHomingPhase <- false
                homingPhaseCompletedEvent.Trigger(EventArgs.Empty))) |> ignore

        // v12 P5 — Agent engine 이 단일 발행한 abnormal 을 받아 AbnormalRecord 로 재구성 → 로컬 AbnormalDetected 재발행.
        // 단일 Agent engine 발행이라 dedup 불필요. AbnormalPayload 엔 model/session 식별이 없어 mode 매칭 없이 수용.
        hub.On(HubMethod.OnAbnormal, Action<AbnormalPayload>(fun p ->
            let pgOpt (s: string) = if String.IsNullOrEmpty s then None else Some(pg s)
            let isSensor = p.KindValue <= 1   // SensorOpen=0 / SensorShort=1
            let target : AbnormalTarget =
                { CallId = pgOpt p.CallId; ApiCallId = pgOpt p.ApiCallId; WorkId = pgOpt p.WorkId }
            let record : AbnormalRecord =
                { Kind = enum<AbnormalKind> p.KindValue
                  Target = target
                  TimestampUtc = p.TimestampUtc
                  ElapsedMs = (if isSensor then None else Some p.ElapsedMs)
                  Observed = (if isSensor then Some p.Observed else None) }
            abnormalDetectedEvent.Trigger(record))) |> ignore

    interface ISimulationEngine with
        // ── 상태 조회 (push-cache) ──
        // SimState.create 의 Clock 은 Zero 이고 setWorkState/setCallState 는 Clock 을 갱신하지 않는다.
        // 진행 시각은 server push 가 currentTimeMs 로만 누적하므로, State.Clock 을 currentTimeMs 로
        // 투영해 노출한다. 이렇게 안 하면 proxy(Control+실PLC / Monitoring) 의 State.Clock 이 0 에 고정돼
        // SimClock 표시·간트 I/O 타임스탬프가 모두 _simStartTime 한 점으로 붕괴한다.
        member _.State = lock stateLock (fun () -> { state with Clock = clockOf currentTimeMs })
        member _.Status = status
        member _.Index = index
        member _.IOMap = ioMap

        // ── 수명 주기 (명령) ──
        member _.Start() = send HubMethod.RuntimeStart (emptyCmd ())
        member _.Pause() = send HubMethod.RuntimePause (emptyCmd ())
        member _.Resume() = send HubMethod.RuntimeResume (emptyCmd ())
        member _.Stop() = send HubMethod.RuntimeStop (emptyCmd ())
        member _.Reset() = send HubMethod.RuntimeReset (emptyCmd ())
        member _.ApplyInitialStates() = send HubMethod.RuntimeApplyInitialStates (emptyCmd ())

        // ── 상태 제어 (명령) ──
        member _.ForceWorkState(g, s) = send HubMethod.RuntimeForceWorkState (workStateCmd g s)
        member _.ForceCallState(g, s) = send HubMethod.RuntimeForceCallState (callStateCmd g s)
        member _.TryForceWorkStateIfGoing(g, s) =
            send HubMethod.RuntimeTryForceWorkStateIfGoing (workStateCmd g s)
        member _.TryForceWorkStateIfReady(g, s) =
            send HubMethod.RuntimeTryForceWorkStateIfReady (workStateCmd g s)
        member _.GetWorkState(g) = lock stateLock (fun () -> state.WorkStates.TryFind g)
        member _.GetCallState(g) = lock stateLock (fun () -> state.CallStates.TryFind g)

        // ── Flow ──
        member _.SetAllFlowStates(tag) = send HubMethod.RuntimeSetAllFlowStates (flowTagCmd tag)
        member _.GetFlowState(g) =
            lock stateLock (fun () -> state.FlowStates.TryFind g |> Option.defaultValue FlowTag.Ready)

        // ── 진행 가능성 (push-cache) ──
        member _.HasStartableWork = hasStartableWork
        member _.HasActiveDuration = hasActiveDuration

        // ── STEP (결과형 invoke — round-trip, STEP UI 한정) ──
        member _.Step() = invokeBool HubMethod.RuntimeStep (emptyCmd ())
        member _.CanAdvanceStep(g, auto) = invokeBool HubMethod.RuntimeCanAdvanceStep (stepPolicyCmd g auto)
        member _.StepWithSourcePriming(g, auto) =
            invokeBool HubMethod.RuntimeStepWithSourcePriming (stepPolicyCmd g auto)
        member _.BeginStepBatch(g, auto) =
            invokeStrings HubMethod.RuntimeBeginStepBatch (stepPolicyCmd g auto) |> Array.map pg
        member _.IsStepBatchActive(batch) =
            invokeBool HubMethod.RuntimeIsStepBatchActive (stepBatchCmd batch)
        member _.AdvanceSimulationTo(t) = send HubMethod.RuntimeAdvanceSimulationTo (advanceCmd t)
        member _.EndStep() = send HubMethod.RuntimeEndStep (emptyCmd ())

        // ── 재계산 (명령) ──
        member _.ReloadConnections() = send HubMethod.RuntimeReloadConnections (emptyCmd ())
        member _.ReloadDurations() = send HubMethod.RuntimeReloadDurations (emptyCmd ())

        // ── 시계 (push-cache) ──
        member _.CurrentTimeMs = currentTimeMs
        member _.NextEventTimeMs = nextEventTimeMs

        // ── 설정 (로컬 — server 명령 계약 없음) ──
        member _.SpeedMultiplier
            with get () = speedMultiplier
            and set (v) = speedMultiplier <- max 0.1 (min 1000.0 v)
        member _.TimeIgnore
            with get () = timeIgnore
            and set (v) = timeIgnore <- v

        // ── 외부 신호 (명령) ──
        member _.InjectIOValue(g, v) = send HubMethod.RuntimeInjectIOValue (ioValueCmd g v)
        member _.InjectIOValueByAddress(a, v) =
            send HubMethod.RuntimeInjectIOValueByAddress (ioAddressCmd a v)
            true

        // ── 토큰 ──
        member _.NextToken() =
            lock stateLock (fun () ->
                let token, s = SimState.nextToken state
                state <- s
                token)
        member _.SeedToken(g, value) =
            let tv = match value with | IntToken n -> n
            send HubMethod.RuntimeSeedToken (tokenCmd g tv)
        member _.StartSourceWork(g) = send HubMethod.RuntimeStartSourceWork (workCmd g)
        member _.DiscardToken(g) = send HubMethod.RuntimeDiscardToken (tokenCmd g 0)
        member _.GetWorkToken(g) =
            match invokeInt HubMethod.RuntimeGetWorkToken (workCmd g) with
            | r when r >= 0 -> Some(IntToken r)
            | _ -> None
        member _.GetTokenOrigin(_) = None  // round-trip 미사용 (Monitoring UI 비소비). 필요 시 P4 에서 보강.

        // ── 원위치 페이즈 ──
        member _.StartWithHomingPhase() = invokeBool HubMethod.RuntimeStartWithHomingPhase (emptyCmd ())
        member _.IsHomingPhase = isHomingPhase

        // ── 이벤트 ──
        [<CLIEvent>] member _.WorkStateChanged = workStateChangedEvent.Publish
        [<CLIEvent>] member _.CallStateChanged = callStateChangedEvent.Publish
        [<CLIEvent>] member _.SimulationStatusChanged = simulationStatusChangedEvent.Publish
        [<CLIEvent>] member _.TokenEvent = tokenEventEvent.Publish
        [<CLIEvent>] member _.CallTimeout = callTimeoutEvent.Publish
        [<CLIEvent>] member _.HomingPhaseCompleted = homingPhaseCompletedEvent.Publish
        [<CLIEvent>] member _.AbnormalDetected = abnormalDetectedEvent.Publish

        member _.Dispose() = ()  // HubConnection lifecycle 은 주입한 client 가 소유 — proxy 는 닫지 않는다.
