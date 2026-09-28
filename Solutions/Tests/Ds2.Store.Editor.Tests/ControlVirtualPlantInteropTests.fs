module Ds2.Store.Editor.Tests.ControlVirtualPlantInteropTests

open System
open System.Collections.Generic
open System.IO
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Passive
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

open Ds2.Store.Editor.Tests.ControlVirtualPlantTestHarness

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``same model Control and VirtualPlant complete and reset Boolean or numeric output`` (numeric: bool) =
    let m = model ()
    let active = call m m.Run "A"
    let outType, activeValue, resetValue =
        if numeric then ValueSpecTypeIndex.Int16, "7", "0"
        else ValueSpecTypeIndex.Bool, "True", "false"
    bind m active m.Api "D0" outType activeValue "X0" |> ignore
    use run = new DualRuntime(m.Store)
    Assert.NotSame(run.Control.Store.Works.[m.Tx.Id], run.Plant.Store.Works.[m.Tx.Id])
    run.Begin()
    Assert.Equal<(string * string) list>([ "D0", activeValue ], outputs run)
    assertBeforeEcho run active m.Tx.Id
    assertFinished run active m.Tx.Id
    Assert.Equal<(string * string) list>([ "D0", activeValue; "D0", resetValue ], outputs run)
    assertEcho run "X0"

[<Theory>]
[<InlineData("D1")>]
[<InlineData("D0")>]
let ``inactive binding declared first cannot divert an active numeric output or its reset`` (inactiveAddress: string) =
    let m = model ()
    let inactive = call m m.Spare "InactiveFirst"
    let inactiveBinding = bind m inactive m.Api inactiveAddress ValueSpecTypeIndex.Int16 "2" "X1"
    let active = call m m.Run "ActiveSecond"
    bind m active m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0" |> ignore
    use run = new DualRuntime(m.Store)
    if inactiveAddress = "D0" then
        Assert.Equal(inactiveBinding, (run.Plant.Engine.IOMap.GetByOutAddress "D0").Head.ApiCallGuid)
    run.Begin()
    Assert.Equal<(string * string) list>([ "D0", "1" ], outputs run)
    assertBeforeEcho run active m.Tx.Id
    assertFinished run active m.Tx.Id
    Assert.Equal<(string * string) list>([ "D0", "1"; "D0", "0" ], outputs run)
    assertEcho run "X0"
    Assert.DoesNotContain(run.Writes, fun value -> value.Source = "virtualplant" && value.Address = "X1" && String.Equals(value.Value, "true", StringComparison.OrdinalIgnoreCase))
    Assert.Equal(Some Status4.Ready, run.Control.Engine.GetCallState inactive)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``two explicit APIs with one Tx receive both echoes including matching shared Out fanout`` (sameOutput: bool) =
    let m = model ()
    let active = call m m.Run "A"
    let otherApi = secondApi m
    let otherAddress, otherValue = if sameOutput then "D0", "1" else "D1", "2"
    bind m active m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0" |> ignore
    bind m active otherApi otherAddress ValueSpecTypeIndex.Int16 otherValue "X1" |> ignore
    use run = new DualRuntime(m.Store)
    run.Begin()
    Assert.Equal<(string * string) list>(List.sort [ "D0", "1"; otherAddress, otherValue ], outputs run |> List.sort)
    assertBeforeEcho run active m.Tx.Id
    assertFinished run active m.Tx.Id
    assertEcho run "X0"
    assertEcho run "X1"
    // Distinct matching bindings may both echo. Repeated wire messages remain
    // separate observations, but the plant still has only one duration cycle.
    Assert.Equal<(string * string) list>(
        List.sort [ "D0", "1"; otherAddress, otherValue; "D0", "0"; otherAddress, "0" ],
        outputs run |> List.sort)

[<Fact>]
let ``shared ApiCall identity survives two stores without duplicate plant echo`` () =
    let m = model ()
    let active = call m m.Run "A"
    let binding = bind m active m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0"
    let inactive = m.Store.AddCallWithLinkedApiDefs(m.Spare.Id, "Shared", "Invoke", [ m.Api.Id ])
    use run = new DualRuntime(m.Store)
    for host in [ run.Control; run.Plant ] do
        Assert.Equal(binding, host.Store.Calls.[inactive].ApiCalls.[0].Id)
        Assert.Same(host.Store.Calls.[active].ApiCalls.[0], host.Store.Calls.[inactive].ApiCalls.[0])
    run.Begin()
    assertBeforeEcho run active m.Tx.Id
    assertFinished run active m.Tx.Id
    Assert.Equal<(string * string) list>([ "D0", "1"; "D0", "0" ], outputs run)
    assertEcho run "X0"
    Assert.Single(run.Writes |> List.filter (fun value -> value.Source = "virtualplant" && value.Address = "X0" && String.Equals(value.Value, "true", StringComparison.OrdinalIgnoreCase))) |> ignore
    Assert.Equal(Some Status4.Ready, run.Control.Engine.GetCallState inactive)

