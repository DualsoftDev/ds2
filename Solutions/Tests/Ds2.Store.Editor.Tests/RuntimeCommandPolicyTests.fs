module Ds2.Store.Editor.Tests.RuntimeCommandPolicyTests

open Ds2.Core
open Ds2.Runtime.Engine.Core
open Xunit

// RuntimeCommandPolicy 의 boolean Can* 함수는 Slice 4 SimulationCommandFacade 의 typed Decision 으로 이관됨
// (SimulationCommandFacadeTests 참조). 본 파일은 *Facade 위임 외 별도 책임* 인 ContinuousInjection
// 관련 함수들만 검증.

[<Fact>]
let ``continuous injection availability excludes monitoring and real-line control`` () =
    Assert.True(RuntimeCommandPolicy.isContinuousInjectionAvailable RuntimeMode.Simulation false)
    Assert.True(RuntimeCommandPolicy.isContinuousInjectionAvailable RuntimeMode.Control false)
    Assert.False(RuntimeCommandPolicy.isContinuousInjectionAvailable RuntimeMode.Control true)
    Assert.False(RuntimeCommandPolicy.isContinuousInjectionAvailable RuntimeMode.Monitoring false)

[<Fact>]
let ``continue source cycle requires running simulation with ready state`` () =
    Assert.True(RuntimeCommandPolicy.canContinueSourceCycle true true false false Status4.Ready)
    Assert.False(RuntimeCommandPolicy.canContinueSourceCycle true true true false Status4.Ready)
    Assert.False(RuntimeCommandPolicy.canContinueSourceCycle true true false false Status4.Going)
