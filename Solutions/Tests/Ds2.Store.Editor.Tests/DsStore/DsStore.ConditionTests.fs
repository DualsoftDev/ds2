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

        store.AddCallCondition(call.Id, ConditionType.SkipAction)

        Assert.Equal(1, call.Conditions.Count)
        Assert.Equal(Some ConditionType.SkipAction, call.Conditions.[0].Type)

    [<Fact>]
    let ``RemoveCallCondition 은 condition 을 제거하고 Undo 가 복원한다`` () =
        let store = createStore ()
        let _, _, _, call, _, _ = setupCallWithApiCalls store
        store.AddCallCondition(call.Id, ConditionType.SkipAction)
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

        store.AddWorkCondition(work.Id, ConditionType.SkipAction)

        Assert.Equal(1, work.Conditions.Count)
        Assert.Equal(Some ConditionType.SkipAction, work.Conditions.[0].Type)

    [<Fact>]
    let ``RemoveWorkCondition 은 condition 을 제거하고 Undo 가 복원한다`` () =
        let store = createStore ()
        let _, work, _, _ = setupWorkWithApiCalls store
        store.AddWorkCondition(work.Id, ConditionType.SkipAction)
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

        store.ReplaceWorkConditionTree(work.Id, ConditionType.SkipAction, dtoFlat false [ ac1 ])
        Assert.Equal(1, work.Conditions.Count)
        Assert.Equal(1, work.Conditions.[0].ApiCalls.Count)

        store.ReplaceWorkConditionTree(work.Id, ConditionType.SkipAction, dtoFlat true [ ac1; ac2 ])
        Assert.Equal(1, work.Conditions.Count)
        Assert.Equal(2, work.Conditions.[0].ApiCalls.Count)
        Assert.True(work.Conditions.[0].IsOR)

    [<Fact>]
    let ``AddWorkConditionWithApiCalls 는 단일 트랜잭션으로 Condition + ApiCall 을 추가한다`` () =
        let store = createStore ()
        let _, work, ac1, ac2 = setupWorkWithApiCalls store

        let condId = store.AddWorkConditionWithApiCalls(work.Id, ConditionType.SkipAction, [ ac1; ac2 ])

        Assert.Equal(1, work.Conditions.Count)
        let cond = work.Conditions.[0]
        Assert.Equal(condId, cond.Id)
        Assert.Equal(Some ConditionType.SkipAction, cond.Type)
        Assert.Equal(2, cond.ApiCalls.Count)

    [<Fact>]
    let ``AddWorkChildCondition 은 중첩 condition 을 추가한다`` () =
        let store = createStore ()
        let _, work, _, _ = setupWorkWithApiCalls store
        store.AddWorkCondition(work.Id, ConditionType.SkipAction)
        let parentId = work.Conditions.[0].Id

        store.AddWorkChildCondition(work.Id, parentId, true)

        Assert.Equal(1, work.Conditions.[0].Children.Count)
        Assert.True(work.Conditions.[0].Children.[0].IsOR)

    [<Fact>]
    let ``UpdateWorkConditionSettings 는 IsOR 을 토글한다`` () =
        let store = createStore ()
        let _, work, _, _ = setupWorkWithApiCalls store
        store.AddWorkCondition(work.Id, ConditionType.SkipAction)
        let condId = work.Conditions.[0].Id

        let changed = store.UpdateWorkConditionSettings(work.Id, condId, true)
        Assert.True(changed)
        Assert.True(work.Conditions.[0].IsOR)

    [<Fact>]
    let ``AddApiCallsToWorkConditionBatch 는 기존 condition 에 ApiCall 들을 추가한다`` () =
        let store = createStore ()
        let _, work, ac1, ac2 = setupWorkWithApiCalls store
        store.AddWorkCondition(work.Id, ConditionType.SkipAction)
        let condId = work.Conditions.[0].Id

        let added = store.AddApiCallsToWorkConditionBatch(work.Id, condId, [ ac1; ac2 ])

        Assert.Equal(2, added)
        Assert.Equal(2, work.Conditions.[0].ApiCalls.Count)

    [<Fact>]
    let ``RemoveApiCallFromWorkCondition 은 단일 ApiCall 만 제거한다`` () =
        let store = createStore ()
        let _, work, ac1, ac2 = setupWorkWithApiCalls store
        store.AddWorkConditionWithApiCalls(work.Id, ConditionType.SkipAction, [ ac1; ac2 ]) |> ignore
        let condId = work.Conditions.[0].Id

        store.RemoveApiCallFromWorkCondition(work.Id, condId, ac1)

        Assert.Equal(1, work.Conditions.[0].ApiCalls.Count)
        Assert.Equal(ac2, work.Conditions.[0].ApiCalls.[0].Id)

    [<Fact>]
    let ``GetWorkConditionsForPanel 은 추가된 Work condition 을 패널 형식으로 반환한다`` () =
        let store = createStore ()
        let _, work, ac1, _ = setupWorkWithApiCalls store
        store.AddWorkConditionWithApiCalls(work.Id, ConditionType.SkipAction, [ ac1 ]) |> ignore

        let items = store.GetWorkConditionsForPanel(work.Id)

        Assert.Equal(1, items.Length)
        Assert.Equal(ConditionType.SkipAction, items.[0].ConditionType)


