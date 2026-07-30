module Ds2.Store.Editor.Tests.AbnormalDetectorTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR
open Ds2.Backend
open Ds2.Backend.Common
open Ds2.Backend.Runtime
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers
open Ds2.Runtime.IO
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Abnormal
open Ds2.Runtime.Engine.Passive

type private NullClientProxy() =
    interface IClientProxy with
        member _.SendCoreAsync(_, _, _) = Task.CompletedTask

type private NullSingleClientProxy() =
    interface ISingleClientProxy with
        member _.InvokeCoreAsync<'T>(_, _, _) = Task.FromResult(Unchecked.defaultof<'T>)

    interface IClientProxy with
        member _.SendCoreAsync(_, _, _) = Task.CompletedTask

type private NullHubClients(proxy: IClientProxy, single: ISingleClientProxy) =
    interface IHubClients<IClientProxy> with
        member _.All = proxy
        member _.AllExcept _ = proxy
        member _.Client _ = proxy
        member _.Clients _ = proxy
        member _.Group _ = proxy
        member _.GroupExcept(_, _) = proxy
        member _.Groups _ = proxy
        member _.User _ = proxy
        member _.Users _ = proxy

    interface IHubClients with
        member _.Client _ = single

type private NullGroupManager() =
    interface IGroupManager with
        member _.AddToGroupAsync(_, _, _: CancellationToken) = Task.CompletedTask
        member _.RemoveFromGroupAsync(_, _, _: CancellationToken) = Task.CompletedTask

