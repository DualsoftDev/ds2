module Ds2.Store.Editor.Tests.SkipActionConditionTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.CSV

// SkipAction 조건 leaf 의 InputSpec 이 UndefinedValue 로 남으면 ValueSpec.evaluate 가 늘 true 라
// 값 비교가 통째로 무력화된다(참조 신호가 무엇이든 통과) → 조건이 조건 구실을 못 한다.
// 그래서 조건에 넣는 leaf 는 기대값 기본을 채워야 한다.
//
// 기본값은 BoolValue(Single true) — "그 신호가 켜졌는가". 예전엔 false 였으나
//   ① 판정이 "만족하면 skip" 으로 정리되고
//   ② 부정은 leaf 접점이 아니라 그룹(Condition.IsInverted)이 지게 되었으며
//   ③ leaf 판정이 "IO 값이 있으면 값이 기준" 으로 바뀌면서
// false 특례 분기(isFalse → RxWork Ready 확인)를 탈 이유가 없어졌다.

let private buildStore () =
    let csv = "FLOW,WORK,CALL\n투입,작업A,A.ADV>A.RET\n투입,선택,DATA.DA"
    match CsvImporter.parseBasicContent csv with
    | Error errors -> failwith (String.concat "\n" errors)
    | Ok doc ->
        match CsvImporter.loadBasicProject doc "P" "S" with
        | Error errors -> failwith (String.concat "\n" errors)
        | Ok store -> store

let private callByName (store: DsStore) name =
    store.Calls.Values |> Seq.find (fun c -> c.Name = name)

let private soleLeaf (call: Call) =
    let cond = call.Conditions |> Seq.exactlyOne
    cond, (cond.ApiCalls |> Seq.exactlyOne)

[<Fact>]
let ``Call SkipAction leaf 는 기대값 기본이 채워진다`` () =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let source = (callByName store "DATA.DA").ApiCalls.[0].Id
    store.AddConditionWithApiCalls(target.Id, ConditionType.SkipAction, [ source ]) |> ignore
    let _, leaf = soleLeaf target
    Assert.Equal(ValueSpec.BoolValue(Single true), leaf.InputSpec)

[<Fact>]
let ``Work SkipAction leaf 도 기대값 기본이 채워진다`` () =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let work = store.Works.[target.ParentId]
    let source = (callByName store "DATA.DA").ApiCalls.[0].Id
    store.AddWorkConditionWithApiCalls(work.Id, ConditionType.SkipAction, [ source ]) |> ignore
    let cond = work.Conditions |> Seq.exactlyOne
    Assert.Equal(ValueSpec.BoolValue(Single true), (cond.ApiCalls |> Seq.exactlyOne).InputSpec)

[<Fact>]
let ``기존 SkipAction 조건에 추가한 leaf 도 기대값이 채워진다`` () =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let source = (callByName store "DATA.DA").ApiCalls.[0].Id
    store.AddCallCondition(target.Id, ConditionType.SkipAction)
    let cond = target.Conditions |> Seq.exactlyOne
    store.AddApiCallsToConditionBatch(target.Id, cond.Id, [ source ]) |> ignore
    Assert.Equal(ValueSpec.BoolValue(Single true), (cond.ApiCalls |> Seq.exactlyOne).InputSpec)

[<Fact>]
let ``조건 유형을 가리지 않고 기대값이 채워진다`` () =
    // 예전에는 SkipAction 에만 채웠다. 그러면 AutoAux/ComAux leaf 가 Undefined 로 남고
    // ValueSpec.evaluate Undefined _ 가 늘 true 라 값 비교가 통째로 무력화된다.
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let source = (callByName store "DATA.DA").ApiCalls.[0].Id
    store.AddConditionWithApiCalls(target.Id, ConditionType.AutoAux, [ source ]) |> ignore
    let _, leaf = soleLeaf target
    Assert.Equal(ValueSpec.BoolValue(Single true), leaf.InputSpec)

[<Fact>]
let ``자식 그룹에 넣은 leaf 도 기대값이 채워진다`` () =
    // 자식 그룹은 Type=None 으로 만들어진다. 유형 게이트가 있던 시절에는 여기가 비어
    // Undefined 로 남았고, 판정이 «성립하면 skip» 인 지금은 곧 무조건 skip 이었다.
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let source = (callByName store "DATA.DA").ApiCalls.[0].Id
    store.AddCallCondition(target.Id, ConditionType.SkipAction)
    let root = target.Conditions |> Seq.exactlyOne
    store.AddChildCondition(target.Id, root.Id, true)
    let child = root.Children |> Seq.exactlyOne
    store.AddApiCallsToConditionBatch(target.Id, child.Id, [ source ]) |> ignore
    Assert.Equal(ValueSpec.BoolValue(Single true), (child.ApiCalls |> Seq.exactlyOne).InputSpec)

