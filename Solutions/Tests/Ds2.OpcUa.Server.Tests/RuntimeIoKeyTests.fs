module Ds2.OpcUa.Server.Tests.RuntimeIoKeyTests

open System
open System.Collections.Generic
open System.IO
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.StandardSubmodels
open Ds2.OpcUa.Server.Server

// -----------------------------------------------------------------------------
// Regression: sim engine 은 SimState.IOValues 를 ApiCall.Id 로 키잉한다.
// EmbeddedUaServer.LoadStore 가 Call.Id 로 runtimeIoNodes 를 인덱스하던 시절,
// WriteRuntimeIo 는 조용히 no-op (Softing 클라이언트에서 값 미갱신 증상).
// 이 테스트는 ApiCall.Id 키 계약을 못박아 회귀 방지.
// -----------------------------------------------------------------------------

let private mkStoreWithApiCalls () : DsStore * Guid list =
    let store = DsStore()

    let project = Project("RuntimeIoKeyTest")
    store.Projects.[project.Id] <- project

    let sys = DsSystem("Line1")
    store.Systems.[sys.Id] <- sys
    project.ActiveSystemIds.Add(sys.Id)

    let flow = Flow("MainFlow", sys.Id)
    store.Flows.[flow.Id] <- flow

    let work = Work("MainFlow", "PickUp", flow.Id)
    store.Works.[work.Id] <- work

    let call = Call("Robot", "Pick", work.Id)
    store.Calls.[call.Id] <- call

    // 두 개의 ApiCall — 각 하나의 runtime.io 노드가 만들어져야 함.
    let ac1 = ApiCall("Robot.Pick.In")
    let ac2 = ApiCall("Robot.Pick.Out")
    call.ApiCalls.Add(ac1)
    call.ApiCalls.Add(ac2)

    store, [ ac1.Id; ac2.Id ]

let private nextPort =
    let counter = ref 48800
    fun () -> System.Threading.Interlocked.Increment counter

let private mkServer () =
    let root = Path.Combine(Path.GetTempPath(), "ds2-runtimeio-" + Guid.NewGuid().ToString("N"))
    let endpoint = sprintf "opc.tcp://127.0.0.1:%d" (nextPort())
    let server =
        new EmbeddedUaServer(
            root,
            endpoint,
            "Ds2.OpcUa.RuntimeIoKeyTest",
            "urn:dualsoft:opcua:runtimeio-key-test",
            true,
            10,
            60_000,
            1)
    server, root

[<Fact>]
let ``LoadStore registers runtime IO nodes indexed by ApiCall.Id, not Call.Id`` () = task {
    let store, apiCallIds = mkStoreWithApiCalls ()
    let server, root = mkServer ()
    try
        let! assets = server.StartForStoreAsync(store, false, true, false)
        Assert.Equal(1, assets)
        // ApiCall 개수 = 2 → runtime IO 노드 2개.
        Assert.Equal(2, server.RuntimeIoNodeCount)

        // ApiCall.Id 키로 write → 모두 hit.
        let values = Dictionary<Guid, string>()
        for id in apiCallIds do values.[id] <- "true"
        let written = server.WriteRuntimeIo(values)
        Assert.Equal(2, written)
        Assert.Equal(0L, server.RuntimeIoMissCount)
        Assert.Equal(2, server.SetRuntimeQuality(uint32 Opc.Ua.StatusCodes.BadOutOfService, DateTime.UtcNow))
    finally
        server.StopAsync().GetAwaiter().GetResult()
        (server :> IDisposable).Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}

[<Fact>]
let ``WriteRuntimeIo with unknown key increments miss counter`` () = task {
    let store, _ = mkStoreWithApiCalls ()
    let server, root = mkServer ()
    try
        let! _ = server.StartForStoreAsync(store, false, true, false)
        // Call.Id — 과거 버그 시절의 잘못된 키. 여전히 무해하지만 진단용 카운터에 잡혀야 함.
        let bogus = Dictionary<Guid, string>()
        let call = store.Calls.Values |> Seq.head
        bogus.[call.Id] <- "true"
        bogus.[Guid.NewGuid()] <- "false"
        let written = server.WriteRuntimeIo(bogus)
        Assert.Equal(0, written)
        Assert.Equal(2L, server.RuntimeIoMissCount)
        Assert.False(server.WriteWorkState(Guid.NewGuid(), "Ready", DateTime.UtcNow))
        Assert.False(server.WriteCallState(Guid.NewGuid(), "Ready", DateTime.UtcNow))
        Assert.Equal(2L, server.StateWriteMissCount)
    finally
        server.StopAsync().GetAwaiter().GetResult()
        (server :> IDisposable).Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}

[<Fact>]
let ``LoadStore with exposeLiveTags=false does not register runtime IO nodes`` () = task {
    let store, _ = mkStoreWithApiCalls ()
    let server, root = mkServer ()
    try
        let! _ = server.StartForStoreAsync(store, false, false, false)
        Assert.Equal(0, server.RuntimeIoNodeCount)
    finally
        server.StopAsync().GetAwaiter().GetResult()
        (server :> IDisposable).Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}

[<Fact>]
let ``AID interaction is projected to deterministic UA variable on store load`` () = task {
    let store, _ = mkStoreWithApiCalls ()
    let project = store.Projects.Values |> Seq.head
    let aid = AssetInterfacesDescription()
    let interaction : OpcUaInteraction = {
        IdShort = "SpindleSpeed"
        SemanticId = SemanticId "urn:dualsoft:cd:spindle-speed:1"
        ValueType = XsDouble
        Unit = Some "rpm"
        Href = "ns=2;s=Line1.CNC01.SpindleSpeed"
        SignalId = SignalId "line1.cnc01.spindle-speed"
    }
    aid.Interfaces.Add(OpcUa(EndpointMetadata.empty, [interaction], []))
    project.AssetInterfaces <- Some aid

    let server, root = mkServer ()
    try
        let! assets = server.StartForStoreAsync(store, false, false, false)
        // 기존 Active System 1개 + AID 자산 1개.
        Assert.Equal(2, assets)
        Assert.Equal(1, server.AidSignalNodeCount)
        Assert.True(
            server.WriteAidSignal(
                "line1.cnc01.spindle-speed",
                box 1234.5,
                DateTime.UtcNow,
                uint32 Opc.Ua.StatusCodes.Good))
        Assert.False(
            server.WriteAidSignal(
                "unknown.signal",
                box 1.0,
                DateTime.UtcNow,
                uint32 Opc.Ua.StatusCodes.Good))
        Assert.Equal(1L, server.AidWriteMissCount)
        Assert.False(
            server.WriteAidSignal(
                "line1.cnc01.spindle-speed",
                box "not-a-double",
                DateTime.UtcNow,
                uint32 Opc.Ua.StatusCodes.Good))
        Assert.Equal(1L, server.TypeMismatchCount)
        Assert.Equal(1, server.SetAllAidSignalQuality(uint32 Opc.Ua.StatusCodes.BadNoCommunication, DateTime.UtcNow))
    finally
        server.StopAsync().GetAwaiter().GetResult()
        (server :> IDisposable).Dispose()
        if Directory.Exists root then
            try Directory.Delete(root, true) with _ -> ()
}
