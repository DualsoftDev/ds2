module Ds2.Store.Editor.Tests.AbnormalDetectorTests

open System
open System.Collections.Generic
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers
open Ds2.Runtime.IO
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Abnormal

// v12 §P3a — Control/Monitoring 공통 abnormal detector 자산 검증.
// 적용 계획: samples/Abnormal-v12-Apply-Plan.md §6 P3a.

module ObservedClockTests =

    let private range : RxTimingRange = { MinMs = 100; MaxMs = 900 }  // width 800

    [<Fact>]
    let ``reliableClock is always reliable`` () =
        let info = AbnormalDetector.reliableClock 500
        Assert.True(info.TimingReliable)
        Assert.Equal(500, info.ElapsedMs)
        Assert.Equal<TimingQuality>(Reliable, info.Quality)

    [<Fact>]
    let ``observedClock within range width stays reliable`` () =
        let info = AbnormalDetector.observedClock range 500 800
        Assert.True(info.TimingReliable)
        Assert.Equal<TimingQuality>(Reliable, info.Quality)

    [<Fact>]
    let ``observedClock with latency over range width is degraded`` () =
        let info = AbnormalDetector.observedClock range 500 801
        Assert.False(info.TimingReliable)
        match info.Quality with
        | Degraded _ -> ()
        | Reliable -> failwith "expected Degraded"

// v12 §6/§6.1 — ILatchPolicy 기본 구현(DefaultLatchPolicy): Kind별 latch 윈도우.
//   spec 시그니처 ShouldEmit(previous, current) — previous=같은 (Kind,Target) 직전 발행.
//   Action*=5000ms dedup window, Sensor*=0(즉시). timestamp 차로 판정.
module LatchPolicyTests =

    let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let private target = Abnormal.target (Some(Guid.NewGuid())) None None

    [<Fact>]
    let ``ShouldEmit true when no previous`` () =
        let policy = DefaultLatchPolicy() :> ILatchPolicy
        Assert.True(policy.ShouldEmit(None, Abnormal.actionOver target 1000 t0))

    // timing(Action*) — 5000ms 윈도우 내 중복 억제 (Control OnTick + rising dedup).
    [<Fact>]
    let ``DefaultLatchPolicy dedups Action within 5s window`` () =
        let policy = DefaultLatchPolicy() :> ILatchPolicy
        let prev = Abnormal.actionOver target 1000 t0
        let cur  = Abnormal.actionOver target 1000 (t0.AddMilliseconds 2000.0)   // 2s < 5s → 억제
        Assert.False(policy.ShouldEmit(Some prev, cur))

    [<Fact>]
    let ``DefaultLatchPolicy re-emits Action after 5s window`` () =
        let policy = DefaultLatchPolicy() :> ILatchPolicy
        let prev = Abnormal.actionOver target 1000 t0
        let cur  = Abnormal.actionOver target 1000 (t0.AddMilliseconds 6000.0)   // 6s ≥ 5s → 발행
        Assert.True(policy.ShouldEmit(Some prev, cur))

    // sensor(Short/Open) — 윈도우 0 → 즉시 재발행. 판정 간격 없이 바로바로 떠야 함.
    [<Fact>]
    let ``DefaultLatchPolicy emits Sensor immediately (no gap)`` () =
        let policy = DefaultLatchPolicy() :> ILatchPolicy
        let prev = Abnormal.sensorShort target t0
        let cur  = Abnormal.sensorShort target (t0.AddMilliseconds 1.0)
        Assert.True(policy.ShouldEmit(Some prev, cur))

module SensingGateTests =

    [<Fact>]
    let ``isPhysicalSensing true for Real`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let apiDef = addApiDef store "ADV" system.Id
        apiDef.SensingType <- SensingType.Real(Level, None)
        Assert.True(AbnormalDetector.isPhysicalSensing apiDef)

    [<Fact>]
    let ``isPhysicalSensing false for Virtual`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let apiDef = addApiDef store "ADV" system.Id
        apiDef.SensingType <- SensingType.Virtual None
        Assert.False(AbnormalDetector.isPhysicalSensing apiDef)

