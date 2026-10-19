module Ds2.Runtime.Tests.SystemScopeTests

open System
open System.Collections.Generic
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.TestKit.ModelBuilder
open Ds2.Runtime.IO
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Abnormal
open Ds2.Runtime.Engine.Passive

// 멀티 PLC — 서로 다른 System 이 같은 IO 주소(Y0/X0)를 쓸 때, 관측 systemId 로 매핑을 좁혀
// 한 PLC 신호가 다른 System 의 Call 을 전이·추론·이상판정시키지 않는지(유령 발화) 검증한다.
// systemId 미제공("" — 구버전 송신자)은 종전 동작(주소 단독 매칭)이어야 한다.

type private Line =
    { SystemId: Guid
      CallId: Guid
      DeviceWorkId: Guid }

let private addLine (store: DsStore) (projectId: Guid) (name: string) =
    let system = addSystem store name projectId true
    let flow = addFlow store (name + "Flow") system.Id
    let work = addWork store "W" flow.Id
    let deviceSystem = addSystem store (name + "Dev") projectId false
    let deviceFlow = addFlow store (name + "DevFlow") deviceSystem.Id
    let deviceWork = addWork store "ADV" deviceFlow.Id
    deviceWork.MinDuration <- Some(TimeSpan.FromMilliseconds 250.0)
    deviceWork.MaxDuration <- Some(TimeSpan.FromMilliseconds 900.0)
    let apiDef = addApiDef store "ADV" deviceSystem.Id
    apiDef.TxGuid <- Some deviceWork.Id
    apiDef.RxGuid <- Some deviceWork.Id
    addCallWithLinkedApiDefs store work.Id (name + "Dev") "ADV" [ apiDef.Id ] |> ignore
    let call = Queries.callsOf work.Id store |> List.head
    let apiCall = call.ApiCalls |> Seq.head
    apiCall.OutTag <- Some(IOTag("OUT", "Y0", ""))
    apiCall.InTag <- Some(IOTag("IN", "X0", ""))
    { SystemId = system.Id; CallId = call.Id; DeviceWorkId = deviceWork.Id }

/// System A·B 가 같은 주소 Y0(OUT)/X0(IN)를 쓰는 모델.
let private setupSharedAddressLines () =
    let store = createStore ()
    let project = addProject store "P"
    let a = addLine store project.Id "A"
    let b = addLine store project.Id "B"
    store, a, b

let private sysText (line: Line) = line.SystemId.ToString()

module ScopeRuleTests =

    [<Fact>]
    let ``shared address gets per-system state key, exclusive or unknown keeps address`` () =
        let store, a, _ = setupSharedAddressLines ()
        let ioMap = SignalIOMap.build store
        Assert.True(SignalSystemScope.isShared ioMap "Y0")
        Assert.Equal("Y0", SignalSystemScope.stateKey ioMap None "Y0")
        Assert.NotEqual<string>("Y0", SignalSystemScope.stateKey ioMap (Some a.SystemId) "Y0")
        Assert.Equal("Z9", SignalSystemScope.stateKey ioMap (Some a.SystemId) "Z9")

    [<Fact>]
    let ``empty or malformed systemId parses as unknown`` () =
        Assert.Equal(None, SignalSystemScope.parse "")
        Assert.Equal(None, SignalSystemScope.parse null)
        Assert.Equal(None, SignalSystemScope.parse "not-a-guid")

module HubSessionTests =

    let private goingTargets (effects: RuntimeHubEffect[]) =
        effects
        |> Array.filter (fun e -> e.Kind = RuntimeHubEffectKind.ForceWorkStateIfReady)
        |> Array.map (fun e -> e.WorkGuid)
        |> Set.ofArray

    [<Fact>]
    let ``monitoring OUT on drives only the observed system's device`` () =
        let store, a, b = setupSharedAddressLines ()
        let session = RuntimeHubSession(SimIndex.build store 10, SignalIOMap.build store, RuntimeMode.Monitoring)
        let targets = goingTargets (session.HandleHubTag("Y0", "true", "plc", sysText a))
        Assert.Equal<Set<Guid>>(Set.singleton a.DeviceWorkId, targets)
        Assert.DoesNotContain(b.DeviceWorkId, targets)

    [<Fact>]
    let ``monitoring OUT on without systemId keeps legacy fan-out`` () =
        let store, a, b = setupSharedAddressLines ()
        let session = RuntimeHubSession(SimIndex.build store 10, SignalIOMap.build store, RuntimeMode.Monitoring)
        let targets = goingTargets (session.HandleHubTag("Y0", "true", "plc"))
        Assert.Equal<Set<Guid>>(Set.ofList [ a.DeviceWorkId; b.DeviceWorkId ], targets)