// =============================================================================
// Work / Call 독립성 — 같은 store 안에서 서로 영향 없음
// =============================================================================

module CallWorkIsolationTests =

    [<Fact>]
    let ``Work 에 추가한 condition 은 같은 work 의 Call 에 영향을 주지 않는다`` () =
        let store = createStore ()
        let _, _, work, call, ac1, _ = setupCallWithApiCalls store

        store.AddWorkCondition(work.Id, ConditionType.SkipAction)
        store.AddCallCondition(call.Id, ConditionType.AutoAux)
        store.AddApiCallsToWorkConditionBatch(work.Id, work.Conditions.[0].Id, [ ac1 ]) |> ignore

        Assert.Equal(1, work.Conditions.Count)
        Assert.Equal(1, call.Conditions.Count)
        Assert.Equal(Some ConditionType.SkipAction, work.Conditions.[0].Type)
        Assert.Equal(Some ConditionType.AutoAux,    call.Conditions.[0].Type)
        Assert.Equal(1, work.Conditions.[0].ApiCalls.Count)
        Assert.Equal(0, call.Conditions.[0].ApiCalls.Count)


// =============================================================================
// Condition formula projection (ConditionFormulaProjection)
//   - IsInverted -> NOT 표기
//   - ContactKind -> 수식 구분 표기 (NcContact `/`, RisingPulse `(R)`, FallingPulse `(F)`)
//   - 빈 condition -> Runtime 의미 (빈 And=true, 빈 Or=false)
// =============================================================================