[<Fact>]
let ``one VP message deduplicates shared ApiCall mappings and a shared Tx start`` () =
    let m = model ()
    let active = call m m.Run "A"
    bind m active m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0" |> ignore
    m.Store.AddCallWithLinkedApiDefs(m.Spare.Id, "Shared", "Invoke", [ m.Api.Id ]) |> ignore
    let index = SimIndex.build m.Store 10
    let session = RuntimeModeSession(index, Ds2.Runtime.IO.SignalIOMap.build m.Store, RuntimeMode.VirtualPlant)
    let effects = session.HandleHubTag("D0", "1", "control")
    Assert.Single(effects |> Array.filter (fun effect -> effect.Kind = RuntimeHubEffectKind.ForceWorkStateIfReady)) |> ignore
    let echo = effects |> Array.filter (fun effect -> effect.Kind = RuntimeHubEffectKind.WriteTag)
    Assert.Single echo |> ignore
    Assert.Equal(("X0", "True", 50), (echo.[0].Address, echo.[0].Value, echo.[0].DelayMs))

[<Fact>]
let ``Monitoring selects matching later output bindings without generating input writes`` () =
    let m = model ()
    let inactive = call m m.Spare "InactiveFirst"
    bind m inactive m.Api "D0" ValueSpecTypeIndex.Int16 "2" "X1" |> ignore
    let active = call m m.Run "ActiveSecond"
    bind m active m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0" |> ignore
    let index = SimIndex.build m.Store 10
    let session = RuntimeModeSession(index, Ds2.Runtime.IO.SignalIOMap.build m.Store, RuntimeMode.Monitoring)
    let effects = session.HandleHubTag("D0", "1", "control")
    let starts = effects |> Array.filter (fun effect -> effect.Kind = RuntimeHubEffectKind.ForceWorkStateIfReady)
    Assert.Single starts |> ignore
    Assert.Equal(m.Tx.Id, starts.[0].WorkGuid)
    Assert.DoesNotContain(effects, fun effect -> effect.Kind = RuntimeHubEffectKind.WriteTag)
    Assert.Contains(effects, fun effect -> effect.Kind = RuntimeHubEffectKind.PassiveObserve)

[<Fact>]
let ``one signal does not reset a shared sensor matched by another binding`` () =
    let m = model ()
    let inactive = call m m.Spare "Inactive"
    bind m inactive m.Api "D0" ValueSpecTypeIndex.Int16 "2" "X0" |> ignore
    let active = call m m.Run "Active"
    bind m active m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0" |> ignore
    let index = SimIndex.build m.Store 10
    let session = RuntimeModeSession(index, Ds2.Runtime.IO.SignalIOMap.build m.Store, RuntimeMode.VirtualPlant)
    let effects = session.HandleHubTag("D0", "1", "control")
    let writes = effects |> Array.filter (fun effect -> effect.Kind = RuntimeHubEffectKind.WriteTag)
    let echo = Assert.Single writes
    Assert.Equal((50, "X0", "True"), (echo.DelayMs, echo.Address, echo.Value))

[<Fact>]
let ``distinct matching bindings with identical response schedule one write per signal`` () =
    let m = model ()
    let active = call m m.Run "Active"
    bind m active m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0" |> ignore
    bind m active (secondApi m) "D0" ValueSpecTypeIndex.Int16 "1" "X0" |> ignore
    let index = SimIndex.build m.Store 10
    let session = RuntimeModeSession(index, Ds2.Runtime.IO.SignalIOMap.build m.Store, RuntimeMode.VirtualPlant)
    let effects = session.HandleHubTag("D0", "1", "control")
    Assert.Single(effects |> Array.filter (fun effect -> effect.Kind = RuntimeHubEffectKind.WriteTag)) |> ignore
    Assert.Single(effects |> Array.filter (fun effect -> effect.Kind = RuntimeHubEffectKind.ForceWorkStateIfReady)) |> ignore
