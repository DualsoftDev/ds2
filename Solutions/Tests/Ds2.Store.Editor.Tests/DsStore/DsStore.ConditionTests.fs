module Ds2.Store.Editor.Tests.DsStoreConditionTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// =============================================================================
// 공용 setup — Call 한 개 + 같은 Work 안에 ApiCall 2 개
// =============================================================================

let private setupCallWithApiCalls (store: DsStore) =
    let project = addProject store "P"
    let system = addSystem store "S" project.Id false
    let activeSystem = addSystem store "A" project.Id true
    let flow = addFlow store "F" activeSystem.Id
    let work = addWork store "W" flow.Id
    store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
    let call = store.Calls.Values |> Seq.head
    let apiDef1 = addApiDef store "Api1" system.Id
    let apiDef2 = addApiDef store "Api2" system.Id
    let ac1 = store.AddApiCallFromPanel(call.Id, apiDef1.Id, "", "", "", "", 0, "", 0, "")
    let ac2 = store.AddApiCallFromPanel(call.Id, apiDef2.Id, "", "", "", "", 0, "", 0, "")
    project, system, work, call, ac1, ac2

// 트리 DTO 빌드 헬퍼
let private dtoFlat isOr (ids: Guid list) : ConditionTreeDto =
    let kinds = List.replicate ids.Length ContactKind.NoContact
    { IsOR = isOr; IsInverted = false
      ApiCallIds     = ids   :> System.Collections.Generic.IReadOnlyList<_>
      ApiCallKinds   = kinds :> System.Collections.Generic.IReadOnlyList<_>
      RawSymbols     = ([]: string list)      :> System.Collections.Generic.IReadOnlyList<_>
      RawSymbolKinds = ([]: ContactKind list) :> System.Collections.Generic.IReadOnlyList<_>
      Children       = ([]: ConditionTreeDto list) :> System.Collections.Generic.IReadOnlyList<_> }


// =============================================================================
// Call 조건 CRUD
// =============================================================================

module CallConditionCrudTests =

    [<Fact>]
    let ``AddCallCondition 은 type 으로 빈 Condition 을 추가한다`` () =
        let store = createStore ()
        let _, _, _, call, _, _ = setupCallWithApiCalls store

        store.AddCallCondition(call.Id, ConditionType.SkipUnmatch)

        Assert.Equal(1, call.Conditions.Count)
        Assert.Equal(Some ConditionType.SkipUnmatch, call.Conditions.[0].Type)

    [<Fact>]
    let ``RemoveCallCondition 은 condition 을 제거하고 Undo 가 복원한다`` () =
        let store = createStore ()
        let _, _, _, call, _, _ = setupCallWithApiCalls store
        store.AddCallCondition(call.Id, ConditionType.SkipUnmatch)
        let condId = (store.Calls.[call.Id]).Conditions.[0].Id

        store.RemoveCallCondition(call.Id, condId)
        Assert.Equal(0, (store.Calls.[call.Id]).Conditions.Count)

        // TrackMutate snapshot 은 dict entry 를 새 객체로 교체 — 항상 dict 에서 다시 fetch.
        store.Undo() |> ignore
        let restored = store.Calls.[call.Id]
        Assert.Equal(1, restored.Conditions.Count)
        Assert.Equal(condId, restored.Conditions.[0].Id)

    [<Fact>]
    let ``ReplaceCallConditionTree 는 같은 type 의 기존 트리를 교체한다`` () =
        let store = createStore ()
        let _, _, _, call, ac1, ac2 = setupCallWithApiCalls store

        store.ReplaceCallConditionTree(call.Id, ConditionType.AutoAux, dtoFlat false [ ac1 ])
        Assert.Equal(1, call.Conditions.Count)
        Assert.Equal(1, call.Conditions.[0].ApiCalls.Count)

        // 같은 type 으로 다시 교체.
        store.ReplaceCallConditionTree(call.Id, ConditionType.AutoAux, dtoFlat true [ ac1; ac2 ])
        Assert.Equal(1, call.Conditions.Count)
        Assert.Equal(2, call.Conditions.[0].ApiCalls.Count)
        Assert.True(call.Conditions.[0].IsOR)

    [<Fact>]
    let ``AddConditionWithApiCalls 는 단일 트랜잭션으로 Condition + ApiCall 을 추가한다`` () =
        let store = createStore ()
        let _, _, _, call, ac1, ac2 = setupCallWithApiCalls store

        let condId = store.AddConditionWithApiCalls(call.Id, ConditionType.ComAux, [ ac1; ac2 ])

        Assert.Equal(1, call.Conditions.Count)
        let cond = call.Conditions.[0]
        Assert.Equal(condId, cond.Id)
        Assert.Equal(Some ConditionType.ComAux, cond.Type)
        Assert.Equal(2, cond.ApiCalls.Count)

    [<Fact>]
    let ``AddChildCondition 은 중첩 condition 을 추가한다`` () =
        let store = createStore ()
        let _, _, _, call, _, _ = setupCallWithApiCalls store
        store.AddCallCondition(call.Id, ConditionType.ComAux)
        let parentId = call.Conditions.[0].Id

        store.AddChildCondition(call.Id, parentId, true)

        Assert.Equal(1, call.Conditions.[0].Children.Count)
        Assert.True(call.Conditions.[0].Children.[0].IsOR)

    [<Fact>]
    let ``UpdateCallConditionSettings 는 IsOR 을 토글한다`` () =
        let store = createStore ()
        let _, _, _, call, _, _ = setupCallWithApiCalls store
        store.AddCallCondition(call.Id, ConditionType.ComAux)
        let condId = call.Conditions.[0].Id

        let changed = store.UpdateCallConditionSettings(call.Id, condId, true)
        Assert.True(changed)
        Assert.True(call.Conditions.[0].IsOR)

        let changedAgain = store.UpdateCallConditionSettings(call.Id, condId, true)
        Assert.False(changedAgain)


