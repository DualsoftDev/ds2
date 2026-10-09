module Ds2.CSV.Tests.AiCsvTests

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


// ---------- 차체라인 회귀 (지침 CSV_FOR_AI_BODYLINE.md 산출물) ----------
// 지침대로 만든 CSV 가 실제로 경고 없이 서는지 회차마다 고정한다.

let private bodylineR1 =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# 차체라인 SIDE OTR 지그셀 — 1회차 생성. 지침 CSV_FOR_AI_BODYLINE.md 로 작성."
        "# Capa 1 = 한 번에 차체 1대. 스테이션은 WORK 로 나누고 ARROW 로 잇는다."
        "SYS,SIDE_OTR_지그셀,Active,,,,"
        "SYS,KA4_1차클램프,Passive,,,,"
        "SYS,KA4_2차클램프,Passive,,,,"
        "SYS,KA4_핀,Passive,,,,"
        "SYS,용접건1,Passive,,,,"
        "SYS,인덱스,Passive,,,,"
        "SYS,투입센서,Passive,,,,"
        "SYS,1호기RT,Passive,,,,"
        "FLOW,차체,,,,,"
        "WORK,차체.투입,Source,,,,"
        "WORK,차체.차종확인,,투입센서.PARTON,,,"
        "WORK,차체.위치결정,,KA4_핀.ADV>KA4_1차클램프.ADV>KA4_2차클램프.ADV,,,"
        "WORK,차체.용접,Ignore,용접건1.타점,,,"
        "WORK,차체.언락,,인덱스.언락,,,"
        "WORK,차체.회전,,인덱스.0_KA4_TURN,,,"
        "WORK,차체.해제,,KA4_2차클램프.RET>KA4_1차클램프.RET>KA4_핀.RET,,,"
        "WORK,차체.취출,,1호기RT.취출,,,"
        "WORK,차체.배출,Sink,,,,"
        "ARROW,차체.투입,Start,차체.차종확인,,,"
        "ARROW,차체.차종확인,Start,차체.위치결정,,,"
        "ARROW,차체.위치결정,Group,차체.용접,,,"
        "ARROW,차체.위치결정,Start,차체.언락,,,"
        "ARROW,차체.언락,Start,차체.회전,,,"
        "ARROW,차체.회전,Start,차체.해제,,,"
        "ARROW,차체.해제,Start,차체.취출,,,"
        "ARROW,차체.취출,Start,차체.배출,,,"
        "ARROW,차체.배출,Reset,차체.투입;차체.차종확인;차체.위치결정;차체.용접;차체.언락;차체.회전;차체.해제;차체.취출,,,"
        "ARROW,차체.투입,Reset,차체.배출,,,"
        "API,투입센서.PARTON,Virtual/Latch(30),,100MS,%IX3.10.0.01,"
        "API,KA4_핀.ADV,Latch/Normal,,800MS,%IX3.30.0.01,%QX3.31.0.01"
        "API,KA4_핀.RET,Virtual/Normal,,800MS,%IX3.30.0.02,"
        "API,KA4_1차클램프.ADV,Latch/Normal,,1S,%IX3.30.0.03,%QX3.31.0.02"
        "API,KA4_1차클램프.RET,Virtual/Normal,,1S,%IX3.30.0.04,"
        "API,KA4_2차클램프.ADV,Latch/Normal,,1S,%IX3.30.0.05,%QX3.31.0.03"
        "API,KA4_2차클램프.RET,Virtual/Normal,,1S,%IX3.30.0.06,"
        "API,용접건1.타점,Normal/Normal,,12S,%IX3.40.0.01,%QX3.41.0.01"
        "API,인덱스.언락,Latch/Normal,,500MS,%IX3.50.0.01,%QX3.51.0.01"
        "API,인덱스.0_KA4_TURN,Latch/Normal,,3S,%IX3.50.0.02,%QX3.51.0.02"
        "API,1호기RT.취출,Normal/Normal,,4S,%IX4.01.0.12,%QX4.01.4.12"
        "COND,차체.용접.용접건1.타점,AutoAux,KA4_1차클램프.ADV & KA4_2차클램프.ADV,,,"
        "COND,차체.회전.인덱스.0_KA4_TURN,AutoAux,인덱스.언락,,,"
    ]

[<Fact>]
let ``차체라인 1회차 산출물은 경고 없이 적재된다`` () =
    let doc =
        match CsvImporter.parseAiContent bodylineR1 with
        | Error es -> failwith (String.concat "\n" es)
        | Ok d -> d
    let store =
        match CsvImporter.loadAiProject doc "BodyLine" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok s -> s
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let names (xs: (System.Guid * string * string) list) =
        xs |> List.map (fun (_, s, w) -> $"{s}.{w}") |> String.concat ", "
    let ctrl =
        Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index
        |> List.filter (fun (_, s, _) -> index.ActiveSystemNames.Contains s)
    Assert.True(List.isEmpty ctrl, $"Source 후보: {names ctrl}")
    let unreset = Ds2.Runtime.Engine.Core.GraphValidator.findUnresetWorks index
    Assert.True(List.isEmpty unreset, $"Reset 누락: {names unreset}")
    let dead = Ds2.Runtime.Engine.Core.GraphValidator.findDeadlockCandidates index
    Assert.True(List.isEmpty dead, $"데드락: {names dead}")


let private bodylineR2 =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# 차체라인 SIDE OTR COMPL LINE — 2회차. 조립작업서 '3-장비&작업자' 실측 구조."
        "# 232 안티스패터 → 233 키#1 → 234 키#2 → 235 키#3 → 236 증타 → 237 체크 → 238 버퍼"
        "# Capa 3 = 라인에 차체 3대가 동시에 흐른다(파이프라인). 스테이션 7개와 다른 수다."
        "SYS,SIDE_OTR_COMPL,Active,,,,"
        "SYS,안티스패터,Passive,,,,"
        "SYS,HDLG1,Passive,,,,"
        "SYS,SPOT1,Passive,,,,"
        "SYS,PED1,Passive,,,,"
        "SYS,SPOT2,Passive,,,,"
        "SYS,PED2,Passive,,,,"
        "SYS,체크로봇,Passive,,,,"
        "SYS,버퍼,Passive,,,,"
        "SYS,투입센서,Passive,,,,"
        "FLOW,차체A,,,,,"
        "FLOW,차체B,,,,,"
        "FLOW,차체C,,,,,"
        "WORK,차체A.투입,Source,,,,"
        "WORK,차체A.안티스패터,,투입센서.PARTON>안티스패터.도포,,,"
        "WORK,차체A.키1용접,,HDLG1.반입>SPOT1.타점>HDLG1.반출,,,"
        "WORK,차체A.배출,Sink,,,,"
        "ARROW,차체A.투입,Start,차체A.안티스패터,,,"
        "ARROW,차체A.안티스패터,StartReset,차체A.키1용접,,,"
        "ARROW,차체A.키1용접,Start,차체A.배출,,,"
        "ARROW,차체A.배출,Reset,차체A.투입;차체A.안티스패터;차체A.키1용접,,,"
        "ARROW,차체A.투입,Reset,차체A.배출,,,"
        "WORK,차체B.투입,Source,,,,"
        "WORK,차체B.키2용접,,SPOT2.타점>PED1.타점,,,"
        "WORK,차체B.키3용접,,PED2.타점,,,"
        "WORK,차체B.배출,Sink,,,,"
        "ARROW,차체B.투입,Start,차체B.키2용접,,,"
        "ARROW,차체B.키2용접,StartReset,차체B.키3용접,,,"
        "ARROW,차체B.키3용접,Start,차체B.배출,,,"
        "ARROW,차체B.배출,Reset,차체B.투입;차체B.키2용접;차체B.키3용접,,,"
        "ARROW,차체B.투입,Reset,차체B.배출,,,"
        "WORK,차체C.투입,Source,,,,"
        "WORK,차체C.체크,,체크로봇.검사,,,"
        "WORK,차체C.버퍼,,버퍼.적재,,,"
        "WORK,차체C.배출,Sink,,,,"
        "ARROW,차체C.투입,Start,차체C.체크,,,"
        "ARROW,차체C.체크,StartReset,차체C.버퍼,,,"
        "ARROW,차체C.버퍼,Start,차체C.배출,,,"
        "ARROW,차체C.배출,Reset,차체C.투입;차체C.체크;차체C.버퍼,,,"
        "ARROW,차체C.투입,Reset,차체C.배출,,,"
        "API,투입센서.PARTON,Virtual/Latch(30),,100MS,%IX3.10.0.01,"
        "API,안티스패터.도포,Normal/Normal,,8S,%IX3.11.0.01,%QX3.12.0.01"
        "API,HDLG1.반입,Normal/Normal,,4S,%IX3.20.0.01,%QX3.21.0.01"
        "API,HDLG1.반출,Normal/Normal,,4S,%IX3.20.0.02,%QX3.21.0.02"
        "API,SPOT1.타점,Normal/Normal,,14S,%IX3.30.0.01,%QX3.31.0.01"
        "API,SPOT2.타점,Normal/Normal,,18S,%IX3.30.0.02,%QX3.31.0.02"
        "API,PED1.타점,Normal/Normal,,12S,%IX3.40.0.01,%QX3.41.0.01"
        "API,PED2.타점,Normal/Normal,,10S,%IX3.40.0.02,%QX3.41.0.02"
        "API,체크로봇.검사,Normal/Normal,,6S,%IX3.50.0.01,%QX3.51.0.01"
        "API,버퍼.적재,Normal/Normal,,5S,%IX3.60.0.01,%QX3.61.0.01"
        "COND,차체A.키1용접.SPOT1.타점,AutoAux,HDLG1.반입,,,"
    ]

[<Fact>]
let ``차체라인 2회차 — Capa 3 파이프라인도 경고가 없다`` () =
    // 차체라인은 여러 대가 동시에 흐르는 파이프라인이다. Flow 가 여럿일 때도
    // 각 Flow 가 제 Source/Sink/재무장을 갖추면 경고가 없어야 한다.
    let doc =
        match CsvImporter.parseAiContent bodylineR2 with
        | Error es -> failwith (String.concat "\n" es)
        | Ok d -> d
    Assert.Equal(3, (CsvImporter.previewAi doc).Capa)
    let store =
        match CsvImporter.loadAiProject doc "SideOtr" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok s -> s
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let names (xs: (System.Guid * string * string) list) =
        xs |> List.map (fun (_, s, w) -> $"{s}.{w}") |> String.concat ", "
    let ctrl =
        Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index
        |> List.filter (fun (_, s, _) -> index.ActiveSystemNames.Contains s)
    Assert.True(List.isEmpty ctrl, $"Source 후보: {names ctrl}")
    let unreset = Ds2.Runtime.Engine.Core.GraphValidator.findUnresetWorks index
    Assert.True(List.isEmpty unreset, $"Reset 누락: {names unreset}")
    let dead = Ds2.Runtime.Engine.Core.GraphValidator.findDeadlockCandidates index
    Assert.True(List.isEmpty dead, $"데드락: {names dead}")
    let bad = Ds2.Runtime.Engine.Core.GraphValidator.findSourcesWithPredecessors index
    Assert.True(List.isEmpty bad, $"Source 선행: {names bad}")


