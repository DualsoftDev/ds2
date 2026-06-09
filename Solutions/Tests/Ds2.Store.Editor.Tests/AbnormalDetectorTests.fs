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

// v12 ¬ßP3a ??Control/Monitoring Í≥µÌÜµ abnormal detector ?êÏÇ∞ Í≤ÄÏ¶?
// ?ÅÏö© Í≥ÑÌöç: samples/Abnormal-v12-Apply-Plan.md ¬ß6 P3a.

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

// v12 ¬ß6/¬ß6.1 ??ILatchPolicy Í∏∞Î≥∏ Íµ¨ÌòÑ(DefaultLatchPolicy): KindÎ≥?latch ?àÎèÑ??
//   spec ?úÍ∑∏?àÏ≤ò ShouldEmit(previous, current) ??previous=Í∞ôÏ? (Kind,Target) ÏßÅÏ†Ñ Î∞úÌñâ.
//   Action*=5000ms dedup window, Sensor*=0(Ï¶âÏãú). timestamp Ï∞®Î°ú ?êÏ†ï.
module LatchPolicyTests =

    let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let private target = Abnormal.target (Some(Guid.NewGuid())) None None

    [<Fact>]
    let ``ShouldEmit true when no previous`` () =
        let policy = DefaultLatchPolicy() :> ILatchPolicy
        Assert.True(policy.ShouldEmit(None, Abnormal.actionOver target 1000 t0))

    // timing(Action*) ??5000ms ?àÎèÑ????Ï§ëÎ≥µ ?µÏ†ú (Control OnTick + rising dedup).
    [<Fact>]
    let ``DefaultLatchPolicy dedups Action within 5s window`` () =
        let policy = DefaultLatchPolicy() :> ILatchPolicy
        let prev = Abnormal.actionOver target 1000 t0
        let cur  = Abnormal.actionOver target 1000 (t0.AddMilliseconds 2000.0)   // 2s < 5s ???µÏ†ú
        Assert.False(policy.ShouldEmit(Some prev, cur))

    [<Fact>]
    let ``DefaultLatchPolicy re-emits Action after 5s window`` () =
        let policy = DefaultLatchPolicy() :> ILatchPolicy
        let prev = Abnormal.actionOver target 1000 t0
        let cur  = Abnormal.actionOver target 1000 (t0.AddMilliseconds 6000.0)   // 6s ??5s ??Î∞úÌñâ
        Assert.True(policy.ShouldEmit(Some prev, cur))

    // sensor(Short/Open) ???àÎèÑ??0 ??Ï¶âÏãú ?¨Î∞ú?? ?êÏ†ï Í∞ÑÍ≤© ?ÜÏù¥ Î∞îÎ°úÎ∞îÎ°ú ?†Ïïº ??
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

    // P2 SimulationTests ??Í≤ÄÏ¶ùÎêú device-range ?ãÏóÖ ?¨ÏÇ¨??
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

// v12 ¬ß2.3/¬ß4 ??canEvaluate gating: ?êÎèô Flow(IsAuto) ??Real sensing ??ÎπÑÏù∏?∞ÎùΩ(Call.Interlocked).
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

