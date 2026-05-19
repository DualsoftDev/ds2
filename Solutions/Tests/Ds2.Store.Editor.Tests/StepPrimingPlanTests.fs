module Ds2.Store.Editor.Tests.StepPrimingPlanTests

open System
open Ds2.Core
open Ds2.Runtime.Engine.Core
open Xunit

let private alwaysTrue _ = true
let private alwaysFalse _ = false
let private noneState _ = None

[<Fact>]
let ``autoStartSources flag selects StartAllSourcesWithoutToken regardless of selection`` () =
    let action =
        StepPrimingPlan.decide
            alwaysFalse alwaysFalse noneState
            (Guid.NewGuid()) true
    Assert.Equal(StartAllSourcesWithoutToken, action)

[<Fact>]
let ``empty guid without auto returns NoAction`` () =
    let action =
        StepPrimingPlan.decide
            alwaysFalse alwaysFalse noneState
            Guid.Empty false
    Assert.Equal(NoAction, action)

[<Fact>]
let ``selected source without token starts the source`` () =
    let g = Guid.NewGuid()
    let action =
        StepPrimingPlan.decide
            (fun x -> x = g) alwaysFalse noneState
            g false
    Assert.Equal(StartSelectedSource g, action)

[<Fact>]
let ``selected source that already holds token is NoAction`` () =
    let g = Guid.NewGuid()
    let action =
        StepPrimingPlan.decide
            (fun x -> x = g) alwaysTrue noneState
            g false
    Assert.Equal(NoAction, action)

[<Fact>]
let ``non-source work in Ready state is forced to Going`` () =
    let g = Guid.NewGuid()
    let action =
        StepPrimingPlan.decide
            alwaysFalse alwaysFalse (fun _ -> Some Status4.Ready)
            g false
    Assert.Equal(ForceSelectedReadyToGoing g, action)

[<Fact>]
let ``non-source work in non-Ready state is NoAction`` () =
    let g = Guid.NewGuid()
    let action =
        StepPrimingPlan.decide
            alwaysFalse alwaysFalse (fun _ -> Some Status4.Going)
            g false
    Assert.Equal(NoAction, action)

[<Fact>]
let ``non-source work with unknown state is NoAction`` () =
    let g = Guid.NewGuid()
    let action =
        StepPrimingPlan.decide
            alwaysFalse alwaysFalse noneState
            g false
    Assert.Equal(NoAction, action)
