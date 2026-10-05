module Ds2.Store.Editor.Tests.Ds2TextTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Text

// DS2 Text v4 원문 생성기의 회귀 테스트.
//
// 판정 기준은 「그럴듯한 문자열」이 아니라 **참조 구현의 계약**이다.
//   · 표기·생략·배치 : ds2_text/ds2text/text.py 의 format_text
//   · 저장 대응       : ds2_text/native/Program.fs (IR → DsStore)
// 그래서 각 테스트는 참조 문서에 적힌 한 줄을 그대로 확인한다.
//
// 모델은 전부 인라인으로 만든다 — 테스트가 파일 경로에 매이면 다른 기계에서 깨진다.

// ─────────────────────────────────────────────────────────── 헬퍼

/// Project → active System → Flow 까지 만든 최소 뼈대.
let private skeleton (projectName: string) (systemName: string) (flowName: string) =
    let store = DsStore()
    let projectId = store.AddProject projectName
    let systemId = store.AddSystem(systemName, projectId, true)
    let flowId = store.AddFlow(flowName, systemId)
    store, projectId, systemId, flowId

let private text (store: DsStore) (projectId: Guid) = Ds2TextWriter.write store projectId

let private lines (source: string) =
    source.Split('\n') |> Array.map (fun l -> l.Trim()) |> Array.filter (fun l -> l <> "")

let private contains (needle: string) (source: string) =
    Assert.True(source.Contains needle, $"다음 줄이 없습니다: «{needle}»\n---- 실제 ----\n{source}")

let private notContains (needle: string) (source: string) =
    Assert.False(source.Contains needle, $"있으면 안 되는 줄입니다: «{needle}»\n---- 실제 ----\n{source}")

let private arrow (store: DsStore) (_systemId: Guid) (a: Guid) (b: Guid) (kind: ArrowType) =
    store.ConnectSelectionInOrder([ a; b ], kind) |> ignore

// ─────────────────────────────────────────────────────────── 골격

[<Fact>]
let ``원문은 ds2 4 헤더로 시작한다`` () =
    let store, projectId, _, _ = skeleton "Line" "Cell" "Process"
    let src = text store projectId
    Assert.Equal("ds2 4;", (lines src).[0])
    contains "project Line {" src
    contains "system Cell active {" src
    contains "flow Process {" src

[<Fact>]
let ``passive 시스템에는 active 표식이 없다`` () =
    let store = DsStore()
    let projectId = store.AddProject "Line"
    store.AddSystem("Jig", projectId, false) |> ignore
    let src = text store projectId
    contains "system Jig {" src
    notContains "system Jig active" src

[<Fact>]
let ``System 의 systemType 은 type 문으로 적힌다`` () =
    let store, projectId, systemId, _ = skeleton "Line" "Jig" "Motion"
    store.Systems.[systemId].SystemType <- Some "Fixture"
    contains "type \"Fixture\";" (text store projectId)

[<Fact>]
let ``Flow 의 비활성·수동은 기본값이 아닐 때만 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let src = text store projectId
    notContains "enabled false;" src
    notContains "auto false;" src

    store.Flows.[flowId].IsDisabled <- true
    store.Flows.[flowId].IsAuto <- false
    let src2 = text store projectId
    contains "enabled false;" src2
    contains "auto false;" src2

// ─────────────────────────────────────────────────────────── 관계

[<Fact>]
let ``다섯 관계가 각자의 기호로 적힌다`` () =
    for (kind, op) in [ ArrowType.Start, " > "; ArrowType.Reset, " |> "; ArrowType.StartReset, " => "; ArrowType.ResetReset, " <|> " ] do
        let store, projectId, systemId, flowId = skeleton "Line" "Cell" "Process"
        let a = store.AddWork("A", flowId)
        let b = store.AddWork("B", flowId)
        arrow store systemId a b kind
        contains ("A" + op + "B;") (text store projectId)

[<Fact>]
let ``인접한 같은 관계는 연쇄로 압축된다`` () =
    let store, projectId, systemId, flowId = skeleton "Line" "Cell" "Process"
    let a = store.AddWork("A", flowId)
    let b = store.AddWork("B", flowId)
    let c = store.AddWork("C", flowId)
    arrow store systemId a b ArrowType.Start
    arrow store systemId b c ArrowType.Start
    contains "A > B > C;" (text store projectId)

[<Fact>]
let ``상호 초기화는 연쇄하지 않는다`` () =
    // `<|>` 는 이름 하나씩만 받는다 — 붙여 쓰면 파서가 거절한다.
    let store, projectId, systemId, flowId = skeleton "Line" "Cell" "Process"
    let a = store.AddWork("A", flowId)
    let b = store.AddWork("B", flowId)
    let c = store.AddWork("C", flowId)
    arrow store systemId a b ArrowType.ResetReset
    arrow store systemId b c ArrowType.ResetReset
    let src = text store projectId
    contains "A <|> B;" src
    contains "B <|> C;" src
    notContains "A <|> B <|> C;" src

