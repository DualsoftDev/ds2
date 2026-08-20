module Ds2.Store.Editor.Tests.AddTargetQueriesTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// AddWork target Flow 결정 — 멀티 System 트리 흐름에서 명시 선택이 활성 탭 fallback 을 이겨야 한다.

[<Fact>]
let ``AddWork target honors flow selected in tree even when another system tab is active`` () =
    let store = createStore ()
    let project = addProject store "Line1"
    let system1 = addSystem store "System1" project.Id true
    let system2 = addSystem store "System2" project.Id true
    let flow1 = addFlow store "Flow1" system1.Id
    let flow2 = addFlow store "Flow2" system2.Id

    // 재현: 활성 캔버스 탭은 기존 System1 인데, 트리에서 새 System2 의 Flow2 를 선택해 Work 추가.
    let resolved =
        AddTargetQueries.tryResolveAddWorkTargetFlow store
            (Some EntityKind.Flow) (Some flow2.Id)
            None
            (Some TabKind.System) (Some system1.Id)

    Assert.Equal(Some flow2.Id, resolved)
    Assert.NotEqual(Some flow1.Id, resolved)

[<Fact>]
let ``AddWork target keeps active flow tab ahead of stale selection`` () =
    let store = createStore ()
    let project = addProject store "Line1"
    let system1 = addSystem store "System1" project.Id true
    let system2 = addSystem store "System2" project.Id true
    let flow1 = addFlow store "Flow1" system1.Id
    let flow2 = addFlow store "Flow2" system2.Id

    // 캔버스에서 Flow1 탭을 열어 작업 중이면, 트리의 잔상 선택(Flow2)보다 활성 탭이 우선.
    let resolved =
        AddTargetQueries.tryResolveAddWorkTargetFlow store
            (Some EntityKind.Flow) (Some flow2.Id)
            None
            (Some TabKind.Flow) (Some flow1.Id)

    Assert.Equal(Some flow1.Id, resolved)

[<Fact>]
let ``AddWork target falls back to first flow of active system tab without selection`` () =
    let store = createStore ()
    let project = addProject store "Line1"
    let system1 = addSystem store "System1" project.Id true
    let flow1 = addFlow store "Flow1" system1.Id
    addFlow store "Flow2" system1.Id |> ignore

    let resolved =
        AddTargetQueries.tryResolveAddWorkTargetFlow store
            None None
            None
            (Some TabKind.System) (Some system1.Id)

    Assert.Equal(Some flow1.Id, resolved)