let private bodylineR3 =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# 233/43 S/OTR 키#1 용접 · S/EXTN 정렬 — 3회차. 작업서 '5. 233_43_도어유' 타임차트 실측 그대로."
        "# Main 체인이 택트를 정한다: 취출8 → QR8 → 정렬6 → 로딩8 → 실링26 → 언로딩8 → 클램프3 → 용접36 → 언클램프3 = 106s"
        "# Sub(로보트1 언로딩·원위치)는 Main 과 병행하므로 Group 으로 묶는다."
        "SYS,ST233_키1용접,Active,,,,"
        "SYS,로보트1,Passive,,,,"
        "SYS,로보트2,Passive,,,,"
        "SYS,로보트3,Passive,,,,"
        "SYS,치구_SOTR,Passive,,,,"
        "SYS,치구_SEXTN,Passive,,,,"
        "SYS,QR리더기,Passive,,,,"
        "SYS,실러,Passive,,,,"
        "FLOW,차체,,,,,"
        "WORK,차체.투입,Source,,,,"
        "WORK,차체.전공정언로딩,Ignore,로보트1.언로딩>로보트1.원위치,,,"
        "WORK,차체.전공정클램프,,치구_SOTR.클램프,,,"
        "WORK,차체.부품취출,,로보트2.취출,,,"
        "WORK,차체.QR인식,,QR리더기.판독,,,"
        "WORK,차체.부품정렬,,로보트2.정렬,,,"
        "WORK,차체.부품로딩,,로보트2.로딩,,,"
        "WORK,차체.실링,,실러.구조용도포,,,"
        "WORK,차체.부품언로딩,,로보트2.언로딩>로보트2.원위치,,,"
        "WORK,차체.지그클램프,,치구_SEXTN.클램프,,,"
        "WORK,차체.용접,,로보트3.용접>로보트3.원위치,,,"
        "WORK,차체.지그언클램프,,치구_SEXTN.언클램프,,,"
        "WORK,차체.배출,Sink,,,,"
        "ARROW,차체.투입,Start,차체.부품취출,,,"
        "ARROW,차체.전공정클램프,Group,차체.전공정언로딩,,,"
        "ARROW,차체.투입,Start,차체.전공정클램프,,,"
        "ARROW,차체.부품취출,Start,차체.QR인식,,,"
        "ARROW,차체.QR인식,Start,차체.부품정렬,,,"
        "ARROW,차체.부품정렬,Start,차체.부품로딩,,,"
        "ARROW,차체.부품로딩,Start,차체.실링,,,"
        "ARROW,차체.실링,Start,차체.부품언로딩,,,"
        "ARROW,차체.부품언로딩,Start,차체.지그클램프,,,"
        "ARROW,차체.지그클램프,Start,차체.용접,,,"
        "ARROW,차체.용접,Start,차체.지그언클램프,,,"
        "ARROW,차체.지그언클램프,Start,차체.배출,,,"
        "ARROW,차체.배출,Reset,차체.투입;차체.전공정언로딩;차체.전공정클램프;차체.부품취출;차체.QR인식;차체.부품정렬;차체.부품로딩;차체.실링;차체.부품언로딩;차체.지그클램프;차체.용접;차체.지그언클램프,,,"
        "ARROW,차체.투입,Reset,차체.배출,,,"
        "API,로보트1.언로딩,Normal/Normal,,8S,%IX3.10.0.01,%QX3.11.0.01"
        "API,로보트1.원위치,Normal/Normal,,5S,%IX3.10.0.02,%QX3.11.0.02"
        "API,치구_SOTR.클램프,Latch/Normal,,3S,%IX3.20.0.01,%QX3.21.0.01"
        "API,로보트2.취출,Normal/Normal,,8S,%IX3.30.0.01,%QX3.31.0.01"
        "API,QR리더기.판독,Virtual/Latch(30),,8S,%IX3.40.0.01,"
        "API,로보트2.정렬,Normal/Normal,,6S,%IX3.30.0.02,%QX3.31.0.02"
        "API,로보트2.로딩,Normal/Normal,,8S,%IX3.30.0.03,%QX3.31.0.03"
        "API,실러.구조용도포,Normal/Normal,,26S,%IX3.50.0.01,%QX3.51.0.01"
        "API,로보트2.언로딩,Normal/Normal,,8S,%IX3.30.0.04,%QX3.31.0.04"
        "API,로보트2.원위치,Normal/Normal,,5S,%IX3.30.0.05,%QX3.31.0.05"
        "API,치구_SEXTN.클램프,Latch/Normal,,3S,%IX3.60.0.01,%QX3.61.0.01"
        "API,로보트3.용접,Normal/Normal,,36S,%IX3.70.0.01,%QX3.71.0.01"
        "API,로보트3.원위치,Normal/Normal,,5S,%IX3.70.0.02,%QX3.71.0.02"
        "API,치구_SEXTN.언클램프,Virtual/Normal,,3S,%IX3.60.0.02,"
        "COND,차체.용접.로보트3.용접,AutoAux,치구_SEXTN.클램프,,,"
        "COND,차체.실링.실러.구조용도포,AutoAux,로보트2.로딩,,,"
    ]

[<Fact>]
let ``차체라인 3회차 — 작업서 타임차트 실측 공정도 경고가 없다`` () =
    // 233/43 S/OTR 키#1 용접. 작업서 '5. 233_43_도어유' 의 시작·시간·누계를 그대로 옮긴 것.
    let doc =
        match CsvImporter.parseAiContent bodylineR3 with
        | Error es -> failwith (String.concat "\n" es)
        | Ok d -> d
    let store =
        match CsvImporter.loadAiProject doc "ST233" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok s -> s
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let names (xs: (System.Guid * string * string) list) =
        xs |> List.map (fun (_, s, w) -> $"{s}.{w}") |> String.concat ", "
    let ctrl =
        Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index
        |> List.filter (fun (_, s, _) -> index.ActiveSystemNames.Contains s)
    Assert.True(List.isEmpty ctrl, $"Source 후보: {names ctrl}")
    let unreset = Ds2.Runtime.Engine.Core.GraphValidator.findUnresetWorks index
    Assert.True(List.isEmpty unreset, $"Reset 누락: {names unreset}")
    let dead = Ds2.Runtime.Engine.Core.GraphValidator.findDeadlockCandidates index
    Assert.True(List.isEmpty dead, $"데드락: {names dead}")


let private bodylineR4 =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# 233/43 키#1 용접 — 4회차. 도어유/도어무 **사양 분기**를 SkipAction 으로 표현."
        "# 작업서 두 시트 비교: 공정 순서는 같고 ① 실링 시간이 다르고(26s/33s)"
        "# ② 도어무에만 'S/OTR ASSY 언로딩' + '지그 언클램프' 가 더 붙는다."
        "# 차종 분기(지그가 다름 → SYS 분리)와 달리 사양 분기는 같은 설비에 공정만 가감한다."
        "SYS,ST233_키1용접,Active,,,,"
        "SYS,사양판정,Passive,,,,"
        "SYS,로보트1,Passive,,,,"
        "SYS,로보트2,Passive,,,,"
        "SYS,로보트3,Passive,,,,"
        "SYS,치구,Passive,,,,"
        "SYS,QR리더기,Passive,,,,"
        "SYS,실러,Passive,,,,"
        "FLOW,차체,,,,,"
        "WORK,차체.투입,Source,,,,"
        "WORK,차체.사양확인,,사양판정.도어유,,,"
        "WORK,차체.부품취출,,로보트2.취출,,,"
        "WORK,차체.QR인식,,QR리더기.판독,,,"
        "WORK,차체.부품정렬,,로보트2.정렬,,,"
        "WORK,차체.부품로딩,,로보트2.로딩,,,"
        "WORK,차체.실링,,실러.구조용도포,,,"
        "WORK,차체.부품언로딩,,로보트2.언로딩,,,"
        "WORK,차체.지그클램프,,치구.클램프,,,"
        "WORK,차체.용접,,로보트3.용접,,,"
        "WORK,차체.ASSY언로딩,,로보트1.ASSY언로딩,,,"
        "WORK,차체.지그언클램프,,치구.언클램프,,,"
        "WORK,차체.배출,Sink,,,,"
        "ARROW,차체.투입,Start,차체.사양확인,,,"
        "ARROW,차체.사양확인,Start,차체.부품취출,,,"
        "ARROW,차체.부품취출,Start,차체.QR인식,,,"
        "ARROW,차체.QR인식,Start,차체.부품정렬,,,"
        "ARROW,차체.부품정렬,Start,차체.부품로딩,,,"
        "ARROW,차체.부품로딩,Start,차체.실링,,,"
        "ARROW,차체.실링,Start,차체.부품언로딩,,,"
        "ARROW,차체.부품언로딩,Start,차체.지그클램프,,,"
        "ARROW,차체.지그클램프,Start,차체.용접,,,"
        "ARROW,차체.용접,Start,차체.ASSY언로딩,,,"
        "ARROW,차체.ASSY언로딩,Start,차체.지그언클램프,,,"
        "ARROW,차체.지그언클램프,Start,차체.배출,,,"
        "ARROW,차체.배출,Reset,차체.투입;차체.사양확인;차체.부품취출;차체.QR인식;차체.부품정렬;차체.부품로딩;차체.실링;차체.부품언로딩;차체.지그클램프;차체.용접;차체.ASSY언로딩;차체.지그언클램프,,,"
        "ARROW,차체.투입,Reset,차체.배출,,,"
        "API,사양판정.도어유,Virtual/Latch(30),,100MS,%IX3.00.0.01,"
        "API,로보트2.취출,Normal/Normal,,8S,%IX3.30.0.01,%QX3.31.0.01"
        "API,QR리더기.판독,Virtual/Latch(30),,8S,%IX3.40.0.01,"
        "API,로보트2.정렬,Normal/Normal,,6S,%IX3.30.0.02,%QX3.31.0.02"
        "API,로보트2.로딩,Normal/Normal,,8S,%IX3.30.0.03,%QX3.31.0.03"
        "API,실러.구조용도포,Normal/Normal,,33S,%IX3.50.0.01,%QX3.51.0.01"
        "API,로보트2.언로딩,Normal/Normal,,8S,%IX3.30.0.04,%QX3.31.0.04"
        "API,치구.클램프,Latch/Normal,,3S,%IX3.60.0.01,%QX3.61.0.01"
        "API,로보트3.용접,Normal/Normal,,36S,%IX3.70.0.01,%QX3.71.0.01"
        "API,로보트1.ASSY언로딩,Normal/Normal,,8S,%IX3.10.0.01,%QX3.11.0.01"
        "API,치구.언클램프,Virtual/Normal,,3S,%IX3.60.0.02,"
        "# 도어유면 ASSY 언로딩·지그 언클램프를 건너뛴다. 조건이 서면 건너뛴다."
        "COND,차체.ASSY언로딩,SkipAction,사양판정.도어유,,,"
        "COND,차체.지그언클램프,SkipAction,사양판정.도어유,,,"
        "COND,차체.용접.로보트3.용접,AutoAux,치구.클램프,,,"
    ]

[<Fact>]
let ``차체라인 4회차 — 사양 분기를 SkipAction 으로 적어도 경고가 없다`` () =
    // 도어유/도어무는 «다른 지그» 가 아니라 «같은 설비에 공정 가감» 이다.
    // 차종 분기(SYS 분리)와 달리 SkipAction 으로 표현한다.
    let doc =
        match CsvImporter.parseAiContent bodylineR4 with
        | Error es -> failwith (String.concat "\n" es)
        | Ok d -> d
    let store =
        match CsvImporter.loadAiProject doc "ST233spec" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok s -> s
    // Work 조건이 실제로 붙었는가 — SkipAction 은 Work 소유다.
    let skipWorks =
        store.Works.Values |> Seq.filter (fun w -> w.Conditions.Count > 0) |> Seq.toList
    Assert.Equal(2, List.length skipWorks)
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let names (xs: (System.Guid * string * string) list) =
        xs |> List.map (fun (_, s, w) -> $"{s}.{w}") |> String.concat ", "
    let ctrl =
        Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index
        |> List.filter (fun (_, s, _) -> index.ActiveSystemNames.Contains s)
    Assert.True(List.isEmpty ctrl, $"Source 후보: {names ctrl}")
    let unreset = Ds2.Runtime.Engine.Core.GraphValidator.findUnresetWorks index
    Assert.True(List.isEmpty unreset, $"Reset 누락: {names unreset}")
    let dead = Ds2.Runtime.Engine.Core.GraphValidator.findDeadlockCandidates index
    Assert.True(List.isEmpty dead, $"데드락: {names dead}")


