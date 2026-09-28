module Ds2.Store.Editor.Tests.AiCsvTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.CSV

// ds2-csv-for-ai/v1 — 7열 형식.
// 이 형식의 목표는 «불러오면 손댈 것이 없다» 이다. 그래서 골든 테스트의 본체는
// 파싱 성공이 아니라 **그래프 검증 경고가 0건** 이라는 사실이다.

/// 예제 모델 — docs/sample_jigcell.csv 와 같은 내용.
/// 파일을 읽지 않고 인라인으로 둔다: 테스트가 절대 경로에 매이면 다른 기계·CI 에서 깨진다.
let private sampleCsv =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# csvForAI/v1 — 지그셀: 한 제품을 고정하고 공정한 뒤 해제한다 (tutorials_Ver71/11_spec_modeling.md 모델 1)"
        "# Capa 1 = FLOW 행 1개. 행 순서에는 의미가 없다 — 모든 관계는 ARROW 행과 Detail 셀에 적혀 있다."
        "SYS,지그셀,Active,,,,"
        "SYS,실린더,Passive,Cylinder_2Pos,,,"
        "SYS,공정기,Passive,,,,"
        "FLOW,제품,,,,,"
        "WORK,제품.투입,Source,,,,"
        "WORK,제품.클램프제어,,실린더.ADV>실린더.RET,,,"
        "WORK,제품.공정제어,Ignore,공정기.PREPARE>공정기.RUN,,,"
        "WORK,제품.배출,Sink,,,,"
        "ARROW,제품.투입,Start,제품.클램프제어,,,"
        "ARROW,제품.클램프제어,Group,제품.공정제어,,,"
        "ARROW,제품.클램프제어,Start,제품.배출,,,"
        "ARROW,제품.배출,Reset,제품.투입;제품.클램프제어;제품.공정제어,,,"
        "ARROW,제품.투입,Reset,제품.배출,,,"
        "API,실린더.ADV,Latch/Normal,,100MS,%IX0.0.0,%QX0.0.0"
        "API,실린더.RET,Virtual/Normal,,100MS,%IX0.0.1,"
        "API,공정기.PREPARE,Virtual/Virtual(200),,?,,"
        "API,공정기.RUN,Normal/Normal,,150MS,%IX0.0.2,%QX0.0.2"
        "COND,제품.공정제어.공정기.RUN,AutoAux,실린더.ADV,,,"
        "COND,제품.클램프제어.실린더.RET,AutoAux,공정기.RUN,,,"
    ]

let private parseOk (csv: string) =
    match CsvImporter.parseAiContent csv with
    | Error es -> failwith (String.concat "\n" es)
    | Ok doc -> doc

let private loadOk (csv: string) =
    match CsvImporter.loadAiProject (parseOk csv) "P" with
    | Error es -> failwith (String.concat "\n" es)
    | Ok store -> store

let private parseErrors (csv: string) =
    match CsvImporter.parseAiContent csv with
    | Error es -> es
    | Ok _ -> []

let private hasCode (code: string) (errors: string list) =
    errors |> List.exists (fun e -> e.Contains code)

// ---------- 파싱 ----------

[<Fact>]
let ``예제 CSV 는 파싱된다`` () =
    let doc = parseOk sampleCsv
    let p = CsvImporter.previewAi doc
    Assert.Equal("지그셀", p.ActiveSystemName)
    Assert.Equal(1, p.Capa)          // FLOW 행 개수가 곧 Capa
    Assert.Equal(4, p.WorkCount)
    Assert.Equal(4, p.ApiCount)
    Assert.Equal(2, p.CondCount)

[<Fact>]
let ``헤더가 다르면 AI001`` () =
    Assert.True(hasCode "AI001" (parseErrors "Kind,Name\nSYS,A,Active"))

[<Fact>]
let ``Call 을 가진 WORK 에 Time 을 적으면 AI021`` () =
    // 계약: 동작 시간은 Call 없는 Work 에만. 구조로 막는다.
    let csv =
        "Kind,Name,Type,Detail,Time,InTag,OutTag\n\
         SYS,S,Active,,,,\n\
         FLOW,F,,,,,\n\
         API,D.A,Normal/Normal,,100MS,%IX0.0.0,%QX0.0.0\n\
         WORK,F.W,,D.A,500MS,,"
    Assert.True(hasCode "AI021" (parseErrors csv))

[<Fact>]
let ``Call 레벨 ARROW 에 StartReset 을 적으면 AI008`` () =
    let csv =
        "Kind,Name,Type,Detail,Time,InTag,OutTag\n\
         SYS,S,Active,,,,\n\
         FLOW,F,,,,,\n\
         API,D.A,Normal/Normal,,100MS,,\n\
         API,D.B,Normal/Normal,,100MS,,\n\
         WORK,F.W,,D.A>D.B,,,\n\
         ARROW,F.W.D.A,StartReset,F.W.D.B,,,"
    Assert.True(hasCode "AI008" (parseErrors csv))

[<Fact>]
let ``같은 괄호에서 and 와 or 를 섞으면 AI052`` () =
    // 우선순위를 몰래 적용하면 사용자가 쓴 식과 저장된 트리가 달라진다.
    let csv =
        "Kind,Name,Type,Detail,Time,InTag,OutTag\n\
         SYS,S,Active,,,,\n\
         FLOW,F,,,,,\n\
         API,D.A,Normal/Normal,,100MS,,\n\
         API,D.B,Normal/Normal,,100MS,,\n\
         API,D.C,Normal/Normal,,100MS,,\n\
         WORK,F.W,,D.A,,,\n\
         COND,F.W.D.A,SkipAction,D.A & D.B | D.C,,,"
    Assert.True(hasCode "AI052" (parseErrors csv))

