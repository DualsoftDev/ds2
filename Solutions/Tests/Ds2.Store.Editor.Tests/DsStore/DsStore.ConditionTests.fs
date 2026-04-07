module Ds2.Store.Editor.Tests.DsStoreConditionTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// =============================================================================
// 조건 CRUD + Undo
// =============================================================================

module ConditionCrudTests =

    let private setupCallWithApiCall (store: DsStore) =
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let activeSystem = addSystem store "A" project.Id true
        let flow = addFlow store "F" activeSystem.Id
        let work = addWork store "W" flow.Id
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let call = store.Calls.Values |> Seq.head
        let apiDef = addApiDef store "Api1" system.Id
        let apiCallId = store.AddApiCallFromPanel(call.Id, apiDef.Id, "", "", "", "", 0, "", 0, "")
        project, system, call, apiDef, apiCallId

    [<Fact>]
    let ``AddCallCondition creates condition and supports undo`` () =
        let store = createStore ()
        let _, _, call, _, _ = setupCallWithApiCall store
        store.AddCallCondition(call.Id, CallConditionType.ComAux)
        let conditions = store.GetCallConditionsForPanel(call.Id)
        Assert.Equal(1, conditions.Length)
        Assert.Equal(CallConditionType.ComAux, conditions.[0].ConditionType)

        store.Undo()
        let after = store.GetCallConditionsForPanel(call.Id)
        Assert.Equal(0, after.Length)

    [<Fact>]
    let ``RemoveCallCondition removes and supports undo`` () =
        let store = createStore ()
        let _, _, call, _, _ = setupCallWithApiCall store
        store.AddCallCondition(call.Id, CallConditionType.AutoAux)
        let condId = (store.GetCallConditionsForPanel(call.Id)).[0].ConditionId
        store.RemoveCallCondition(call.Id, condId)
        Assert.Equal(0, (store.GetCallConditionsForPanel(call.Id)).Length)

        store.Undo()
        Assert.Equal(1, (store.GetCallConditionsForPanel(call.Id)).Length)

    [<Fact>]
    let ``AddConditionWithApiCalls creates condition with ApiCalls in single transaction`` () =
        let store = createStore ()
        let _, _, call, _, apiCallId = setupCallWithApiCall store
        let _condId = store.AddConditionWithApiCalls(call.Id, CallConditionType.SkipUnmatch, [ apiCallId ])
        let conditions = store.GetCallConditionsForPanel(call.Id)
        Assert.Equal(1, conditions.Length)
        Assert.Equal(1, conditions.[0].Items.Length)

        // Undo 1회로 조건+ApiCall 모두 롤백
        store.Undo()
        Assert.Equal(0, (store.GetCallConditionsForPanel(call.Id)).Length)

    [<Fact>]
    let ``UpdateCallConditionSettings toggles OR and Rising`` () =
        let store = createStore ()
        let _, _, call, _, _ = setupCallWithApiCall store
        store.AddCallCondition(call.Id, CallConditionType.ComAux)
        let condId = (store.GetCallConditionsForPanel(call.Id)).[0].ConditionId

        let changed = store.UpdateCallConditionSettings(call.Id, condId, true, true)
        Assert.True(changed)
        let cond = (store.GetCallConditionsForPanel(call.Id)).[0]
        Assert.True(cond.IsOR)
        Assert.True(cond.IsRising)

        // 같은 값이면 false 반환
        let notChanged = store.UpdateCallConditionSettings(call.Id, condId, true, true)
        Assert.False(notChanged)

    [<Fact>]
    let ``AddChildCondition creates nested condition`` () =
        let store = createStore ()
        let _, _, call, _, _ = setupCallWithApiCall store
        store.AddCallCondition(call.Id, CallConditionType.AutoAux)
        let condId = (store.GetCallConditionsForPanel(call.Id)).[0].ConditionId
        store.AddChildCondition(call.Id, condId, true)

        let cond = (store.GetCallConditionsForPanel(call.Id)).[0]
        Assert.Equal(1, cond.Children.Length)
        Assert.True(cond.Children.[0].IsOR)

// =============================================================================
// FormulaText 수식 생성
// =============================================================================

module FormulaTextTests =

    [<Fact>]
    let ``Empty condition returns (empty)`` () =
        let item = CallConditionPanelItem(Guid.NewGuid(), CallConditionType.ComAux, false, false, [], [])
        Assert.Equal("(empty)", item.FormulaText())

    [<Fact>]
    let ``Single ApiCall shows name only`` () =
        let apiItem = CallConditionApiCallItem(Guid.NewGuid(), "ac1", "MyApi", "", 0, "", 0)
        let item = CallConditionPanelItem(Guid.NewGuid(), CallConditionType.ComAux, false, false, [apiItem], [])
        Assert.Equal("MyApi", item.FormulaText())

    [<Fact>]
    let ``ApiCall with spec shows name=spec`` () =
        let apiItem = CallConditionApiCallItem(Guid.NewGuid(), "ac1", "MyApi", "True", 0, "", 0)
        let item = CallConditionPanelItem(Guid.NewGuid(), CallConditionType.ComAux, false, false, [apiItem], [])
        Assert.Equal("MyApi=True", item.FormulaText())

    [<Fact>]
    let ``Undefined spec is hidden`` () =
        let apiItem = CallConditionApiCallItem(Guid.NewGuid(), "ac1", "MyApi", "Undefined", 0, "", 0)
        let item = CallConditionPanelItem(Guid.NewGuid(), CallConditionType.ComAux, false, false, [apiItem], [])
        Assert.Equal("MyApi", item.FormulaText())

    [<Fact>]
    let ``AND operator joins with &`` () =
        let a = CallConditionApiCallItem(Guid.NewGuid(), "a", "A", "", 0, "", 0)
        let b = CallConditionApiCallItem(Guid.NewGuid(), "b", "B", "", 0, "", 0)
        let item = CallConditionPanelItem(Guid.NewGuid(), CallConditionType.ComAux, false, false, [a; b], [])
        Assert.Equal("A&B", item.FormulaText())

    [<Fact>]
    let ``OR operator joins with |`` () =
        let a = CallConditionApiCallItem(Guid.NewGuid(), "a", "A", "", 0, "", 0)
        let b = CallConditionApiCallItem(Guid.NewGuid(), "b", "B", "", 0, "", 0)
        let item = CallConditionPanelItem(Guid.NewGuid(), CallConditionType.ComAux, true, false, [a; b], [])
        Assert.Equal("A|B", item.FormulaText())

    [<Fact>]
    let ``Rising appends arrow`` () =
        let a = CallConditionApiCallItem(Guid.NewGuid(), "a", "A", "", 0, "", 0)
        let item = CallConditionPanelItem(Guid.NewGuid(), CallConditionType.ComAux, false, true, [a], [])
        Assert.Equal("A \u2191", item.FormulaText())

    [<Fact>]
    let ``Children are wrapped in parentheses`` () =
        let a = CallConditionApiCallItem(Guid.NewGuid(), "a", "A", "", 0, "", 0)
        let child = CallConditionPanelItem(Guid.NewGuid(), CallConditionType.ComAux, true, false, [a], [])
        let b = CallConditionApiCallItem(Guid.NewGuid(), "b", "B", "", 0, "", 0)
        let item = CallConditionPanelItem(Guid.NewGuid(), CallConditionType.ComAux, false, false, [b], [child])
        Assert.Equal("B&(A)", item.FormulaText())