[<Fact>]
let ``Flow 를 넘는 관계는 System 레벨에 Flow 경로로 적힌다`` () =
    let store, projectId, systemId, flowId = skeleton "Line" "Cell" "First"
    let second = store.AddFlow("Second", systemId)
    let a = store.AddWork("A", flowId)
    let b = store.AddWork("B", second)
    arrow store systemId a b ArrowType.Start
    let src = text store projectId
    contains "First.A > Second.B;" src

// ─────────────────────────────────────────────────────────── Work

[<Fact>]
let ``제품 역할은 entry·token ignore·exit 로 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let load = store.AddWork("Load", flowId)
    let assist = store.AddWork("Assist", flowId)
    let out = store.AddWork("Out", flowId)
    store.Works.[load].TokenRole <- TokenRole.Source
    store.Works.[assist].TokenRole <- TokenRole.Ignore
    store.Works.[out].TokenRole <- TokenRole.Sink
    let src = text store projectId
    contains "entry Load;" src
    contains "token ignore Assist;" src
    contains "exit Out;" src

[<Fact>]
let ``내용 없는 Work 는 한 줄 목록으로 합쳐진다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    store.AddWork("A", flowId) |> ignore
    store.AddWork("B", flowId) |> ignore
    store.AddWork("C", flowId) |> ignore
    contains "work A, B, C;" (text store projectId)

[<Fact>]
let ``duration·limits·initial 은 Work 본문에 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Closed", flowId)
    let work = store.Works.[w]
    work.Duration <- Some (TimeSpan.FromMilliseconds 100.0)
    work.MinDuration <- Some (TimeSpan.FromMilliseconds 80.0)
    work.MaxDuration <- Some (TimeSpan.FromMilliseconds 150.0)
    let sim = SimulationWorkProperties()
    sim.IsFinished <- true
    work.SetSimulationProperties sim
    let src = text store projectId
    contains "duration 100ms;" src
    contains "limits 80ms, 150ms;" src
    contains "initial finish;" src

[<Fact>]
let ``duration과 limits는 100ns tick을 정수 밀리초로 반올림하지 않는다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Precise", flowId)
    let work = store.Works.[w]
    work.Duration <- Some (TimeSpan.FromTicks 1L)
    work.MinDuration <- Some (TimeSpan.FromTicks 1000L)
    work.MaxDuration <- Some (TimeSpan.FromTicks 6000L)
    let src = text store projectId
    contains "duration 0.0001ms;" src
    contains "limits 0.1ms, 0.6ms;" src