// =============================================================================
// Work 조건 CRUD (신규 — Work 에도 동일 Condition 모델 적용)
// =============================================================================

module WorkConditionCrudTests =

    let private setupWorkWithApiCalls (store: DsStore) =
        let project, _, work, _, ac1, ac2 = setupCallWithApiCalls store
        project, work, ac1, ac2

    [<Fact>]
    let ``AddWorkCondition 은 type 으로 빈 Condition 을 추가한다`` () =
        let store = createStore ()
        let _, work, _, _ = setupWorkWithApiCalls store

        store.AddWorkCondition(work.Id, ConditionType.SkipUnmatch)

        Assert.Equal(1, work.Conditions.Count)
        Assert.Equal(Some ConditionType.SkipUnmatch, work.Conditions.[0].Type)

    [<Fact>]
    let ``RemoveWorkCondition 은 condition 을 제거하고 Undo 가 복원한다`` () =
        let store = createStore ()
        let _, work, _, _ = setupWorkWithApiCalls store
        store.AddWorkCondition(work.Id, ConditionType.SkipUnmatch)
        let condId = (store.Works.[work.Id]).Conditions.[0].Id

        store.RemoveWorkCondition(work.Id, condId)
        Assert.Equal(0, (store.Works.[work.Id]).Conditions.Count)

        store.Undo() |> ignore
        let restored = store.Works.[work.Id]
        Assert.Equal(1, restored.Conditions.Count)
        Assert.Equal(condId, restored.Conditions.[0].Id)

    [<Fact>]
    let ``ReplaceWorkConditionTree 는 같은 type 의 기존 트리를 교체한다`` () =
        let store = createStore ()
        let _, work, ac1, ac2 = setupWorkWithApiCalls store

        store.ReplaceWorkConditionTree(work.Id, ConditionType.SkipUnmatch, dtoFlat false [ ac1 ])
        Assert.Equal(1, work.Conditions.Count)
        Assert.Equal(1, work.Conditions.[0].ApiCalls.Count)

        store.ReplaceWorkConditionTree(work.Id, ConditionType.SkipUnmatch, dtoFlat true [ ac1; ac2 ])
        Assert.Equal(1, work.Conditions.Count)
        Assert.Equal(2, work.Conditions.[0].ApiCalls.Count)
        Assert.True(work.Conditions.[0].IsOR)

    [<Fact>]
    let ``AddWorkConditionWithApiCalls 는 단일 트랜잭션으로 Condition + ApiCall 을 추가한다`` () =
        let store = createStore ()
        let _, work, ac1, ac2 = setupWorkWithApiCalls store

        let condId = store.AddWorkConditionWithApiCalls(work.Id, ConditionType.SkipUnmatch, [ ac1; ac2 ])

        Assert.Equal(1, work.Conditions.Count)
        let cond = work.Conditions.[0]
        Assert.Equal(condId, cond.Id)
        Assert.Equal(Some ConditionType.SkipUnmatch, cond.Type)
        Assert.Equal(2, cond.ApiCalls.Count)

    [<Fact>]
    let ``AddWorkChildCondition 은 중첩 condition 을 추가한다`` () =
        let store = createStore ()
        let _, work, _, _ = setupWorkWithApiCalls store
        store.AddWorkCondition(work.Id, ConditionType.SkipUnmatch)
        let parentId = work.Conditions.[0].Id

        store.AddWorkChildCondition(work.Id, parentId, true)

        Assert.Equal(1, work.Conditions.[0].Children.Count)
        Assert.True(work.Conditions.[0].Children.[0].IsOR)

    [<Fact>]
    let ``UpdateWorkConditionSettings 는 IsOR 을 토글한다`` () =
        let store = createStore ()
        let _, work, _, _ = setupWorkWithApiCalls store
        store.AddWorkCondition(work.Id, ConditionType.SkipUnmatch)
        let condId = work.Conditions.[0].Id

        let changed = store.UpdateWorkConditionSettings(work.Id, condId, true)
        Assert.True(changed)
        Assert.True(work.Conditions.[0].IsOR)

    [<Fact>]
    let ``AddApiCallsToWorkConditionBatch 는 기존 condition 에 ApiCall 들을 추가한다`` () =
        let store = createStore ()
        let _, work, ac1, ac2 = setupWorkWithApiCalls store
        store.AddWorkCondition(work.Id, ConditionType.SkipUnmatch)
        let condId = work.Conditions.[0].Id

        let added = store.AddApiCallsToWorkConditionBatch(work.Id, condId, [ ac1; ac2 ])

        Assert.Equal(2, added)
        Assert.Equal(2, work.Conditions.[0].ApiCalls.Count)

    [<Fact>]
    let ``RemoveApiCallFromWorkCondition 은 단일 ApiCall 만 제거한다`` () =
        let store = createStore ()
        let _, work, ac1, ac2 = setupWorkWithApiCalls store
        store.AddWorkConditionWithApiCalls(work.Id, ConditionType.SkipUnmatch, [ ac1; ac2 ]) |> ignore
        let condId = work.Conditions.[0].Id

        store.RemoveApiCallFromWorkCondition(work.Id, condId, ac1)

        Assert.Equal(1, work.Conditions.[0].ApiCalls.Count)
        Assert.Equal(ac2, work.Conditions.[0].ApiCalls.[0].Id)

    [<Fact>]
    let ``GetWorkConditionsForPanel 은 추가된 Work condition 을 패널 형식으로 반환한다`` () =
        let store = createStore ()
        let _, work, ac1, _ = setupWorkWithApiCalls store
        store.AddWorkConditionWithApiCalls(work.Id, ConditionType.SkipUnmatch, [ ac1 ]) |> ignore

        let items = store.GetWorkConditionsForPanel(work.Id)

        Assert.Equal(1, items.Length)
        Assert.Equal(ConditionType.SkipUnmatch, items.[0].ConditionType)


// =============================================================================
// Work / Call 독립성 — 같은 store 안에서 서로 영향 없음
// =============================================================================

module CallWorkIsolationTests =

    [<Fact>]
    let ``Work 에 추가한 condition 은 같은 work 의 Call 에 영향을 주지 않는다`` () =
        let store = createStore ()
        let _, _, work, call, ac1, _ = setupCallWithApiCalls store

        store.AddWorkCondition(work.Id, ConditionType.SkipUnmatch)
        store.AddCallCondition(call.Id, ConditionType.AutoAux)
        store.AddApiCallsToWorkConditionBatch(work.Id, work.Conditions.[0].Id, [ ac1 ]) |> ignore

        Assert.Equal(1, work.Conditions.Count)
        Assert.Equal(1, call.Conditions.Count)
        Assert.Equal(Some ConditionType.SkipUnmatch, work.Conditions.[0].Type)
        Assert.Equal(Some ConditionType.AutoAux,    call.Conditions.[0].Type)
        Assert.Equal(1, work.Conditions.[0].ApiCalls.Count)
        Assert.Equal(0, call.Conditions.[0].ApiCalls.Count)
