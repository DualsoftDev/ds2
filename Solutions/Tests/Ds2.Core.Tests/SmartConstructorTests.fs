module Ds2.Core.Tests.SmartConstructorTests

open Ds2.Core
open Ds2.Core.SmartCtor
open Xunit

// ApiDefType(Normal/Pulse/Latch/Virtual) × TimeOption — Smart Constructor 매핑 검증.

[<Fact>]
let ``Action smart constructors map to new ActionType cases`` () =
    Assert.Equal(ActionType.Normal None, Action.normal)
    Assert.Equal(ActionType.Normal (Some 200), Action.normalHold 200)
    Assert.Equal(ActionType.Pulse None, Action.pulse)
    Assert.Equal(ActionType.Pulse (Some 150), Action.pulseHold 150)
    Assert.Equal(ActionType.Latch, Action.latch)
    Assert.Equal(ActionType.Virtual, Action.virt)

[<Fact>]
let ``Sensing smart constructors map to new SensingType cases`` () =
    Assert.Equal(SensingType.Normal None, Sensing.normal)
    Assert.Equal(SensingType.Normal (Some 50), Sensing.stable 50)
    Assert.Equal(SensingType.Latch 50, Sensing.latch 50)
    Assert.Equal(SensingType.Virtual 500, Sensing.virt 500)