let private bodylineR5 =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# SIDE OTR COMPL LINE 전체 — 5회차. 차종 2종(KA4/MV) × 스테이션 4개 × Capa 2."
        "# 지침 §2 차종은 SYS 분리 · §6 라인은 Capa>1 · §11 작업서 타임차트 실측 시간"
        "SYS,SIDE_OTR_LINE,Active,,,,"
        "SYS,KA4_핀,Passive,,,,"
        "SYS,KA4_1차클램프,Passive,,,,"
        "SYS,KA4_2차클램프,Passive,,,,"
        "SYS,KA4_3차클램프,Passive,,,,"
        "SYS,MV_핀,Passive,,,,"
        "SYS,MV_1차클램프,Passive,,,,"
        "SYS,MV_2차클램프,Passive,,,,"
        "SYS,MV_3차클램프,Passive,,,,"
        "SYS,HDLG1,Passive,,,,"
        "SYS,HDLG2,Passive,,,,"
        "SYS,SPOT1,Passive,,,,"
        "SYS,SPOT2,Passive,,,,"
        "SYS,PED1,Passive,,,,"
        "SYS,PED2,Passive,,,,"
        "SYS,실러,Passive,,,,"
        "SYS,QR리더기,Passive,,,,"
        "SYS,투입센서,Passive,,,,"
        "SYS,체크로봇,Passive,,,,"
        "SYS,버퍼,Passive,,,,"
        "SYS,사양판정,Passive,,,,"
        "FLOW,차체A,,,,,"
        "FLOW,차체B,,,,,"
        "WORK,차체A.투입,Source,,,,"
        "WORK,차체A.사양확인,,사양판정.도어유;투입센서.PARTON,,,"
        "WORK,차체A.반입,,HDLG1.반입,,,"
        "WORK,차체A.위치결정,,KA4_핀.ADV>KA4_1차클램프.ADV>KA4_2차클램프.ADV>KA4_3차클램프.ADV,,,"
        "WORK,차체A.키1용접,,SPOT1.타점,,,"
        "WORK,차체A.실링,,실러.구조용도포,,,"
        "WORK,차체A.키2용접,,SPOT2.타점>PED1.타점,,,"
        "WORK,차체A.증타용접,,PED2.타점,,,"
        "WORK,차체A.체크,,체크로봇.검사>QR리더기.판독,,,"
        "WORK,차체A.해제,,KA4_3차클램프.RET>KA4_2차클램프.RET>KA4_1차클램프.RET>KA4_핀.RET,,,"
        "WORK,차체A.반출,,HDLG2.반출,,,"
        "WORK,차체A.버퍼적재,,버퍼.적재,,,"
        "WORK,차체A.배출,Sink,,,,"
        "WORK,차체B.투입,Source,,,,"
        "WORK,차체B.사양확인,,사양판정.도어유;투입센서.PARTON,,,"
        "WORK,차체B.반입,,HDLG1.반입,,,"
        "WORK,차체B.위치결정,,MV_핀.ADV>MV_1차클램프.ADV>MV_2차클램프.ADV>MV_3차클램프.ADV,,,"
        "WORK,차체B.키1용접,,SPOT1.타점,,,"
        "WORK,차체B.실링,,실러.구조용도포,,,"
        "WORK,차체B.키2용접,,SPOT2.타점>PED1.타점,,,"
        "WORK,차체B.증타용접,,PED2.타점,,,"
        "WORK,차체B.체크,,체크로봇.검사>QR리더기.판독,,,"
        "WORK,차체B.해제,,MV_3차클램프.RET>MV_2차클램프.RET>MV_1차클램프.RET>MV_핀.RET,,,"
        "WORK,차체B.반출,,HDLG2.반출,,,"
        "WORK,차체B.버퍼적재,,버퍼.적재,,,"
        "WORK,차체B.배출,Sink,,,,"
        "ARROW,차체A.투입,Start,차체A.사양확인,,,"
        "ARROW,차체A.사양확인,Start,차체A.반입,,,"
        "ARROW,차체A.반입,Start,차체A.위치결정,,,"
        "ARROW,차체A.위치결정,Start,차체A.키1용접,,,"
        "ARROW,차체A.키1용접,Start,차체A.실링,,,"
        "ARROW,차체A.실링,Start,차체A.키2용접,,,"
        "ARROW,차체A.키2용접,Start,차체A.증타용접,,,"
        "ARROW,차체A.증타용접,Start,차체A.체크,,,"
        "ARROW,차체A.체크,Start,차체A.해제,,,"
        "ARROW,차체A.해제,Start,차체A.반출,,,"
        "ARROW,차체A.반출,Start,차체A.버퍼적재,,,"
        "ARROW,차체A.버퍼적재,Start,차체A.배출,,,"
        "ARROW,차체A.배출,Reset,차체A.투입;차체A.사양확인;차체A.반입;차체A.위치결정;차체A.키1용접;차체A.실링;차체A.키2용접;차체A.증타용접;차체A.체크;차체A.해제;차체A.반출;차체A.버퍼적재,,,"
        "ARROW,차체A.투입,Reset,차체A.배출,,,"
        "ARROW,차체B.투입,Start,차체B.사양확인,,,"
        "ARROW,차체B.사양확인,Start,차체B.반입,,,"
        "ARROW,차체B.반입,Start,차체B.위치결정,,,"
        "ARROW,차체B.위치결정,Start,차체B.키1용접,,,"
        "ARROW,차체B.키1용접,Start,차체B.실링,,,"
        "ARROW,차체B.실링,Start,차체B.키2용접,,,"
        "ARROW,차체B.키2용접,Start,차체B.증타용접,,,"
        "ARROW,차체B.증타용접,Start,차체B.체크,,,"
        "ARROW,차체B.체크,Start,차체B.해제,,,"
        "ARROW,차체B.해제,Start,차체B.반출,,,"
        "ARROW,차체B.반출,Start,차체B.버퍼적재,,,"
        "ARROW,차체B.버퍼적재,Start,차체B.배출,,,"
        "ARROW,차체B.배출,Reset,차체B.투입;차체B.사양확인;차체B.반입;차체B.위치결정;차체B.키1용접;차체B.실링;차체B.키2용접;차체B.증타용접;차체B.체크;차체B.해제;차체B.반출;차체B.버퍼적재,,,"
        "ARROW,차체B.투입,Reset,차체B.배출,,,"
        "API,KA4_핀.ADV,Latch/Normal,,800MS,%IX3.30.0.01,%QX3.31.0.01"
        "API,KA4_핀.RET,Virtual/Normal,,800MS,%IX3.30.0.02,"
        "API,KA4_1차클램프.ADV,Latch/Normal,,1S,%IX3.30.0.01,%QX3.31.0.01"
        "API,KA4_1차클램프.RET,Virtual/Normal,,1S,%IX3.30.0.02,"
        "API,KA4_2차클램프.ADV,Latch/Normal,,1S,%IX3.30.0.01,%QX3.31.0.01"
        "API,KA4_2차클램프.RET,Virtual/Normal,,1S,%IX3.30.0.02,"
        "API,KA4_3차클램프.ADV,Latch/Normal,,1S,%IX3.30.0.01,%QX3.31.0.01"
        "API,KA4_3차클램프.RET,Virtual/Normal,,1S,%IX3.30.0.02,"
        "API,MV_핀.ADV,Latch/Normal,,800MS,%IX3.30.0.01,%QX3.31.0.01"
        "API,MV_핀.RET,Virtual/Normal,,800MS,%IX3.30.0.02,"
        "API,MV_1차클램프.ADV,Latch/Normal,,1S,%IX3.30.0.01,%QX3.31.0.01"
        "API,MV_1차클램프.RET,Virtual/Normal,,1S,%IX3.30.0.02,"
        "API,MV_2차클램프.ADV,Latch/Normal,,1S,%IX3.30.0.01,%QX3.31.0.01"
        "API,MV_2차클램프.RET,Virtual/Normal,,1S,%IX3.30.0.02,"
        "API,MV_3차클램프.ADV,Latch/Normal,,1S,%IX3.30.0.01,%QX3.31.0.01"
        "API,MV_3차클램프.RET,Virtual/Normal,,1S,%IX3.30.0.02,"
        "API,HDLG1.반입,Normal/Normal,,4S,%IX3.40.0.01,%QX3.41.0.01"
        "API,HDLG2.반출,Normal/Normal,,4S,%IX3.40.0.02,%QX3.41.0.02"
        "API,SPOT1.타점,Normal/Normal,,14S,%IX3.40.0.03,%QX3.41.0.03"
        "API,SPOT2.타점,Normal/Normal,,18S,%IX3.40.0.04,%QX3.41.0.04"
        "API,PED1.타점,Normal/Normal,,12S,%IX3.40.0.05,%QX3.41.0.05"
        "API,PED2.타점,Normal/Normal,,10S,%IX3.40.0.06,%QX3.41.0.06"
        "API,실러.구조용도포,Normal/Normal,,26S,%IX3.40.0.07,%QX3.41.0.07"
        "API,체크로봇.검사,Normal/Normal,,6S,%IX3.40.0.08,%QX3.41.0.08"
        "API,버퍼.적재,Normal/Normal,,5S,%IX3.40.0.09,%QX3.41.0.09"
        "API,QR리더기.판독,Virtual/Latch(30),,8S,%IX3.50.0.01,"
        "API,투입센서.PARTON,Virtual/Latch(30),,100MS,%IX3.50.0.02,"
        "API,사양판정.도어유,Virtual/Latch(30),,100MS,%IX3.50.0.03,"
        "COND,차체A.증타용접,SkipAction,사양판정.도어유,,,"
        "COND,차체A.키1용접.SPOT1.타점,AutoAux,HDLG1.반입,,,"
        "COND,차체B.증타용접,SkipAction,사양판정.도어유,,,"
        "COND,차체B.키1용접.SPOT1.타점,AutoAux,HDLG1.반입,,,"
    ]

[<Fact>]
let ``차체라인 5회차 — 라인 전체 x 차종 2종도 경고가 없다`` () =
    // 규모가 커져도(Passive 20 · Work 66 · Call 40) 구조 규칙이 유지되는지 본다.
    // 차종 2종이면 지그 계열이 2배가 된다 — §2 의 «차종은 SYS 분리» 가 규모로 드러나는 지점.
    let doc =
        match CsvImporter.parseAiContent bodylineR5 with
        | Error es -> failwith (String.concat "\n" es)
        | Ok d -> d
    Assert.Equal(2, (CsvImporter.previewAi doc).Capa)
    let store =
        match CsvImporter.loadAiProject doc "SideOtrLine" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok s -> s
    Assert.True(store.Systems.Count >= 20, $"Passive 규모 부족: {store.Systems.Count}")
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let names (xs: (System.Guid * string * string) list) =
        xs |> List.map (fun (_, s, w) -> $"{s}.{w}") |> String.concat ", "
    let ctrl =
        Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index
        |> List.filter (fun (_, s, _) -> index.ActiveSystemNames.Contains s)
    Assert.True(List.isEmpty ctrl, $"Source 후보: {names ctrl}")
    let unreset = Ds2.Runtime.Engine.Core.GraphValidator.findUnresetWorks index
    Assert.True(List.isEmpty unreset, $"Reset 누락: {names unreset}")
    let dead = Ds2.Runtime.Engine.Core.GraphValidator.findDeadlockCandidates index
    Assert.True(List.isEmpty dead, $"데드락: {names dead}")
    let bad = Ds2.Runtime.Engine.Core.GraphValidator.findSourcesWithPredecessors index
    Assert.True(List.isEmpty bad, $"Source 선행: {names bad}")


