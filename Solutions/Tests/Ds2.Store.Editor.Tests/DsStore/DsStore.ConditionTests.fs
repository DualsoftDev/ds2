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
        let apiCallId = store.AddApiCallFromPanel(call.Id, apiDef.Id, "", "", "", "", 0, "", 0, "", true)
        project, system, call, apiDef, apiCallId

    // ── ReplaceCallConditionTree round-trip ──────────────────────────────
    let private dtoEx isOr isInverted (ids: Guid list) (kinds: ContactKind list)
                       (children: CallConditionTreeDto list) : CallConditionTreeDto =
        { IsOR = isOr; IsInverted = isInverted
          ApiCallIds   = ids   :> System.Collections.Generic.IReadOnlyList<_>
          ApiCallKinds = kinds :> System.Collections.Generic.IReadOnlyList<_>
          Children     = children :> System.Collections.Generic.IReadOnlyList<_> }

    let private dto isOr (ids: Guid list) (children: CallConditionTreeDto list) : CallConditionTreeDto =
        let kinds = List.replicate ids.Length ContactKind.NoContact
        dtoEx isOr false ids kinds children

    [<Fact>]
    let ``ReplaceCallConditionTree saves flat AND group`` () =
        let store = createStore ()
        let _, system, call, _, ac1 = setupCallWithApiCall store
        let apiDef2 = addApiDef store "Api2" system.Id
        let ac2 = store.AddApiCallFromPanel(call.Id, apiDef2.Id, "", "", "", "", 0, "", 0, "", true)

        let tree = dto false [ ac1; ac2 ] []
        store.ReplaceCallConditionTree(call.Id, CallConditionType.AutoAux, tree)

        let conds = store.GetCallConditionsForPanel(call.Id)
        Assert.Equal(1, conds.Length)
        Assert.False(conds.[0].IsOR)
        Assert.Equal(2, conds.[0].Items.Length)

    [<Fact>]
    let ``ReplaceCallConditionTree saves OR group`` () =
        let store = createStore ()
        let _, system, call, _, ac1 = setupCallWithApiCall store
        let apiDef2 = addApiDef store "Api2" system.Id
        let ac2 = store.AddApiCallFromPanel(call.Id, apiDef2.Id, "", "", "", "", 0, "", 0, "", true)

        let tree = dto true [ ac1; ac2 ] []
        store.ReplaceCallConditionTree(call.Id, CallConditionType.AutoAux, tree)

        let conds = store.GetCallConditionsForPanel(call.Id)
        Assert.Equal(1, conds.Length)
        Assert.True(conds.[0].IsOR)
        Assert.Equal(2, conds.[0].Items.Length)

    [<Fact>]
    let ``ReplaceCallConditionTree preserves nested OR child`` () =
        let store = createStore ()
        let _, system, call, _, ac1 = setupCallWithApiCall store
        let apiDef2 = addApiDef store "Api2" system.Id
        let ac2 = store.AddApiCallFromPanel(call.Id, apiDef2.Id, "", "", "", "", 0, "", 0, "", true)
        let apiDef3 = addApiDef store "Api3" system.Id
        let ac3 = store.AddApiCallFromPanel(call.Id, apiDef3.Id, "", "", "", "", 0, "", 0, "", true)

        // (ac1 AND ac2) OR ac3 — root is OR, child is AND group with ac1+ac2, leaf ac3.
        // 모델: top IsOR=true, items=[ac3], children=[{IsOR=false, items=[ac1,ac2]}]
        let child = dto false [ ac1; ac2 ] []
        let tree = dto true [ ac3 ] [ child ]
        store.ReplaceCallConditionTree(call.Id, CallConditionType.AutoAux, tree)

        let conds = store.GetCallConditionsForPanel(call.Id)
        Assert.Equal(1, conds.Length)
        let root = conds.[0]
        Assert.True(root.IsOR)
        Assert.Equal(1, root.Items.Length)
        Assert.Equal(1, root.Children.Length)
        Assert.False(root.Children.[0].IsOR)
        Assert.Equal(2, root.Children.[0].Items.Length)

    [<Fact>]
    let ``ReplaceCallConditionTree replaces existing of same type`` () =
        let store = createStore ()
        let _, _, call, _, ac1 = setupCallWithApiCall store

        // 처음 1개 저장
        store.ReplaceCallConditionTree(call.Id, CallConditionType.AutoAux, dto false [ ac1 ] [])
        Assert.Equal(1, (store.GetCallConditionsForPanel(call.Id)).Length)

        // 같은 type 으로 재저장 — 기존이 사라지고 새것만 남아야 함
        store.ReplaceCallConditionTree(call.Id, CallConditionType.AutoAux, dto true [ ac1 ] [])
        let conds = store.GetCallConditionsForPanel(call.Id)
        Assert.Equal(1, conds.Length)
        Assert.True(conds.[0].IsOR)

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
    let ``UpdateCallConditionSettings toggles OR`` () =
        let store = createStore ()
        let _, _, call, _, _ = setupCallWithApiCall store
        store.AddCallCondition(call.Id, CallConditionType.ComAux)
        let condId = (store.GetCallConditionsForPanel(call.Id)).[0].ConditionId

        let changed = store.UpdateCallConditionSettings(call.Id, condId, true)
        Assert.True(changed)
        let cond = (store.GetCallConditionsForPanel(call.Id)).[0]
        Assert.True(cond.IsOR)

        // 같은 값이면 false 반환
        let notChanged = store.UpdateCallConditionSettings(call.Id, condId, true)
        Assert.False(notChanged)

    [<Fact>]
    let ``ReplaceCallConditionTree preserves per-leaf ContactKind`` () =
        let store = createStore ()
        let _, system, call, _, ac1 = setupCallWithApiCall store
        let apiDef2 = addApiDef store "Api2" system.Id
        let ac2 = store.AddApiCallFromPanel(call.Id, apiDef2.Id, "", "", "", "", 0, "", 0, "", true)
        let tree = dtoEx false false [ ac1; ac2 ]
                          [ ContactKind.NcContact; ContactKind.RisingPulse ] []
        store.ReplaceCallConditionTree(call.Id, CallConditionType.AutoAux, tree)
        let conds = store.GetCallConditionsForPanel(call.Id)
        Assert.Equal(2, conds.[0].Items.Length)
        Assert.Equal(ContactKind.NcContact,   conds.[0].Items.[0].ContactKind)
        Assert.Equal(ContactKind.RisingPulse, conds.[0].Items.[1].ContactKind)

    [<Fact>]
    let ``ReplaceCallConditionTree preserves Inverter placeholder leaf`` () =
        let store = createStore ()
        let _, _, call, _, ac1 = setupCallWithApiCall store
        // 인버터 leaf 는 Guid.Empty + ContactKind.Inverter 로 들어감.
        let tree = dtoEx false false [ ac1; Guid.Empty ]
                          [ ContactKind.NoContact; ContactKind.Inverter ] []
        store.ReplaceCallConditionTree(call.Id, CallConditionType.AutoAux, tree)
        let conds = store.GetCallConditionsForPanel(call.Id)
        Assert.Equal(2, conds.[0].Items.Length)
        let kinds = conds.[0].Items |> List.map (fun i -> i.ContactKind)
        Assert.Contains(ContactKind.Inverter, kinds)

    [<Fact>]
    let ``ReplaceCallConditionTree preserves IsInverted on group`` () =
        let store = createStore ()
        let _, _, call, _, ac1 = setupCallWithApiCall store
        let tree = dtoEx false true [ ac1 ] [ ContactKind.NoContact ] []
        store.ReplaceCallConditionTree(call.Id, CallConditionType.AutoAux, tree)
        let conds = store.GetCallConditionsForPanel(call.Id)
        Assert.True(conds.[0].IsInverted)

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

    let private aci (name: string) (spec: string) =
        CallConditionApiCallItem(Guid.NewGuid(), name, name, spec, 0, "", 0,
                                 ContactKind.NoContact, ValueSpec.UndefinedValue, true)

    let private panel isOr items children =
        CallConditionPanelItem(Guid.NewGuid(), CallConditionType.ComAux,
                               isOr, false, items, children)

    [<Fact>]
    let ``Empty condition returns (empty)`` () =
        Assert.Equal("(empty)", (panel false [] []).FormulaText())

    [<Fact>]
    let ``Single ApiCall shows name only`` () =
        Assert.Equal("MyApi", (panel false [aci "MyApi" ""] []).FormulaText())

    [<Fact>]
    let ``ApiCall with spec shows name=spec`` () =
        Assert.Equal("MyApi=True", (panel false [aci "MyApi" "True"] []).FormulaText())

    [<Fact>]
    let ``Undefined spec is hidden`` () =
        Assert.Equal("MyApi", (panel false [aci "MyApi" "Undefined"] []).FormulaText())

    [<Fact>]
    let ``AND operator joins with &`` () =
        Assert.Equal("A&B", (panel false [aci "A" ""; aci "B" ""] []).FormulaText())

    [<Fact>]
    let ``OR operator joins with |`` () =
        Assert.Equal("A|B", (panel true [aci "A" ""; aci "B" ""] []).FormulaText())

    [<Fact>]
    let ``Children are wrapped in parentheses`` () =
        let child = panel true [aci "A" ""] []
        Assert.Equal("B&(A)", (panel false [aci "B" ""] [child]).FormulaText())
