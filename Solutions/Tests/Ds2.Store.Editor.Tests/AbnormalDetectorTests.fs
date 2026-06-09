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

// v12 §P3a ??Control/Monitoring 공통 abnormal detector ?�산 검�?
// ?�용 계획: samples/Abnormal-v12-Apply-Plan.md §6 P3a.

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

// v12 §6/§6.1 ??ILatchPolicy 기본 구현(DefaultLatchPolicy): Kind�?latch ?�도??
//   spec ?�그?�처 ShouldEmit(previous, current) ??previous=같�? (Kind,Target) 직전 발행.
//   Action*=5000ms dedup window, Sensor*=0(즉시). timestamp 차로 ?�정.
module LatchPolicyTests =

    let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let private target = Abnormal.target (Some(Guid.NewGuid())) None None

    [<Fact>]
    let ``ShouldEmit true when no previous`` () =
        let policy = DefaultLatchPolicy() :> ILatchPolicy
        Assert.True(policy.ShouldEmit(None, Abnormal.actionOver target 1000 t0))

    // timing(Action*) ??5000ms ?�도????중복 ?�제 (Control OnTick + rising dedup).
    [<Fact>]
    let ``DefaultLatchPolicy dedups Action within 5s window`` () =
        let policy = DefaultLatchPolicy() :> ILatchPolicy
        let prev = Abnormal.actionOver target 1000 t0
        let cur  = Abnormal.actionOver target 1000 (t0.AddMilliseconds 2000.0)   // 2s < 5s ???�제
        Assert.False(policy.ShouldEmit(Some prev, cur))

    [<Fact>]
    let ``DefaultLatchPolicy re-emits Action after 5s window`` () =
        let policy = DefaultLatchPolicy() :> ILatchPolicy
        let prev = Abnormal.actionOver target 1000 t0
        let cur  = Abnormal.actionOver target 1000 (t0.AddMilliseconds 6000.0)   // 6s ??5s ??발행
        Assert.True(policy.ShouldEmit(Some prev, cur))

    // sensor(Short/Open) ???�도??0 ??즉시 ?�발?? ?�정 간격 ?�이 바로바로 ?�야 ??
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

    // P2 SimulationTests ??검증된 device-range ?�업 ?�사??
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

// v12 §2.3/§4 ??canEvaluate gating: ?�동 Flow(IsAuto) ??Real sensing ??비인?�락(Call.Interlocked).
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