let private bodylineR6 =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# S507 정렬셀 로봇 핸드셰이크 — 6회차. IO 정본(tags_io.csv) 실측 쌍을 그대로 옮긴다."
        "# §3 의 핵심: 쌍을 이름으로 추측하면 틀린다. 1·3호기와 5호기가 서로 다르게 짝지어져 있다."
        "#   1·3호기: IN_기동OK  ↔ OUT_1차_진입OK"
        "#   5호기  : IN_기동OK  ↔ OUT_자동기동"
        "# %QX = PLC 출력(가능) · %IX = PLC 입력(완료). 방향은 주소가 정한다."
        "SYS,S507_정렬셀,Active,,,,"
        "SYS,RT1호기,Passive,,,,"
        "SYS,RT3호기,Passive,,,,"
        "SYS,RT5호기,Passive,,,,"
        "SYS,정렬지그,Passive,,,,"
        "SYS,낙하방지,Passive,,,,"
        "SYS,부품감지,Passive,,,,"
        "FLOW,부품,,,,,"
        "WORK,부품.투입,Source,,,,"
        "WORK,부품.감지,,부품감지.PARTON,,,"
        "WORK,부품.기동,,RT5호기.자동기동,,,"
        "WORK,부품.1호기진입,,RT1호기.진입OK,,,"
        "WORK,부품.3호기진입,,RT3호기.진입OK,,,"
        "WORK,부품.안착,,RT5호기.정렬안착,,,"
        "WORK,부품.낙하방지,,낙하방지.ADV,,,"
        "WORK,부품.래치,,정렬지그.LATCH_ADV,,,"
        "WORK,부품.셋팅,,RT5호기.셋팅안착,,,"
        "WORK,부품.래치해제,,정렬지그.LATCH_RET,,,"
        "WORK,부품.낙하방지해제,,낙하방지.RET,,,"
        "WORK,부품.취출,,RT5호기.정렬취출,,,"
        "WORK,부품.작업완료,,RT5호기.작업완료RST;RT1호기.작업완료RST;RT3호기.작업완료RST,,,"
        "WORK,부품.배출,Sink,,,,"
        "ARROW,부품.투입,Start,부품.감지,,,"
        "ARROW,부품.감지,Start,부품.기동,,,"
        "ARROW,부품.기동,Start,부품.1호기진입,,,"
        "ARROW,부품.1호기진입,Group,부품.3호기진입,,,"
        "ARROW,부품.1호기진입,Start,부품.안착,,,"
        "ARROW,부품.안착,Start,부품.낙하방지,,,"
        "ARROW,부품.낙하방지,Start,부품.래치,,,"
        "ARROW,부품.래치,Start,부품.셋팅,,,"
        "ARROW,부품.셋팅,Start,부품.래치해제,,,"
        "ARROW,부품.래치해제,Start,부품.낙하방지해제,,,"
        "ARROW,부품.낙하방지해제,Start,부품.취출,,,"
        "ARROW,부품.취출,Start,부품.작업완료,,,"
        "ARROW,부품.작업완료,Start,부품.배출,,,"
        "ARROW,부품.배출,Reset,부품.투입;부품.감지;부품.기동;부품.1호기진입;부품.3호기진입;부품.안착;부품.낙하방지;부품.래치;부품.셋팅;부품.래치해제;부품.낙하방지해제;부품.취출;부품.작업완료,,,"
        "ARROW,부품.투입,Reset,부품.배출,,,"
        "API,부품감지.PARTON,Virtual/Latch(30),,100MS,%IX4.05.1.09,"
        "# 1·3호기 — 기동OK(IN)가 1차_진입OK(OUT)와 짝이다. 이름만 보면 자동기동과 짝일 것 같지만 아니다."
        "API,RT1호기.진입OK,Normal/Normal,,3S,%IX4.01.0.12,%QX4.01.4.12"
        "API,RT3호기.진입OK,Normal/Normal,,3S,%IX4.03.0.12,%QX4.03.4.12"
        "# 5호기 — 같은 기동OK(IN)가 자동기동(OUT)과 짝이다. 같은 신호가 호기마다 다르게 쓰인다."
        "API,RT5호기.자동기동,Normal/Normal,,2S,%IX4.05.0.12,%QX4.05.4.13"
        "API,RT5호기.정렬안착,Normal/Normal,,5S,%IX4.05.1.09,%QX4.05.4.11"
        "API,RT5호기.셋팅안착,Normal/Normal,,5S,%IX4.05.1.11,%QX4.05.4.09"
        "API,RT5호기.정렬취출,Normal/Normal,,5S,%IX4.05.1.10,%QX4.05.4.10"
        "# 작업완료_RST 는 출력만 있다 — 로봇이 답하지 않는 일방 지령이다."
        "API,RT5호기.작업완료RST,Pulse(500)/Virtual(100),,?,,%QX4.05.4.08"
        "API,RT1호기.작업완료RST,Pulse(500)/Virtual(100),,?,,%QX4.01.4.08"
        "API,RT3호기.작업완료RST,Pulse(500)/Virtual(100),,?,,%QX4.03.4.08"
        "API,낙하방지.ADV,Latch/Normal,,1S,%IX3.60.1.09,%QX3.63.0.07"
        "API,낙하방지.RET,Virtual/Normal,,1S,%IX3.60.1.10,"
        "API,정렬지그.LATCH_ADV,Latch/Normal,,1S,%IX3.60.0.01,%QX3.63.0.01"
        "API,정렬지그.LATCH_RET,Virtual/Normal,,1S,%IX3.60.0.02,"
        "COND,부품.안착.RT5호기.정렬안착,AutoAux,부품감지.PARTON,,,"
        "COND,부품.셋팅.RT5호기.셋팅안착,AutoAux,정렬지그.LATCH_ADV,,,"
        "COND,부품.취출.RT5호기.정렬취출,AutoAux,정렬지그.LATCH_RET & 낙하방지.RET,,,"
    ]

[<Fact>]
let ``차체라인 6회차 — 로봇 핸드셰이크 실측 쌍이 보존된다`` () =
    // IO 정본 실측: 1·3호기는 기동OK↔1차_진입OK, 5호기는 기동OK↔자동기동 으로 짝이 다르다.
    // 이름으로 추측하면 틀리는 자리라, 적은 대로 들어가는지 확인한다.
    let doc =
        match CsvImporter.parseAiContent bodylineR6 with
        | Error es -> failwith (String.concat "\n" es)
        | Ok d -> d
    let paired = doc.Apis |> List.filter (fun a -> a.InTag.IsSome && a.OutTag.IsSome) |> List.length
    let outOnly = doc.Apis |> List.filter (fun a -> a.InTag.IsNone && a.OutTag.IsSome) |> List.length
    Assert.Equal(8, paired)      // 핸드셰이크 쌍
    Assert.Equal(3, outOnly)     // 작업완료_RST — 로봇이 답하지 않는 일방 지령
    let store =
        match CsvImporter.loadAiProject doc "S507Align" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok s -> s
    // API 행을 적었으면 ApiDef 가 전부 생겨야 한다 — 어느 Call 도 안 부르면 조용히 사라진다.
    Assert.Equal(List.length doc.Apis, store.ApiDefs.Count)
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let names (xs: (System.Guid * string * string) list) =
        xs |> List.map (fun (_, s, w) -> $"{s}.{w}") |> String.concat ", "
    let ctrl =
        Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index
        |> List.filter (fun (_, s, _) -> index.ActiveSystemNames.Contains s)
    Assert.True(List.isEmpty ctrl, $"Source 후보: {names ctrl}")
    let unreset = Ds2.Runtime.Engine.Core.GraphValidator.findUnresetWorks index
    Assert.True(List.isEmpty unreset, $"Reset 누락: {names unreset}")

[<Fact>]
let ``호출되지 않는 API 행은 경고로 드러난다`` () =
    // 적어 놓고 아무도 부르지 않으면 ApiDef 가 만들어지지 않는다(캐스케이드는 Call 기준).
    // 조용히 사라지면 IO 를 배선해 놓고도 모델에 없는 상태가 된다.
    let csv =
        "Kind,Name,Type,Detail,Time,InTag,OutTag\n\
         SYS,S,Active,,,,\n\
         FLOW,F,,,,,\n\
         API,D.쓰임,Normal/Normal,,1S,,\n\
         API,D.안쓰임,Normal/Normal,,1S,,\n\
         WORK,F.W,Source,D.쓰임,,,"
    match CsvImporter.parseAiContent csv with
    | Error es -> failwith (String.concat "\n" es)
    | Ok doc ->
        Assert.Contains(doc.Warnings, fun w -> w.Contains "AI-W9" && w.Contains "D.안쓰임")


let private bodylineR7 =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# S507 8분할 인덱스 테이블 — 7회차. 실측 각도 구성(0/135/180/270/315)을 그대로 옮긴다."
        "# 각도가 곧 스테이션이고 각 각도에 다른 차종이 선다: 0=KA4 · 180=RJ · 315=MV · 135/270=D(복귀)"
        "# 회전 전에는 반드시 언락, 회전 후에는 반드시 락 — 이 쌍을 빠뜨리면 테이블이 돌다 멈춘다."
        "SYS,S507_인덱스셀,Active,,,,"
        "SYS,인덱스락,Passive,,,,"
        "SYS,인덱스서보,Passive,,,,"
        "SYS,KA4_지그,Passive,,,,"
        "SYS,RJ_지그,Passive,,,,"
        "SYS,MV_지그,Passive,,,,"
        "SYS,차종판정,Passive,,,,"
        "SYS,안착센서,Passive,,,,"
        "FLOW,캐리어,,,,,"
        "WORK,캐리어.투입,Source,,,,"
        "WORK,캐리어.차종판정,,차종판정.KA4;차종판정.RJ;차종판정.MV,,,"
        "WORK,캐리어.안착확인,,안착센서.PARTON,,,"
        "WORK,캐리어.언락,,인덱스락.언락,,,"
        "WORK,캐리어.KA4회전,,인덱스서보.0_KA4_TURN,,,"
        "WORK,캐리어.락,,인덱스락.락,,,"
        "WORK,캐리어.KA4작업,,KA4_지그.클램프>KA4_지그.언클램프,,,"
        "WORK,캐리어.언락2,,인덱스락.언락,,,"
        "WORK,캐리어.RJ회전,,인덱스서보.180_RJ_TURN,,,"
        "WORK,캐리어.락2,,인덱스락.락,,,"
        "WORK,캐리어.RJ작업,,RJ_지그.클램프>RJ_지그.언클램프,,,"
        "WORK,캐리어.언락3,,인덱스락.언락,,,"
        "WORK,캐리어.MV복귀,,인덱스서보.315_MV_RETURN,,,"
        "WORK,캐리어.락3,,인덱스락.락,,,"
        "WORK,캐리어.MV작업,,MV_지그.클램프>MV_지그.언클램프,,,"
        "WORK,캐리어.배출,Sink,,,,"
        "ARROW,캐리어.투입,Start,캐리어.차종판정,,,"
        "ARROW,캐리어.차종판정,Start,캐리어.안착확인,,,"
        "ARROW,캐리어.안착확인,Start,캐리어.언락,,,"
        "ARROW,캐리어.언락,Start,캐리어.KA4회전,,,"
        "ARROW,캐리어.KA4회전,Start,캐리어.락,,,"
        "ARROW,캐리어.락,Start,캐리어.KA4작업,,,"
        "ARROW,캐리어.KA4작업,Start,캐리어.언락2,,,"
        "ARROW,캐리어.언락2,Start,캐리어.RJ회전,,,"
        "ARROW,캐리어.RJ회전,Start,캐리어.락2,,,"
        "ARROW,캐리어.락2,Start,캐리어.RJ작업,,,"
        "ARROW,캐리어.RJ작업,Start,캐리어.언락3,,,"
        "ARROW,캐리어.언락3,Start,캐리어.MV복귀,,,"
        "ARROW,캐리어.MV복귀,Start,캐리어.락3,,,"
        "ARROW,캐리어.락3,Start,캐리어.MV작업,,,"
        "ARROW,캐리어.MV작업,Start,캐리어.배출,,,"
        "ARROW,캐리어.배출,Reset,캐리어.투입;캐리어.차종판정;캐리어.안착확인;캐리어.언락;캐리어.KA4회전;캐리어.락;캐리어.KA4작업;캐리어.언락2;캐리어.RJ회전;캐리어.락2;캐리어.RJ작업;캐리어.언락3;캐리어.MV복귀;캐리어.락3;캐리어.MV작업,,,"
        "ARROW,캐리어.투입,Reset,캐리어.배출,,,"
        "API,차종판정.KA4,Virtual/Latch(30),,100MS,%IX3.20.0.01,"
        "API,차종판정.RJ,Virtual/Latch(30),,100MS,%IX3.20.0.02,"
        "API,차종판정.MV,Virtual/Latch(30),,100MS,%IX3.20.0.03,"
        "API,안착센서.PARTON,Virtual/Latch(30),,100MS,%IX3.30.0.12,"
        "API,인덱스락.언락,Latch/Normal,,500MS,%IX3.31.0.01,%QX3.31.0.01"
        "API,인덱스락.락,Latch/Normal,,500MS,%IX3.31.0.02,%QX3.31.0.02"
        "API,인덱스서보.0_KA4_TURN,Latch/Normal,,3S,%IX3.40.0.01,%QX3.41.0.01"
        "API,인덱스서보.180_RJ_TURN,Latch/Normal,,4S,%IX3.40.0.02,%QX3.41.0.02"
        "API,인덱스서보.315_MV_RETURN,Latch/Normal,,4S,%IX3.40.0.03,%QX3.41.0.03"
        "API,KA4_지그.클램프,Latch/Normal,,1S,%IX3.50.0.01,%QX3.51.0.01"
        "API,KA4_지그.언클램프,Virtual/Normal,,1S,%IX3.50.0.02,"
        "API,RJ_지그.클램프,Latch/Normal,,1S,%IX3.50.0.03,%QX3.51.0.03"
        "API,RJ_지그.언클램프,Virtual/Normal,,1S,%IX3.50.0.04,"
        "API,MV_지그.클램프,Latch/Normal,,1S,%IX3.50.0.05,%QX3.51.0.05"
        "API,MV_지그.언클램프,Virtual/Normal,,1S,%IX3.50.0.06,"
        "# 회전은 언락이 선 뒤에만 — 락 상태로 돌리면 테이블이 파손된다."
        "COND,캐리어.KA4회전.인덱스서보.0_KA4_TURN,AutoAux,인덱스락.언락,,,"
        "COND,캐리어.RJ회전.인덱스서보.180_RJ_TURN,AutoAux,인덱스락.언락,,,"
        "COND,캐리어.MV복귀.인덱스서보.315_MV_RETURN,AutoAux,인덱스락.언락,,,"
        "# 지그 작업은 락이 선 뒤에만 — 돌아가는 중에 클램프하면 간섭이 난다."
        "COND,캐리어.KA4작업.KA4_지그.클램프,AutoAux,인덱스락.락,,,"
        "COND,캐리어.RJ작업.RJ_지그.클램프,AutoAux,인덱스락.락,,,"
        "COND,캐리어.MV작업.MV_지그.클램프,AutoAux,인덱스락.락,,,"
    ]

