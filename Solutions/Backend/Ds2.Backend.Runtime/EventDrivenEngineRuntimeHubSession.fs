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
open Ds2.Runtime.Engine.Abnormal
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

    // v12 V12 — Monitoring 은 abnormal 소스가 둘(engine onDeviceDurationExpired tick + MonitoringAbnormalAdapter
    //   In rising)이고 각자 다른 latch 라 같은 (Kind,Target) ActionOver 가 중복 broadcast 될 수 있다. 합류점인
    //   여기서 단일 dedup(DefaultLatchPolicy 5s window). Control 은 adapter 가 OnCallReset 으로 사이클별 정확히
    //   관리하므로 추가 dedup 을 하지 않는다(사이클마다의 정상 over 가 5s window 에 억제되는 회귀 방지).
    let abnormalIsMonitoring = identity.Mode.Equals("monitoring", StringComparison.OrdinalIgnoreCase)
    let abnormalDedupPolicy : ILatchPolicy = DefaultLatchPolicy()
    let abnormalDedupLock = obj ()
    let mutable abnormalLastEmitted : Map<AbnormalLatchKey, AbnormalRecord> = Map.empty
    let passiveLog = log4net.LogManager.GetLogger("PassiveInference")

    // ── 통신 blackout (PLC 단절 → 사이클 무효화) ────────────────────────────
    // PLC 단절~재연결 구간은 신호 순서/edge 신뢰가 없다(누락 edge 가 재연결 후 burst 로 보이면
    // 가짜 SensorShort/ActionOver 발생). 상태머신:
    //   NORMAL ─(down 전이)→ BLACKOUT(전면 억제+관측 무효화)
    //          ─(게이트웨이 resync baseline 배치 도착)→ REARMING(Call 별 새 OUT rising 까지 억제)
    //          ─(전 Call 재무장 또는 타임박스)→ NORMAL
    // 어댑터 구분 없이 전역 blackout(현장 대부분 PLC 1대 — 부분화는 멀티 PLC 요구 시 후속).
    // Monitoring 전용 — Control 은 단절 시 제어 불능이 더 큰 이슈라 후속(비범위).
    let blackoutLock = obj ()
    let mutable commBlackout = false
    let mutable rearming = false
    let mutable rearmStartedUtc = DateTime.MinValue
    let rearmedCalls = System.Collections.Generic.HashSet<Guid>()
    let rearmTimebox = TimeSpan.FromMinutes 5.0
    let totalMappedCalls =
        engine.IOMap.Mappings |> List.map (fun m -> m.CallGuid) |> List.distinct |> List.length

    /// blackout/REARMING 중 발행 억제 여부. REARMING 은 타임박스 도달 시 자동 종료
    /// (정지 라인의 Call 은 OUT rising 이 영영 없을 수 있다 — 억제 영구화 방지 겸 가시화).
    let isSuppressedByBlackout (record: AbnormalRecord) =
        if not commBlackout && not rearming then false
        else
            lock blackoutLock (fun () ->
                if commBlackout then true
                elif rearming then
                    if DateTime.UtcNow - rearmStartedUtc > rearmTimebox then
                        rearming <- false
                        passiveLog.Info("[CommBlackout] re-arm timebox expired — abnormal evaluation fully resumed")
                        false
                    else
                        match record.Target.CallId with
                        | Some callId -> not (rearmedCalls.Contains callId)
                        | None -> true   // 타깃 Call 미상 = 재무장 확인 불가 — REARMING 동안은 drop
                else false)

    // v12 P5 — abnormal → OnAbnormal broadcast. Control engine 발행 / Monitoring adapter sink 공용.
    let broadcastAbnormal (record: AbnormalRecord) =
        if isSuppressedByBlackout record then
            passiveLog.Info($"[CommBlackout] abnormal suppressed: {record.Kind} call={record.Target.CallId}")
        else
            let pass =
                if abnormalIsMonitoring then
                    lock abnormalDedupLock (fun () ->
                        let key = Abnormal.latchKeyOf record
                        let prev = abnormalLastEmitted |> Map.tryFind key
                        if abnormalDedupPolicy.ShouldEmit(prev, record) then
                            abnormalLastEmitted <- abnormalLastEmitted.Add(key, record)
                            true
                        else false)
                else true
            if pass then
                let gOpt (o: Guid option) = match o with | Some g -> string g | None -> ""
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
                hub.Clients.All.SendAsync(HubMethod.OnAbnormal, p) |> ignore

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

        // v12 P5 — abnormal 단일 발행. Control 은 engine.AbnormalDetected, Monitoring 은 adapter sink
        // (아래 monitoringAbnormal) 가 같은 broadcastAbnormal 로 흘려보낸다. client 는 OnAbnormal 한 번만 수신.
        engine.AbnormalDetected.Add(broadcastAbnormal)

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
            Some(PassiveInferenceSession(engine.Index, engine.IOMap, runtimeMode, (runtimeMode = RuntimeMode.Monitoring)))
        else None
    // v12 P3c — Monitoring 만 abnormal adapter. observeAndInfer 의 IO 를 OnObservedIo 로도 흘려
    // "Going 없이 Finish" = SensorShort / Action* 판정 → broadcastAbnormal. (Control 은 engine 자체 adapter.)
    let monitoringAbnormal =
        if runtimeMode = RuntimeMode.Monitoring then
            // SensorOpen 판정용 Call state — passive inference 가 engine 에 Force 한 현재 상태를 읽는다.
            let getCallStateForOpen g = match engine.GetCallState(g) with Some s -> s | None -> Status4.Ready
            let ab = MonitoringAbnormalAdapter(engine.Index, engine.IOMap, getCallStateForOpen, (fun () -> DateTime.UtcNow), broadcastAbnormal, 250)
            // 자동 줄자 학습 확정 → client(Promaker)로 push. 정지 시 "업데이트" 선택하면 모델 dirty 반영.
            ab.OnLearnedDuration <- (fun workGuid avg minMs maxMs ->
                let workName =
                    Ds2.Core.Store.Queries.getWork workGuid engine.Index.Store
                    |> Option.map (fun w -> w.Name) |> Option.defaultValue ""
                let p : LearnedDurationPayload =
                    { WorkId = string workGuid; WorkName = workName; AvgMs = avg; MinMs = minMs; MaxMs = maxMs }
                hub.Clients.All.SendAsync(HubMethod.OnLearnedDuration, p) |> ignore)
            Some ab
        else None
    // v12 — Monitoring abnormal 사이클별 재검출. Control 은 adapter 가 OnCallReset(callStateChanged)으로 사이클마다
    //   latch 를 비우지만, Monitoring 합류점(Layer B) latch 엔 그 hook 이 없어 DefaultLatchPolicy 5s window 가
    //   사이클간 같은 (Kind,Target) Under/Over 를 5초 억제했다(사이클<5s 면 매 사이클 누락). Call 이 새 사이클로
    //   진입(Going)하면 그 Call 의 직전발행을 비워 다음 사이클이 즉시 재검출되게 한다. (adapter Layer A 는
    //   OnObservedIo 의 OUT rising 에서 동일하게 비운다 — 양 layer 모두 사이클당 1회로 유지.)
    do
        match monitoringAbnormal with
        | Some _ ->
            engine.CallStateChanged.Add(fun args ->
                if args.NewState = Status4.Going then
                    lock abnormalDedupLock (fun () ->
                        abnormalLastEmitted <-
                            abnormalLastEmitted |> Map.filter (fun key _ -> key.Target.CallId <> Some args.CallGuid)))
        | None -> ()
    let getWorkStateSafe =
        Func<Guid, Status4>(fun g -> match engine.GetWorkState(g) with Some s -> s | None -> Status4.Ready)
    let getCallStateSafe =
        Func<Guid, Status4>(fun g -> match engine.GetCallState(g) with Some s -> s | None -> Status4.Ready)
    let drainCurrentTick () =
        // 벽시계 타깃까지 advance — 처진 시계(마지막 loop wake)로 전이가 stamp 되어
        // 간트 막대가 0ms 로 붕괴하거나 늘어나던 왜곡 차단. 점프 폭이 크면 그동안
        // 시계가 정지해 있었다는 뜻이라 진단 로그로 남긴다(수정 효과 실측용).
        let before = engine.CurrentTimeMs
        engine.AdvanceSimulationToRealTime()
        let jumped = engine.CurrentTimeMs - before
        if jumped > 500L then
            passiveLog.Info($"[ClockSync] sim clock jumped {jumped}ms on hub-thread drain (stale stamp window)")
    let isMappedDeviceWork workGuid =
        (engine.IOMap.TxWorkToOutAddresses |> Map.containsKey workGuid)
        || (engine.IOMap.RxWorkToInAddresses |> Map.containsKey workGuid)
    /// 진단용 이름 — Work 는 Index 이름 맵, Call 은 이름 맵이 없어 guid 8자.
    let inferName (kind: PassiveInferenceTarget) (guid: Guid) =
        match kind with
        | PassiveInferenceTarget.Work ->
            match engine.Index.WorkName |> Map.tryFind guid with
            | Some n -> n
            | None -> guid.ToString("N").Substring(0, 8)
        | _ -> guid.ToString("N").Substring(0, 8)

    let observeAndInfer (address: string) (value: string) =
        match passiveInference with
        | Some pi ->
            let mutable scheduledStateChange = false
            let mutable actionCount = 0
            for action in pi.Observe(address, value, getWorkStateSafe, getCallStateSafe) do
                actionCount <- actionCount + 1
                match action.TargetKind with
                | PassiveInferenceTarget.Work ->
                    if not (isMappedDeviceWork action.TargetGuid)
                       && getWorkStateSafe.Invoke(action.TargetGuid) <> action.State then
                        engine.ForceWorkState(action.TargetGuid, action.State)
                        scheduledStateChange <- true
                        passiveLog.Info($"[Infer] Work {inferName PassiveInferenceTarget.Work action.TargetGuid} → {action.State}")
                | PassiveInferenceTarget.Call ->
                    if getCallStateSafe.Invoke(action.TargetGuid) <> action.State then
                        engine.ForceCallState(action.TargetGuid, action.State)
                        scheduledStateChange <- true
                        passiveLog.Info($"[Infer] Call {inferName PassiveInferenceTarget.Call action.TargetGuid} → {action.State}")
                | _ -> ()
            // obs 단위 진단 — self-hosted(Promaker RuntimeMode.cs)의 [Infer] obs 와 동형.
            // "신호는 왔는데 액션이 0개였는지 / 신호 자체가 안 왔는지"를 Agent 파일에서 판별
            // (실 PLC ADV 미반영류 현장 진단 — Agent root 레벨이 INFO 라 Info 로 발행).
            passiveLog.Info($"[Infer] obs {address}={value} → {actionCount} action(s)")
            if scheduledStateChange then
                drainCurrentTick ()
            for entry in pi.DrainLogs() do
                match entry.Kind with
                | PassiveInferenceLogKind.Warn -> passiveLog.Warn(entry.Message)
                | _ -> passiveLog.Info(entry.Message)
        | None -> ()

        let abnormalReady =
            match passiveInference with
            | Some pi -> pi.IsAbnormalReadyForAddress(address)
            | None -> true
        match monitoringAbnormal with
        | Some ab when abnormalReady -> ab.OnObservedIo(address, value, Environment.TickCount)
        | None -> ()
        | _ -> ()
    let applyEffect (effect: RuntimeHubEffect) =
        match effect.Kind with
        | RuntimeHubEffectKind.InjectIoByAddress ->
            engine.InjectIOValueByAddress(effect.Address, effect.Value) |> ignore
            drainCurrentTick ()
        | RuntimeHubEffectKind.ForceWorkState ->
            if effect.WorkGuid <> Guid.Empty then
                engine.ForceWorkState(effect.WorkGuid, effect.State)
                drainCurrentTick ()
        | RuntimeHubEffectKind.ForceWorkStateIfGoing ->
            if effect.WorkGuid <> Guid.Empty then
                engine.TryForceWorkStateIfGoing(effect.WorkGuid, effect.State)
                drainCurrentTick ()
        | RuntimeHubEffectKind.ForceWorkStateIfReady ->
            if effect.WorkGuid <> Guid.Empty then
                engine.TryForceWorkStateIfReady(effect.WorkGuid, effect.State)
                drainCurrentTick ()
        | RuntimeHubEffectKind.PassiveObserve -> observeAndInfer effect.Address effect.Value
        | RuntimeHubEffectKind.PassiveBaseline ->
            match passiveInference with
            | Some pi -> pi.Baseline(effect.Address, effect.Value)
            | None -> ()
        | RuntimeHubEffectKind.Log ->
            // 세션 진단 로그 — 과거 "노이즈 회피"로 버리던 것을 파일로 노출(현장 진단 임시조치).
            // 인퍼런스/세션이 남기는 판단 근거가 여기로 온다 — 버리면 미반영류 추적이 불가능.
            match effect.Severity with
            | RuntimeHubLogSeverity.Warn -> passiveLog.Warn($"[Session] {effect.Message}")
            | _ -> passiveLog.Info($"[Session] {effect.Message}")
        | RuntimeHubEffectKind.WriteTag -> ()  // Monitoring read-only — PLC 재기록 안 함 (Control write 는 P4)
        | _ -> ()

    let preApplyMonitoringInput (item: TagWrite) =
        if runtimeMode = RuntimeMode.Monitoring
           && not (isNull (box item))
           && not (String.IsNullOrWhiteSpace item.Address) then
            match engine.IOMap.InAddressToMappings |> Map.tryFind item.Address with
            | Some mappings ->
                for mapping in mappings do
                    engine.InjectIOValue(mapping.ApiCallGuid, item.Value)
            | None -> ()

    let applyHubTag address value source =
        for effect in modeSession.HandleHubTag(address, value, source) do
            applyEffect effect

    // ── comm blackout: resync baseline + per-call 재무장 ──────────────────
    let isResyncItem (item: TagWrite) =
        not (isNull (box item))
        && String.Equals(item.Source, HubSource.Resync, StringComparison.OrdinalIgnoreCase)

    /// Resync(재연결 baseline) — edge 가 아니라 현재값 스냅샷. IO 현재값 + passive 추론 기준선 +
    /// abnormal 어댑터 prevActive 만 갱신한다. 어댑터는 InvalidateObservations 직후라
    /// "첫 관측 = baseline" 규칙을 타서 발행/edge 판정 없이 기준선만 세워진다.
    let applyResyncBaseline (item: TagWrite) =
        preApplyMonitoringInput item
        match passiveInference with
        | Some pi -> pi.Baseline(item.Address, item.Value)
        | None -> ()
        match monitoringAbnormal with
        | Some ab -> ab.OnObservedIo(item.Address, item.Value, Environment.TickCount)
        | None -> ()

    /// resync 배치 수신 = blackout 해제 신호 → REARMING 진입. "연결됨" status 는 해제 신호로
    /// 쓰지 않는다(connect 성공 직후 read 가 다시 전부 실패할 수 있다 — 첫 성공 스캔만 신뢰).
    let exitBlackoutToRearming () =
        lock blackoutLock (fun () ->
            if commBlackout then
                commBlackout <- false
                rearming <- true
                rearmStartedUtc <- DateTime.UtcNow
                rearmedCalls.Clear()
                passiveLog.Info("[CommBlackout] resync baseline received — REARMING (per-call until next OUT rising)"))

    /// REARMING 중 OUT rising(새 사이클 시작) 관측 → 해당 Call 재무장. resync 가 전 태그의
    /// 기준선을 세웠으므로 이후 도착하는 변화는 전부 진짜 edge — "OUT 주소가 active 값으로 도착"
    /// = rising 으로 간주해도 안전하다. PLC 스캔 변화는 배치로만 오므로 본 hook 은 배치 경로에만 건다.
    let tryRearmFromTag (address: string) (value: string) =
        if rearming then
            match engine.IOMap.OutAddressToMappings |> Map.tryFind address with
            | Some mappings when not mappings.IsEmpty ->
                match Ds2.Core.Store.Queries.getApiCall mappings.Head.ApiCallGuid engine.Index.Store with
                | Some apiCall when RuntimeSemantics.isActiveOutputValue apiCall value ->
                    lock blackoutLock (fun () ->
                        if rearming then
                            for m in mappings do rearmedCalls.Add m.CallGuid |> ignore
                            if rearmedCalls.Count >= totalMappedCalls then
                                rearming <- false
                                passiveLog.Info("[CommBlackout] all mapped calls re-armed — abnormal evaluation fully resumed"))
                | _ -> ()
            | _ -> ()

    let applyHubTagBatch (items: TagWrite array) =
        if not (isNull items) && items.Length > 0 then
            // Resync(재연결 baseline)는 Monitoring 에서만 분리 처리 — edge 로 추론하면 안 된다.
            // 다른 모드는 기존 경로 유지(Control 엔진은 자체 상태가 권위라 값 주입만으로 안전).
            let resyncItems, normalItems =
                if runtimeMode = RuntimeMode.Monitoring then
                    items |> Array.partition isResyncItem
                else
                    Array.empty, items

            if resyncItems.Length > 0 then
                for item in resyncItems do
                    if not (String.IsNullOrWhiteSpace item.Address) then
                        applyResyncBaseline item
                exitBlackoutToRearming ()

            if normalItems.Length > 0 then
                if runtimeMode = RuntimeMode.Monitoring then
                    for item in normalItems do
                        preApplyMonitoringInput item

                for item in normalItems do
                    if not (isNull (box item))
                       && not (String.IsNullOrWhiteSpace item.Address) then
                        tryRearmFromTag item.Address item.Value
                        let source =
                            if String.IsNullOrWhiteSpace item.Source then HubSource.Plc else item.Source
                        for effect in modeSession.HandleHubTag(item.Address, item.Value, source) do
                            if runtimeMode = RuntimeMode.Monitoring
                               && effect.Kind = RuntimeHubEffectKind.InjectIoByAddress then
                                ()
                            else
                                applyEffect effect

            if runtimeMode = RuntimeMode.Monitoring then
                drainCurrentTick ()

    // device=plan: engine plan-duration 이 device work 의 Finish 시점(=actual In 이 켜질 시점)을 정한다.
    //   VP 는 가상 plant 로서 바로 그 시점에 해당 device 의 In 을 자기 passive inference 에 observe 시킨다.
    //   이때 Call 은 이미 Going(Out observe)이고 duration 도 경과한 상태라 Inference 의
    //   'state=Going + 모든 In On(callInHigh ⊇ expected)' 조건이 충족돼 Call 이 Finish 한다.
    //   (HubSession 에서 Out On 시점에 In observe 를 delay 로 넣던 방식은 applyEffect 가 DelayMs 를 안 지켜
    //    In observe 가 Out observe(going)보다 먼저 즉시 실행 → state=Going 미충족 → Call 이 영구 Going 이었다.)
    do
        if runtimeMode = RuntimeMode.VirtualPlant then
            let inputValueFor (apiCallGuid: Guid) (active: bool) =
                Ds2.Core.Store.Queries.getApiCall apiCallGuid engine.Index.Store
                |> Option.map (fun ac ->
                    if active then RuntimeSemantics.activeInputValue ac else RuntimeSemantics.resetInputValue ac)
                |> Option.defaultValue (if active then "true" else "false")
            engine.WorkStateChanged.Add(fun args ->
                if isMappedDeviceWork args.WorkGuid then
                    // 이 device 가 출력주체(Tx)인 mapping 의 InAddress = device 동작완료 시 켜지는 actual In.
                    let deviceInMappings =
                        engine.IOMap.CallToMappings
                        |> Seq.collect (fun kvp -> kvp.Value)
                        |> Seq.filter (fun m -> m.TxWorkGuid = Some args.WorkGuid && not (String.IsNullOrEmpty m.InAddress))
                    match args.NewState with
                    | Status4.Finish ->
                        for m in deviceInMappings do observeAndInfer m.InAddress (inputValueFor m.ApiCallGuid true)
                    | Status4.Ready | Status4.Homing ->
                        for m in deviceInMappings do observeAndInfer m.InAddress (inputValueFor m.ApiCallGuid false)
                    | _ -> ())

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
        member _.TryForceWorkStateIfReadyAsync cmd =
            if allow cmd.Envelope then
                engine.TryForceWorkStateIfReady(pg cmd.WorkId, enum<Status4> cmd.StatusValue)
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
                applyHubTag cmd.Address cmd.Value "plc"
            Task.CompletedTask
        member _.InjectIOValuesByAddressAsync cmd =
            if allow cmd.Envelope then
                applyHubTagBatch cmd.Items
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

        member _.NotifyPlcConnectionAsync (status: PlcConnectionStatus) =
            // down 전이 → blackout 진입(1회). 지속 실패의 반복 status 는 이미 blackout 이라 no-op.
            // up 전이는 무시 — 해제는 resync 배치 도착(applyHubTagBatch)으로만 한다:
            // connect 성공 직후 read 가 전부 실패할 수 있어 "연결됨" 신호는 신뢰하지 않는다.
            if runtimeMode = RuntimeMode.Monitoring && not status.IsConnected then
                lock blackoutLock (fun () ->
                    if not commBlackout then
                        commBlackout <- true
                        rearming <- false
                        rearmedCalls.Clear()
                        // 관측 진행 상태 무효화(학습 줄자는 보존) — 단절 시간이 포함된 elapsed/
                        // 누락 edge 의 가짜 rising 이 만들어지지 않게 goingClock/prevActive 를 비운다.
                        match monitoringAbnormal with
                        | Some ab -> ab.InvalidateObservations()
                        | None -> ()
                        lock abnormalDedupLock (fun () -> abnormalLastEmitted <- Map.empty)
                        passiveLog.Warn($"[CommBlackout] PLC down ({status.Name}: {status.LastError}) — abnormal suppressed, observations invalidated"))
            Task.CompletedTask