[<Fact>]
let ``사용자가 지정한 기대값은 덮어쓰지 않는다`` () =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let sourceCall = callByName store "DATA.DA"
    // 기본값(true)과 다른 값을 미리 넣어야 «덮어쓰지 않는다» 가 실제로 검증된다.
    sourceCall.ApiCalls.[0].InputSpec <- ValueSpec.BoolValue(Single false)
    store.AddConditionWithApiCalls(target.Id, ConditionType.SkipAction, [ sourceCall.ApiCalls.[0].Id ]) |> ignore
    let _, leaf = soleLeaf target
    Assert.Equal(ValueSpec.BoolValue(Single false), leaf.InputSpec)


// =============================================================================
// 그룹 부정(IsInverted) — 저장 API 와 런타임 판정
// =============================================================================

[<Fact>]
let ``그룹 부정 토글은 값이 바뀔 때만 true 를 돌려준다`` () =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    store.AddCallCondition(target.Id, ConditionType.SkipAction)
    let cond = target.Conditions |> Seq.exactlyOne
    Assert.False(cond.IsInverted)
    Assert.True(store.SetCallConditionInverted(target.Id, cond.Id, true))
    Assert.True(cond.IsInverted)
    // 같은 값으로 다시 부르면 변경 없음 — undo 스택에 헛 항목을 쌓지 않는다.
    Assert.False(store.SetCallConditionInverted(target.Id, cond.Id, true))
    Assert.True(store.SetCallConditionInverted(target.Id, cond.Id, false))
    Assert.False(cond.IsInverted)

[<Fact>]
let ``Work 그룹 부정도 같은 규칙으로 토글된다`` () =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let work = store.Works.[target.ParentId]
    store.AddWorkCondition(work.Id, ConditionType.SkipAction)
    let cond = work.Conditions |> Seq.exactlyOne
    Assert.True(store.SetWorkConditionInverted(work.Id, cond.Id, true))
    Assert.True(cond.IsInverted)
    Assert.False(store.SetWorkConditionInverted(work.Id, cond.Id, true))

/// SkipAction 진리표용 — 조건을 건 Call 과 참조 leaf 를 만들고 런타임 인덱스를 세운다.
let private buildSkipCase (inverted: bool) =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let source = (callByName store "DATA.DA").ApiCalls.[0]
    store.AddConditionWithApiCalls(target.Id, ConditionType.SkipAction, [ source.Id ]) |> ignore
    let cond = target.Conditions |> Seq.exactlyOne
    if inverted then store.SetCallConditionInverted(target.Id, cond.Id, true) |> ignore
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let state =
        Ds2.Runtime.Model.SimState.create 10
            (store.Works.Values |> Seq.map (fun w -> w.Id))
            (store.Calls.Values |> Seq.map (fun c -> c.Id))
            (store.Flows.Values |> Seq.map (fun f -> f.Id))
    store, target, source, index, state

/// leaf 를 성립시킨다 — 기대값 기본이 true 이므로 IO 에 "true" 를 실어 준다.
let private withLeafValue (value: string) (source: ApiCall) state =
    Ds2.Runtime.Model.SimState.setIOValue source.Id value state

[<Theory>]
[<InlineData(false, "true", true)>]    // 부정 없음 + 성립   → skip
[<InlineData(false, "false", false)>]  // 부정 없음 + 불성립 → 실행
[<InlineData(true, "true", false)>]    // 부정      + 성립   → 실행
[<InlineData(true, "false", true)>]    // 부정      + 불성립 → skip
let ``SkipAction 진리표 — 그룹 부정 × leaf 성립`` (inverted: bool) (io: string) (expectedSkip: bool) =
    // 「조건이 성립하면 건너뛴다」 가 규칙이고, 부정은 그룹이 진다.
    let _, target, source, index, state0 = buildSkipCase inverted
    let state = withLeafValue io source state0
    Assert.Equal(expectedSkip, Ds2.Runtime.Engine.Core.WorkConditionChecker.shouldSkipCall index state target.Id)

[<Fact>]
let ``빈 조건 그룹에 부정을 걸어도 무조건 skip 되지 않는다`` () =
    // Not (Or []) = true 라 예전에는 그 Call 이 언제나 건너뛰어졌다.
    // 그룹 부정 토글이 생기면서 «빈 그룹 추가 → 부정» 두 번으로 닿을 수 있게 된 경로.
    let store = buildStore ()
    let target = callByName store "A.ADV"
    store.AddCallCondition(target.Id, ConditionType.SkipAction)
    let cond = target.Conditions |> Seq.exactlyOne
    cond.IsOR <- true
    store.SetCallConditionInverted(target.Id, cond.Id, true) |> ignore
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let state =
        Ds2.Runtime.Model.SimState.create 10
            (store.Works.Values |> Seq.map (fun w -> w.Id))
            (store.Calls.Values |> Seq.map (fun c -> c.Id))
            (store.Flows.Values |> Seq.map (fun f -> f.Id))
    Assert.False(Ds2.Runtime.Engine.Core.WorkConditionChecker.shouldSkipCall index state target.Id)
