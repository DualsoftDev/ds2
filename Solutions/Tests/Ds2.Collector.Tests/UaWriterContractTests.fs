module Ds2.Adapter.Common.Tests.UaWriterContractTests

open System
open System.Threading.Tasks
open Ds2.Core
open Ds2.Adapter.Common
open Ds2.OpcUa.Server.NodeIds
open Xunit

[<Fact>]
let ``InMemory writer collects writes`` () = task {
    let w = InMemoryUaWriter()
    let iw = w :> IUaWriter
    do! iw.ConnectAsync()
    Assert.True(iw.IsConnected)

    let node = DeterministicNodeId.build 5 (Variable (SignalId "line.a.b"))
    let! status = iw.WriteAsync(node, ValueDouble 3.14, DateTimeOffset.UtcNow, 0u)
    Assert.Equal(0u, status)

    let writes = w.Writes |> Seq.toList
    Assert.Single(writes) |> ignore
}

[<Fact>]
let ``InMemory writer records RaiseAssetEvent call`` () = task {
    let w = InMemoryUaWriter()
    let iw = w :> IUaWriter
    do! iw.ConnectAsync()

    let mnode = DeterministicNodeId.build 5 RaiseAssetEventMethod
    let! (eid, status) =
        iw.CallRaiseAssetEventAsync(
            mnode,
            "urn:opcfoundation:autoid:OpticalScanEventType",
            SignalId "line.bcr05.code",
            DateTimeOffset.UtcNow,
            """{"code":"123"}""")
    Assert.NotEqual<Guid>(Guid.Empty, eid)
    Assert.Equal(0u, status)
    Assert.Single(w.Writes) |> ignore
}