module RangeResolverTests =

    // P2 SimulationTests 의 검증된 device-range 셋업 재사용.
    let private setupDeviceRange () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "ADV" deviceFlow.Id
        deviceWork.MinDuration <- Some(TimeSpan.FromMilliseconds 250.0)
        deviceWork.MaxDuration <- Some(TimeSpan.FromMilliseconds 900.0)
        let apiDef = addApiDef store "ADV" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let index = SimIndex.build store 10
        store, index, work

    [<Fact>]
    let ``tryResolveWorkRange returns device-derived range for active work`` () =
        let _, index, work = setupDeviceRange ()
        match AbnormalDetector.tryResolveWorkRange index work.Id with
        | Some range ->
            Assert.Equal(250, range.MinMs)
            Assert.Equal(900, range.MaxMs)
        | None -> failwith "expected range"

    [<Fact>]
    let ``tryResolveRangeFromCall resolves via CallWorkGuid`` () =
        let store, index, work = setupDeviceRange ()
        let callId = (Queries.callsOf work.Id store |> List.head).Id
        match AbnormalDetector.tryResolveRangeFromCall index callId with
        | Some range ->
            Assert.Equal(250, range.MinMs)
            Assert.Equal(900, range.MaxMs)
        | None -> failwith "expected range"

// v12 §2.3/§4 — canEvaluate gating: 자동 Flow(IsAuto) ∧ Real sensing ∧ 비인터락(Call.Interlocked).
module GatingTests =

    let private setup () =
        let store = createStore ()
        let project, _, flow, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let apiDef = addApiDef store "ADV" deviceSystem.Id
        apiDef.SensingType <- SensingType.Real(Level, None)
        let callId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ])
        store, store.Calls.[callId], apiDef, flow

    [<Fact>]
    let ``canEvaluate true for auto flow + Real + non-interlocked`` () =
        let store, call, def, _ = setup ()
        Assert.True(AbnormalDetector.canEvaluate store call.Id def)

    [<Fact>]
    let ``canEvaluate false when flow is manual (IsAuto=false)`` () =
        let store, call, def, flow = setup ()
        flow.IsAuto <- false
        Assert.False(AbnormalDetector.canEvaluate store call.Id def)

    [<Fact>]
    let ``canEvaluate false when call interlocked`` () =
        let store, call, def, _ = setup ()
        call.Interlocked <- true
        Assert.False(AbnormalDetector.canEvaluate store call.Id def)

    [<Fact>]
    let ``canEvaluate false for Virtual sensing`` () =
        let store, call, def, _ = setup ()
        def.SensingType <- SensingType.Virtual None
        Assert.False(AbnormalDetector.canEvaluate store call.Id def)