module ConditionFormulaProjectionTests =

    /// leaf 패널 항목 생성 헬퍼 — projection 표시만 검증하므로 store 없이 직접 구성.
    let private leaf (name: string) (kind: ContactKind) : ConditionApiCallItem =
        ConditionApiCallItem(
            Guid.NewGuid(), name, name,
            "", 0,        // outputSpec (text/index) — 기대값 없음
            "", 0,        // inputSpec (text/index)
            kind, UndefinedValue)

    /// inputSpec 텍스트를 가진 leaf (= 기대값 표기 검증용).
    /// condition leaf 기대값은 InputSpec(Runtime 평가 대상)이므로 inputSpecText 인자에 채운다.
    let private leafWithSpec (name: string) (specText: string) : ConditionApiCallItem =
        ConditionApiCallItem(
            Guid.NewGuid(), name, name,
            "", 0,             // outputSpec — condition leaf 표시에 쓰지 않음
            specText, 0,       // inputSpec — 기대값(=spec)
            ContactKind.NoContact, UndefinedValue)

    /// ValueSpec(InputSpec) 으로부터 leaf 를 만든다 — eq 기대값 표시(BoolValue/StringValue/numeric) 검증용.
    /// Panel.fs 생성부와 동일하게 PropertyPanelValueSpec.format 으로 InputSpecText 를 채운다.
    let private leafOfInputSpec (name: string) (kind: ContactKind) (inputSpec: ValueSpec) : ConditionApiCallItem =
        ConditionApiCallItem(
            Guid.NewGuid(), name, name,
            "", 0,
            PropertyPanelValueSpec.format inputSpec, PropertyPanelValueSpec.dataTypeIndex inputSpec,
            kind, inputSpec)

    let private cond (isOR: bool) (isInverted: bool)
                     (items: ConditionApiCallItem list) (children: ConditionPanelItem list) : ConditionPanelItem =
        ConditionPanelItem(Guid.NewGuid(), ConditionType.AutoAux, isOR, isInverted, items, children)

    let private formula (c: ConditionPanelItem) = ConditionFormulaProjection.formatCondition c

    // ── IsInverted ──

    [<Fact>]
    let ``IsInverted=true 인 OR 조건은 not (A | B) 로 표시된다`` () =
        let c = cond true true [ leaf "A" ContactKind.NoContact; leaf "B" ContactKind.NoContact ] []
        Assert.Equal("not (A | B)", formula c)

    [<Fact>]
    let ``IsInverted=false 면 NOT 표기가 없다`` () =
        let c = cond true false [ leaf "A" ContactKind.NoContact; leaf "B" ContactKind.NoContact ] []
        Assert.Equal("A | B", formula c)

    [<Fact>]
    let ``중첩 자식의 IsInverted 도 NOT 으로 표시된다`` () =
        // A & not (B | C)
        let child = cond true true [ leaf "B" ContactKind.NoContact; leaf "C" ContactKind.NoContact ] []
        let c = cond false false [ leaf "A" ContactKind.NoContact ] [ child ]
        Assert.Equal("A & (not (B | C))", formula c)

    // ── ContactKind ──

    [<Fact>]
    let ``NcContact 는 leaf 앞에 슬래시로 표시된다`` () =
        let c = cond false false [ leaf "A" ContactKind.NcContact ] []
        Assert.Equal("/A", formula c)

    [<Fact>]
    let ``RisingPulse 는 leaf 뒤에 (R) 로 표시된다`` () =
        let c = cond false false [ leaf "A" ContactKind.RisingPulse ] []
        Assert.Equal("A(R)", formula c)

    [<Fact>]
    let ``FallingPulse 는 leaf 뒤에 (F) 로 표시된다`` () =
        let c = cond false false [ leaf "A" ContactKind.FallingPulse ] []
        Assert.Equal("A(F)", formula c)

    [<Fact>]
    let ``NoContact 는 ContactKind 표기 없이 이름만 표시된다`` () =
        let c = cond false false [ leaf "A" ContactKind.NoContact ] []
        Assert.Equal("A", formula c)

    [<Fact>]
    let ``ContactKind 5종이 한 수식에서 구분되어 표시된다`` () =
        let c =
            cond false false
                [ leaf "A" ContactKind.NoContact
                  leaf "B" ContactKind.NcContact
                  leaf "C" ContactKind.RisingPulse
                  leaf "D" ContactKind.FallingPulse
                  leaf "E" ContactKind.Inverter ]
                []
        // Inverter 는 placeholder leaf (ApiCallId 무시) → `*`
        Assert.Equal("A & /B & C(R) & D(F) & *", formula c)

    [<Fact>]
    let ``ContactKind 표기는 기대값(=) 표기와 함께 보존된다`` () =
        // RisingPulse + inputSpec(기대값) → name=spec(R)
        let item = ConditionApiCallItem(Guid.NewGuid(), "A", "A", "", 0, "true", 0, ContactKind.RisingPulse, UndefinedValue)
        let c = cond false false [ item ] []
        Assert.Equal("A=true(R)", formula c)

    // ── 빈 condition (Runtime 의미) ──

    [<Fact>]
    let ``빈 And 조건은 true 로 표시된다`` () =
        let c = cond false false [] []
        Assert.Equal("true", formula c)

    [<Fact>]
    let ``빈 Or 조건은 false 로 표시된다`` () =
        let c = cond true false [] []
        Assert.Equal("false", formula c)

    [<Fact>]
    let ``빈 And 자식은 부모 And 에서 항등원이라 생략된다`` () =
        // A & (빈 And=true) → A 만 남는다 (true 는 And 항등원).
        let emptyChild = cond false false [] []
        let c = cond false false [ leaf "A" ContactKind.NoContact ] [ emptyChild ]
        Assert.Equal("A", formula c)

    [<Fact>]
    let ``빈 Or 자식은 부모 And 에서 false 로 표시되어 보존된다`` () =
        // A & (빈 Or=false) → A & (false). false 는 And 항등원이 아니라 의미가 있어 표시.
        let emptyOrChild = cond true false [] []
        let c = cond false false [ leaf "A" ContactKind.NoContact ] [ emptyOrChild ]
        Assert.Equal("A & (false)", formula c)

    // ── 회귀: 기존 중첩/연산자 동작 ──

    [<Fact>]
    let ``A & (B | C) 중첩 그룹이 보존된다`` () =
        let child = cond true false [ leaf "B" ContactKind.NoContact; leaf "C" ContactKind.NoContact ] []
        let c = cond false false [ leaf "A" ContactKind.NoContact ] [ child ]
        Assert.Equal("A & (B | C)", formula c)

    [<Fact>]
    let ``inputSpec 기대값은 name=spec 으로 표시된다`` () =
        let c = cond false false [ leafWithSpec "A" "true" ] []
        Assert.Equal("A=true", formula c)

    // ── eq 기대값(InputSpec) 표시 — condition leaf 기대값 = InputSpec 확정 ──
    // (7-reviewer Major: condition leaf 기대값은 InputSpec(Phase 2 의 eq 저장 위치, Runtime 평가 대상)이며
    //  수식에 `=spec` 으로 표시되어야 한다. OutputSpec 은 condition leaf 표시에 쓰지 않는다.)

    [<Fact>]
    let ``InputSpec BoolValue true 는 수식에 =true 로 표시된다`` () =
        let c = cond false false [ leafOfInputSpec "A" ContactKind.NoContact (ValueSpec.singleBool true) ] []
        Assert.Equal("A=true", formula c)

    [<Fact>]
    let ``InputSpec StringValue OPEN 은 수식에 =OPEN 으로 표시된다`` () =
        let c = cond false false [ leafOfInputSpec "Door" ContactKind.NoContact (ValueSpec.singleString "OPEN") ] []
        Assert.Equal("Door=OPEN", formula c)

    [<Fact>]
    let ``InputSpec numeric(Int32) 는 수식에 =42 로 표시된다`` () =
        let c = cond false false [ leafOfInputSpec "Cnt" ContactKind.NoContact (ValueSpec.singleInt32 42) ] []
        Assert.Equal("Cnt=42", formula c)

    [<Fact>]
    let ``InputSpec 기대값과 ContactKind 표기가 결합된다`` () =
        // A접(NoContact) → name=spec, B접(NcContact) → /name=spec, RisingPulse → name=spec(R)
        let mk kind = leafOfInputSpec "A" kind (ValueSpec.singleBool true)
        Assert.Equal("A=true",    formula (cond false false [ mk ContactKind.NoContact ] []))
        Assert.Equal("/A=true",   formula (cond false false [ mk ContactKind.NcContact ] []))
        Assert.Equal("A=true(R)", formula (cond false false [ mk ContactKind.RisingPulse ] []))
        Assert.Equal("A=true(F)", formula (cond false false [ mk ContactKind.FallingPulse ] []))

    [<Fact>]
    let ``OutputSpec 만 있고 InputSpec 이 비면 기대값을 표시하지 않는다`` () =
        // condition leaf 표시는 InputSpec 기준 — OutputSpec 에 값이 있어도 InputSpec 이 비면 name 만.
        let item = ConditionApiCallItem(Guid.NewGuid(), "A", "A", "true", 0, "", 0, ContactKind.NoContact, UndefinedValue)
        let c = cond false false [ item ] []
        Assert.Equal("A", formula c)