type private NullSignalHubContext() =
    let proxy = NullClientProxy() :> IClientProxy
    let single = NullSingleClientProxy() :> ISingleClientProxy
    let clients = NullHubClients(proxy, single) :> IHubClients
    let groups = NullGroupManager() :> IGroupManager

    interface IHubContext<SignalHub> with
        member _.Clients = clients
        member _.Groups = groups

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
        apiDef.SensingType <- SensingType.Normal None
        Assert.True(AbnormalDetector.isPhysicalSensing apiDef)

    [<Fact>]
    let ``isPhysicalSensing false for Virtual`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let apiDef = addApiDef store "ADV" system.Id
        apiDef.SensingType <- SensingType.Virtual 200
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
        apiDef.SensingType <- SensingType.Normal None
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
        def.SensingType <- SensingType.Virtual 200
        Assert.False(AbnormalDetector.canEvaluate store call.Id def)

// v12 §P3b ??Control adapter 4 케?�스 + ?�탐 방�? + latch dedup.
module ControlAdapterTests =

    let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    // active Call + device range(250~900) + physical InTag completion trigger setup.
    let private setupWithSensing sensingType =
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
        apiDef.SensingType <- sensingType
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
        adapter.IsMinMeasured <- fun _ -> true   // ActionUnder 게이트 기본 통과(대다수 테스트 편의). 미확정 케이스는 전용 테스트.
        adapter.IsMaxMeasured <- fun _ -> true   // ActionOver 게이트 기본 통과(대다수 테스트 편의).
        adapter, emitted, states, inputActive, call.Id, apiCall.Id

    let private setup () =
        setupWithSensing (SensingType.Normal None)

    let private setupLatched () =
        setupWithSensing (SensingType.Latch 50)

    let private setupStable () =
        setupWithSensing (SensingType.Normal (Some 50))

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

    [<Fact>]
    let ``ActionUnder suppressed when Min not measured`` () =
        // Min 이 실측 확정(calibration-state)되지 않은 Work 는 ActionUnder 비활성 — 게이트 false.
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        adapter.IsMinMeasured <- fun _ -> false
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 1100)   // elapsed 100 < 250 — 게이트 없으면 ActionUnder 였을 것
        Assert.Empty(emitted)

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

    // 디바이스 공유 — 같은 InTag 주소를 두 Call 이 단계별로 호출하는 모델.
    // 한 Call 이 기대 중(Going)일 때 들어온 신호는 Ready 인 동거 Call 에 Short 가 아니다.
    let private setupSharedAddress () =
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
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "A", [ apiDef.Id ]) |> ignore
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "B", [ apiDef.Id ]) |> ignore
        let calls = Queries.callsOf work.Id store
        let callA, callB = calls.[0], calls.[1]
        let apiCallA = callA.ApiCalls |> Seq.head
        let apiCallB = callB.ApiCalls |> Seq.head
        for ac in [ apiCallA; apiCallB ] do
            ac.InTag <- Some(IOTag("IN", "X0", ""))    // 같은 주소 공유
            ac.OutTag <- Some(IOTag("OUT", "Y0", ""))
        let index = SimIndex.build store 10
        let ioMap = SignalIOMap.build store
        let emitted = ResizeArray<AbnormalRecord>()
        let states = Dictionary<Guid, Status4>()
        let getCallState cid =
            match states.TryGetValue cid with
            | true, s -> s
            | _ -> Status4.Ready
        let adapter =
            ControlAbnormalAdapter(index, ioMap, getCallState, (fun _ -> false), (fun () -> t0), (fun r -> emitted.Add r))
        adapter, emitted, states, callA.Id, apiCallB.Id

    [<Fact>]
    let ``shared address rising is not Short while sibling call expects it`` () =
        let adapter, emitted, states, expectingCallId, readyApiCallId = setupSharedAddress ()
        states.[expectingCallId] <- Status4.Going
        adapter.OnCallGoing(expectingCallId, 1000)
        adapter.OnInputRising(readyApiCallId, 1500)   // Ready 쪽 매핑에 도착한 신호 — Going 쪽의 정상 완료
        Assert.DoesNotContain(emitted, fun r -> r.Kind = AbnormalKind.SensorShort)

    [<Fact>]
    let ``shared address rising is Short when nobody expects it`` () =
        let adapter, emitted, _, _, readyApiCallId = setupSharedAddress ()
        adapter.OnInputRising(readyApiCallId, 1500)   // 둘 다 Ready — 진짜 Short
        Assert.Contains(emitted, fun r -> r.Kind = AbnormalKind.SensorShort)

    // 묶음(멀티 디바이스) Call — Tester3 실모델 동형: 개별 Call 4개(주소 1쌍씩) +
    // 묶음 Call 1개(같은 디바이스 4개를 한 번에 = 같은 주소 4쌍). 묶음 차례(Going)에
    // In 4개가 동시 rising — Ready 인 개별 Call 들에 Short 가 아니다
    // (실기 Control 로그의 "사이클마다 ADV/RET 4개 일제 SensorShort" 재현 픽스처).
    let private setupGroupedSharedAddress () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let defs =
            [ for i in 1 .. 4 ->
                let dw = addWork store $"ADV{i}" deviceFlow.Id
                dw.MinDuration <- Some(TimeSpan.FromMilliseconds 250.0)
                dw.MaxDuration <- Some(TimeSpan.FromMilliseconds 900.0)
                let d = addApiDef store $"ADV{i}" deviceSystem.Id
                d.TxGuid <- Some dw.Id
                d.RxGuid <- Some dw.Id
                d ]
        let individualCallIds =
            defs |> List.mapi (fun i d -> store.AddCallWithLinkedApiDefs(work.Id, "Device", $"ADV{i + 1}", [ d.Id ]))
        let groupCallId = store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADVALL", [ for d in defs -> d.Id ])
        // 같은 디바이스의 ApiCall 은 개별/묶음 모두 같은 In/Out 주소 — ApiDefId 로 짝 맞춤.
        let addressOf =
            defs |> List.mapi (fun i d -> d.Id, ($"%%I110{i + 1}", $"%%Q100{i + 1}")) |> dict
        for callId in groupCallId :: individualCallIds do
            for ac in store.Calls.[callId].ApiCalls do
                match ac.ApiDefId with
                | Some defId ->
                    let inAddr, outAddr = addressOf.[defId]
                    ac.InTag <- Some(IOTag("IN", inAddr, ""))
                    ac.OutTag <- Some(IOTag("OUT", outAddr, ""))
                | None -> ()
        let index = SimIndex.build store 10
        let ioMap = SignalIOMap.build store
        let emitted = ResizeArray<AbnormalRecord>()
        let states = Dictionary<Guid, Status4>()
        let getCallState cid =
            match states.TryGetValue cid with
            | true, s -> s
            | _ -> Status4.Ready
        let adapter =
            ControlAbnormalAdapter(index, ioMap, getCallState, (fun _ -> false), (fun () -> t0), (fun r -> emitted.Add r))
        let individualApiCallIds =
            [ for cid in individualCallIds -> (store.Calls.[cid].ApiCalls |> Seq.head).Id ]
        adapter, emitted, states, groupCallId, individualApiCallIds

    [<Fact>]
    let ``grouped call going suppresses Short on all individual sibling mappings`` () =
        let adapter, emitted, states, groupCallId, individualApiCallIds = setupGroupedSharedAddress ()
        states.[groupCallId] <- Status4.Going
        adapter.OnCallGoing(groupCallId, 1000)
        for apiCallId in individualApiCallIds do
            adapter.OnInputRising(apiCallId, 1500)   // 묶음 차례의 In 4개 — 개별(Ready)에 Short 아님
        Assert.DoesNotContain(emitted, fun r -> r.Kind = AbnormalKind.SensorShort)

    [<Fact>]
    let ``grouped fixture rising is Short when nobody is going`` () =
        let adapter, emitted, _, _, individualApiCallIds = setupGroupedSharedAddress ()
        adapter.OnInputRising(List.head individualApiCallIds, 1500)   // 전부 Ready — 진짜 Short
        Assert.Contains(emitted, fun r -> r.Kind = AbnormalKind.SensorShort)

    // 워밍업 게이트 — 시작 직후는 신호 순서/잔류 상태가 정착 전이라 Call 별 첫 N 완주
    // 사이클은 판정하지 않는다 (Control=1. VP/Monitoring 은 인퍼런스 Synced 워밍업이 대신).
    let private setupWithWarmup () =
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
        apiDef.SensingType <- SensingType.Normal None
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.InTag <- Some(IOTag("IN", "X0", ""))
        apiCall.OutTag <- Some(IOTag("OUT", "Y0", ""))
        let index = SimIndex.build store 10
        let ioMap = SignalIOMap.build store
        let emitted = ResizeArray<AbnormalRecord>()
        let states = Dictionary<Guid, Status4>()
        let getCallState cid =
            match states.TryGetValue cid with
            | true, s -> s
            | _ -> Status4.Ready
        let adapter =
            ControlAbnormalAdapter(
                index, ioMap, getCallState, (fun _ -> false), (fun () -> t0),
                (fun r -> emitted.Add r), warmupCycles = 1)
        adapter.IsMinMeasured <- fun _ -> true   // ActionUnder 게이트 통과(이 테스트는 워밍업·발행 검증).
        adapter, emitted, states, call.Id, apiCall.Id

    [<Fact>]
    let ``warmup first cycle suppresses judgement then second cycle judges`` () =
        let adapter, emitted, states, callId, apiCallId = setupWithWarmup ()

        // 첫 사이클 — Short 도 Under 도 억제.
        adapter.OnInputRising(apiCallId, 100)        // Ready 중 rising — 워밍업이라 Short 아님
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 1000)
        adapter.OnInputRising(apiCallId, 1100)       // elapsed 100 < Min 250 — 워밍업이라 Under 아님
        Assert.Empty(emitted)

        // 첫 사이클 완주(Going 을 거쳐 Ready 복귀) → 둘째 사이클부터 판정.
        states.[callId] <- Status4.Ready
        adapter.OnCallReset(callId)
        states.[callId] <- Status4.Going
        adapter.OnCallGoing(callId, 5000)
        adapter.OnInputRising(apiCallId, 5100)       // elapsed 100 < 250 — 이제 ActionUnder
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionUnder, emitted.[0].Kind)

    // Ready(출발 ?? falling ?� ?�상 ??SensorOpen ?�님.
    [<Fact>]
    let ``falling when Ready is not SensorOpen`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Ready
        adapter.OnInputFalling(apiCallId, 1500)
        Assert.Empty(emitted)

    [<Fact>]
    let ``falling during Finish level sensor is not SensorOpen`` () =
        let adapter, emitted, states, _, callId, apiCallId = setup ()
        states.[callId] <- Status4.Finish
        adapter.OnInputFalling(apiCallId, 1500)
        Assert.Empty(emitted)

    // Normal(Some T) = 감지 후 T 유지 약속 — Finish 중 falling 은 단선/이탈 = SensorOpen.
    [<Fact>]
    let ``falling during Finish stable sensor is SensorOpen`` () =
        let adapter, emitted, states, _, callId, apiCallId = setupStable ()
        states.[callId] <- Status4.Finish
        adapter.OnInputFalling(apiCallId, 1500)
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    // Latch(T) = 채터링 허용 — falling 은 abnormal 이 아니다.
    [<Fact>]
    let ``falling during Finish latch sensor is not SensorOpen`` () =
        let adapter, emitted, states, _, callId, apiCallId = setupLatched ()
        states.[callId] <- Status4.Finish
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

    // device range(250~900) + ApiCall Out/In addresses + adapter.
    let private setupWithSensing sensingType =
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
        apiDef.SensingType <- sensingType
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
        adapter.IsMinMeasured <- fun _ -> true   // ActionUnder 게이트 기본 통과(대다수 테스트 편의). 미확정 케이스는 전용 테스트.
        adapter, emitted, states, call.Id, apiCall.Id

    let private setup () =
        setupWithSensing (SensingType.Normal None)

    let private setupLatched () =
        setupWithSensing (SensingType.Latch 50)

    let private setupStable () =
        setupWithSensing (SensingType.Normal (Some 50))

    // baseline(off) ??�?깔고 off?�on rising ?�로 going/finish ??만든??
    let private goingThenFinish (adapter: MonitoringAbnormalAdapter) (goingMs: int) (finishMs: int) =
        adapter.OnObservedIo("Y0", "false", int64 goingMs)   // OUT baseline
        adapter.OnObservedIo("Y0", "true", int64 goingMs)    // OUT rising ??going
        adapter.OnObservedIo("X0", "false", int64 goingMs)   // IN baseline
        adapter.OnObservedIo("X0", "true", int64 finishMs)   // IN rising ??finish

    [<Fact>]
    let ``elapsed in range is normal ??no false positive`` () =
        let adapter, emitted, _, _, _ = setup ()
        goingThenFinish adapter 0 500          // elapsed 500 in [250,900]
        Assert.Empty(emitted)

    [<Fact>]
    let ``elapsed below Min is ActionUnder`` () =
        let adapter, emitted, _, _, _ = setup ()
        // 자동 줄자: 정상 ~500ms 3사이클로 학습(Min = 500 - (500*0.05 + 100*1.5) = 325) → 100 < 325 → ActionUnder.
        for _ in 1..3 do goingThenFinish adapter 0 500
        emitted.Clear()
        goingThenFinish adapter 0 100
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionUnder, emitted.[0].Kind)

    [<Fact>]
    let ``ActionUnder suppressed when Min not measured`` () =
        // Min 실측 미확정(calibration-state) Work 는 학습 Min 아래여도 ActionUnder 비활성 — 게이트 false.
        let adapter, emitted, _, _, _ = setup ()
        adapter.IsMinMeasured <- fun _ -> false
        for _ in 1..3 do goingThenFinish adapter 0 500
        emitted.Clear()
        goingThenFinish adapter 0 100   // 학습 Min 아래지만 미확정 → 비발행
        Assert.Empty(emitted)

    // 자동 정합 OFF — 학습 줄자 무시, 모델 WorkDurationRange(setup: Min 250) 기준 판정.
    // ON 이면 ~500 학습 → Min 325 라 300ms 가 Under 였겠지만, OFF 는 모델 Min 250 기준 → 300 정상.
    // (모델 확정값을 신뢰하는 모드 — 양자화 빠른값이 학습 경계에 걸리던 오탐을 끄는 경로.)
    [<Fact>]
    let ``autoCalibrate OFF judges by model range not learned`` () =
        let adapter, emitted, _, _, _ = setup ()
        adapter.AutoCalibrate <- false
        // 300ms — 모델 Min(250) 위라 정상. (학습 모드였으면 Min 325 라 Under)
        goingThenFinish adapter 0 300
        Assert.Empty(emitted)
        // 100ms — 모델 Min(250) 아래 → 모델 기준 ActionUnder (학습 아님).
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
        // If OUT was already on before Monitoring attached, a later IN rise is
        // not enough evidence to call a 1-cycle short.
        adapter.OnObservedIo("Y0", "true", 0L)
        adapter.OnObservedIo("X0", "false", 0L)
        adapter.OnObservedIo("X0", "true", 100L)
        Assert.Empty(emitted)

    [<Fact>]
    let ``finish before any output observation is dropped (unknown baseline)`` () =
        let adapter, emitted, _, _, _ = setup ()
        adapter.OnObservedIo("X0", "false", 0L)
        adapter.OnObservedIo("X0", "true", 100L)
        Assert.Empty(emitted)

    [<Fact>]
    let ``finish without going and output off is SensorShort`` () =
        // Short 의 전제 = 이 OUT 의 rising 을 *edge 로 직접* 본 적이 있을 것 —
        // OUT=off "값"만으로는(시작/주기 resync baseline 주입으로도 채워짐) 증거가 못 된다.
        // 한 사이클을 정상 완주(OUT rising/falling 관측)한 뒤의 유령 IN 이 진짜 Short.
        let adapter, emitted, _, _, _ = setup ()
        goingThenFinish adapter 0 500            // 정상 1사이클 — OUT rising 을 edge 로 관측
        emitted.Clear()
        adapter.OnObservedIo("Y0", "false", 1000L)  // OUT off (사이클 종료)
        adapter.OnObservedIo("X0", "false", 1000L)
        adapter.OnObservedIo("X0", "true", 1500L)   // 유령 IN rising — Going 없이 Finish
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorShort, emitted.[0].Kind)

    [<Fact>]
    let ``finish with only baseline OUT off observation is dropped (resync attach)`` () =
        // 시작/주기 resync 가 OUT=off 를 baseline 으로 주입한 직후(중간 합류) —
        // rising 을 edge 로 본 적 없는 OUT 의 IN rising 은 Short 증거 불충분.
        let adapter, emitted, _, _, _ = setup ()
        adapter.OnObservedIo("Y0", "false", 0L)   // baseline 주입과 동형 — edge 아님
        adapter.OnObservedIo("X0", "false", 0L)
        adapter.OnObservedIo("X0", "true", 100L)
        Assert.Empty(emitted)

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
    let ``in falling when not Finish is not SensorOpen`` () =
        let adapter, emitted, states, callId, _ = setup ()
        goingThenFinish adapter 0 500
        Assert.Empty(emitted)
        states.[callId] <- Status4.Ready           // reset?�Ready = ?�상 종료, Open ?�님
        adapter.OnObservedIo("X0", "false", 600L)
        Assert.Empty(emitted)

    [<Fact>]
    let ``in falling during Finish level sensor is not SensorOpen`` () =
        let adapter, emitted, states, callId, _ = setup ()
        goingThenFinish adapter 0 500
        Assert.Empty(emitted)
        states.[callId] <- Status4.Finish
        adapter.OnObservedIo("X0", "false", 600L)
        Assert.Empty(emitted)

    // Normal(Some T) = 감지 후 T 유지 약속 — Finish 중 In falling = SensorOpen (출력 활성 중).
    [<Fact>]
    let ``in falling during Finish stable sensor is SensorOpen while output active`` () =
        let adapter, emitted, states, callId, _ = setupStable ()
        goingThenFinish adapter 0 500
        Assert.Empty(emitted)
        states.[callId] <- Status4.Finish
        adapter.OnObservedIo("X0", "false", 600L)
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.SensorOpen, emitted.[0].Kind)

    // Latch(T) = 채터링 허용 — Finish 중 In falling 은 abnormal 이 아니다.
    [<Fact>]
    let ``in falling during Finish latch sensor is not SensorOpen`` () =
        let adapter, emitted, states, callId, _ = setupLatched ()
        goingThenFinish adapter 0 500
        Assert.Empty(emitted)
        states.[callId] <- Status4.Finish
        adapter.OnObservedIo("X0", "false", 600L)
        Assert.Empty(emitted)

    [<Fact>]
    let ``in falling during Finish stable sensor is not SensorOpen after output off`` () =
        let adapter, emitted, states, callId, _ = setupStable ()
        goingThenFinish adapter 0 500
        Assert.Empty(emitted)
        states.[callId] <- Status4.Finish
        adapter.OnObservedIo("Y0", "false", 550L)
        adapter.OnObservedIo("X0", "false", 600L)
        Assert.Empty(emitted)

    // ── OUT-falling ActionOver (doc/ACTIONOVER_MONITORING_MISS_AGENT_FIX_HANDOFF_2026-07-03.md §7 옵션A) ──
    // 라인 전체 정지(관측 블랙아웃) 중 엔진 due-tick 이 평가 기회를 못 얻어도 OUT falling 은 실제로
    // 들어오는 이벤트 — 그 순간 Going + IN 미도달(goingClock 잔존) + 모델 Max 초과면 ActionOver 발행.
    // 16:33 실사건 재현: OUT rising(t0) → 15초 뒤 OUT falling, IN 은 끝내 미도달.

    [<Fact>]
    let ``out falling with call Going and elapsed above Max emits ActionOver with measured elapsed`` () =
        let adapter, emitted, states, callId, _ = setup ()
        adapter.IsMaxMeasured <- fun _ -> true
        states.[callId] <- Status4.Going
        adapter.OnObservedIo("Y0", "false", 0L)      // OUT baseline
        adapter.OnObservedIo("Y0", "true", 0L)       // OUT rising → goingClock=0
        adapter.OnObservedIo("X0", "false", 0L)      // IN baseline — 끝내 미도달
        adapter.OnObservedIo("Y0", "false", 1500L)   // OUT falling, elapsed 1500 > Max 900
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionOver, emitted.[0].Kind)
        // elapsed 는 실측(Control adapter 규약) — 엔진 due 경로의 MaxMs+1 과 다르다.
        Assert.Equal(1500, emitted.[0].ElapsedMs |> Option.defaultValue -1)

    [<Fact>]
    let ``out falling within Max is not ActionOver`` () =
        let adapter, emitted, states, callId, _ = setup ()
        adapter.IsMaxMeasured <- fun _ -> true
        states.[callId] <- Status4.Going
        adapter.OnObservedIo("Y0", "false", 0L)
        adapter.OnObservedIo("Y0", "true", 0L)
        adapter.OnObservedIo("X0", "false", 0L)
        adapter.OnObservedIo("Y0", "false", 500L)    // elapsed 500 ≤ 900 — 정상 범위 종료
        Assert.Empty(emitted)

    [<Fact>]
    let ``out falling after normal finish is not ActionOver (goingClock consumed)`` () =
        let adapter, emitted, states, callId, _ = setup ()
        adapter.IsMaxMeasured <- fun _ -> true
        states.[callId] <- Status4.Going            // Going 이어도 goingClock 부재면 발행 없음
        goingThenFinish adapter 0 500               // IN rising 이 goingClock 소비
        adapter.OnObservedIo("Y0", "false", 1500L)
        Assert.Empty(emitted)

    [<Fact>]
    let ``out falling ActionOver suppressed when Max not measured`` () =
        // IsMaxMeasured 기본 false — 엔진 watchdog 와 동일 게이트 유지(§7 가드2).
        let adapter, emitted, states, callId, _ = setup ()
        states.[callId] <- Status4.Going
        adapter.OnObservedIo("Y0", "false", 0L)
        adapter.OnObservedIo("Y0", "true", 0L)
        adapter.OnObservedIo("X0", "false", 0L)
        adapter.OnObservedIo("Y0", "false", 1500L)
        Assert.Empty(emitted)

    [<Fact>]
    let ``out falling ActionOver suppressed when call not Going`` () =
        let adapter, emitted, _, _, _ = setup ()
        adapter.IsMaxMeasured <- fun _ -> true
        // states 미설정 → Ready (사이클 이미 종료/리셋된 뒤의 OUT off)
        adapter.OnObservedIo("Y0", "false", 0L)
        adapter.OnObservedIo("Y0", "true", 0L)
        adapter.OnObservedIo("X0", "false", 0L)
        adapter.OnObservedIo("Y0", "false", 1500L)
        Assert.Empty(emitted)

    [<Fact>]
    let ``out falling after blackout invalidation is not ActionOver`` () =
        // 통신 blackout 가드 — InvalidateObservations 가 goingClock 을 비워 단절 구간이 낀
        // elapsed 로는 발행 자체가 안 만들어진다(재개 첫 관측은 baseline → rising 아님).
        let adapter, emitted, states, callId, _ = setup ()
        adapter.IsMaxMeasured <- fun _ -> true
        states.[callId] <- Status4.Going
        adapter.OnObservedIo("Y0", "false", 0L)
        adapter.OnObservedIo("Y0", "true", 0L)       // goingClock=0
        adapter.OnObservedIo("X0", "false", 0L)
        adapter.InvalidateObservations()            // PLC 단절
        adapter.OnObservedIo("Y0", "true", 1000L)    // 재개 첫 관측 = baseline — goingClock 안 찍힘
        adapter.OnObservedIo("Y0", "false", 1500L)   // falling 이지만 goingClock 부재 → 발행 없음
        Assert.Empty(emitted)

    [<Fact>]
    let ``late in rising after out-falling ActionOver is not SensorShort`` () =
        // goingClock 을 발행 후에도 유지하는 이유 — 뒤늦은 IN(실사건 16:46:58)이 정상 finish 경로를
        // 타야 "goingClock 부재+OUT off" 조합의 SensorShort 오판이 없다.
        let adapter, emitted, states, callId, _ = setup ()
        adapter.IsMaxMeasured <- fun _ -> true
        states.[callId] <- Status4.Going
        adapter.OnObservedIo("Y0", "false", 0L)
        adapter.OnObservedIo("Y0", "true", 0L)
        adapter.OnObservedIo("X0", "false", 0L)
        adapter.OnObservedIo("Y0", "false", 1500L)   // ActionOver 1회
        adapter.OnObservedIo("X0", "true", 2000L)    // 뒤늦은 IN rising — 정상 finish 경로(goingClock 소비)
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionOver, emitted.[0].Kind)

    [<Fact>]
    let ``next cycle re-emits ActionOver after new out rising`` () =
        let adapter, emitted, states, callId, _ = setup ()
        adapter.IsMaxMeasured <- fun _ -> true
        states.[callId] <- Status4.Going
        adapter.OnObservedIo("Y0", "false", 0L)
        adapter.OnObservedIo("Y0", "true", 0L)
        adapter.OnObservedIo("X0", "false", 0L)
        adapter.OnObservedIo("Y0", "false", 1500L)   // 사이클1 ActionOver
        adapter.OnObservedIo("Y0", "true", 2000L)    // 사이클2 시작 — latch 클리어 + goingClock=2000
        adapter.OnObservedIo("Y0", "false", 3500L)   // elapsed 1500 > 900 → 재발행
        Assert.Equal(2, emitted.Count)
        Assert.Equal(AbnormalKind.ActionOver, emitted.[0].Kind)
        Assert.Equal(AbnormalKind.ActionOver, emitted.[1].Kind)

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
        apiDef.SensingType <- SensingType.Normal None
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
        apiDef.SensingType <- SensingType.Normal None
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.OutTag <- Some(IOTag("OUT", "Y0", ""))
        apiCall.InTag <- Some(IOTag("IN", "X0", ""))
        let index = SimIndex.build store 10
        use edEngine = new EventDrivenEngine(index, RuntimeMode.Control)
        edEngine.SetMaxMeasured(System.Func<Guid, bool>(fun _ -> true))   // ActionOver 게이트 통과
        let engine = edEngine :> ISimulationEngine
        let emitted = ResizeArray<AbnormalRecord>()
        engine.AbnormalDetected.Add(fun record -> emitted.Add record)

        engine.ForceCallState(call.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(200L)
        Assert.Equal(Some Status4.Finish, engine.GetWorkState(deviceWork.Id))

        // 첫 사이클은 워밍업(Control=1) — Max 초과여도 판정 억제.
        engine.AdvanceSimulationTo(901L)
        Assert.Empty(emitted)

        // 첫 사이클 완주(Ready 복귀) 후 둘째 사이클 — 이제 Max 초과 시 ActionOver.
        // device work 도 Ready 로 되돌려 재사이클(OnTick 은 device duration 만료 이벤트에서 돈다).
        engine.ForceCallState(call.Id, Status4.Ready)
        engine.ForceWorkState(deviceWork.Id, Status4.Ready)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.ForceCallState(call.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        Assert.Equal(Some Status4.Going, engine.GetCallState(call.Id))
        engine.AdvanceSimulationTo(engine.CurrentTimeMs + 901L)
        Assert.Equal(Some Status4.Going, engine.GetCallState(call.Id))

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
        apiDef.ActionType <- ActionType.Normal (Some 200)
        apiDef.SensingType <- SensingType.Normal None
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
        apiDef.ActionType <- ActionType.Normal (Some 200)
        apiDef.SensingType <- SensingType.Normal None
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Monitoring)) :> ISimulationEngine

        // passive 모드??HubSession ???�던 device going force �?직접 ?�내 (Out On ??device Going).
        engine.ForceWorkState(deviceWork.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(2000L)                  // plan duration(200) ?�머

        Assert.Equal(Some Status4.Finish, engine.GetWorkState(deviceWork.Id))

    [<Fact>]
    let ``Monitoring delays ActionOver until PLC observation grace expires`` () =
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
        apiDef.SensingType <- SensingType.Normal None
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.OutTag <- Some(IOTag("OUT", "Y0", ""))
        apiCall.InTag <- Some(IOTag("IN", "X0", ""))
        let index = SimIndex.build store 10
        use edEngine = new EventDrivenEngine(index, RuntimeMode.Monitoring)
        edEngine.SetMaxMeasured(System.Func<Guid, bool>(fun _ -> true))   // ActionOver 게이트 통과
        let engine = edEngine :> ISimulationEngine
        let emitted = ResizeArray<AbnormalRecord>()
        engine.AbnormalDetected.Add(fun record -> emitted.Add record)

        engine.ForceCallState(call.Id, Status4.Going)
        engine.ForceWorkState(deviceWork.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(200L)
        Assert.Equal(Some Status4.Finish, engine.GetWorkState(deviceWork.Id))

        engine.AdvanceSimulationTo(901L)
        Assert.Empty(emitted)

        engine.AdvanceSimulationTo(1151L)

        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionOver, emitted.[0].Kind)

    [<Fact>]
    let ``Monitoring suppresses ActionOver when input arrives within PLC observation grace`` () =
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
        apiDef.SensingType <- SensingType.Normal None
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.OutTag <- Some(IOTag("OUT", "Y0", ""))
        apiCall.InTag <- Some(IOTag("IN", "X0", ""))
        let index = SimIndex.build store 10
        use edEngine = new EventDrivenEngine(index, RuntimeMode.Monitoring)
        edEngine.SetMaxMeasured(System.Func<Guid, bool>(fun _ -> true))   // 게이트는 통과시켜 'input 도착 억제' 로직 자체를 검증
        let engine = edEngine :> ISimulationEngine
        let emitted = ResizeArray<AbnormalRecord>()
        engine.AbnormalDetected.Add(fun record -> emitted.Add record)

        engine.ForceCallState(call.Id, Status4.Going)
        engine.ForceWorkState(deviceWork.Id, Status4.Going)
        engine.AdvanceSimulationTo(engine.CurrentTimeMs)
        engine.AdvanceSimulationTo(901L)
        engine.InjectIOValue(apiCall.Id, "true")
        engine.AdvanceSimulationTo(1151L)

        Assert.Empty(emitted)

    [<Fact>]
    let ``Monitoring batch finishes call when output falls before input rises in same PLC scan`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "RET" deviceFlow.Id
        deviceWork.Duration <- Some(TimeSpan.FromMilliseconds 100.0)
        deviceWork.MinDuration <- Some(TimeSpan.FromMilliseconds 50.0)
        deviceWork.MaxDuration <- Some(TimeSpan.FromMilliseconds 3000.0)
        let apiDef = addApiDef store "RET" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        apiDef.ActionType <- ActionType.Normal None
        apiDef.SensingType <- SensingType.Normal None
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "RET", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.OutTag <- Some(IOTag("OUT", "%QX0.1.13", ""))
        apiCall.InTag <- Some(IOTag("IN", "%IX0.0.13", ""))
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Monitoring)) :> ISimulationEngine
        let identity =
            { SessionId = "test-session"
              ModelHash = "test-model"
              Generation = 1
              Mode = "Monitoring" }
        let session = EventDrivenEngineRuntimeHubSession(engine, NullSignalHubContext(), identity, 100, System.Func<Guid, bool>(fun _ -> true), System.Func<Guid, bool>(fun _ -> true))
        let command : RuntimeIOAddressBatchCommand =
            { Envelope = RuntimeHubDefaults.selfEnvelope identity
              Items =
                // WallClockMs=0 — 이 테스트는 OriginTsMs 기반 판정만 검증한다(기록 시각은 관심 밖).
                [| { Address = "%QX0.1.13"; Value = "true"; Source = HubSource.Plc; OriginTsMs = 0L; WallClockMs = 0L }
                   { Address = "%QX0.1.13"; Value = "false"; Source = HubSource.Plc; OriginTsMs = 0L; WallClockMs = 0L }
                   { Address = "%IX0.0.13"; Value = "true"; Source = HubSource.Plc; OriginTsMs = 0L; WallClockMs = 0L } |] }

        (session :> IRuntimeHubSession)
            .InjectIOValuesByAddressAsync(command)
            .GetAwaiter()
            .GetResult()

        Assert.Equal(Some Status4.Finish, engine.GetCallState(call.Id))

    [<Fact>]
    let ``Monitoring Virtual sensing call self-finishes at output plus T without input`` () =
        // v16 Virtual 센싱(Monitoring passive): IN 이 없는 Virtual call 은 출력 관측 + T 경과 시 셀프 finish.
        //   능동 엔진(Call Going 진입 시 ScheduleAfter T)과 동형. passive 는 scheduler 가 없어
        //   다음 IO 관측 틱에 elapsed(실시간)≥T 로 finish 시킨다.
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "VIRT" deviceFlow.Id
        deviceWork.Duration <- Some(TimeSpan.FromMilliseconds 50.0)
        let apiDef = addApiDef store "VIRT" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        apiDef.ActionType <- ActionType.Normal None
        apiDef.SensingType <- SensingType.Virtual 50      // IN 없는 가상 센싱, T=50ms
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "VIRT", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.OutTag <- Some(IOTag("OUT", "%QX0.1.20", ""))
        // IN 없음 — Virtual
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Monitoring)) :> ISimulationEngine
        let identity =
            { SessionId = "test-session"
              ModelHash = "test-model"
              Generation = 1
              Mode = "Monitoring" }
        let session = EventDrivenEngineRuntimeHubSession(engine, NullSignalHubContext(), identity, 100, System.Func<Guid, bool>(fun _ -> true), System.Func<Guid, bool>(fun _ -> true))
        let inject addr v ts =
            let cmd : RuntimeIOAddressBatchCommand =
                { Envelope = RuntimeHubDefaults.selfEnvelope identity
                  Items = [| { Address = addr; Value = v; Source = HubSource.Plc; OriginTsMs = ts; WallClockMs = 0L } |] }
            (session :> IRuntimeHubSession).InjectIOValuesByAddressAsync(cmd).GetAwaiter().GetResult()

        // 출력 관측(원천 ts=0) → Going
        inject "%QX0.1.20" "true" 0L
        Assert.Equal(Some Status4.Going, engine.GetCallState(call.Id))

        // 원천 ts 가 T(50ms) 경과한 다음 관측 → 셀프 finish (IN 없이). 원천 ts 기반이라 Sleep 불필요(결정론).
        inject "%QX0.1.20" "false" 200L
        Assert.Equal(Some Status4.Finish, engine.GetCallState(call.Id))

    [<Fact>]
    let ``PassiveInference Virtual call records going and self-finishes after T (unit)`` () =
        // 로직 격리: drain/work-cycle 없이 PassiveInferenceSession 단독으로 going 기록 + T 후 finish 확인.
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let deviceSystem = addSystem store "Device" project.Id false
        let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
        let deviceWork = addWork store "VIRT2" deviceFlow.Id
        let apiDef = addApiDef store "VIRT2" deviceSystem.Id
        apiDef.TxGuid <- Some deviceWork.Id
        apiDef.RxGuid <- Some deviceWork.Id
        apiDef.ActionType <- ActionType.Normal None
        apiDef.SensingType <- SensingType.Virtual 50
        store.AddCallWithLinkedApiDefs(work.Id, "Device", "VIRT2", [ apiDef.Id ]) |> ignore
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        apiCall.OutTag <- Some(IOTag("OUT", "%QX0.2.0", ""))
        let index = SimIndex.build store 10
        use engine = (new EventDrivenEngine(index, RuntimeMode.Monitoring)) :> ISimulationEngine
        let session = PassiveInferenceSession(index, engine.IOMap, RuntimeMode.Monitoring)
        let mutable cs = Status4.Ready
        let getC = System.Func<Guid, Status4>(fun _ -> cs)
        let getW = System.Func<Guid, Status4>(fun _ -> Status4.Ready)

        let a1 = session.Observe("%QX0.2.0", "true", getW, getC, 0L)
        Assert.Contains(Status4.Going, a1 |> Array.map (fun x -> x.State))
        cs <- Status4.Going
        // 원천 ts 100 > T 50 → finish (Sleep 불필요, 명시 ts 로 결정론)
        let a2 = session.TickVirtualFinish(getC, 100L)
        Assert.Contains(Status4.Finish, a2 |> Array.map (fun x -> x.State))