[<Fact>]
let ``차체라인 7회차 — 인덱스 회전에서 같은 API 를 여러 Work 가 재사용한다`` () =
    // 8분할 테이블은 각도마다 서고, 매번 언락→회전→락 을 되풀이한다.
    // 같은 락/언락 API 를 여러 Work 가 부르므로 Call 은 늘고 ApiDef 는 하나여야 한다.
    let doc =
        match CsvImporter.parseAiContent bodylineR7 with
        | Error es -> failwith (String.concat "\n" es)
        | Ok d -> d
    let store =
        match CsvImporter.loadAiProject doc "S507Index" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok s -> s
    let lockCalls =
        store.Calls.Values |> Seq.filter (fun c -> c.DevicesAlias = "인덱스락") |> Seq.length
    Assert.Equal(6, lockCalls)                       // 언락 3 + 락 3
    let lockDefs =
        store.Systems.Values
        |> Seq.filter (fun s -> s.Name = "인덱스락")
        |> Seq.collect (fun s -> Ds2.Core.Store.Queries.apiDefsOf s.Id store)
        |> Seq.length
    Assert.Equal(2, lockDefs)                        // 락 · 언락 — Call 이 6이어도 정의는 2
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let names (xs: (System.Guid * string * string) list) =
        xs |> List.map (fun (_, s, w) -> $"{s}.{w}") |> String.concat ", "
    let ctrl =
        Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index
        |> List.filter (fun (_, s, _) -> index.ActiveSystemNames.Contains s)
    Assert.True(List.isEmpty ctrl, $"Source 후보: {names ctrl}")
    let unreset = Ds2.Runtime.Engine.Core.GraphValidator.findUnresetWorks index
    Assert.True(List.isEmpty unreset, $"Reset 누락: {names unreset}")
    let dead = Ds2.Runtime.Engine.Core.GraphValidator.findDeadlockCandidates index
    Assert.True(List.isEmpty dead, $"데드락: {names dead}")


let private bodylineR8 =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# 다점 확인센서 — 8회차. 실측: 1차클램프 RET 은 리드스위치 5개가 모두 서야 «풀렸다»."
        "# csvForAI v1 은 API 행 하나에 InTag 하나다. 나머지 4개를 버리지 않고 조건으로 싣는다."
        "#   API InTag  = 대표 센서(RET1) — 동작 완료 판정"
        "#   COND       = 나머지 RET2~RET5 를 AND — 전수 확인"
        "# 이렇게 하면 «대표만 서고 나머지는 안 선» 반쪽 해제를 조건이 잡아낸다."
        "SYS,다점클램프셀,Active,,,,"
        "SYS,KA4_1차클램프,Passive,,,,"
        "SYS,클램프확인,Passive,,,,"
        "SYS,안착센서,Passive,,,,"
        "FLOW,차체,,,,,"
        "WORK,차체.투입,Source,,,,"
        "WORK,차체.안착,,안착센서.PARTON,,,"
        "WORK,차체.클램프,,KA4_1차클램프.ADV,,,"
        "WORK,차체.작업,Ignore,KA4_1차클램프.유지,,,"
        "WORK,차체.해제,,KA4_1차클램프.RET,,,"
        "WORK,차체.전수확인,,클램프확인.RET2;클램프확인.RET3;클램프확인.RET4;클램프확인.RET5,,,"
        "WORK,차체.배출,Sink,,,,"
        "ARROW,차체.투입,Start,차체.안착,,,"
        "ARROW,차체.안착,Start,차체.클램프,,,"
        "ARROW,차체.클램프,Group,차체.작업,,,"
        "ARROW,차체.클램프,Start,차체.해제,,,"
        "ARROW,차체.해제,Start,차체.전수확인,,,"
        "ARROW,차체.전수확인,Start,차체.배출,,,"
        "ARROW,차체.배출,Reset,차체.투입;차체.안착;차체.클램프;차체.작업;차체.해제;차체.전수확인,,,"
        "ARROW,차체.투입,Reset,차체.배출,,,"
        "API,안착센서.PARTON,Virtual/Latch(30),,100MS,%IX3.30.0.12,"
        "# 대표 센서만 InTag 에. ADV 는 확인점이 1개(ADV1)라 그대로 쓴다."
        "API,KA4_1차클램프.ADV,Latch/Normal,,1S,%IX3.30.0.03,%QX3.31.0.02"
        "API,KA4_1차클램프.유지,Virtual/Virtual(200),,?,,"
        "API,KA4_1차클램프.RET,Virtual/Normal,,1S,%IX3.30.0.04,"
        "# 나머지 확인점 4개를 센서 API 로 세운다 — 조건에서 부르려면 Device.Api 여야 하기 때문."
        "API,클램프확인.RET2,Virtual/Latch(30),,100MS,%IX3.30.0.05,"
        "API,클램프확인.RET3,Virtual/Latch(30),,100MS,%IX3.30.0.06,"
        "API,클램프확인.RET4,Virtual/Latch(30),,100MS,%IX3.30.0.07,"
        "API,클램프확인.RET5,Virtual/Latch(30),,100MS,%IX3.30.0.08,"
        "# 전수 확인 — 대표(RET1)는 API InTag 가 이미 보고, 나머지 4개를 AND 로 묶는다."
        "COND,차체.전수확인.클램프확인.RET2,AutoAux,클램프확인.RET3 & 클램프확인.RET4 & 클램프확인.RET5,,,"
        "# 작업은 클램프가 선 뒤에만."
        "COND,차체.작업.KA4_1차클램프.유지,AutoAux,KA4_1차클램프.ADV,,,"
    ]

[<Fact>]
let ``차체라인 8회차 — 다점 확인센서를 조건으로 보존한다`` () =
    // 실측 1차클램프 RET 은 리드스위치 5개가 모두 서야 «풀렸다».
    // API 행은 InTag 하나뿐이라 대표(RET1)만 싣고, 나머지 4개는 AND 조건으로 보존한다.
    let doc =
        match CsvImporter.parseAiContent bodylineR8 with
        | Error es -> failwith (String.concat "\n" es)
        | Ok d -> d
    let store =
        match CsvImporter.loadAiProject doc "MultiSensor" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok s -> s
    // AND 세 개가 한 조건에 실려야 한다 — 쪼개지면 전수 확인이 성립하지 않는다.
    let multi =
        store.Calls.Values
        |> Seq.collect (fun c -> c.Conditions)
        |> Seq.filter (fun cond -> cond.ApiCalls.Count > 1)
        |> Seq.toList
    Assert.Single(multi) |> ignore
    Assert.Equal(3, multi.Head.ApiCalls.Count)
    Assert.False(multi.Head.IsOR, "전수 확인은 AND 여야 한다")
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let names (xs: (System.Guid * string * string) list) =
        xs |> List.map (fun (_, s, w) -> $"{s}.{w}") |> String.concat ", "
    let ctrl =
        Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index
        |> List.filter (fun (_, s, _) -> index.ActiveSystemNames.Contains s)
    Assert.True(List.isEmpty ctrl, $"Source 후보: {names ctrl}")
    let unreset = Ds2.Runtime.Engine.Core.GraphValidator.findUnresetWorks index
    Assert.True(List.isEmpty unreset, $"Reset 누락: {names unreset}")


let private bodylineR9 =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# 병렬 2열 지그 — 9회차. 지그A 가 용접되는 동안 지그B 는 로딩한다(번갈아)."
        "# 두 지그는 서로를 리셋한다: A 가 시작하면 B 가 비워지고, B 가 시작하면 A 가 비워진다."
        "# 이것이 ResetReset(상호 재무장)이다. 한쪽은 초기 상태를 Finish 로 두어야 첫 사이클이 선다."
        "SYS,트윈지그셀,Active,,,,"
        "SYS,지그A,Passive,,,,"
        "SYS,지그B,Passive,,,,"
        "SYS,용접로봇,Passive,,,,"
        "SYS,반입로봇,Passive,,,,"
        "SYS,센서A,Passive,,,,"
        "SYS,센서B,Passive,,,,"
        "FLOW,차체,,,,,"
        "WORK,차체.투입,Source,,,,"
        "WORK,차체.A안착,,센서A.PARTON,,,"
        "WORK,차체.A클램프,,지그A.클램프,,,"
        "WORK,차체.A용접,,용접로봇.A타점,,,"
        "WORK,차체.A해제,,지그A.언클램프,,,"
        "WORK,차체.B로딩,Finish,반입로봇.B반입,,,"
        "WORK,차체.B안착,,센서B.PARTON,,,"
        "WORK,차체.B클램프,,지그B.클램프,,,"
        "WORK,차체.B용접,,용접로봇.B타점,,,"
        "WORK,차체.B해제,,지그B.언클램프,,,"
        "WORK,차체.배출,Sink,,,,"
        "ARROW,차체.투입,Start,차체.A안착,,,"
        "ARROW,차체.A안착,Start,차체.A클램프,,,"
        "ARROW,차체.A클램프,Start,차체.A용접,,,"
        "ARROW,차체.A용접,Start,차체.A해제,,,"
        "ARROW,차체.A해제,Start,차체.B안착,,,"
        "ARROW,차체.B안착,Start,차체.B클램프,,,"
        "ARROW,차체.B클램프,Start,차체.B용접,,,"
        "ARROW,차체.B용접,Start,차체.B해제,,,"
        "ARROW,차체.B해제,Start,차체.배출,,,"
        "# 상호 재무장 — A 용접이 시작되면 B 로딩이 리셋되고, B 로딩이 시작되면 A 용접이 리셋된다."
        "# 두 자리가 번갈아 비워져 끊김 없이 돈다."
        "ARROW,차체.A용접,ResetReset,차체.B로딩,,,"
        "ARROW,차체.배출,Reset,차체.투입;차체.A안착;차체.A클램프;차체.A해제;차체.B안착;차체.B클램프;차체.B용접;차체.B해제,,,"
        "ARROW,차체.투입,Reset,차체.배출,,,"
        "API,센서A.PARTON,Virtual/Latch(30),,100MS,%IX3.30.0.01,"
        "API,센서B.PARTON,Virtual/Latch(30),,100MS,%IX3.30.0.02,"
        "API,지그A.클램프,Latch/Normal,,1S,%IX3.40.0.01,%QX3.41.0.01"
        "API,지그A.언클램프,Virtual/Normal,,1S,%IX3.40.0.02,"
        "API,지그B.클램프,Latch/Normal,,1S,%IX3.40.0.03,%QX3.41.0.03"
        "API,지그B.언클램프,Virtual/Normal,,1S,%IX3.40.0.04,"
        "API,용접로봇.A타점,Normal/Normal,,16S,%IX3.50.0.01,%QX3.51.0.01"
        "API,용접로봇.B타점,Normal/Normal,,16S,%IX3.50.0.02,%QX3.51.0.02"
        "API,반입로봇.B반입,Normal/Normal,,5S,%IX3.60.0.01,%QX3.61.0.01"
        "COND,차체.A용접.용접로봇.A타점,AutoAux,지그A.클램프,,,"
        "COND,차체.B용접.용접로봇.B타점,AutoAux,지그B.클램프,,,"
    ]