// v12 §P3b — Control adapter 4 케이스 + 오탐 방지 + latch dedup.
module ControlAdapterTests =

    let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    // active Call + device range(250~900) + 물리 InTag(Level completion trigger) 셋업.
    let private setup () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "ADV" deviceFlow.Id
        deviceWork.MinDuration <- Some(TimeSpan.FromMilliseconds 250.0)
        deviceWork.MaxDuration <- Some(TimeSpan.FromMilliseconds 900.0)
        let apiDef = addApiDef store "ADV" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        apiDef.SensingType <- SensingType.Real(Level, None)
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.InTag <- Some(IOTag("IN", "X0", ""))
        apiCall.OutTag <- Some(IOTag("OUT", "Y0", ""))
        let index = SimIndex.build store 10
        let ioMap = SignalIOMap.build store
        let emitted = ResizeArray<AbnormalRecord>()
        let states = Dictionary<Guid, Status4>()
        let inputActive = Dictionary<Guid, bool>()
        let getCallState cid =
            match states.TryGetValue cid with
            | true, s -> s
            | _ -> Status4.Ready
        let isInputActive acid =
            match inputActive.TryGetValue acid with
            | true, b -> b
            | _ -> false
        let adapter =
            ControlAbnormalAdapter(index, ioMap, getCallState, isInputActive, (fun () -> t0), (fun r -> emitted.Add r))
        adapter, emitted, states, inputActive, call.Id, apiCall.Id

    [<Fact>]
    let ``rising in range is normal — no false positive`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 1500)   // elapsed 500 ∈ [250,900]
        Assert.Empty(emitted)

    [<Fact>]
    let ``rising below Min is ActionUnder`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 1100)   // elapsed 100 < 250
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionUnder, emitted.[0].Kind)

    // Over 는 Max 시점 OnTick 이 SSOT — InTag 가 Max 이후 늦게 rising 해도 over 를 내지 않는다
    //   (늦은 센싱 over 는 의미 없어 제외, 사용자 확정). over 발행 자체는 아래 tick 테스트가 검증.
    [<Fact>]
    let ``rising above Max does not emit (over is tick-only)`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 2000)   // elapsed 1000 > 900 — rising 경로는 over 안 냄
        Assert.Empty(emitted)

    [<Fact>]
    let ``rising when call not Going is SensorShort`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Ready
        adapter.OnInputRising(apiCallId, 1500)
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorShort, emitted.[0].Kind)

    [<Fact>]
    let ``falling during Finish level sensor is SensorOpen`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Finish     // Finish 유지(reset 전)
        adapter.OnInputFalling(apiCallId, 1500)
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    // v12 §3.2 — RxWork≠Ready(Going 포함) 중 level 센서 falling 도 SensorOpen (Finish 만이 아님).
    [<Fact>]
    let ``falling during Going level sensor is SensorOpen`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnInputFalling(apiCallId, 1500)
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    // Ready(출발 전) falling 은 정상 — SensorOpen 아님.
    [<Fact>]
    let ``falling when Ready is not SensorOpen`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Ready
        adapter.OnInputFalling(apiCallId, 1500)
        Assert.Empty(emitted)

    [<Fact>]
    let ``tick over Max with inactive input is ActionOver`` () =
        let adapter, emitted, states, inputActive, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        inputActive.[apiCallId] <- false
        adapter.OnCallGoing(callId, 1000)
        adapter.OnTick(2000)   // elapsed 1000 > 900, 입력 미도달
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionOver, emitted.[0].Kind)

    [<Fact>]
    let ``duplicate abnormal within latch window emits once`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 1100)   // ActionUnder
        adapter.OnInputRising(apiCallId, 1150)   // 같은 (Kind,Target) 5000ms 내 → 억제
        Assert.Single(emitted) |> ignore

    // OnTick over 는 사이클당 1회(latch)지만, OnCallReset 으로 latch 가 비면 다음 사이클에 다시 판정.
    [<Fact>]
    let ``tick over re-emits after OnCallReset (per-cycle latch clear)`` () =
        let adapter, emitted, states, inputActive, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        inputActive.[apiCallId] <- false
        adapter.OnCallGoing(callId, 1000)
        adapter.OnTick(2000)                       // over #1 (elapsed 1000 > 900)
        adapter.OnTick(2100)                       // 같은 사이클 5000ms 내 → 억제
        Assert.Single(emitted) |> ignore
        adapter.OnCallReset(callId)                // 사이클 종료 → latch clear
        adapter.OnCallGoing(callId, 3000)
        adapter.OnTick(4000)                       // over #2: 4000-2000<5000 이지만 reset 으로 재판정
        Assert.Equal(2, emitted.Count)