// v12 §P3b ??Control adapter 4 케?�스 + ?�탐 방�? + latch dedup.
module ControlAdapterTests =

    let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    // active Call + device range(250~900) + 물리 InTag(Level completion trigger) ?�업.
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
    let ``rising in range is normal ??no false positive`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 1500)   // elapsed 500 ??[250,900]
        Assert.Empty(emitted)

    [<Fact>]
    let ``rising below Min is ActionUnder`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 1100)   // elapsed 100 < 250
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionUnder, emitted.[0].Kind)

    // Over ??Max ?�점 OnTick ??SSOT ??InTag 가 Max ?�후 ??�� rising ?�도 over �??��? ?�는??    //   (??? ?�싱 over ???��? ?�어 ?�외, ?�용???�정). over 발행 ?�체???�래 tick ?�스?��? 검�?
    [<Fact>]
    let ``rising above Max does not emit (over is tick-only)`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 2000)
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
        states.[callId] <- Status4.Finish     // Finish ?��?(reset ??
        adapter.OnInputFalling(apiCallId, 1500)
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    // v12 §3.2 ??RxWork?�Ready(Going ?�함) �?level ?�서 falling ??SensorOpen (Finish 만이 ?�님).
    [<Fact>]
    let ``falling during Going level sensor is SensorOpen`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnInputFalling(apiCallId, 1500)
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    // Ready(출발 ?? falling ?� ?�상 ??SensorOpen ?�님.
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
        adapter.OnTick(2000)   // elapsed 1000 > 900, ?�력 미도??        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionOver, emitted.[0].Kind)

    [<Fact>]
    let ``duplicate abnormal within latch window emits once`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 1100)   // ActionUnder
        adapter.OnInputRising(apiCallId, 1150)   // 같�? (Kind,Target) 5000ms ?????�제
        Assert.Single(emitted) |> ignore

    // OnTick over ???�이?�당 1??latch)지�? OnCallReset ?�로 latch 가 비면 ?�음 ?�이?�에 ?�시 ?�정.
    [<Fact>]
    let ``tick over re-emits after OnCallReset (per-cycle latch clear)`` () =
        let adapter, emitted, states, inputActive, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        inputActive.[apiCallId] <- false
        adapter.OnCallGoing(callId, 1000)
        adapter.OnTick(2000)                       // over #1 (elapsed 1000 > 900)
        adapter.OnTick(2100)                       // 같�? ?�이??5000ms ?????�제
        Assert.Single(emitted) |> ignore
        adapter.OnCallReset(callId)                // ?�이??종료 ??latch clear
        adapter.OnCallGoing(callId, 3000)
        adapter.OnTick(4000)                       // over #2: 4000-2000<5000 ?��?�?reset ?�로 ?�판??        Assert.Equal(2, emitted.Count)

// v12 §P3c ??Monitoring adapter (IO-edge): OutTag On=going, InTag On=finish, elapsed vs device range.
// cycle ?�습(synced)�??�립. going off?�on rising 기반?�라 중간?�작 ?�이?��? ?�동 배제.
module MonitoringAdapterTests =

    let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    // device range(250~900) + ApiCall ??Out/In 주소 부??+ adapter.
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

    // baseline(off) ??�?깔고 off?�on rising ?�로 going/finish ??만든??
    let private goingThenFinish (adapter: MonitoringAbnormalAdapter) (goingMs: int) (finishMs: int) =
        adapter.OnObservedIo("Y0", "false", goingMs)   // OUT baseline
        adapter.OnObservedIo("Y0", "true", goingMs)    // OUT rising ??going
        adapter.OnObservedIo("X0", "false", goingMs)   // IN baseline
        adapter.OnObservedIo("X0", "true", finishMs)   // IN rising ??finish

    [<Fact>]
    let ``elapsed in range is normal ??no false positive`` () =
        let adapter, emitted, _, _, _ = setup ()
        goingThenFinish adapter 0 500          // elapsed 500 in [250,900]
        Assert.Empty(emitted)

    [<Fact>]
    let ``elapsed below Min is ActionUnder`` () =
        let adapter, emitted, _, _, _ = setup ()
        // 자동 줄자: 정상 ~500ms 3사이클로 학습(min≈350) → 100ms 는 학습 min 아래 → ActionUnder.
        for _ in 1..3 do goingThenFinish adapter 0 500
        emitted.Clear()
        goingThenFinish adapter 0 100
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionUnder, emitted.[0].Kind)

    // Over ??engine watchdog(onDeviceDurationExpired)??SSOT ??In ??Max ?�후 ??�� rising ?�도
    //   adapter ??over �??��? ?�는????? ?�싱 over ?�외, ?�용???�정).
    [<Fact>]
    let ``finish above Max does not emit (over is watchdog-only)`` () =
        let adapter, emitted, _, _, _ = setup ()
        goingThenFinish adapter 0 1000
        Assert.Empty(emitted)

    [<Fact>]
    let ``finish without observed going start is dropped (mid-cycle 1cycle)`` () =
        let adapter, emitted, _, _, _ = setup ()
        // OUT ???��? on ???�태�?관�??�작(baseline=on) ??going rising �?�?        adapter.OnObservedIo("Y0", "true", 0)  // baseline on, rising ?�님 ??going 기록 ????        adapter.OnObservedIo("X0", "false", 0)
        adapter.OnObservedIo("X0", "true", 100)
        Assert.Empty(emitted)                  // Out ?�재 on(mid-cycle) ??short ?�님

    [<Fact>]
    let ``finish without going and output off is SensorShort`` () =
        let adapter, emitted, _, _, _ = setup ()
        adapter.OnObservedIo("Y0", "false", 0)   // OUT off ??going ?�적 ?�음
        adapter.OnObservedIo("X0", "false", 0)   // IN baseline
        adapter.OnObservedIo("X0", "true", 100)  // IN rising ??Going ?�이 Finish
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorShort, emitted.[0].Kind)

    [<Fact>]
    let ``target carries Call ApiCall and Work ids`` () =
        let adapter, emitted, _, _, _ = setup ()
        for _ in 1..3 do goingThenFinish adapter 0 500   // 줄자 학습
        emitted.Clear()
        goingThenFinish adapter 0 100                     // 학습 min 아래 → ActionUnder 발행
        Assert.Single(emitted) |> ignore
        Assert.True(emitted.[0].Target.CallId.IsSome)
        Assert.True(emitted.[0].Target.ApiCallId.IsSome)
        Assert.True(emitted.[0].Target.WorkId.IsSome)

    [<Fact>]
    let ``in falling during Finish level sensor is SensorOpen`` () =
        let adapter, emitted, states, callId, _ = setup ()
        goingThenFinish adapter 0 500              // ?�상 going?�finish (elapsed 500 ??[250,900])
        Assert.Empty(emitted)
        states.[callId] <- Status4.Finish          // Call Finish(reset ?? ?��?
        adapter.OnObservedIo("X0", "false", 600)   // level ?�서 In falling ???�선 = SensorOpen
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    [<Fact>]
    let ``in falling when not Finish is not SensorOpen`` () =
        let adapter, emitted, states, callId, _ = setup ()
        goingThenFinish adapter 0 500
        Assert.Empty(emitted)
        states.[callId] <- Status4.Ready           // reset?�Ready = ?�상 종료, Open ?�님
        adapter.OnObservedIo("X0", "false", 600)
        Assert.Empty(emitted)

// device work = plan: In(?�제 IO) ?�이 duration plan ?�로 Going?�Finish ?�야 ?�다 (?�용???�정).
// "Control device Finish ???? ?��?�?코드 ?�벨?�서 못박???�합?�스??
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

        // Call going ??executeApiCall ??device work �?Going ?�로 force.
        engine.ForceCallState(call.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)   // forced going drain
        // In(actual) ?� ?��? 주입 ??????device ??In 무�? duration plan ?�로 Finish ?�야.
        engine.AdvanceSimulationTo(2000L)                  // device duration(<=900) ?�머

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
        // work.Duration 500 + ActionType timeAppend 200 ??device Going 지??= 500ms �?
        // timeAppend(출력 ?��?)??Going 막�?�??�이지 ?�는??간트??빨간 ?�선 ?�각?�로�??�기).
        apiDef.ActionType <- ActionType.Real(Level, Some(Append 200))
        apiDef.SensingType <- SensingType.Real(Level, None)
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Control)) :> ISimulationEngine

        engine.ForceCallState(call.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(400L)   // duration 500 미만 ???�직 Going
        Assert.Equal(Some Status4.Going, engine.GetWorkState(deviceWork.Id))
        engine.AdvanceSimulationTo(600L)   // duration 500 초과(timeAppend 무시) ??Finish
        Assert.Equal(Some Status4.Finish, engine.GetWorkState(deviceWork.Id))

    [<Fact>]
    let ``Monitoring device work finishes by duration plan (passive, forced going)`` () =
        // Monitoring ??device ??plan(duration) ?�로 Finish ?�야 ??passive ?�도.
        // ?�에??Monitoring device 가 Going ??박히?�데, 코드 ?�벨?�서 ?�는지(=??scheduler 문제?��?) 가른다.
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

        // passive 모드??HubSession ???�던 device going force �?직접 ?�내 (Out On ??device Going).
        engine.ForceWorkState(deviceWork.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(2000L)                  // plan duration(200) ?�머

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