[<Theory>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
let ``explicit initial ready and finish survive Work and Reference writing`` finished reference =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let original = store.AddWork("Original", flowId)
    let selected = if reference then store.AddWork("Alias", flowId) else original
    if reference then store.Works.[selected].ReferenceOf <- Some original
    let properties = SimulationWorkProperties()
    properties.IsFinished <- finished
    store.Works.[selected].SetSimulationProperties properties
    let source = text store projectId
    contains (if finished then "initial finish;" else "initial ready;") source
    if reference then
        contains "work Alias = ref Original {" source
        notContains "work Original {" source

[<Fact>]
let ``TimeSpan 표기는 전체 범위에서도 마지막 tick을 보존한다`` () =
    Assert.Equal("922337203685477.5807ms", Ds2TextLexeme.ms TimeSpan.MaxValue)
    Assert.Equal("-922337203685477.5808ms", Ds2TextLexeme.ms TimeSpan.MinValue)
    Assert.Equal("-0.0001ms", Ds2TextLexeme.ms (TimeSpan.FromTicks -1L))

[<Fact>]
let ``limits 의 빈 경계는 none 으로 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Closed", flowId)
    store.Works.[w].MaxDuration <- Some (TimeSpan.FromMilliseconds 150.0)
    contains "limits none, 150ms;" (text store projectId)

[<Fact>]
let ``참조 Work 는 ref 로 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let original = store.AddWork("Hold", flowId)
    store.AddReferenceWork original |> ignore
    contains "= ref Hold;" (text store projectId)

// ─────────────────────────────────────────────────────────── Call

/// Work 안에 `alias.api` Call 하나를 만든다 (장치 System·ApiDef 포함).
let private addCall (store: DsStore) projectId workId (alias: string) (api: string) =
    store.AddCallsWithDevice(projectId, workId, [ $"{alias}.{api}" ], true, None) |> ignore
    store.Calls.Values |> Seq.find (fun c -> c.ParentId = workId && c.DevicesAlias = alias && c.ApiName = api)

/// 조건 writer의 native/Text fixture는 대상 System을 명시 이름으로 선언한다.
/// editor AddCallsWithDevice의 {Flow}_{alias} 범위 이름은 Text의 System.Api와
/// 다르다. 일반 addCall helper는 그대로 두고 조건 성공 fixture에만 적용한다.
let private useExplicitTextTargets (store: DsStore) (calls: Call list) =
    for call in calls do
        for apiCall in call.ApiCalls do
            let apiDef = store.ApiDefs.[apiCall.ApiDefId.Value]
            store.Systems.[apiDef.ParentId].Name <- (apiCall.Name.Split '.').[0]

[<Fact>]
let ``Call 은 장비 점 기능 으로 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    addCall store projectId w "Jig" "Close" |> ignore
    let src = text store projectId
    contains "Jig.Close {" src
    // 편집 API 가 만든 ApiCall 의 사양은 undefined 다. 기본 binding(주소 없는 bool true)과 다르므로
    // 생략하면 안 된다 — 참조 구현도 undefined 를 별도 사양으로 보존한다.
    contains "input undefined;" src

[<Fact>]
let ``한 장치의 여러 기능은 각자 제 줄로 적힌다`` () =
    // 전에는 손잡이가 별칭(`Jig`)이라 한 장치의 여러 API 가 겹쳤고,
    // `Jig_Close`·`Jig_Open` 으로 넓혀 풀었다. 이름이 `장비.기능` 이 된 지금은
    // 애초에 겹치지 않는다 — 그 합성 규칙이 통째로 필요 없어졌다.
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    addCall store projectId w "Jig" "Close" |> ignore
    addCall store projectId w "Jig" "Open" |> ignore
    let src = text store projectId
    contains "Jig.Close {" src
    contains "Jig.Open {" src

[<Fact>]
let ``Call 사이 관계는 Work 본문에 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let close = addCall store projectId w "Jig" "Close"
    let opened = addCall store projectId w "Gate" "Open"
    store.ConnectSelectionInOrder([ close.Id; opened.Id ], ArrowType.Start) |> ignore
    contains "Jig.Close > Gate.Open;" (text store projectId)

[<Fact>]
let ``Call 옵션은 기본값이 아닐 때만 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let call = addCall store projectId w "Jig" "Close"
    notContains "completion" (text store projectId)


    let props = SimulationCallProperties()
    props.CallType <- CallType.SkipIfCompleted
    props.Timeout <- Some (TimeSpan.FromMilliseconds 2000.0)
    call.SetSimulationProperties props
    call.SequenceLabel <- SequenceLabel.Head
    call.Interlocked <- true
    let src = text store projectId
    contains "completion existing;" src
    contains "timeout 2000ms;" src
    contains "label head;" src
    contains "interlocked true;" src

[<Fact>]
let ``주소 없는 bool true binding도 미정 값과 구별해 명시한다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let call = addCall store projectId w "Jig" "Close"
    call.ApiCalls.[0].InputSpec <- ValueSpec.singleBool true
    call.ApiCalls.[0].OutputSpec <- ValueSpec.singleBool true
    let src = text store projectId
    contains "input bool == true;" src
    contains "output bool = true;" src

[<Fact>]
let ``주소가 붙은 값은 타입 뒤 at 으로 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let call = addCall store projectId w "Jig" "Close"
    let api = call.ApiCalls.[0]
    api.InputSpec <- ValueSpec.singleInt16 4s
    api.InTag <- Some (IOTag("D10", "D10", ""))
    api.OutputSpec <- ValueSpec.singleBool true
    api.OutTag <- Some (IOTag("Y0", "Y0", ""))
    let src = text store projectId
    contains "input int16 at \"D10\" == 4;" src
    contains "output bool at \"Y0\" = true;" src

[<Fact>]
let ``접점은 no 가 아닐 때만 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let call = addCall store projectId w "Jig" "Close"
    call.ApiCalls.[0].ContactKind <- ContactKind.NcContact
    contains "contact nc;" (text store projectId)

// ─────────────────────────────────────────────────────────── 값 사양

[<Fact>]
let ``값 사양은 타입과 함께 적힌다`` () =
    Assert.Equal("undefined", Ds2TextSpec.format "==" UndefinedValue)
    Assert.Equal("int16 any", Ds2TextSpec.format "==" (Int16Value Undefined))
    Assert.Equal("int16 == 4", Ds2TextSpec.format "==" (ValueSpec.singleInt16 4s))
    Assert.Equal("int16 = 4", Ds2TextSpec.format "=" (ValueSpec.singleInt16 4s))
    Assert.Equal("string in {\"OK\", \"REWORK\"}", Ds2TextSpec.format "==" (StringValue (Multiple [ "OK"; "REWORK" ])))
    Assert.Equal("bool == true", Ds2TextSpec.format "==" (ValueSpec.singleBool true))

[<Fact>]
let ``숫자 구간은 경계 기호와 무한으로 적힌다`` () =
    let seg lower upper : RangeSegment<float32> = { Lower = lower; Upper = upper }
    let spec =
        Float32Value (Ranges [ seg None (Some (0.0f, Open))
                               seg (Some (9.5f, Closed)) (Some (10.5f, Open))
                               seg (Some (100.0f, Open)) None ])
    Assert.Equal("float32 in (*, 0.0) | [9.5, 10.5) | (100.0, *)", Ds2TextSpec.format "==" spec)

// ─────────────────────────────────────────────────────────── 조건

[<Fact>]
let ``조건은 start·permit·skip when 으로 적힌다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let guard = addCall store projectId w "Guard" "Safe"
    let run = addCall store projectId w "Driver" "Torque"
    useExplicitTextTargets store [ guard; run ]
    let leaf = guard.ApiCalls.[0]
    leaf.InputSpec <- ValueSpec.singleBool true
    for (kind, _) in [ ConditionType.AutoAux, "start"; ConditionType.ComAux, "permit"; ConditionType.SkipAction, "skip" ] do
        store.AddConditionWithApiCalls(run.Id, kind, [ leaf.Id ]) |> ignore
    let src = text store projectId
    // 편집 API 는 조건 leaf 로 **그 Call 의 ApiCall 을 그대로** 담는다(id 공유).
    // 그건 「특정 실행 Call 의 입력을 공유한다」는 뜻이므로 원문에도 from 이 살아야 한다.
    contains "start when Guard.Safe from Cell.Process.Hold.Guard.Safe as bool == true;" src
    contains "permit when Guard.Safe from Cell.Process.Hold.Guard.Safe as bool == true;" src
    contains "skip when Guard.Safe from Cell.Process.Hold.Guard.Safe as bool == true;" src

[<Fact>]
let ``조건 leaf 의 접점은 이름 앞에 붙는다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let guard = addCall store projectId w "Guard" "Blocked"
    let run = addCall store projectId w "Driver" "Torque"
    useExplicitTextTargets store [ guard; run ]
    guard.ApiCalls.[0].InputSpec <- ValueSpec.singleBool true
    let condId = store.AddConditionWithApiCalls(run.Id, ConditionType.AutoAux, [ guard.ApiCalls.[0].Id ])
    let cond = run.Conditions |> Seq.find (fun c -> c.Id = condId)
    // 조건은 원본 ApiCall 을 복사해 담으므로, 복사본의 접점을 바꾼다.
    for a in cond.ApiCalls do a.ContactKind <- ContactKind.NcContact
    contains "start when nc Guard.Blocked from Cell.Process.Hold.Guard.Blocked as bool == true;" (text store projectId)

let private conditionCase () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let workId = store.AddWork("Hold", flowId)
    let first = addCall store projectId workId "GuardA" "Safe"
    let second = addCall store projectId workId "GuardB" "Safe"
    let target = addCall store projectId workId "Driver" "Torque"
    useExplicitTextTargets store [ first; second; target ]
    for call in [ first; second ] do
        call.ApiCalls.[0].InputSpec <- ValueSpec.singleBool true
    store, projectId, workId, target, first.ApiCalls.[0], second.ApiCalls.[0]

let private rejectsCondition (store: DsStore) projectId (condition: Condition) reason =
    // 거절이 원본을 정규화하거나 leaf를 삭제하는 복구가 되어서는 안 된다.
    let before = Ds2.Serialization.JsonConverter.serialize store
    let ex = Assert.Throws<NotSupportedException>(fun () -> text store projectId |> ignore)
    Assert.Contains("DS2TEXT_CONDITION_SHAPE", ex.Message)
    Assert.Contains(string condition.Id, ex.Message)
    Assert.Contains(reason, ex.Message)
    Assert.Equal(before, Ds2.Serialization.JsonConverter.serialize store)

[<Theory>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
let ``직접 leaf 와 자식이 섞인 조건은 어느 논리에서도 생략하지 않고 거절한다`` isOr inverted =
    let store, projectId, _, target, first, second = conditionCase ()
    let rootId = store.AddConditionWithApiCalls(target.Id, ConditionType.AutoAux, [ first.Id ])
    let root = target.Conditions |> Seq.exactlyOne
    root.IsOR <- isOr
    root.IsInverted <- inverted
    store.AddChildCondition(target.Id, rootId, false)
    let child = root.Children |> Seq.exactlyOne
    store.AddApiCallsToConditionBatch(target.Id, child.Id, [ second.Id ]) |> ignore
    rejectsCondition store projectId root "혼합"

[<Theory>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
let ``복수 직접 leaf 는 어느 논리에서도 true 로 바꾸지 않고 거절한다`` isOr inverted =
    let store, projectId, _, target, first, second = conditionCase ()
    store.AddConditionWithApiCalls(target.Id, ConditionType.SkipAction, [ first.Id; second.Id ]) |> ignore
    let root = target.Conditions |> Seq.exactlyOne
    root.IsOR <- isOr
    root.IsInverted <- inverted
    rejectsCondition store projectId root "여러 직접 ApiCall"

[<Theory>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
let ``빈 Work 또는 Call 조건과 빈 자식은 true 로 바꾸지 않고 거절한다`` workOwner nested =
    let store, projectId, workId, target, _, _ = conditionCase ()
    let root =
        if workOwner then
            store.AddWorkCondition(workId, ConditionType.SkipAction)
            store.Works.[workId].Conditions |> Seq.exactlyOne
        else
            store.AddCallCondition(target.Id, ConditionType.ComAux)
            target.Conditions |> Seq.exactlyOne
    let empty =
        if not nested then root
        else
            if workOwner then store.AddWorkChildCondition(workId, root.Id, true)
            else store.AddChildCondition(target.Id, root.Id, true)
            root.Children |> Seq.exactlyOne
    empty.IsInverted <- true
    rejectsCondition store projectId empty "빈 조건"

[<Theory>]
[<InlineData(4)>]
[<InlineData(999)>]
let ``조건에서 표기할 수 없는 접점은 무접점으로 바꾸지 않고 거절한다`` contactValue =
    let store, projectId, _, target, first, _ = conditionCase ()
    store.AddConditionWithApiCalls(target.Id, ConditionType.AutoAux, [ first.Id ]) |> ignore
    let leaf = (target.Conditions |> Seq.exactlyOne).ApiCalls |> Seq.exactlyOne
    leaf.ContactKind <- enum<ContactKind> contactValue
    let before = Ds2.Serialization.JsonConverter.serialize store
    let ex = Assert.Throws<NotSupportedException>(fun () -> text store projectId |> ignore)
    Assert.Contains("DS2TEXT_CONDITION_CONTACT", ex.Message)
    Assert.Equal(before, Ds2.Serialization.JsonConverter.serialize store)

[<Fact>]
let ``정규 AND OR NOT 조건은 입력 식별자와 순서를 보존하고 저장 왕복 뒤 같은 원문을 낸다`` () =
    for (kind, keyword) in [ ConditionType.AutoAux, "start"; ConditionType.ComAux, "permit"; ConditionType.SkipAction, "skip" ] do
        let store, projectId, _, target, first, second = conditionCase ()
        store.AddCallCondition(target.Id, kind)
        let root = target.Conditions |> Seq.exactlyOne
        store.AddChildCondition(target.Id, root.Id, false)
        let firstChild = root.Children.[0]
        store.AddApiCallsToConditionBatch(target.Id, firstChild.Id, [ first.Id ]) |> ignore
        store.AddChildCondition(target.Id, root.Id, true)
        let alternative = root.Children.[1]
        store.AddChildCondition(target.Id, alternative.Id, false)
        store.AddApiCallsToConditionBatch(target.Id, alternative.Children.[0].Id, [ second.Id ]) |> ignore
        store.AddChildCondition(target.Id, alternative.Id, false)
        let negation = alternative.Children.[1]
        negation.IsInverted <- true
        store.AddChildCondition(target.Id, negation.Id, false)
        // 같은 입력을 다시 읽는 leaf도 중복 제거하지 않는다.
        store.AddApiCallsToConditionBatch(target.Id, negation.Children.[0].Id, [ first.Id ]) |> ignore
        let expected = keyword + " when (GuardA.Safe from Cell.Process.Hold.GuardA.Safe as bool == true and (GuardB.Safe from Cell.Process.Hold.GuardB.Safe as bool == true or not GuardA.Safe from Cell.Process.Hold.GuardA.Safe as bool == true));"
        let before = Ds2.Serialization.JsonConverter.serialize store
        let source = text store projectId
        contains expected source
        notContains (keyword + " when true;") source
        Assert.Equal(before, Ds2.Serialization.JsonConverter.serialize store)
        let restored = Ds2.Serialization.JsonConverter.deserialize<DsStore> before
        Assert.Equal(source, text restored projectId)
        let restoredRoot = restored.Calls.[target.Id].Conditions |> Seq.exactlyOne
        let restoredFirst = restoredRoot.Children.[0].ApiCalls |> Seq.exactlyOne
        let restoredRepeated = restoredRoot.Children.[1].Children.[1].Children.[0].ApiCalls |> Seq.exactlyOne
        Assert.Equal(first.Id, restoredFirst.Id)
        Assert.Equal(first.Id, restoredRepeated.Id)
        Assert.False(Object.ReferenceEquals(restoredFirst, restoredRepeated))

let private conditionTargetCase () =
    let store, projectId, _, target, first, second = conditionCase ()
    store.AddConditionWithApiCalls(target.Id, ConditionType.AutoAux, [ first.Id ]) |> ignore
    let leaf = target.Conditions.[0].ApiCalls.[0]
    store, projectId, target, first, second, leaf

let private rejectsConditionTarget (store: DsStore) projectId (leaf: ApiCall) reason =
    let before = Ds2.Serialization.JsonConverter.serialize store
    let ex = Assert.Throws<NotSupportedException>(fun () -> text store projectId |> ignore)
    Assert.Contains("DS2TEXT_CONDITION_TARGET", ex.Message)
    Assert.Contains(string leaf.Id, ex.Message)
    Assert.Contains(reason, ex.Message)
    Assert.Equal(before, Ds2.Serialization.JsonConverter.serialize store)

[<Fact>]
let ``editor의 범위 장치 별칭을 명시 System 이름으로 오인하지 않고 거절한다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let workId = store.AddWork("Hold", flowId)
    let guard = addCall store projectId workId "Guard" "Safe"
    let target = addCall store projectId workId "Driver" "Torque"
    store.AddConditionWithApiCalls(target.Id, ConditionType.AutoAux, [ guard.ApiCalls.[0].Id ]) |> ignore
    let leaf = target.Conditions.[0].ApiCalls.[0]
    let api = store.ApiDefs.[leaf.ApiDefId.Value]
    Assert.Equal("Guard.Safe", leaf.Name)
    Assert.Equal("Process_Guard", store.Systems.[api.ParentId].Name)
    rejectsConditionTarget store projectId leaf "Name과 ApiDefId"

[<Theory>]
[<InlineData("_ON")>]
[<InlineData("_OFF")>]
[<InlineData("")>]
[<InlineData(null)>]
[<InlineData("GuardA")>]
[<InlineData(".Safe")>]
[<InlineData("GuardA.")>]
[<InlineData("GuardA.Safe.Extra")>]
let ``상수 또는 잘못된 조건 경로를 읽을 수 없는 API 비교식으로 쓰지 않는다`` name =
    let store, projectId, _, _, _, leaf = conditionTargetCase ()
    leaf.Id <- Guid.NewGuid()
    leaf.Name <- name
    leaf.ApiDefId <- None
    rejectsConditionTarget store projectId leaf "System.Api"

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``조건의 ApiDefId가 없거나 사라졌으면 이름으로 대상을 추측하지 않는다`` dangling =
    let store, projectId, _, _, _, leaf = conditionTargetCase ()
    leaf.Id <- Guid.NewGuid()
    leaf.ApiDefId <- if dangling then Some(Guid.NewGuid()) else None
    rejectsConditionTarget store projectId leaf "ApiDefId"

[<Fact>]
let ``독립 조건의 이름과 실제 API 식별자가 다르면 다른 API로 변환하지 않는다`` () =
    let store, projectId, target, first, _, leaf = conditionTargetCase ()
    leaf.Id <- Guid.NewGuid()
    leaf.Name <- target.ApiCalls.[0].Name
    Assert.Equal(first.ApiDefId, leaf.ApiDefId)
    Assert.NotEqual(target.ApiCalls.[0].ApiDefId, leaf.ApiDefId)
    rejectsConditionTarget store projectId leaf "Name과 ApiDefId"

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``실제 API 또는 System 이름이 바뀌면 오래된 조건 경로를 그대로 쓰지 않는다`` systemName =
    let store, projectId, _, _, _, leaf = conditionTargetCase ()
    leaf.Id <- Guid.NewGuid()
    let api = store.ApiDefs.[leaf.ApiDefId.Value]
    if systemName then store.Systems.[api.ParentId].Name <- "RenamedSystem"
    else api.Name <- "RenamedApi"
    rejectsConditionTarget store projectId leaf "Name과 ApiDefId"

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``조건 API의 System이 없거나 출력 프로젝트에서 제외되면 거절한다`` absent =
    let store, projectId, _, _, _, leaf = conditionTargetCase ()
    leaf.Id <- Guid.NewGuid()
    let api = store.ApiDefs.[leaf.ApiDefId.Value]
    if absent then api.ParentId <- Guid.NewGuid()
    else store.Projects.[projectId].PassiveSystemIds.Remove(api.ParentId) |> ignore
    rejectsConditionTarget store projectId leaf "System이 출력 프로젝트"

[<Fact>]
let ``from 입력 식별자를 공유해도 서로 다른 API 대상을 함께 쓰지 않는다`` () =
    let store, projectId, _, first, second, leaf = conditionTargetCase ()
    leaf.Name <- second.Name
    leaf.ApiDefId <- second.ApiDefId
    Assert.Equal(first.Id, leaf.Id)
    rejectsConditionTarget store projectId leaf "from 입력을 공유"

[<Fact>]
let ``from 소유자의 이름과 API 식별자 불일치도 조건에서 검증한다`` () =
    let store, projectId, _, first, second, leaf = conditionTargetCase ()
    first.Name <- second.Name
    rejectsConditionTarget store projectId leaf "Name과 ApiDefId"

[<Fact>]
let ``여러 Call이 같은 입력 식별자를 소유하면 마지막 from 경로를 고르지 않는다`` () =
    let store, projectId, _, first, second, leaf = conditionTargetCase ()
    second.Id <- first.Id
    rejectsConditionTarget store projectId leaf "여러 Call"

[<Fact>]
let ``정상 독립 leaf와 같은 대상을 공유한 from leaf는 각각 원래 입력 사양을 쓴다`` () =
    let store, projectId, _, first, _, leaf = conditionTargetCase ()
    // 조건의 기대값은 공유 원본과 달라도 된다. 공유하는 것은 입력 id와 API 대상이다.
    leaf.InputSpec <- ValueSpec.singleBool false
    Assert.Equal(ValueSpec.singleBool true, first.InputSpec)
    contains "start when GuardA.Safe from Cell.Process.Hold.GuardA.Safe as bool == false;" (text store projectId)
    leaf.Id <- Guid.NewGuid()
    contains "start when GuardA.Safe == false;" (text store projectId)

[<Fact>]
let ``API 와 운전 설정은 command·observe 와 기본값 생략으로 적힌다`` () =
    let store, projectId, systemId, _ = skeleton "Line" "Jig" "Motion"
    // ApiDef 는 장치 Call 을 만들 때 생기므로, Call 을 통해 생긴 ApiDef 로 기본값 생략을 확인한다.
    let holdFlow = store.AddFlow("Hold", systemId)
    let hold = store.AddWork("Hold", holdFlow)
    addCall store projectId hold "Jig" "Close" |> ignore
    let src = text store projectId
    contains "api Close {" src
    contains "command " src
    contains "observe " src
    // 저장 기본값 Normal None 은 Text 기본값(virtual) 과 다르므로 반드시 적혀야 한다.
    contains "action normal;" src
    // sensing 기본값 Normal None 은 Text 기본값과 같으므로 생략된다.
    notContains "sensing normal;" src

// ─────────────────────────────────────────────────────────── 이름 인용

[<Fact>]
let ``예약어와 특수문자 이름은 JSON 문자열로 감싼다`` () =
    Assert.Equal("Load", Ds2TextLexeme.quote "Load")
    Assert.Equal("투입", Ds2TextLexeme.quote "투입")
    Assert.Equal("_x1", Ds2TextLexeme.quote "_x1")
    Assert.Equal("\"work\"", Ds2TextLexeme.quote "work")
    Assert.Equal("\"a b\"", Ds2TextLexeme.quote "a b")
    Assert.Equal("\"1st\"", Ds2TextLexeme.quote "1st")
    Assert.Equal("\"a.b\"", Ds2TextLexeme.quote "a.b")

[<Fact>]
let ``product 는 TokenSpec 에서 나온다`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let load = store.AddWork("Load", flowId)
    store.Works.[load].TokenRole <- TokenRole.Source
    store.Projects.[projectId].TokenSpecs.Add
        { Id = 4; Label = "Sedan"; Fields = Map.ofList [ "variant", "A" ]; WorkId = Some load }
    let src = text store projectId
    contains "product 4 \"Sedan\" from Cell.Process.Load {" src
    contains "variant = \"A\";" src

[<Fact>]
let ``A call is written as device.api with no handle`` () =
    // `Call.TextName` 을 지웠다. 손잡이를 둘 이유가 **한 Work 안에 같은 `장비.기능` 이
    // 둘일 때 구분하려는 것** 하나였는데, 그 둘은 Call 레퍼런스로만 만들어졌고
    // 레퍼런스를 글에서 없애면서 겹칠 일 자체가 사라졌다.
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let call = addCall store projectId w "Jig" "Close"
    Assert.Equal("Jig.Close", call.Name)
    let src = text store projectId
    contains "Jig.Close {" src
    // 선언문이 남아 있으면 안 된다 — 옛 꼴이 조용히 계속 쓰인다
    Assert.DoesNotContain("call ", src)

[<Fact>]
let ``A call keeps its own block line because bindings cannot live in a relation`` () =
    // 관계문이 이름을 이미 적으니 `Jig.Close;` 한 줄은 군더더기로 보인다.
    // 그런데 **binding 은 관계문이 대신 말해 줄 수 없다** — 생략된 사양은 UndefinedValue 이고
    // 그것은 기본값(주소 없는 bool true)과 **다른 값**이다. 그래서 줄은 늘 블록으로 남는다.
    // (군더더기를 줄이는 것은 binding 이 없는 **생성기**의 몫이다.)
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let a = addCall store projectId w "Jig" "Close"
    let b = addCall store projectId w "Gate" "Open"
    store.ConnectSelectionInOrder([ a.Id; b.Id ], ArrowType.Start) |> ignore
    let src = text store projectId
    contains "Jig.Close > Gate.Open;" src
    contains "Jig.Close {" src
    contains "input undefined;" src

[<Fact>]
let ``Two devices in one Work each keep their own line`` () =
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    addCall store projectId w "Jig" "Close" |> ignore
    addCall store projectId w "Robot" "Move" |> ignore
    let src = text store projectId
    contains "Jig.Close {" src
    contains "Robot.Move {" src

[<Fact>]
let ``A Call reference cannot be written as text`` () =
    // `ref` 는 **Work 전용**이다(뜻이 «또는»). Call 레퍼런스는 글에서 폐지했다.
    // 조용히 흘리면 「적히지 않은 Call」이 생겨 왕복이 거짓이 된다 — 큰 소리로 멈춘다.
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let call = addCall store projectId w "Jig" "Close"
    store.AddReferenceCall call.Id |> ignore
    let error = Assert.Throws<Exception>(fun () -> text store projectId |> ignore)
    contains "Call 레퍼런스" error.Message

[<Fact>]
let ``A multi-target Call cannot be written as text`` () =
    // 함께 부르는 것은 **묶음**으로 적는다. 표기가 둘일 이유가 없다.
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let w = store.AddWork("Hold", flowId)
    let call = addCall store projectId w "Jig" "Close"
    let other = addCall store projectId w "Robot" "Move"
    for a in other.ApiCalls do call.ApiCalls.Add a
    let error = Assert.Throws<Exception>(fun () -> text store projectId |> ignore)
    contains "묶음으로" error.Message

[<Fact>]
let ``Declaration order follows causality, not insertion or name`` () =
    // `DsChild.Ordinal` 을 지웠다. 저장소가 들고 있던 「적은 차례」는
    // **간선이 이미 들고 있다** — 사슬이면 위상 차례가 원문 차례를 되살린다.
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let z = store.AddWork("Z", flowId)
    let a = store.AddWork("A", flowId)
    let m = store.AddWork("M", flowId)
    // Z > A > M — 이름 차례(A,M,Z)와도 넣은 차례(Z,A,M)와도 다른 답을 요구한다
    store.ConnectSelectionInOrder([ z; a ], ArrowType.Start) |> ignore
    store.ConnectSelectionInOrder([ a; m ], ArrowType.Start) |> ignore
    contains "work Z, A, M;" (text store projectId)
    // 글로 적는 일이 저장소를 건드리면 안 된다
    Assert.Equal(z, store.Works.Keys |> Seq.head)

[<Fact>]
let ``Declaration order falls back to names when there is no causality`` () =
    // 인과가 없으면 되살릴 원문 차례가 **없다** — 그때는 이름 차례로 떨어진다.
    // 결정적이어야 diff 가 뜻을 가진다.
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    store.AddWork("Z", flowId) |> ignore
    store.AddWork("A", flowId) |> ignore
    contains "work A, Z;" (text store projectId)

[<Fact>]
let ``Declaration order stays deterministic when causality has a cycle`` () =
    // 고리가 있으면 위상이 성립하지 않는다. 멈추거나 들쭉날쭉하면 안 된다.
    let store, projectId, _, flowId = skeleton "Line" "Cell" "Process"
    let z = store.AddWork("Z", flowId)
    let a = store.AddWork("A", flowId)
    store.ConnectSelectionInOrder([ z; a ], ArrowType.Start) |> ignore
    store.ConnectSelectionInOrder([ a; z ], ArrowType.Start) |> ignore
    let once = text store projectId
    contains "work A, Z;" once
    Assert.Equal(once, text store projectId)