// v12 ¬ßP3b ??Control adapter 4 ÏºÄ?¥Ïä§ + ?§ÌÉê Î∞©Ï? + latch dedup.
module ControlAdapterTests =

    let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    // active Call + device range(250~900) + Î¨ºÎ¶¨ InTag(Level completion trigger) ?ãÏóÖ.
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

    // Over ??Max ?úÏ†ê OnTick ??SSOT ??InTag Í∞Ä Max ?¥ÌõÑ ??≤å rising ?¥ÎèÑ over Î•??¥Ï? ?äÎäî??    //   (??? ?ºÏã± over ???òÎ? ?ÜÏñ¥ ?úÏô∏, ?¨Ïö©???ïÏ†ï). over Î∞úÌñâ ?êÏ≤¥???ÑÎûò tick ?åÏä§?∏Í? Í≤ÄÏ¶?
    [<Fact>]
    let ``rising above Max does not emit (over is tick-only)`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 2000)   // elapsed 1000 > 900 ??rising Í≤ΩÎ°ú??over ????        Assert.Empty(emitted)

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
        states.[callId] <- Status4.Finish     // Finish ?†Ï?(reset ??
        adapter.OnInputFalling(apiCallId, 1500)
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    // v12 ¬ß3.2 ??RxWork?†Ready(Going ?¨Ìï®) Ï§?level ?ºÏÑú falling ??SensorOpen (Finish ÎßåÏù¥ ?ÑÎãò).
    [<Fact>]
    let ``falling during Going level sensor is SensorOpen`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnInputFalling(apiCallId, 1500)
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    // Ready(Ï∂úÎ∞ú ?? falling ?Ä ?ïÏÉÅ ??SensorOpen ?ÑÎãò.
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
        adapter.OnTick(2000)   // elapsed 1000 > 900, ?ÖÎ†• ÎØ∏ÎèÑ??        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionOver, emitted.[0].Kind)

    [<Fact>]
    let ``duplicate abnormal within latch window emits once`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 1100)   // ActionUnder
        adapter.OnInputRising(apiCallId, 1150)   // Í∞ôÏ? (Kind,Target) 5000ms ?????µÏ†ú
        Assert.Single(emitted) |> ignore

    // OnTick over ???¨Ïù¥?¥Îãπ 1??latch)ÏßÄÎß? OnCallReset ?ºÎ°ú latch Í∞Ä ÎπÑÎ©¥ ?§Ïùå ?¨Ïù¥?¥Ïóê ?§Ïãú ?êÏ†ï.
    [<Fact>]
    let ``tick over re-emits after OnCallReset (per-cycle latch clear)`` () =
        let adapter, emitted, states, inputActive, callId, apiCallId = setup ()
        states.[callId] <- Status4.Going
        inputActive.[apiCallId] <- false
        adapter.OnCallGoing(callId, 1000)
        adapter.OnTick(2000)                       // over #1 (elapsed 1000 > 900)
        adapter.OnTick(2100)                       // Í∞ôÏ? ?¨Ïù¥??5000ms ?????µÏ†ú
        Assert.Single(emitted) |> ignore
        adapter.OnCallReset(callId)                // ?¨Ïù¥??Ï¢ÖÎ£å ??latch clear
        adapter.OnCallGoing(callId, 3000)
        adapter.OnTick(4000)                       // over #2: 4000-2000<5000 ?¥Ï?Îß?reset ?ºÎ°ú ?¨Ìåê??        Assert.Equal(2, emitted.Count)

// v12 ¬ßP3c ??Monitoring adapter (IO-edge): OutTag On=going, InTag On=finish, elapsed vs device range.
// cycle ?ôÏäµ(synced)Í≥??ÖÎ¶Ω. going off?íon rising Í∏∞Î∞ò?¥Îùº Ï§ëÍ∞Ñ?úÏûë ?¨Ïù¥?¥Ï? ?êÎèô Î∞∞Ï†ú.
module MonitoringAdapterTests =

    let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    // device range(250~900) + ApiCall ??Out/In Ï£ºÏÜå Î∂Ä??+ adapter.
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

    // baseline(off) ??Î≤?ÍπîÍ≥† off?íon rising ?ºÎ°ú going/finish ??ÎßåÎì†??
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
        goingThenFinish adapter 0 100          // elapsed 100 < 250
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionUnder, emitted.[0].Kind)

    // Over ??engine watchdog(onDeviceDurationExpired)??SSOT ??In ??Max ?¥ÌõÑ ??≤å rising ?¥ÎèÑ
    //   adapter ??over Î•??¥Ï? ?äÎäî????? ?ºÏã± over ?úÏô∏, ?¨Ïö©???ïÏ†ï).
    [<Fact>]
    let ``finish above Max does not emit (over is watchdog-only)`` () =
        let adapter, emitted, _, _, _ = setup ()
        goingThenFinish adapter 0 1000         // elapsed 1000 > 900 ??finish Í≤ΩÎ°ú??over ????        Assert.Empty(emitted)

    [<Fact>]
    let ``finish without observed going start is dropped (mid-cycle 1cycle)`` () =
        let adapter, emitted, _, _, _ = setup ()
        // OUT ???¥Î? on ???ÅÌÉúÎ°?Í¥ÄÏ∏??úÏûë(baseline=on) ??going rising Î™?Î¥?        adapter.OnObservedIo("Y0", "true", 0)  // baseline on, rising ?ÑÎãò ??going Í∏∞Î°ù ????        adapter.OnObservedIo("X0", "false", 0)
        adapter.OnObservedIo("X0", "true", 100)
        Assert.Empty(emitted)                  // Out ?ÑÏû¨ on(mid-cycle) ??short ?ÑÎãò

    [<Fact>]
    let ``finish without going and output off is SensorShort`` () =
        let adapter, emitted, _, _, _ = setup ()
        adapter.OnObservedIo("Y0", "false", 0)   // OUT off ??going ?îÏ†Å ?ÜÏùå
        adapter.OnObservedIo("X0", "false", 0)   // IN baseline
        adapter.OnObservedIo("X0", "true", 100)  // IN rising ??Going ?ÜÏù¥ Finish
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
        goingThenFinish adapter 0 500              // ?ïÏÉÅ going?ífinish (elapsed 500 ??[250,900])
        Assert.Empty(emitted)
        states.[callId] <- Status4.Finish          // Call Finish(reset ?? ?†Ï?
        adapter.OnObservedIo("X0", "false", 600)   // level ?ºÏÑú In falling ???®ÏÑ† = SensorOpen
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    [<Fact>]
    let ``in falling when not Finish is not SensorOpen`` () =
        let adapter, emitted, states, callId, _ = setup ()
        goingThenFinish adapter 0 500
        Assert.Empty(emitted)
        states.[callId] <- Status4.Ready           // reset?íReady = ?ïÏÉÅ Ï¢ÖÎ£å, Open ?ÑÎãò
        adapter.OnObservedIo("X0", "false", 600)
        Assert.Empty(emitted)

// device work = plan: In(?§Ï†ú IO) ?ÜÏù¥ duration plan ?ºÎ°ú Going?íFinish ?¥Ïïº ?úÎã§ (?¨Ïö©???ïÏ†ï).
// "Control device Finish ???? ?åÍ?Î•?ÏΩîÎìú ?àÎ≤®?êÏÑú Î™ªÎ∞ï???µÌï©?åÏä§??
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

        // Call going ??executeApiCall ??device work Î•?Going ?ºÎ°ú force.
        engine.ForceCallState(call.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)   // forced going drain
        // In(actual) ?Ä ?àÎ? Ï£ºÏûÖ ??????device ??In Î¨¥Í? duration plan ?ºÎ°ú Finish ?¥Ïïº.
        engine.AdvanceSimulationTo(2000L)                  // device duration(<=900) ?àÎ®∏

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
        // work.Duration 500 + ActionType timeAppend 200 ??device Going ÏßÄ??= 500ms Îß?
        // timeAppend(Ï∂úÎ†• ?†Ï?)??Going ÎßâÎ?Î•??òÏù¥ÏßÄ ?äÎäî??Í∞ÑÌä∏??Îπ®Í∞Ñ ?êÏÑ† ?úÍ∞Å?îÎ°úÎß??úÍ∏∞).
        apiDef.ActionType <- ActionType.Real(Level, Some(Append 200))
        apiDef.SensingType <- SensingType.Real(Level, None)
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Control)) :> ISimulationEngine

        engine.ForceCallState(call.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(400L)   // duration 500 ÎØ∏Îßå ???ÑÏßÅ Going
        Assert.Equal(Some Status4.Going, engine.GetWorkState(deviceWork.Id))
        engine.AdvanceSimulationTo(600L)   // duration 500 Ï¥àÍ≥º(timeAppend Î¨¥Ïãú) ??Finish
        Assert.Equal(Some Status4.Finish, engine.GetWorkState(deviceWork.Id))

    [<Fact>]
    let ``Monitoring device work finishes by duration plan (passive, forced going)`` () =
        // Monitoring ??device ??plan(duration) ?ºÎ°ú Finish ?¥Ïïº ??passive ?ºÎèÑ.
        // ?±Ïóê??Monitoring device Í∞Ä Going ??Î∞ïÌûà?îÎç∞, ÏΩîÎìú ?àÎ≤®?êÏÑú ?òÎäîÏßÄ(=??scheduler Î¨∏Ï†ú?∏Ï?) Í∞ÄÎ•∏Îã§.
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

        // passive Î™®Îìú??HubSession ???òÎçò device going force Î•?ÏßÅÏ†ë ?âÎÇ¥ (Out On ??device Going).
        engine.ForceWorkState(deviceWork.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(2000L)                  // plan duration(200) ?àÎ®∏

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
