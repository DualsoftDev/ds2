module Ds2.Core.Tests.SmartConstructorTests

open Ds2.Core
open Ds2.Core.SmartCtor
open Xunit

// v10 spec §8 — Smart Constructor 캐노니컬 매핑 검증.

[<Fact>]
let ``Action.normal = Real(Level, None)`` () =
    Assert.Equal(ActionType.Real (Level, None), Action.normal)

[<Fact>]
let ``Action.pulse = Real(OneShot, None)`` () =
    Assert.Equal(ActionType.Real (OneShot, None), Action.pulse)

[<Fact>]
let ``Action.set = Real(Latched, None)`` () =
    Assert.Equal(ActionType.Real (Latched, None), Action.set)

[<Fact>]
let ``Action.timeAppend = Real(Level, Some Append)`` () =
    Assert.Equal(ActionType.Real (Level, Some (Append 200)), Action.timeAppend 200)

[<Fact>]
let ``Action.pulseHold = Real(OneShot, Some Append)`` () =
    Assert.Equal(ActionType.Real (OneShot, Some (Append 200)), Action.pulseHold 200)

[<Fact>]
let ``Action.virt = Virtual None`` () =
    Assert.Equal(ActionType.Virtual None, Action.virt)

[<Fact>]
let ``Action.virtPlus = Virtual Some Append`` () =
    Assert.Equal(ActionType.Virtual (Some (Append 500)), Action.virtPlus 500)

[<Fact>]
let ``Sensing.normal = Real(Level, None)`` () =
    Assert.Equal(SensingType.Real (Level, None), Sensing.normal)

[<Fact>]
let ``Sensing.edge = Real(OneShot, None)`` () =
    Assert.Equal(SensingType.Real (OneShot, None), Sensing.edge)

[<Fact>]
let ``Sensing.latched = Real(Latched, None)`` () =
    Assert.Equal(SensingType.Real (Latched, None), Sensing.latched)

[<Fact>]
let ``Sensing.debounce = Real(Level, Some Append)`` () =
    Assert.Equal(SensingType.Real (Level, Some (Append 50)), Sensing.debounce 50)

[<Fact>]
let ``Sensing.edgeStable = Real(OneShot, Some Append)`` () =
    Assert.Equal(SensingType.Real (OneShot, Some (Append 50)), Sensing.edgeStable 50)

[<Fact>]
let ``Sensing.virt = Virtual None`` () =
    Assert.Equal(SensingType.Virtual None, Sensing.virt)

[<Fact>]
let ``Sensing.virtPlus = Virtual Some Append`` () =
    Assert.Equal(SensingType.Virtual (Some (Append 500)), Sensing.virtPlus 500)
