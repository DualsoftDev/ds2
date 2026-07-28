module Ds2.OpcUa.Server.Tests.DeterministicNodeIdTests

open Ds2.Core
open Ds2.OpcUa.Server.NodeIds
open Xunit

[<Fact>]
let ``Variable NodeId format uses ns={idx};s={signalId}`` () =
    let n = DeterministicNodeId.build 5 (Variable (SignalId "line1.cnc01.spindle-speed"))
    Assert.Equal("ns=5;s=line1.cnc01.spindle-speed", n.Format())

[<Fact>]
let ``AssetFolder NodeId is s=Asset`` () =
    let n = DeterministicNodeId.build 3 AssetFolder
    Assert.Equal("ns=3;s=Asset", n.Format())

[<Fact>]
let ``RaiseAssetEvent Method NodeId has canonical path`` () =
    let n = DeterministicNodeId.build 3 RaiseAssetEventMethod
    Assert.Equal("ns=3;s=Events/RaiseAssetEvent", n.Format())

[<Fact>]
let ``parse and format roundtrip`` () =
    let cases = [
        "ns=2;s=Asset"
        "ns=15;s=line1.cnc01.spindle-speed"
        "ns=5;s=Events/RaiseAssetEvent"
    ]
    for c in cases do
        match DeterministicNodeId.parse c with
        | Some n -> Assert.Equal(c, n.Format())
        | None -> Assert.Fail(sprintf "failed to parse %s" c)

[<Fact>]
let ``parse rejects malformed`` () =
    Assert.True((DeterministicNodeId.parse "s=foo").IsNone)
    Assert.True((DeterministicNodeId.parse "ns=foo;s=bar").IsNone)
    Assert.True((DeterministicNodeId.parse "ns=5;i=100").IsNone)