[<Fact>]
let ``Work 조건에 AutoAux 를 적으면 AI014`` () =
    // 런타임이 무시하는 것을 조용히 저장하면 «적었는데 아무 일도 없는» 침묵이 남는다.
    let csv =
        "Kind,Name,Type,Detail,Time,InTag,OutTag\n\
         SYS,S,Active,,,,\n\
         FLOW,F,,,,,\n\
         API,D.A,Normal/Normal,,100MS,,\n\
         WORK,F.W,,D.A,,,\n\
         COND,F.W,AutoAux,D.A,,,"
    Assert.True(hasCode "AI014" (parseErrors csv))

[<Fact>]
let ``API 행이 없는 Call 을 쓰면 AI065`` () =
    let csv =
        "Kind,Name,Type,Detail,Time,InTag,OutTag\n\
         SYS,S,Active,,,,\n\
         FLOW,F,,,,,\n\
         WORK,F.W,,없는장치.액션,,,"
    Assert.True(hasCode "AI065" (parseErrors csv))

// ---------- 불러오기 ----------

[<Fact>]
let ``불러오면 TokenRole 과 TokenSpec 이 붙는다`` () =
    let store = loadOk sampleCsv
    let sources =
        store.Works.Values |> Seq.filter (fun w -> w.TokenRole.HasFlag TokenRole.Source) |> Seq.toList
    Assert.NotEmpty(sources)
    let project = store.Projects.Values |> Seq.head
    let linked = project.TokenSpecs |> Seq.choose (fun s -> s.WorkId) |> Set.ofSeq
    for s in sources do Assert.True(linked.Contains s.Id, $"TokenSpec 없음: {s.Name}")

[<Fact>]
let ``ApiDef 의 Action 과 Sensing 이 CSV 대로 들어간다`` () =
    let store = loadOk sampleCsv
    let apiDef =
        store.ApiDefs.Values |> Seq.find (fun a -> a.Name = "ADV")
    Assert.Equal(ActionType.Latch, apiDef.ActionType)

[<Fact>]
let ``조건이 Call 에 붙는다`` () =
    let store = loadOk sampleCsv
    let withCond =
        store.Calls.Values |> Seq.filter (fun c -> c.Conditions.Count > 0) |> Seq.toList
    Assert.Equal(2, List.length withCond)
    // 기대값을 적지 않은 leaf 는 «그 신호가 켜졌는가» 를 뜻하는 true 로 채워져야 한다.
    for c in withCond do
        for cond in c.Conditions do
            for leaf in cond.ApiCalls do
                Assert.Equal(ValueSpec.BoolValue(Single true), leaf.InputSpec)

// ---------- 골든: 불러오면 손댈 것이 없다 ----------

[<Fact>]
let ``불러온 모델은 그래프 검증 경고가 없다`` () =
    let store = loadOk sampleCsv
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let names (xs: (System.Guid * string * string) list) =
        xs |> List.map (fun (_, s, w) -> $"{s}.{w}") |> String.concat ", "
    // Device Work 는 Token Source 로 지정할 수 없으므로 UI 와 같은 기준으로 Control 만 본다.
    let controlSourceCandidates =
        Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index
        |> List.filter (fun (_, s, _) -> index.ActiveSystemNames.Contains s)
    Assert.True(List.isEmpty controlSourceCandidates,
                $"Source 후보: {names controlSourceCandidates}")
    let deadlocks = Ds2.Runtime.Engine.Core.GraphValidator.findDeadlockCandidates index
    Assert.True(List.isEmpty deadlocks, $"데드락 위험: {names deadlocks}")
    let badSources = Ds2.Runtime.Engine.Core.GraphValidator.findSourcesWithPredecessors index
    Assert.True(List.isEmpty badSources, $"Source 선행 있음: {names badSources}")

// ---------- 형식 자동 인식 ----------

[<Fact>]
let ``7열 헤더는 AiModel 로 판별된다`` () =
    let header = CsvFormatDetector.detect sampleCsv
    Assert.True(header.IsRecognized)
    Assert.Equal(CsvFormat.AiModel, header.Format)

[<Fact>]
let ``판별된 규격은 실제 파서가 받아들인다`` () =
    // 판별과 파싱이 서로 다른 전처리를 쓰면 «배지는 초록인데 불러오기는 실패» 가 된다.
    // 이 왕복이 그 계약을 지키는 마지막 방어선이다.
    let header = CsvFormatDetector.detect sampleCsv
    Assert.Equal(CsvFormat.AiModel, header.Format)
    match CsvImporter.parseAiContent sampleCsv with
    | Error es -> failwith (String.concat "\n" es)
    | Ok _ -> ()

[<Fact>]
let ``기존 3열 헤더는 여전히 Basic3 로 판별된다`` () =
    // 신규 형식을 얹으면서 기존 판별이 흔들리면 안 된다.
    let header = CsvFormatDetector.detect "FLOW,WORK,CALL\n투입,작업A,A.ADV>A.RET"
    Assert.Equal(CsvFormat.Basic3, header.Format)