// v12 §P3c — Monitoring adapter (IO-edge): OutTag On=going, InTag On=finish, elapsed vs device range.
// cycle 학습(synced)과 독립. going off→on rising 기반이라 중간시작 사이클은 자동 배제.
module MonitoringAdapterTests =

    let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    // device range(250~900) + ApiCall 에 Out/In 주소 부여 + adapter.
    let private setup () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "ADV" deviceFlow.Id
        deviceWork.MinDuration <- Some(TimeSpan.FromMilliseconds 250.0)
        deviceWork.MaxDuration <- Some(TimeSpan.FromMilliseconds 900.0)
        let apiDef = addApiDef store "ADV" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        apiDef.SensingType <- SensingType.Real(Level, None)
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.OutTag <- Some(IOTag("OUT", "Y0", ""))
        apiCall.InTag <- Some(IOTag("IN", "X0", ""))
        let index = SimIndex.build store 10
        let ioMap = SignalIOMap.build store
        let emitted = ResizeArray<AbnormalRecord>()
        let states = Dictionary<Guid, Status4>()
        let getCallState cid =
            match states.TryGetValue cid with
            | true, s -> s
            | _ -> Status4.Ready
        let adapter = MonitoringAbnormalAdapter(index, ioMap, getCallState, (fun () -> t0), (fun r -> emitted.Add r))
        adapter, emitted, states, call.Id, apiCall.Id

    // baseline(off) 한 번 깔고 off→on rising 으로 going/finish 을 만든다.
    let private goingThenFinish (adapter: MonitoringAbnormalAdapter) (goingMs: int) (finishMs: int) =
        adapter.OnObservedIo("Y0", "false", goingMs)   // OUT baseline
        adapter.OnObservedIo("Y0", "true", goingMs)    // OUT rising → going
        adapter.OnObservedIo("X0", "false", goingMs)   // IN baseline
        adapter.OnObservedIo("X0", "true", finishMs)   // IN rising → finish

    [<Fact>]
    let ``elapsed in range is normal — no false positive`` () =
        let adapter, emitted, _, _, _ = setup ()
        goingThenFinish adapter 0 500          // elapsed 500 in [250,900]
        Assert.Empty(emitted)

    [<Fact>]
    let ``elapsed below Min is ActionUnder`` () =
        let adapter, emitted, _, _, _ = setup ()
        goingThenFinish adapter 0 100          // elapsed 100 < 250
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionUnder, emitted.[0].Kind)

    // Over 는 engine watchdog(onDeviceDurationExpired)이 SSOT — In 이 Max 이후 늦게 rising 해도
    //   adapter 는 over 를 내지 않는다(늦은 센싱 over 제외, 사용자 확정).
    [<Fact>]
    let ``finish above Max does not emit (over is watchdog-only)`` () =
        let adapter, emitted, _, _, _ = setup ()
        goingThenFinish adapter 0 1000         // elapsed 1000 > 900 — finish 경로는 over 안 냄
        Assert.Empty(emitted)

    [<Fact>]
    let ``finish without observed going start is dropped (mid-cycle 1cycle)`` () =
        let adapter, emitted, _, _, _ = setup ()
        // OUT 이 이미 on 인 상태로 관측 시작(baseline=on) → going rising 못 봄
        adapter.OnObservedIo("Y0", "true", 0)  // baseline on, rising 아님 → going 기록 안 됨
        adapter.OnObservedIo("X0", "false", 0)
        adapter.OnObservedIo("X0", "true", 100)
        Assert.Empty(emitted)                  // Out 현재 on(mid-cycle) → short 아님

    [<Fact>]
    let ``finish without going and output off is SensorShort`` () =
        let adapter, emitted, _, _, _ = setup ()
        adapter.OnObservedIo("Y0", "false", 0)   // OUT off — going 흔적 없음
        adapter.OnObservedIo("X0", "false", 0)   // IN baseline
        adapter.OnObservedIo("X0", "true", 100)  // IN rising → Going 없이 Finish
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorShort, emitted.[0].Kind)

    [<Fact>]
    let ``target carries Call ApiCall and Work ids`` () =
        let adapter, emitted, _, _, _ = setup ()
        goingThenFinish adapter 0 100
        Assert.Single(emitted) |> ignore
        Assert.True(emitted.[0].Target.CallId.IsSome)
        Assert.True(emitted.[0].Target.ApiCallId.IsSome)
        Assert.True(emitted.[0].Target.WorkId.IsSome)

    [<Fact>]
    let ``in falling during Finish level sensor is SensorOpen`` () =
        let adapter, emitted, states, callId, _ = setup ()
        goingThenFinish adapter 0 500              // 정상 going→finish (elapsed 500 ∈ [250,900])
        Assert.Empty(emitted)
        states.[callId] <- Status4.Finish          // Call Finish(reset 전) 유지
        adapter.OnObservedIo("X0", "false", 600)   // level 센서 In falling → 단선 = SensorOpen
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    [<Fact>]
    let ``in falling when not Finish is not SensorOpen`` () =
        let adapter, emitted, states, callId, _ = setup ()
        goingThenFinish adapter 0 500
        Assert.Empty(emitted)
        states.[callId] <- Status4.Ready           // reset→Ready = 정상 종료, Open 아님
        adapter.OnObservedIo("X0", "false", 600)
        Assert.Empty(emitted)