[<Fact>]
let ``차체라인 9회차 — 상호 재무장(ResetReset)과 초기 Finish`` () =
    // 병렬 2열 지그: A 가 용접되는 동안 B 는 로딩한다. 두 자리가 서로를 리셋해 번갈아 돈다.
    // 한쪽은 초기 상태가 Finish 여야 첫 사이클이 선다.
    let doc =
        match CsvImporter.parseAiContent bodylineR9 with
        | Error es -> failwith (String.concat "\n" es)
        | Ok d -> d
    let store =
        match CsvImporter.loadAiProject doc "TwinJig" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok s -> s
    // 내가 적은 상호 재무장이 Active System 에 실렸는가 (나머지는 캐스케이드가 만든 설비 내부 쌍)
    let activeSystemId =
        (store.Projects.Values |> Seq.head).ActiveSystemIds |> Seq.head
    let mine =
        store.ArrowWorks.Values
        |> Seq.filter (fun a -> a.ArrowType = Ds2.Core.ArrowType.ResetReset && a.ParentId = activeSystemId)
        |> Seq.toList
    Assert.Single(mine) |> ignore
    // Finish 표기가 초기 상태로 반영되는가
    let finished =
        store.Works.Values |> Seq.filter (fun w -> w.Status4 = Ds2.Core.Status4.Finish) |> Seq.toList
    Assert.Single(finished) |> ignore
    Assert.Contains("B로딩", finished.Head.Name)
    let index = Ds2.Runtime.Engine.Core.SimIndex.build store 10
    let names (xs: (System.Guid * string * string) list) =
        xs |> List.map (fun (_, s, w) -> $"{s}.{w}") |> String.concat ", "
    let ctrl =
        Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index
        |> List.filter (fun (_, s, _) -> index.ActiveSystemNames.Contains s)
    Assert.True(List.isEmpty ctrl, $"Source 후보: {names ctrl}")
    let unreset = Ds2.Runtime.Engine.Core.GraphValidator.findUnresetWorks index
    Assert.True(List.isEmpty unreset, $"Reset 누락: {names unreset}")

[<Fact>]
let ``sensor 표기가 모델을 바꾸지 않음을 경고로 알린다`` () =
    // 파서가 받아들이기만 하고 매퍼가 쓰지 않는 플래그다. 조용히 두면
    // «적었으니 효과가 있다» 고 믿게 된다.
    let csv =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,S,Active,,,,"
            "FLOW,F,,,,,"
            "API,센서.DETECT,Virtual/Latch(30),sensor,1S,%IX0.0.0,"
            "WORK,F.W,Source,센서.DETECT,,,"
        ]
    match CsvImporter.parseAiContent csv with
    | Error es -> failwith (String.concat "\n" es)
    | Ok doc -> Assert.Contains(doc.Warnings, fun w -> w.Contains "AI-W10")


let private bodylineR10 =
    String.concat "\n" [
        "Kind,Name,Type,Detail,Time,InTag,OutTag"
        "# 조건 표기 검증 — 10회차. 엣지 펄스와 기대값을 실제 패턴으로 쓴다."
        "#   (R) 상승 — 기동 버튼이 «눌리는 순간» (누르고 있는 동안이 아니다)"
        "#   (F) 하강 — 차체가 «빠져나간 순간» (없는 동안이 아니다)"
        "#   =값   — QR 이 읽은 차종 코드 비교. bool 이 아닌 값 조건"
        "SYS,반출셀,Active,,,,"
        "SYS,기동버튼,Passive,,,,"
        "SYS,QR리더기,Passive,,,,"
        "SYS,진출게이트,Passive,,,,"
        "SYS,이탈센서,Passive,,,,"
        "SYS,컨베이어,Passive,,,,"
        "FLOW,차체,,,,,"
        "WORK,차체.투입,Source,,,,"
        "WORK,차체.기동대기,,기동버튼.PB,,,"
        "WORK,차체.차종판독,,QR리더기.코드읽기,,,"
        "WORK,차체.게이트열림,,진출게이트.열림,,,"
        "WORK,차체.반출,,컨베이어.이송,,,"
        "WORK,차체.이탈확인,,이탈센서.감지,,,"
        "WORK,차체.게이트닫힘,,진출게이트.닫힘,,,"
        "WORK,차체.배출,Sink,,,,"
        "ARROW,차체.투입,Start,차체.기동대기,,,"
        "ARROW,차체.기동대기,Start,차체.차종판독,,,"
        "ARROW,차체.차종판독,Start,차체.게이트열림,,,"
        "ARROW,차체.게이트열림,Start,차체.반출,,,"
        "ARROW,차체.반출,Start,차체.이탈확인,,,"
        "ARROW,차체.이탈확인,Start,차체.게이트닫힘,,,"
        "ARROW,차체.게이트닫힘,Start,차체.배출,,,"
        "ARROW,차체.배출,Reset,차체.투입;차체.기동대기;차체.차종판독;차체.게이트열림;차체.반출;차체.이탈확인;차체.게이트닫힘,,,"
        "ARROW,차체.투입,Reset,차체.배출,,,"
        "API,기동버튼.PB,Virtual/Latch(30),,100MS,%IX3.00.0.01,"
        "API,QR리더기.코드읽기,Virtual/Latch(30),,8S,%IX3.10.0.01:DINT,"
        "API,진출게이트.열림,Latch/Normal,,1500MS,%IX3.20.0.01,%QX3.21.0.01"
        "API,진출게이트.닫힘,Virtual/Normal,,1500MS,%IX3.20.0.02,"
        "API,컨베이어.이송,Normal/Normal,,6S,%IX3.30.0.01,%QX3.31.0.01"
        "API,이탈센서.감지,Virtual/Latch(30),,100MS,%IX3.40.0.01,"
        "# (R) 상승 펄스 — 버튼이 «눌리는 순간» 에만 통과. 누르고 있어도 다시 서지 않는다."
        "COND,차체.차종판독.QR리더기.코드읽기,AutoAux,기동버튼.PB(R),,,"
        "# =값 — QR 이 읽은 코드가 특정 차종일 때만. bool 이 아닌 정수 비교."
        "COND,차체.게이트열림.진출게이트.열림,AutoAux,QR리더기.코드읽기=100,,,"
        "# (F) 하강 펄스 — 차체가 «빠져나간 순간». 없는 동안 내내가 아니다."
        "COND,차체.게이트닫힘.진출게이트.닫힘,AutoAux,이탈센서.감지(F),,,"
    ]

[<Fact>]
let ``차체라인 10회차 — 엣지 펄스와 기대값이 조건에 실린다`` () =
    // (R) 는 «눌리는 순간», (F) 는 «빠져나간 순간», =값 은 bool 이 아닌 비교다.
    // 토크나이저가 (R)/(F) 의 괄호를 묶음 괄호로 오인하면 조건식이 통째로 깨진다.
    let doc =
        match CsvImporter.parseAiContent bodylineR10 with
        | Error es -> failwith (String.concat "\n" es)
        | Ok d -> d
    let store =
        match CsvImporter.loadAiProject doc "EdgeValue" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok s -> s
    let leaves =
        store.Calls.Values
        |> Seq.collect (fun c -> c.Conditions |> Seq.collect (fun cond -> cond.ApiCalls))
        |> Seq.toList
    Assert.Equal(3, List.length leaves)
    Assert.Contains(leaves, fun l -> l.ContactKind = Ds2.Core.ContactKind.RisingPulse)
    Assert.Contains(leaves, fun l -> l.ContactKind = Ds2.Core.ContactKind.FallingPulse)
    // =100 이 정수 기대값으로 들어갔는가 — bool 로 뭉개지면 값 비교가 사라진다.
    Assert.Contains(leaves, fun l ->
        match l.InputSpec with
        | Ds2.Core.ValueSpec.Int64Value _ -> true
        | _ -> false)

[<Fact>]
let ``엣지 접미사의 괄호는 묶음 괄호와 다르게 읽힌다`` () =
    // `A.B(R) & C.D` 에서 앞의 괄호는 leaf 의 일부이고 & 는 묶음 연산자다.
    let csv =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,S,Active,,,,"
            "FLOW,F,,,,,"
            "API,D.A,Normal/Normal,,1S,,"
            "API,D.B,Normal/Normal,,1S,,"
            "API,D.C,Normal/Normal,,1S,,"
            "WORK,F.W,Source,D.A,,,"
            "COND,F.W.D.A,SkipAction,D.B(R) & D.C(F),,,"
        ]
    match CsvImporter.parseAiContent csv with
    | Error es -> failwith (String.concat "\n" es)
    | Ok doc ->
        let cond = List.exactlyOne doc.Conds
        match cond.Expr with
        | AiGroup(isOr, _, children) ->
            Assert.False(isOr)
            Assert.Equal(2, List.length children)
        | _ -> failwith "묶음으로 읽히지 않았다"

[<Fact>]
let ``AutoAux 와 ComAux 는 같은 Call 에 공존한다`` () =
    // 엔진이 둘을 AND 로 함께 보므로(WorkConditionChecker.fs) 한 Call 이 둘 다 갖는 것이 정상이다.
    let csv =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,셀,Active,,,,"
            "FLOW,차체,,,,,"
            "API,지그.클램프,Normal/Normal,,1S,,"
            "API,안전문.닫힘,Virtual/Latch(30),,100MS,,"
            "API,로봇.타점,Normal/Normal,,16S,,"
            "WORK,차체.클램프,Source,지그.클램프,,,"
            "WORK,차체.점검,,안전문.닫힘,,,"
            "WORK,차체.용접,Sink,로봇.타점,,,"
            "COND,차체.용접.로봇.타점,AutoAux,지그.클램프,,,"
            "COND,차체.용접.로봇.타점,ComAux,안전문.닫힘,,,"
        ]
    match CsvImporter.parseAiContent csv with
    | Error es -> failwith (String.concat "\n" es)
    | Ok doc ->
        Assert.Equal(2, List.length doc.Conds)
        let types = doc.Conds |> List.map (fun c -> c.CondType) |> List.sort
        Assert.Equal<ConditionType list>([ ConditionType.AutoAux; ConditionType.ComAux ], types)
        // 같은 소유자(Call)에 둘 다 붙는다.
        Assert.Equal(1, doc.Conds |> List.map (fun c -> c.OwnerPath) |> List.distinct |> List.length)

[<Fact>]
let ``Work 조건에 AutoAux 를 쓰면 AI014 로 거부한다`` () =
    // 엔진 SimIndex 에 Work 용 Aux 자리가 없다 — 조용히 무시되지 않고 막혀야 한다.
    let csv =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,셀,Active,,,,"
            "FLOW,차체,,,,,"
            "API,안전문.닫힘,Virtual/Latch(30),,100MS,,"
            "API,로봇.타점,Normal/Normal,,16S,,"
            "WORK,차체.점검,Source,안전문.닫힘,,,"
            "WORK,차체.용접,Sink,로봇.타점,,,"
            "COND,차체.용접,AutoAux,안전문.닫힘,,,"
        ]
    match CsvImporter.parseAiContent csv with
    | Ok _ -> failwith "Work 레벨 AutoAux 가 통과했다"
    | Error es -> Assert.Contains(es, fun e -> e.Contains "AI014")

