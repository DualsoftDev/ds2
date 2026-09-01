module Ds2.Store.Editor.Tests.SkipActionConditionTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.CSV

// SkipAction 조건 leaf 의 InputSpec 이 UndefinedValue 로 남으면 런타임
// checkConditionSpecBase 가 "RxWork 가 Finish 인가" 분기를 타서 참조 신호의 값을 보지 않는다.
// → 어떤 상태에서도 skip 이 일어나지 않는다(실사용 버그: SkipAction 을 걸어도 전부 실행됨).
// 조건에 넣는 leaf 는 기대값 기본을 BoolValue(Single false) 로 채워야 한다.

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
    Assert.Equal(ValueSpec.BoolValue(Single false), leaf.InputSpec)

[<Fact>]
let ``Work SkipAction leaf 도 기대값 기본이 채워진다`` () =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let work = store.Works.[target.ParentId]
    let source = (callByName store "DATA.DA").ApiCalls.[0].Id
    store.AddWorkConditionWithApiCalls(work.Id, ConditionType.SkipAction, [ source ]) |> ignore
    let cond = work.Conditions |> Seq.exactlyOne
    Assert.Equal(ValueSpec.BoolValue(Single false), (cond.ApiCalls |> Seq.exactlyOne).InputSpec)

[<Fact>]
let ``기존 SkipAction 조건에 추가한 leaf 도 기대값이 채워진다`` () =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let source = (callByName store "DATA.DA").ApiCalls.[0].Id
    store.AddCallCondition(target.Id, ConditionType.SkipAction)
    let cond = target.Conditions |> Seq.exactlyOne
    store.AddApiCallsToConditionBatch(target.Id, cond.Id, [ source ]) |> ignore
    Assert.Equal(ValueSpec.BoolValue(Single false), (cond.ApiCalls |> Seq.exactlyOne).InputSpec)

[<Fact>]
let ``SkipAction 이 아닌 조건은 기대값을 건드리지 않는다`` () =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let source = (callByName store "DATA.DA").ApiCalls.[0].Id
    store.AddConditionWithApiCalls(target.Id, ConditionType.AutoAux, [ source ]) |> ignore
    let _, leaf = soleLeaf target
    Assert.Equal(ValueSpec.UndefinedValue, leaf.InputSpec)

[<Fact>]
let ``사용자가 지정한 기대값은 덮어쓰지 않는다`` () =
    let store = buildStore ()
    let target = callByName store "A.ADV"
    let sourceCall = callByName store "DATA.DA"
    sourceCall.ApiCalls.[0].InputSpec <- ValueSpec.BoolValue(Single true)
    store.AddConditionWithApiCalls(target.Id, ConditionType.SkipAction, [ sourceCall.ApiCalls.[0].Id ]) |> ignore
    let _, leaf = soleLeaf target
    Assert.Equal(ValueSpec.BoolValue(Single true), leaf.InputSpec)
