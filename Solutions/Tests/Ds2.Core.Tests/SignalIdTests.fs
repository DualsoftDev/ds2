module Ds2.Core.Tests.SignalIdTests

open Ds2.Core
open Xunit

// Phase 0 — SignalId value type contract.
// See ADR-002 (deterministic NodeId contract).

[<Fact>]
let ``create accepts kebab-case lowercase with dots`` () =
    let id = SignalId.create "line1.cnc01.spindle-speed"
    Assert.Equal("line1.cnc01.spindle-speed", id.Value)

[<Fact>]
let ``tryCreate rejects null`` () =
    match SignalId.tryCreate null with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "null must be rejected"

[<Fact>]
let ``tryCreate rejects empty and whitespace`` () =
    Assert.True(match SignalId.tryCreate "" with Error _ -> true | _ -> false)
    Assert.True(match SignalId.tryCreate "   " with Error _ -> true | _ -> false)

[<Fact>]
let ``tryCreate rejects uppercase`` () =
    match SignalId.tryCreate "line1.CNC01.spindle-speed" with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "uppercase must be rejected"

[<Fact>]
let ``tryCreate rejects whitespace inside`` () =
    match SignalId.tryCreate "line 1.cnc01.spindle-speed" with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "spaces must be rejected"

[<Fact>]
let ``tryCreate rejects leading and trailing dot`` () =
    Assert.True(match SignalId.tryCreate ".line1.cnc01" with Error _ -> true | _ -> false)
    Assert.True(match SignalId.tryCreate "line1.cnc01." with Error _ -> true | _ -> false)

[<Fact>]
let ``tryCreate rejects disallowed characters`` () =
    match SignalId.tryCreate "line1/cnc01/spindle-speed" with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "slash must be rejected"

[<Fact>]
let ``tryCreate rejects length over 128`` () =
    let long = String.replicate 129 "a"
    match SignalId.tryCreate long with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "length must be capped at 128"

[<Fact>]
let ``struct equality compares by value`` () =
    let a = SignalId.create "line1.cnc01.spindle-speed"
    let b = SignalId.create "line1.cnc01.spindle-speed"
    Assert.Equal<SignalId>(a, b)
    Assert.Equal(a.GetHashCode(), b.GetHashCode())

[<Fact>]
let ``ToString returns raw value`` () =
    let id = SignalId.create "line1.pm03.active-power"
    Assert.Equal("line1.pm03.active-power", id.ToString())

[<Fact>]
let ``empty sentinel has empty value`` () =
    Assert.Equal("", SignalId.empty.Value)