[<Fact>]
let ``Call 레벨 Group 은 선행을 묶음 전체에 물려준다`` () =
    // 4점 클램프 — 대표 하나만 핀상승에 잇고 나머지는 Group 으로 묶는다.
    // expandCallGroupArrows 가 선행을 전원에게 확산시켜야 넷이 «동시에» 나간다.
    let csv =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,벅,Active,,,,"
            "FLOW,차체,,,,,"
            "API,지그.핀상승,Normal/Normal,,1S,,"
            "API,지그.핀하강,Normal/Normal,,1S,,"
            "API,지그.클램프A,Normal/Normal,,2S,,"
            "API,지그.클램프B,Normal/Normal,,2S,,"
            "API,지그.클램프C,Normal/Normal,,2S,,"
            "WORK,차체.위치결정,Source,지그.핀상승 > 지그.클램프A ; 지그.클램프B ; 지그.클램프C,,,"
            "WORK,차체.해제,Sink,지그.핀하강,,,"
            "ARROW,차체.위치결정,Start,차체.해제,,,"
            "ARROW,차체.해제,Reset,차체.위치결정,,,"
            "ARROW,차체.위치결정,Reset,차체.해제,,,"
            "ARROW,차체.위치결정.지그.클램프A,Group,차체.위치결정.지그.클램프B,,,"
            "ARROW,차체.위치결정.지그.클램프A,Group,차체.위치결정.지그.클램프C,,,"
        ]
    match CsvImporter.parseAiContent csv |> Result.bind (fun d -> CsvImporter.loadAiProject d "벅") with
    | Error es -> failwith (String.concat "\n" es)
    | Ok store ->
        let index = Ds2.Runtime.Engine.Core.SimIndex.build store 100
        let callId name =
            store.Calls.Values |> Seq.find (fun c -> c.Name = name) |> fun c -> c.Id
        let pinId = callId "지그.핀상승"
        // Group 화살표를 직접 받지 않은 B·C 도 핀상승을 선행으로 가져야 한다.
        for target in [ "지그.클램프A"; "지그.클램프B"; "지그.클램프C" ] do
            let preds = index.CallStartPreds |> Map.tryFind (callId target) |> Option.defaultValue []
            Assert.Contains(pinId, preds)

[<Fact>]
let ``액추에이터에 짝 동작을 적으면 Source 후보 경고가 사라진다`` () =
    // API 가 하나뿐인 Passive 디바이스는 DONE 더미가 붙어 구조적으로 «Source 후보» 가 된다.
    // 짝(원위치)을 적으면 두 Work 가 순환으로 이어져 DONE 이 생기지 않는다.
    let build (detail: string) (extraApi: string list) =
        String.concat "\n" (
            [ "Kind,Name,Type,Detail,Time,InTag,OutTag"
              "SYS,셀,Active,,,,"
              "FLOW,차체,,,,,"
              "API,로봇.타점,Normal/Normal,,20S,," ]
            @ extraApi
            @ [ $"WORK,차체.용접,Source,{detail},,,"
                "WORK,차체.완료,Sink,로봇.타점,,,"
                "ARROW,차체.용접,Start,차체.완료,,,"
                "ARROW,차체.완료,Reset,차체.용접,,,"
                "ARROW,차체.용접,Reset,차체.완료,,," ])
    let sourceCandidates csv =
        match CsvImporter.parseAiContent csv |> Result.bind (fun d -> CsvImporter.loadAiProject d "셀") with
        | Error es -> failwith (String.concat "\n" es)
        | Ok store ->
            Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates
                (Ds2.Runtime.Engine.Core.SimIndex.build store 100)
            |> List.length
    // 단일 API — 구조적 경고가 난다.
    Assert.True(sourceCandidates (build "로봇.타점" []) > 0)
    // 짝을 적으면 사라진다.
    Assert.Equal(0, sourceCandidates (build "로봇.타점 > 로봇.원위치" [ "API,로봇.원위치,Normal/Normal,,3S,," ]))

[<Fact>]
let ``Group 묶음은 전달자 하나만 남기고 나머지가 Ignore 여야 한다`` () =
    // 그룹 N개 중 비Ignore 가 2개 이상이면 findGroupWorksWithoutIgnore 가 잡는다.
    let build (secondRole: string) =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,셀,Active,,,,"
            "FLOW,부품,,,,,"
            "API,RT1.진입,Normal/Normal,,2S,,"
            "API,RT1.복귀,Normal/Normal,,2S,,"
            "API,RT3.진입,Normal/Normal,,2S,,"
            "API,RT3.복귀,Normal/Normal,,2S,,"
            "API,지그.안착,Normal/Normal,,1S,,"
            "API,지그.해제,Normal/Normal,,1S,,"
            "WORK,부품.1호기진입,Source,RT1.진입 > RT1.복귀,,,"
            $"WORK,부품.3호기진입,{secondRole},RT3.진입 > RT3.복귀,,,"
            "WORK,부품.안착,Sink,지그.안착 > 지그.해제,,,"
            "ARROW,부품.1호기진입,Group,부품.3호기진입,,,"
            "ARROW,부품.1호기진입,Start,부품.안착,,,"
            "ARROW,부품.안착,Reset,부품.1호기진입;부품.3호기진입,,,"
            "ARROW,부품.1호기진입,Reset,부품.안착,,,"
        ]
    let groupWarnings csv =
        match CsvImporter.parseAiContent csv |> Result.bind (fun d -> CsvImporter.loadAiProject d "셀") with
        | Error es -> failwith (String.concat "\n" es)
        | Ok store ->
            Ds2.Runtime.Engine.Core.GraphValidator.findGroupWorksWithoutIgnore
                (Ds2.Runtime.Engine.Core.SimIndex.build store 100)
            |> List.length
    // 둘 다 비Ignore — 경고.
    Assert.True(groupWarnings (build "") > 0)
    // 전달자(1호기)만 남기고 3호기를 Ignore — 경고 없음.
    Assert.Equal(0, groupWarnings (build "Ignore"))

[<Fact>]
let ``선행이 없는 Call 들을 Group 으로 묶어도 선행은 생기지 않는다`` () =
    // Group 은 «선행을 옮기는» 장치다. 옮길 선행이 없으면 아무 일도 하지 않는다.
    // 12회차 테스트(선행이 있을 때 확산)의 반대쪽을 고정한다.
    let csv =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,셀,Active,,,,"
            "FLOW,부품,,,,,"
            "API,클램프.A,Normal/Normal,,600MS,,"
            "API,클램프.B,Normal/Normal,,600MS,,"
            "API,클램프.C,Normal/Normal,,600MS,,"
            "API,클램프.해제,Normal/Normal,,600MS,,"
            "WORK,부품.물림,Source,클램프.A ; 클램프.B ; 클램프.C,,,"
            "WORK,부품.풀림,Sink,클램프.해제,,,"
            "ARROW,부품.물림,Start,부품.풀림,,,"
            "ARROW,부품.풀림,Reset,부품.물림,,,"
            "ARROW,부품.물림,Reset,부품.풀림,,,"
            "ARROW,부품.물림.클램프.A,Group,부품.물림.클램프.B,,,"
            "ARROW,부품.물림.클램프.A,Group,부품.물림.클램프.C,,,"
        ]
    match CsvImporter.parseAiContent csv |> Result.bind (fun d -> CsvImporter.loadAiProject d "셀") with
    | Error es -> failwith (String.concat "\n" es)
    | Ok store ->
        let index = Ds2.Runtime.Engine.Core.SimIndex.build store 100
        for target in [ "클램프.A"; "클램프.B"; "클램프.C" ] do
            let id = store.Calls.Values |> Seq.find (fun c -> c.Name = target) |> fun c -> c.Id
            let preds = index.CallStartPreds |> Map.tryFind id |> Option.defaultValue []
            Assert.Empty(preds)

[<Fact>]
let ``작업서의 대기는 Call 없는 Work 의 Duration 으로 옮긴다`` () =
    // FLR F131 의 「작업대기 22초」처럼 병렬 로봇을 기다리는 시간이 작업서에 번호 붙어 있다.
    let csv =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,셀,Active,,,,"
            "FLOW,부품,,,,,"
            "API,로봇.취출,Normal/Normal,,15S,,"
            "API,로봇.원위치,Normal/Normal,,3S,,"
            "WORK,부품.취출,Source,로봇.취출 > 로봇.원위치,,,"
            "WORK,부품.작업대기,,,22S,,"
            "WORK,부품.완료,Sink,로봇.취출,,,"
            "ARROW,부품.취출,Start,부품.작업대기,,,"
            "ARROW,부품.작업대기,Start,부품.완료,,,"
            "ARROW,부품.완료,Reset,부품.취출;부품.작업대기,,,"
            "ARROW,부품.취출,Reset,부품.완료,,,"
        ]
    match CsvImporter.parseAiContent csv with
    | Error es -> failwith (String.concat "\n" es)
    | Ok doc ->
        let wait = doc.Works |> List.find (fun w -> w.WorkName = "작업대기")
        Assert.Empty(wait.Nodes)
        Assert.Equal(Some (System.TimeSpan.FromSeconds 22.0), wait.Duration)
        // 적재까지 통과해야 한다.
        match CsvImporter.loadAiProject doc "셀" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok store -> Assert.Contains(store.Works.Values, fun w -> w.LocalName = "작업대기")

[<Fact>]
let ``협동 작업은 Group 과 Call AutoAux 로 적는다`` () =
    // DS2 디자인 패턴 Ver110 §3.1. 고정 Work 전체 완료를 용접 조건으로 쓰면
    // 후진까지 기다려 서로 멈춘다 — «전진 완료» 라는 더 작은 사실을 조건으로 삼는다.
    let csv =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,셀,Active,,,,"
            "FLOW,패널,,,,,"
            "API,지그.전진,Normal/Normal,,611MS,,"
            "API,지그.후진,Normal/Normal,,602MS,,"
            "API,건.타점,Normal/Normal,,49400MS,,"
            "API,건.원위치,Normal/Normal,,3S,,"
            "API,반송.투입,Normal/Normal,,5S,,"
            "API,반송.배출,Normal/Normal,,5S,,"
            "WORK,패널.반입,Source,반송.투입 > 반송.배출,,,"
            "WORK,패널.고정,,지그.전진 > 지그.후진,,,"
            "WORK,패널.용접,Ignore,건.타점 > 건.원위치,,,"
            "WORK,패널.반출,Sink,지그.전진,,,"
            "ARROW,패널.반입,Start,패널.고정,,,"
            // Group 동반자는 «전달자의 선행» 을 물려받는다. 전달자가 Source 면 물려받을
            // 선행이 없어 동반자가 Source 후보로 잡힌다 — 앞에 선행 Work 가 있어야 한다.
            "ARROW,패널.고정,Group,패널.용접,,,"
            "ARROW,패널.고정,Start,패널.반출,,,"
            "ARROW,패널.반출,Reset,패널.반입;패널.고정;패널.용접,,,"
            "ARROW,패널.반입,Reset,패널.반출,,,"
            "COND,패널.용접.건.타점,AutoAux,지그.전진,,,"
            "COND,패널.고정.지그.후진,AutoAux,건.타점,,,"
        ]
    match CsvImporter.parseAiContent csv |> Result.bind (fun d -> CsvImporter.loadAiProject d "셀") with
    | Error es -> failwith (String.concat "\n" es)
    | Ok store ->
        // 두 AutoAux 가 서로 다른 Call 에 붙어야 한다 — 한쪽에 몰리면 고리가 끊기지 않는다.
        let owners =
            store.Calls.Values
            |> Seq.filter (fun c -> c.Conditions |> Seq.exists (fun d -> d.Type = Some ConditionType.AutoAux))
            |> Seq.map (fun c -> c.Name) |> Seq.sort |> Seq.toList
        Assert.Equal<string list>([ "건.타점"; "지그.후진" ], owners)
        // Group 은 전달자 하나만 남아야 하고, 그래프 검증이 조용해야 한다.
        let index = Ds2.Runtime.Engine.Core.SimIndex.build store 100
        Assert.Empty(Ds2.Runtime.Engine.Core.GraphValidator.findGroupWorksWithoutIgnore index)
        Assert.Empty(Ds2.Runtime.Engine.Core.GraphValidator.findSourceCandidates index)

