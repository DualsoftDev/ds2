module Ds2.Store.Editor.Tests.RuntimeCommandPolicyTests

open Ds2.Core
open Ds2.Runtime.Engine.Core
open Xunit

[<Fact>]
let ``start is allowed only when stopped or paused outside homing`` () =
    Assert.True(RuntimeCommandPolicy.canStartSimulation false false false)
    Assert.True(RuntimeCommandPolicy.canStartSimulation true true false)
    Assert.False(RuntimeCommandPolicy.canStartSimulation true false false)
    Assert.False(RuntimeCommandPolicy.canStartSimulation false false true)

[<Fact>]
let ``pause is disabled for passive and real line modes`` () =
    Assert.True(RuntimeCommandPolicy.canPauseSimulation true false false RuntimeMode.Simulation false)
    Assert.True(RuntimeCommandPolicy.canPauseSimulation true false false RuntimeMode.Control false)
    Assert.False(RuntimeCommandPolicy.canPauseSimulation true false false RuntimeMode.Control true)
    Assert.False(RuntimeCommandPolicy.canPauseSimulation true false false RuntimeMode.VirtualPlant false)
    Assert.False(RuntimeCommandPolicy.canPauseSimulation true false false RuntimeMode.Monitoring false)
    Assert.False(RuntimeCommandPolicy.canPauseSimulation true true false RuntimeMode.Simulation false)
    Assert.False(RuntimeCommandPolicy.canPauseSimulation true false true RuntimeMode.Simulation false)

[<Fact>]
let ``step is simulation mode only and requires stopped or paused`` () =
    Assert.True(RuntimeCommandPolicy.canStepSimulation false false false RuntimeMode.Simulation)
    Assert.True(RuntimeCommandPolicy.canStepSimulation true true false RuntimeMode.Simulation)
    Assert.False(RuntimeCommandPolicy.canStepSimulation true false false RuntimeMode.Simulation)
    Assert.False(RuntimeCommandPolicy.canStepSimulation true true true RuntimeMode.Simulation)
    Assert.False(RuntimeCommandPolicy.canStepSimulation true true false RuntimeMode.Control)
    Assert.False(RuntimeCommandPolicy.canStepSimulation true true false RuntimeMode.VirtualPlant)
    Assert.False(RuntimeCommandPolicy.canStepSimulation true true false RuntimeMode.Monitoring)

[<Fact>]
let ``manual commands exclude passive modes and require selection`` () =
    Assert.True(RuntimeCommandPolicy.canForceWork true false false RuntimeMode.Simulation true)
    Assert.True(RuntimeCommandPolicy.canSeedToken true false false RuntimeMode.Control true)
    Assert.False(RuntimeCommandPolicy.canForceWork true false false RuntimeMode.VirtualPlant true)
    Assert.False(RuntimeCommandPolicy.canSeedToken true false false RuntimeMode.Monitoring true)
    Assert.False(RuntimeCommandPolicy.canForceWork true false false RuntimeMode.Simulation false)
    Assert.False(RuntimeCommandPolicy.canSeedToken true false true RuntimeMode.Simulation true)

[<Fact>]
let ``homing and continuous injection policies keep real line ownership explicit`` () =
    Assert.True(RuntimeCommandPolicy.canBeginHoming RuntimeMode.Control true false false false)
    Assert.False(RuntimeCommandPolicy.canBeginHoming RuntimeMode.Control false false false false)
    Assert.False(RuntimeCommandPolicy.canBeginHoming RuntimeMode.Monitoring true false false false)
    Assert.False(RuntimeCommandPolicy.canBeginHoming RuntimeMode.Control true true false false)
    Assert.False(RuntimeCommandPolicy.canBeginHoming RuntimeMode.Control true false true false)
    Assert.False(RuntimeCommandPolicy.canBeginHoming RuntimeMode.Control true false false true)

    Assert.True(RuntimeCommandPolicy.isContinuousInjectionAvailable RuntimeMode.Simulation false)
    Assert.True(RuntimeCommandPolicy.isContinuousInjectionAvailable RuntimeMode.Control false)
    Assert.False(RuntimeCommandPolicy.isContinuousInjectionAvailable RuntimeMode.Control true)
    Assert.False(RuntimeCommandPolicy.isContinuousInjectionAvailable RuntimeMode.Monitoring false)

    Assert.True(RuntimeCommandPolicy.canContinueSourceCycle true true false false Status4.Ready)
    Assert.False(RuntimeCommandPolicy.canContinueSourceCycle true true true false Status4.Ready)
    Assert.False(RuntimeCommandPolicy.canContinueSourceCycle true true false false Status4.Going)