// device work = plan: In(실제 IO) 없이 duration plan 으로 Going→Finish 해야 한다 (사용자 확정).
// "Control device Finish 안 됨" 회귀를 코드 레벨에서 못박는 통합테스트.
module DeviceControlCycleTests =

    [<Fact>]
    let ``Control device work finishes by duration plan without external In`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "ADV" deviceFlow.Id
        deviceWork.MinDuration <- Some(TimeSpan.FromMilliseconds 250.0)
        deviceWork.MaxDuration <- Some(TimeSpan.FromMilliseconds 900.0)
        let apiDef = addApiDef store "ADV" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        apiDef.SensingType <- SensingType.Real(Level, None)
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Control)) :> ISimulationEngine

        // Call going → executeApiCall 이 device work 를 Going 으로 force.
        engine.ForceCallState(call.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)   // forced going drain
        // In(actual) 은 절대 주입 안 함 — device 는 In 무관 duration plan 으로 Finish 해야.
        engine.AdvanceSimulationTo(2000L)                  // device duration(<=900) 너머

        Assert.Equal(Some Status4.Finish, engine.GetWorkState(deviceWork.Id))

    [<Fact>]
    let ``Control emits ActionOver at device Max even after device plan Finish`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "ADV" deviceFlow.Id
        deviceWork.Duration <- Some(TimeSpan.FromMilliseconds 200.0)
        deviceWork.MinDuration <- Some(TimeSpan.FromMilliseconds 250.0)
        deviceWork.MaxDuration <- Some(TimeSpan.FromMilliseconds 900.0)
        let apiDef = addApiDef store "ADV" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        apiDef.SensingType <- SensingType.Real(Level, None)
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.OutTag <- Some(IOTag("OUT", "Y0", ""))
        apiCall.InTag <- Some(IOTag("IN", "X0", ""))
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Control)) :> ISimulationEngine
        let emitted = ResizeArray<AbnormalRecord>()
        engine.AbnormalDetected.Add(fun record -> emitted.Add record)

        engine.ForceCallState(call.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(200L)
        Assert.Equal(Some Status4.Finish, engine.GetWorkState(deviceWork.Id))

        engine.AdvanceSimulationTo(901L)

        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionOver, emitted.[0].Kind)

    [<Fact>]
    let ``Control device Going lasts work duration only (timeAppend does not extend)`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "ADV" deviceFlow.Id
        deviceWork.Duration <- Some(TimeSpan.FromMilliseconds 500.0)
        let apiDef = addApiDef store "ADV" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        // work.Duration 500 + ActionType timeAppend 200 → device Going 지속 = 500ms 만.
        // timeAppend(출력 유지)는 Going 막대를 늘이지 않는다(간트에 빨간 점선 시각화로만 표기).
        apiDef.ActionType <- ActionType.Real(Level, Some(Append 200))
        apiDef.SensingType <- SensingType.Real(Level, None)
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Control)) :> ISimulationEngine

        engine.ForceCallState(call.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(400L)   // duration 500 미만 → 아직 Going
        Assert.Equal(Some Status4.Going, engine.GetWorkState(deviceWork.Id))
        engine.AdvanceSimulationTo(600L)   // duration 500 초과(timeAppend 무시) → Finish
        Assert.Equal(Some Status4.Finish, engine.GetWorkState(deviceWork.Id))

    [<Fact>]
    let ``Monitoring device work finishes by duration plan (passive, forced going)`` () =
        // Monitoring 도 device 는 plan(duration) 으로 Finish 해야 — passive 라도.
        // 앱에서 Monitoring device 가 Going 에 박히는데, 코드 레벨에서 되는지(=앱 scheduler 문제인지) 가른다.
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "ADV" deviceFlow.Id
        let apiDef = addApiDef store "ADV" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        apiDef.ActionType <- ActionType.Real(Level, Some(Append 200))
        apiDef.SensingType <- SensingType.Real(Level, None)
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Monitoring)) :> ISimulationEngine

        // passive 모드라 HubSession 이 하던 device going force 를 직접 흉내 (Out On → device Going).
        engine.ForceWorkState(deviceWork.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(2000L)                  // plan duration(200) 너머

        Assert.Equal(Some Status4.Finish, engine.GetWorkState(deviceWork.Id))

    [<Fact>]
    let ``Monitoring emits ActionOver at device Max while Call waits external In`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "ADV" deviceFlow.Id
        deviceWork.Duration <- Some(TimeSpan.FromMilliseconds 200.0)
        deviceWork.MinDuration <- Some(TimeSpan.FromMilliseconds 250.0)
        deviceWork.MaxDuration <- Some(TimeSpan.FromMilliseconds 900.0)
        let apiDef = addApiDef store "ADV" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        apiDef.SensingType <- SensingType.Real(Level, None)
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.OutTag <- Some(IOTag("OUT", "Y0", ""))
        apiCall.InTag <- Some(IOTag("IN", "X0", ""))
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Monitoring)) :> ISimulationEngine
        let emitted = ResizeArray<AbnormalRecord>()
        engine.AbnormalDetected.Add(fun record -> emitted.Add record)

        engine.ForceCallState(call.Id, Status4.Going)
        engine.ForceWorkState(deviceWork.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(200L)
        Assert.Equal(Some Status4.Finish, engine.GetWorkState(deviceWork.Id))

        engine.AdvanceSimulationTo(901L)

        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionOver, emitted.[0].Kind)

// ── [임시 진단] project_260608.json Sim 2사이클 재현 — Conveyor2 reset/멈춤 추적 ──
module Project260608ReproTests =

    [<Fact>]
    let ``project_260608 simulation runs past 2nd cycle Conveyor2`` () =
        let store = createStore ()
        store.LoadFromFile(@"C:\Users\Gamekun\Documents\Dualsoft\project_260608.json")
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Simulation)) :> ISimulationEngine
        engine.Start()
        engine.AdvanceSimulationTo(60000L)   // 사이클 ~12s → 넉넉히 5사이클분
        let conv2 = store.Works.Values |> Seq.find (fun w -> w.Name = "Conveyor2_Flow.MOVE")
        let epoch = Ds2.Runtime.Model.SimState.getWorkEpoch conv2.Id engine.State
        Assert.True(epoch >= 2, sprintf "Conveyor2_Flow.MOVE epoch=%d — 1이면 2nd 사이클 진입 못 하고 멈춤" epoch)