[<Fact>]
let ``공유 자원은 제품 수락과 반납을 따로 건다`` () =
    // Ver110 §3.4. 지그가 비었다고 로봇을 쓸 수 있는 것이 아니다 —
    // 앞 라인이 반납(원위치) 중일 수 있다. 그래서 «상대 원위치» 를 ComAux 로 건다.
    let csv =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,공유셀,Active,,,,"
            "FLOW,A라인,,,,,"
            "FLOW,B라인,,,,,"
            "API,지그A.전진,Normal/Normal,,611MS,,"
            "API,지그A.후진,Normal/Normal,,602MS,,"
            "API,지그B.전진,Normal/Normal,,611MS,,"
            "API,지그B.후진,Normal/Normal,,602MS,,"
            // 제품마다 따로 작업하므로 API 를 나눈다 — 하나를 둘이 부르면 한 번 돈 것을 둘이 읽는다.
            "API,로봇.A작업,Normal/Normal,,22S,,"
            "API,로봇.A원위치,Normal/Normal,,3S,,"
            "API,로봇.B작업,Normal/Normal,,22S,,"
            "API,로봇.B원위치,Normal/Normal,,3S,,"
            "WORK,A라인.고정,Source,지그A.전진 > 지그A.후진,,,"
            "WORK,A라인.로봇작업,Ignore,로봇.A작업 > 로봇.A원위치,,,"
            "WORK,A라인.반출,Sink,지그A.전진,,,"
            "WORK,B라인.고정,Source,지그B.전진 > 지그B.후진,,,"
            "WORK,B라인.로봇작업,Ignore,로봇.B작업 > 로봇.B원위치,,,"
            "WORK,B라인.반출,Sink,지그B.전진,,,"
            "ARROW,A라인.고정,Group,A라인.로봇작업,,,"
            "ARROW,A라인.고정,Start,A라인.반출,,,"
            "ARROW,A라인.반출,Reset,A라인.고정;A라인.로봇작업,,,"
            "ARROW,A라인.고정,Reset,A라인.반출,,,"
            "ARROW,B라인.고정,Group,B라인.로봇작업,,,"
            "ARROW,B라인.고정,Start,B라인.반출,,,"
            "ARROW,B라인.반출,Reset,B라인.고정;B라인.로봇작업,,,"
            "ARROW,B라인.고정,Reset,B라인.반출,,,"
            "COND,A라인.로봇작업.로봇.A작업,ComAux,로봇.B원위치,,,"
            "COND,B라인.로봇작업.로봇.B작업,ComAux,로봇.A원위치,,,"
        ]
    match CsvImporter.parseAiContent csv with
    | Error es -> failwith (String.concat "\n" es)
    | Ok doc ->
        // Flow 2개 = Capa 2. 스테이션 수가 아니다.
        Assert.Equal(2, (CsvImporter.previewAi doc).Capa)
        match CsvImporter.loadAiProject doc "공유셀" with
        | Error es -> failwith (String.concat "\n" es)
        | Ok store ->
            // 반납 조건이 각 라인의 작업 Call 에 하나씩 붙어야 한다.
            let owners =
                store.Calls.Values
                |> Seq.filter (fun c -> c.Conditions |> Seq.exists (fun d -> d.Type = Some ConditionType.ComAux))
                |> Seq.map (fun c -> c.Name) |> Seq.sort |> Seq.toList
            Assert.Equal<string list>([ "로봇.A작업"; "로봇.B작업" ], owners)

[<Fact>]
let ``IO 를 물음표로 적으면 AI-W3 대신 AI-W11 로 묶인다`` () =
    // 빈 칸은 «잊었다», `?` 는 «알고 비웠다» — 뜻이 다르므로 경고도 달라야 한다.
    let build (tagCell: string) =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,셀,Active,,,,"
            "FLOW,부품,,,,,"
            $"API,지그.전진,Normal/Normal,,1S,{tagCell},{tagCell}"
            $"API,지그.후진,Normal/Normal,,1S,{tagCell},{tagCell}"
            "WORK,부품.고정,Source,지그.전진 > 지그.후진,,,"
            "WORK,부품.해제,Sink,지그.전진,,,"
            "ARROW,부품.고정,Start,부품.해제,,,"
            "ARROW,부품.해제,Reset,부품.고정,,,"
            "ARROW,부품.고정,Reset,부품.해제,,,"
        ]
    let warningsOf csv =
        match CsvImporter.parseAiContent csv with
        | Error es -> failwith (String.concat "\n" es)
        | Ok doc -> doc.Warnings
    // 빈 칸 — API 마다 AI-W3 가 난다.
    let blank = warningsOf (build "")
    Assert.Equal(2, blank |> List.filter (fun w -> w.Contains "AI-W3") |> List.length)
    Assert.DoesNotContain(blank, fun (w: string) -> w.Contains "AI-W11")
    // `?` — AI-W3 는 사라지고 AI-W11 한 줄로 묶인다.
    let unknown = warningsOf (build "?")
    Assert.DoesNotContain(unknown, fun (w: string) -> w.Contains "AI-W3")
    Assert.Equal(1, unknown |> List.filter (fun w -> w.Contains "AI-W11") |> List.length)

[<Fact>]
let ``같은 디바이스를 Flow 마다 다르게 다루면 AI-W12 로 알린다`` () =
    // 차종 A 는 로봇을 원위치시키고 B 는 그냥 두는 모델 — 경고도 오류도 없이 만들어졌었다.
    let build (bDetail: string) =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,라인,Active,,,,"
            "FLOW,차체A,,,,,"
            "FLOW,차체B,,,,,"
            "API,로봇.타점,Normal/Normal,,16S,,"
            "API,로봇.원위치,Normal/Normal,,3S,,"
            "WORK,차체A.용접,Source,로봇.타점 > 로봇.원위치,,,"
            "WORK,차체A.배출,Sink,로봇.타점,,,"
            $"WORK,차체B.용접,Source,{bDetail},,,"
            "WORK,차체B.배출,Sink,로봇.타점,,,"
            "ARROW,차체A.용접,Start,차체A.배출,,,"
            "ARROW,차체A.배출,Reset,차체A.용접,,,"
            "ARROW,차체A.용접,Reset,차체A.배출,,,"
            "ARROW,차체B.용접,Start,차체B.배출,,,"
            "ARROW,차체B.배출,Reset,차체B.용접,,,"
            "ARROW,차체B.용접,Reset,차체B.배출,,,"
        ]
    let w12 csv =
        match CsvImporter.parseAiContent csv with
        | Error es -> failwith (String.concat "\n" es)
        | Ok doc -> doc.Warnings |> List.filter (fun w -> w.Contains "AI-W12")
    // B 가 원위치를 부르지 않는다 — 비대칭.
    Assert.NotEmpty(w12 (build "로봇.타점"))
    // 양쪽을 같게 다루면 조용하다.
    Assert.Empty(w12 (build "로봇.타점 > 로봇.원위치"))

[<Fact>]
let ``같은 IO 주소를 두 API 가 쓰면 AI-W13 으로 알린다`` () =
    // 입력 비트 하나가 여러 센서일 수 없다. 실제로 지그 API 16개가 주소 2개를
    // 공유하는 모델이 만들어졌고, 문법도 연결도 멀쩡해 그래프 검증까지 통과했다.
    let build (secondAddr: string) =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,셀,Active,,,,"
            "FLOW,부품,,,,,"
            "API,핀.ADV,Normal/Normal,,1S,%IX3.30.0.01,%QX3.31.0.01"
            "API,클램프.ADV,Normal/Normal,,1S," + secondAddr + ",%QX3.31.0.02"
            "API,핀.RET,Normal/Normal,,1S,%IX3.30.1.01,%QX3.31.1.01"
            "WORK,부품.고정,Source,핀.ADV > 클램프.ADV > 핀.RET,,,"
            "WORK,부품.해제,Sink,핀.ADV,,,"
            "ARROW,부품.고정,Start,부품.해제,,,"
            "ARROW,부품.해제,Reset,부품.고정,,,"
            "ARROW,부품.고정,Reset,부품.해제,,,"
        ]
    let w13 csv =
        match CsvImporter.parseAiContent csv with
        | Error es -> failwith (String.concat "\n" es)
        | Ok doc -> doc.Warnings |> List.filter (fun w -> w.Contains "AI-W13")
    // 핀과 클램프가 같은 입력을 읽는다 — 물리적으로 불가능하다.
    let dup = w13 (build "%IX3.30.0.01")
    Assert.Single(dup) |> ignore
    Assert.Contains("%IX3.30.0.01", List.head dup)
    // 주소를 나누면 조용하다.
    Assert.Empty(w13 (build "%IX3.30.0.02"))

[<Fact>]
let ``OutTag 에 입력 영역 주소를 적으면 AI-W14 로 알린다`` () =
    // 실측에서 숫자만 가져오고 영역 문자를 바꿔 적는 실수 — %I 는 PLC 가 읽는 비트다.
    let build (outAddr: string) =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,셀,Active,,,,"
            "FLOW,부품,,,,,"
            "API,클램프.ADV,Normal/Normal,,611MS,%MX1.2051.03," + outAddr
            "API,클램프.RET,Normal/Normal,,602MS,%MX1.2051.04,%QX5.43.0.05"
            "WORK,부품.고정,Source,클램프.ADV > 클램프.RET,,,"
            "WORK,부품.해제,Sink,클램프.ADV,,,"
            "ARROW,부품.고정,Start,부품.해제,,,"
            "ARROW,부품.해제,Reset,부품.고정,,,"
            "ARROW,부품.고정,Reset,부품.해제,,,"
        ]
    let w14 csv =
        match CsvImporter.parseAiContent csv with
        | Error es -> failwith (String.concat "\n" es)
        | Ok doc -> doc.Warnings |> List.filter (fun w -> w.Contains "AI-W14")
    Assert.Single(w14 (build "%IX5.43.0.04")) |> ignore
    // 메모리 판정 비트를 InTag 로 읽는 것은 정상이다 — 경고 대상이 아니다.
    Assert.Empty(w14 (build "%QX5.43.0.04"))

[<Fact>]
let ``API 행 IO 의 기대값은 ApiCall 의 Input OutputSpec 으로 실린다`` () =
    // 엔진이 이것을 쓴다 — OutputSpec 은 «내보낼 값», InputSpec 은 «받은 값이 활성인가».
    // 31회차 전에는 매퍼가 AiTagSpec.Expected 를 버려서 주소만 실렸다.
    let csv =
        String.concat "\n" [
            "Kind,Name,Type,Detail,Time,InTag,OutTag"
            "SYS,셀,Active,,,,"
            "FLOW,부품,,,,,"
            "API,감시.위치,Virtual/Latch(30),,100MS,현재@%IW6.21.2:INT=1200,"
            "API,축.목표,Normal/Normal,,100MS,?,목표@%QW6.21.9:INT=1200"
            "WORK,부품.이송,Source,축.목표 ; 감시.위치,,,"
            "WORK,부품.완료,Sink,축.목표,,,"
            "ARROW,부품.이송,Start,부품.완료,,,"
            "ARROW,부품.완료,Reset,부품.이송,,,"
            "ARROW,부품.이송,Reset,부품.완료,,,"
        ]
    match CsvImporter.parseAiContent csv |> Result.bind (fun d -> CsvImporter.loadAiProject d "셀") with
    | Error es -> failwith (String.concat "\n" es)
    | Ok store ->
        let specs =
            store.ApiCalls.Values
            |> Seq.collect (fun ac -> [ ac.InputSpec; ac.OutputSpec ])
            |> Seq.filter (fun v -> v <> ValueSpec.UndefinedValue)
            |> Seq.toList
        // 워드 기대값이 Int64 1200 으로 실려야 한다 (같은 API 를 여러 Work 가 부르면 ApiCall 도 늘어난다).
        Assert.NotEmpty(specs)
        Assert.All(specs, fun v -> Assert.Equal(ValueSpec.Int64Value(Single 1200L), v))
        // 읽기(InputSpec)와 쓰기(OutputSpec) 양쪽에 하나씩은 실려야 한다.
        let ins = store.ApiCalls.Values |> Seq.filter (fun a -> a.InputSpec <> ValueSpec.UndefinedValue) |> Seq.length
        let outs = store.ApiCalls.Values |> Seq.filter (fun a -> a.OutputSpec <> ValueSpec.UndefinedValue) |> Seq.length
        Assert.True(ins >= 1 && outs >= 1, $"in={ins} out={outs}")