module MonitoringAbnormalTests =

    let private setup () =
        let store, a, b = setupSharedAddressLines ()
        let emitted = ResizeArray<AbnormalRecord>()
        let adapter =
            MonitoringAbnormalAdapter(
                SimIndex.build store 10, SignalIOMap.build store, (fun _ -> Status4.Ready), (fun () -> DateTime.UtcNow),
                (fun r -> emitted.Add r))
        adapter.AutoCalibrate <- false          // 모델 range(250~900) 로 판정
        adapter.IsMinMeasured <- fun _ -> true
        adapter, emitted, a, b

    [<Fact>]
    let ``other system's OUT rising does not start this system's going clock`` () =
        // A 의 OUT rising 뒤 150ms 에 B 의 IN 이 켜졌다. 주소 단독이면 B 도 A 의 going clock 을 받아
        // 150 < Min 250 → ActionUnder 오탐. System 스코프면 B 는 going 을 거친 적이 없어 판정 대상이 아니다.
        let adapter, emitted, a, b = setup ()
        adapter.OnObservedIo("Y0", "false", 0L, sysText a)
        adapter.OnObservedIo("Y0", "true", 0L, sysText a)
        adapter.OnObservedIo("X0", "false", 0L, sysText b)
        adapter.OnObservedIo("X0", "true", 150L, sysText b)
        Assert.Empty(emitted)

    [<Fact>]
    let ``without systemId the shared clock behaves as before`` () =
        let adapter, emitted, _, _ = setup ()
        adapter.OnObservedIo("Y0", "false", 0L)
        adapter.OnObservedIo("Y0", "true", 0L)
        adapter.OnObservedIo("X0", "false", 0L)
        adapter.OnObservedIo("X0", "true", 150L)
        Assert.NotEmpty(emitted)
        Assert.True(emitted |> Seq.forall (fun r -> r.Kind = AbnormalKind.ActionUnder))

    [<Fact>]
    let ``interleaved values of two PLCs keep separate edges`` () =
        // B 의 OUT=true 가 A 의 직전값을 덮으면 A 의 off→on 이 rising 으로 안 보인다(edge 소실).
        let adapter, emitted, a, b = setup ()
        adapter.OnObservedIo("Y0", "false", 0L, sysText a)
        adapter.OnObservedIo("Y0", "true", 0L, sysText b)
        adapter.OnObservedIo("Y0", "true", 0L, sysText a)     // A 의 rising — B 값과 무관해야 한다
        adapter.OnObservedIo("X0", "false", 0L, sysText a)
        adapter.OnObservedIo("X0", "true", 150L, sysText a)   // 150 < Min 250 → A 의 ActionUnder
        Assert.Single(emitted) |> ignore
        Assert.Equal(AbnormalKind.ActionUnder, emitted.[0].Kind)

module PassiveInferenceTests =

    let private states () =
        let d = Dictionary<Guid, Status4>()
        let get (g: Guid) =
            match d.TryGetValue g with
            | true, s -> s
            | _ -> Status4.Ready
        d, Func<Guid, Status4>(get)

    let private callTargets (actions: PassiveInferenceAction[]) =
        actions
        |> Array.filter (fun x -> x.TargetKind = PassiveInferenceTarget.Call)
        |> Array.map (fun x -> x.TargetGuid)
        |> Set.ofArray

    let private setup () =
        let store, a, b = setupSharedAddressLines ()
        let session = PassiveInferenceSession(SimIndex.build store 10, SignalIOMap.build store, RuntimeMode.Monitoring)
        session, a, b

    [<Fact>]
    let ``observe with systemId infers only that system's calls`` () =
        let session, a, _ = setup ()
        let _, get = states ()
        let actions = session.Observe("Y0", "true", get, get, 0L, sysText a)
        Assert.Equal<Set<Guid>>(Set.singleton a.CallId, callTargets actions)

    [<Fact>]
    let ``observe without systemId keeps legacy fan-out`` () =
        let session, a, b = setup ()
        let _, get = states ()
        let actions = session.Observe("Y0", "true", get, get)
        Assert.Equal<Set<Guid>>(Set.ofList [ a.CallId; b.CallId ], callTargets actions)

    [<Fact>]
    let ``same value from another PLC is not deduplicated away`` () =
        // 주소 단독 키면 A 가 true 를 보낸 뒤 B 의 true 는 "같은 값 재수신"으로 버려져 B 의 Going 이 사라진다.
        let session, a, b = setup ()
        let _, get = states ()
        session.Observe("Y0", "true", get, get, 0L, sysText a) |> ignore
        let fromB = session.Observe("Y0", "true", get, get, 0L, sysText b)
        Assert.Equal<Set<Guid>>(Set.singleton b.CallId, callTargets fromB)
