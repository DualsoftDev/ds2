namespace Ds2.Backend.Runtime

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR
open Ds2.Backend
open Ds2.Backend.Common
open Ds2.Core
open Ds2.Runtime.Engine
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Passive
open Ds2.Runtime.IO

/// IRuntimeHubSession 의 실제 구현 — Agent 가 보유한 ISimulationEngine 에 위임하고,
/// engine 이벤트를 IHubContext 로 OnRuntime* push 한다.
/// R1: 이 모듈만 Runtime + Backend 둘 다 참조한다. Ds2.Backend / Ds2.Runtime 은 서로 모른다.
type EventDrivenEngineRuntimeHubSession
    ( engine: ISimulationEngine,
      hub: IHubContext<SignalHub>,
      identity: RuntimeSessionIdentity ) =

    // ── 변환 헬퍼 ───────────────────────────────────────────────
    let gs (g: Guid) = g.ToString()
    let pg (s: string) = match Guid.TryParse s with | true, g -> g | _ -> Guid.Empty
    let st4Name (s: Status4) = string s
    let st4Val (s: Status4) = int s
    let flowName (t: FlowTag) = string t
    let flowVal (t: FlowTag) = int t
    let tokenInt (t: TokenValue) = match t with | IntToken n -> n
    let simStatusName (s: SimulationStatus) =
        match s with | Running -> "Running" | Paused -> "Paused" | Stopped -> "Stopped"
    let simStatusVal (s: SimulationStatus) =
        match s with | Running -> 0 | Paused -> 1 | Stopped -> 2

    // ── 이벤트 → 클라이언트 push (생성자에서 구독) ───────────────
    do
        engine.WorkStateChanged.Add(fun args ->
            let p : RuntimeWorkStateChangedPayload =
                { SessionId = identity.SessionId; ModelHash = identity.ModelHash
                  Generation = identity.Generation; Mode = identity.Mode
                  WorkId = gs args.WorkGuid; WorkName = args.WorkName
                  PreviousStatusName = st4Name args.PreviousState; PreviousStatusValue = st4Val args.PreviousState
                  NewStatusName = st4Name args.NewState; NewStatusValue = st4Val args.NewState
                  ClockMs = int64 args.Clock.TotalMilliseconds }
            hub.Clients.All.SendAsync(HubMethod.OnRuntimeWorkStateChanged, p) |> ignore)

        engine.CallStateChanged.Add(fun args ->
            let p : RuntimeCallStateChangedPayload =
                { SessionId = identity.SessionId; ModelHash = identity.ModelHash
                  Generation = identity.Generation; Mode = identity.Mode
                  CallId = gs args.CallGuid; CallName = args.CallName
                  PreviousStatusName = st4Name args.PreviousState; PreviousStatusValue = st4Val args.PreviousState
                  NewStatusName = st4Name args.NewState; NewStatusValue = st4Val args.NewState
                  IsSkipped = args.IsSkipped
                  ClockMs = int64 args.Clock.TotalMilliseconds }
            hub.Clients.All.SendAsync(HubMethod.OnRuntimeCallStateChanged, p) |> ignore)

        engine.SimulationStatusChanged.Add(fun args ->
            let p : RuntimeStatusChangedPayload =
                { SessionId = identity.SessionId; ModelHash = identity.ModelHash
                  Generation = identity.Generation; Mode = identity.Mode
                  PreviousStatusName = simStatusName args.PreviousStatus; PreviousStatusValue = simStatusVal args.PreviousStatus
                  NewStatusName = simStatusName args.NewStatus; NewStatusValue = simStatusVal args.NewStatus }
            hub.Clients.All.SendAsync(HubMethod.OnRuntimeStatusChanged, p) |> ignore)

        engine.TokenEvent.Add(fun args ->
            let p : RuntimeTokenEventPayload =
                { SessionId = identity.SessionId; ModelHash = identity.ModelHash
                  Generation = identity.Generation; Mode = identity.Mode
                  KindName = string args.Kind; KindValue = 0
                  TokenValue = tokenInt args.Token
                  WorkId = gs args.WorkGuid; WorkName = args.WorkName
                  TargetWorkId = (match args.TargetWorkGuid with Some g -> gs g | None -> "")
                  TargetWorkName = (match args.TargetWorkName with Some n -> n | None -> "")
                  ClockMs = int64 args.Clock.TotalMilliseconds }
            hub.Clients.All.SendAsync(HubMethod.OnRuntimeTokenEvent, p) |> ignore)

        engine.CallTimeout.Add(fun args ->
            let p : RuntimeCallTimeoutPayload =
                { SessionId = identity.SessionId; ModelHash = identity.ModelHash
                  Generation = identity.Generation; Mode = identity.Mode
                  CallId = gs args.CallGuid; CallName = args.CallName
                  TimeoutMs = args.TimeoutMs
                  ClockMs = int64 args.Clock.TotalMilliseconds }
            hub.Clients.All.SendAsync(HubMethod.OnRuntimeCallTimeout, p) |> ignore)

        engine.HomingPhaseCompleted.Add(fun _ ->
            let p : RuntimeHomingPhaseCompletedPayload =
                { SessionId = identity.SessionId; ModelHash = identity.ModelHash
                  Generation = identity.Generation; Mode = identity.Mode
                  TimestampUtc = DateTime.UtcNow }
            hub.Clients.All.SendAsync(HubMethod.OnRuntimeHomingPhaseCompleted, p) |> ignore)

        // v12 P5 — engine 이 Agent 한 곳에 단일 호스팅되므로 abnormal 도 여기서 단일 발행한다.
        // Control/Monitoring 이 각자 engine 을 돌려 같은 이상을 중복 발행하던 문제의 근본 해소.
        // client 는 OnAbnormal 한 번만 받으므로 dedup 불필요.
        engine.AbnormalDetected.Add(fun record ->
            let gOpt (o: Guid option) = match o with | Some g -> string g | None -> ""
            let isSensor = int record.Kind <= 1   // SensorOpen=0 / SensorShort=1
            let p : AbnormalPayload =
                { Kind = string record.Kind
                  KindValue = int record.Kind
                  CallId = gOpt record.Target.CallId
                  ApiCallId = gOpt record.Target.ApiCallId
                  WorkId = gOpt record.Target.WorkId
                  ElapsedMs = (match record.ElapsedMs with | Some n -> n | None -> -1)
                  Observed = (match record.Observed with | Some b -> b | None -> false)
                  Mode = identity.Mode.ToLowerInvariant()
                  Source = identity.Mode.ToLowerInvariant()
                  TimestampUtc = record.TimestampUtc }
            hub.Clients.All.SendAsync(HubMethod.OnAbnormal, p) |> ignore)

    // ── stale command guard ─────────────────────────────────────
    let allow (env: RuntimeCommandEnvelope) : bool =
        match RuntimeSessionContract.tryRejectCommand identity env with
        | Some reason ->
            let payload = RuntimeCommandRejected.fromEnvelope reason env
            hub.Clients.All.SendAsync(HubMethod.OnRuntimeCommandRejected, payload) |> ignore
            false
        | None -> true

    // ── Monitoring passive inference ─────────────────────────────
    // Monitoring/VP engine 은 passive(조건평가 OFF) — IO 만 넣으면 상태 전이가 안 일어난다.
    // RuntimeModeSession.HandleHubTag → effect → PassiveInferenceSession.Observe 로 Work/Call 추론해 engine 에 Force.
    // (기존 DSPilot SimulationEngineService.ApplySingleEffect/ObserveAndInferPassiveState 와 동일 의미 — P4 에서 공용화.)
    let runtimeMode =
        match identity.Mode with
        | "Control" -> RuntimeMode.Control
        | "Monitoring" -> RuntimeMode.Monitoring
        | "VirtualPlant" -> RuntimeMode.VirtualPlant
        | _ -> RuntimeMode.Simulation
    let modeSession = RuntimeModeSession(engine.Index, engine.IOMap, runtimeMode)
    let passiveInference =
        if modeSession.RequiresPassiveInference then
            Some(PassiveInferenceSession(engine.Index, engine.IOMap, runtimeMode))
        else None
    let getWorkStateSafe =
        Func<Guid, Status4>(fun g -> match engine.GetWorkState(g) with Some s -> s | None -> Status4.Ready)
    let getCallStateSafe =
        Func<Guid, Status4>(fun g -> match engine.GetCallState(g) with Some s -> s | None -> Status4.Ready)
    let observeAndInfer (address: string) (value: string) =
        match passiveInference with
        | Some pi ->
            for action in pi.Observe(address, value, getWorkStateSafe, getCallStateSafe) do
                match action.TargetKind with
                | PassiveInferenceTarget.Work ->
                    if getWorkStateSafe.Invoke(action.TargetGuid) <> action.State then
                        engine.ForceWorkState(action.TargetGuid, action.State)
                | PassiveInferenceTarget.Call ->
                    if getCallStateSafe.Invoke(action.TargetGuid) <> action.State then
                        engine.ForceCallState(action.TargetGuid, action.State)
                | _ -> ()
            pi.DrainLogs() |> ignore
        | None -> ()
    let applyEffect (effect: RuntimeHubEffect) =
        match effect.Kind with
        | RuntimeHubEffectKind.InjectIoByAddress -> engine.InjectIOValueByAddress(effect.Address, effect.Value) |> ignore
        | RuntimeHubEffectKind.ForceWorkState ->
            if effect.WorkGuid <> Guid.Empty then engine.ForceWorkState(effect.WorkGuid, effect.State)
        | RuntimeHubEffectKind.ForceWorkStateIfGoing ->
            if effect.WorkGuid <> Guid.Empty then engine.TryForceWorkStateIfGoing(effect.WorkGuid, effect.State)
        | RuntimeHubEffectKind.PassiveObserve -> observeAndInfer effect.Address effect.Value
        | RuntimeHubEffectKind.Log -> ()       // 진단 로그 — Agent log4net 노이즈 회피로 생략
        | RuntimeHubEffectKind.WriteTag -> ()  // Monitoring read-only — PLC 재기록 안 함 (Control write 는 P4)
        | _ -> ()

    let guidStatus (id: string) (s: Status4) : RuntimeGuidStatus =
        { Id = id; StatusName = st4Name s; StatusValue = st4Val s }

    interface IRuntimeHubSession with
        member _.CurrentIdentity = identity

        member _.StartAsync cmd =
            if allow cmd.Envelope then engine.Start()
            Task.CompletedTask
        member _.PauseAsync cmd =
            if allow cmd.Envelope then engine.Pause()
            Task.CompletedTask
        member _.ResumeAsync cmd =
            if allow cmd.Envelope then engine.Resume()
            Task.CompletedTask
        member _.StopAsync cmd =
            if allow cmd.Envelope then engine.Stop()
            Task.CompletedTask
        member _.ResetAsync cmd =
            if allow cmd.Envelope then engine.Reset()
            Task.CompletedTask
        member _.ApplyInitialStatesAsync cmd =
            if allow cmd.Envelope then engine.ApplyInitialStates()
            Task.CompletedTask

        member _.StepAsync cmd =
            if allow cmd.Envelope then engine.Step() |> ignore
            Task.CompletedTask
        member _.CanAdvanceStepAsync cmd =
            if allow cmd.Envelope then
                Task.FromResult(engine.CanAdvanceStep(pg cmd.SelectedSourceWorkId, cmd.AutoStartSources))
            else Task.FromResult false
        member _.StepWithSourcePrimingAsync cmd =
            if allow cmd.Envelope then
                engine.StepWithSourcePriming(pg cmd.SelectedSourceWorkId, cmd.AutoStartSources) |> ignore
            Task.CompletedTask
        // TODO: RuntimeStepBatchCommand(BatchIds) ↔ engine.BeginStepBatch(source,auto)->Guid[] 시그니처 불일치.
        member _.BeginStepBatchAsync _ = Task.CompletedTask
        // TODO: engine.IsStepBatchActive(Guid[]) 인자 필요한데 RuntimeEmptyCommand 엔 batch 없음.
        member _.IsStepBatchActiveAsync _ = Task.FromResult false
        member _.EndStepAsync cmd =
            if allow cmd.Envelope then engine.EndStep()
            Task.CompletedTask
        member _.AdvanceSimulationToAsync cmd =
            if allow cmd.Envelope then engine.AdvanceSimulationTo(cmd.TargetTimeMs)
            Task.CompletedTask

        member _.ForceWorkStateAsync cmd =
            if allow cmd.Envelope then engine.ForceWorkState(pg cmd.WorkId, enum<Status4> cmd.StatusValue)
            Task.CompletedTask
        member _.ForceCallStateAsync cmd =
            if allow cmd.Envelope then engine.ForceCallState(pg cmd.CallId, enum<Status4> cmd.StatusValue)
            Task.CompletedTask
        member _.TryForceWorkStateIfGoingAsync cmd =
            if allow cmd.Envelope then
                engine.TryForceWorkStateIfGoing(pg cmd.WorkId, enum<Status4> cmd.StatusValue)
                Task.FromResult true
            else Task.FromResult false

        member _.GetWorkStateAsync cmd =
            match engine.GetWorkState(pg cmd.WorkId) with
            | Some s -> Task.FromResult(guidStatus cmd.WorkId s)
            | None -> Task.FromResult({ Id = cmd.WorkId; StatusName = ""; StatusValue = 0 })
        member _.GetCallStateAsync cmd =
            match engine.GetCallState(pg cmd.CallId) with
            | Some s -> Task.FromResult(guidStatus cmd.CallId s)
            | None -> Task.FromResult({ Id = cmd.CallId; StatusName = ""; StatusValue = 0 })
        // TODO: RuntimeFlowTagCommand 엔 flow Guid 가 없어 engine.GetFlowState(Guid) 를 못 부른다. command 보강 필요.
        member _.GetFlowStateAsync cmd =
            Task.FromResult({ Id = cmd.FlowTagName; FlowTagName = ""; FlowTagValue = 0 })

        member _.SeedTokenAsync cmd =
            if allow cmd.Envelope then engine.SeedToken(pg cmd.WorkId, IntToken cmd.TokenValue)
            Task.CompletedTask
        member _.StartSourceWorkAsync cmd =
            if allow cmd.Envelope then engine.StartSourceWork(pg cmd.WorkId)
            Task.CompletedTask
        member _.DiscardTokenAsync cmd =
            if allow cmd.Envelope then engine.DiscardToken(pg cmd.WorkId)
            Task.CompletedTask

        member _.InjectIOValueAsync cmd =
            if allow cmd.Envelope then engine.InjectIOValue(pg cmd.ApiCallId, cmd.Value)
            Task.CompletedTask
        member _.InjectIOValueByAddressAsync cmd =
            if allow cmd.Envelope then
                // PLC IN → mode session effect → engine 적용.
                // Monitoring: passive 추론으로 Work/Call Force (engine 자체 조건평가는 OFF).
                for effect in modeSession.HandleHubTag(cmd.Address, cmd.Value, "plc") do
                    applyEffect effect
            Task.CompletedTask
        member _.SetAllFlowStatesAsync cmd =
            if allow cmd.Envelope then engine.SetAllFlowStates(enum<FlowTag> cmd.FlowTagValue)
            Task.CompletedTask
        member _.ReloadConnectionsAsync cmd =
            if allow cmd.Envelope then engine.ReloadConnections()
            Task.CompletedTask
        member _.ReloadDurationsAsync cmd =
            if allow cmd.Envelope then engine.ReloadDurations()
            Task.CompletedTask
        member _.StartWithHomingPhaseAsync cmd =
            if allow cmd.Envelope then engine.StartWithHomingPhase() |> ignore
            Task.CompletedTask

        member _.GetWorkTokenAsync cmd =
            match engine.GetWorkToken(pg cmd.WorkId) with
            | Some t -> Task.FromResult(tokenInt t)
            | None -> Task.FromResult(-1)
        member _.GetTokenOriginAsync cmd =
            match engine.GetTokenOrigin(IntToken cmd.TokenValue) with
            | Some (name, seq) -> Task.FromResult(sprintf "%s:%d" name seq)
            | None -> Task.FromResult("")

        member _.GetSnapshotAsync _ =
            let stt = engine.State
            let snapshot : RuntimeStateSnapshot =
                { SessionId = identity.SessionId; ModelHash = identity.ModelHash
                  Generation = identity.Generation; Mode = identity.Mode
                  StatusName = simStatusName engine.Status; StatusValue = simStatusVal engine.Status
                  ClockMs = int64 stt.Clock.TotalMilliseconds
                  CurrentTimeMs = engine.CurrentTimeMs
                  NextEventTimeMs = (match engine.NextEventTimeMs with Some v -> Nullable v | None -> Nullable())
                  WorkStates = stt.WorkStates |> Map.toArray |> Array.map (fun (g, s) -> guidStatus (gs g) s)
                  CallStates = stt.CallStates |> Map.toArray |> Array.map (fun (g, s) -> guidStatus (gs g) s)
                  FlowStates = stt.FlowStates |> Map.toArray |> Array.map (fun (g, t) -> { Id = gs g; FlowTagName = flowName t; FlowTagValue = flowVal t })
                  IOValues = stt.IOValues |> Map.toArray |> Array.map (fun (g, v) -> { Id = gs g; Value = v })
                  HasStartableWork = engine.HasStartableWork
                  HasActiveDuration = engine.HasActiveDuration
                  IsHomingPhase = engine.IsHomingPhase
                  TimestampUtc = DateTime.UtcNow }
            Task.FromResult snapshot

        member _.GetIndexProjectionAsync _ =
            let idx = engine.Index
            let proj : RuntimeIndexProjection =
                { SessionId = identity.SessionId; ModelHash = identity.ModelHash
                  Generation = identity.Generation; Mode = identity.Mode
                  WorkNames = idx.WorkName |> Map.toArray |> Array.map (fun (g, n) -> { Id = gs g; Name = n })
                  WorkSystemNames = idx.WorkSystemName |> Map.toArray |> Array.map (fun (g, n) -> { Id = gs g; Name = n })
                  WorkFlowGuids = idx.WorkFlowGuid |> Map.toArray |> Array.map (fun (g, f) -> { Id = gs g; RefId = gs f })
                  CallWorkGuids = idx.CallWorkGuid |> Map.toArray |> Array.map (fun (c, w) -> { Id = gs c; RefId = gs w })
                  WorkCallGuids = idx.WorkCallGuids |> Map.toArray |> Array.map (fun (w, cs) -> { Id = gs w; Values = cs |> List.map gs |> Array.ofList })
                  TokenSourceGuids = idx.TokenSourceGuids |> List.map gs |> Array.ofList
                  TokenSinkGuids = idx.TokenSinkGuids |> Set.toArray |> Array.map gs }
            Task.FromResult proj

        member _.GetIOMapProjectionAsync _ =
            let m = engine.IOMap
            let proj : RuntimeIOMapProjection =
                { SessionId = identity.SessionId; ModelHash = identity.ModelHash
                  Generation = identity.Generation; Mode = identity.Mode
                  OutAddresses = m.OutAddressToMappings |> Map.toArray |> Array.map fst
                  InAddresses = m.InAddressToMappings |> Map.toArray |> Array.map fst
                  Mappings = m.Mappings |> List.map (fun sm ->
                      { ApiCallId = gs sm.ApiCallGuid; CallId = gs sm.CallGuid
                        TxWorkId = (match sm.TxWorkGuid with Some g -> gs g | None -> "")
                        RxWorkId = (match sm.RxWorkGuid with Some g -> gs g | None -> "")
                        OutAddress = sm.OutAddress; InAddress = sm.InAddress }) |> Array.ofList }
            Task.FromResult proj
