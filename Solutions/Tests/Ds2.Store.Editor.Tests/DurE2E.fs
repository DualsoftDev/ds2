module Ds2.Store.Editor.Tests.DurE2ETests

open System

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.CSV

let private csvOf (rows: string list) = String.concat "\n" ("FLOW,WORK,CALL" :: rows)

let private load content =
    match CsvImporter.parseBasicContent content with
    | Error e -> failwith (String.concat "\n" e)
    | Ok doc ->
        match CsvImporter.loadBasicProject doc "P" "S" with
        | Error e -> failwith (String.concat "\n" e)
        | Ok store -> doc, store

let private msOf (store: DsStore) (suffix: string) =
    store.Works.Values
    |> Seq.filter (fun w -> w.Name.EndsWith(suffix: string))
    |> Seq.tryPick (fun w -> w.Duration |> Option.map (fun d -> d.TotalMilliseconds))

let private parseErrs content =
    match BasicCsvParser.parse content with
    | Ok _ -> failwith "오류를 기대했습니다."
    | Error e -> e |> List.map (fun x -> x.Message)

// ── A. 단일 API 디바이스 — ApiDef.Rx 가 DONE 으로 재지정되는 경로 ────────────
[<Fact>]
let ``단일 API 디바이스도 API Work 에 시간이 들어간다`` () =
    let _, store = load (csvOf [ "F,W,런너.START=3000MS>실린더.전진=100MS>실린더.후진=100MS" ])
    Assert.Equal(Some 3000.0, msOf store "런너_Flow.START")
    // DONE 더미에는 시간을 주지 않는다(부여 시 완료·재기동 지연).
    Assert.Equal(None, msOf store "런너_Flow.DONE")

// 단일 API 디바이스는 Rx 가 DONE 더미로 재지정되고 DONE 에는 Duration 이 없다.
// callDeviceDurationMs 의 Tx 폴백이 없으면 런너의 3000ms 가 critical path 에서 누락된다.
[<Fact>]
let ``단일 API 디바이스도 critical path 에 잡힌다`` () =
    let _, store = load (csvOf [ "F,W,런너.START=3000MS>실린더.전진=100MS>실린더.후진=100MS" ])
    let workId = store.Works.Values |> Seq.find (fun w -> w.Name = "F.W") |> fun w -> w.Id
    // 3000(런너) + 100(전진) + 100(후진) = 3200
    Assert.Equal(Some 3200, Queries.tryGetDeviceDurationMs workId store)
    Assert.Equal(Some 3000.0, msOf store "런너_Flow.START")

[<Fact>]
let ``2 API 디바이스만 있으면 폴백이 동작에 영향을 주지 않는다`` () =
    let _, store = load (csvOf [ "F,W,실린더.전진=100MS>실린더.후진=100MS" ])
    let workId = store.Works.Values |> Seq.find (fun w -> w.Name = "F.W") |> fun w -> w.Id
    Assert.Equal(Some 200, Queries.tryGetDeviceDurationMs workId store)

// ── B~C. '=' 경계 케이스 메시지 품질 ─────────────────────────────────────────
[<Fact>]
let ``값이 비면 시간 오류로 안내한다`` () =
    let m = parseErrs (csvOf [ "F,W,실린더.전진=" ])
    Assert.True(m |> List.exists (fun x -> x.StartsWith "DUR001:"), String.concat " | " m)

[<Fact>]
let ``이름이 비면 Call 형식 오류로 안내한다`` () =
    let m = parseErrs (csvOf [ "F,W,=1000MS" ])
    Assert.True(m |> List.exists (fun x -> x.StartsWith "CALL001:"), String.concat " | " m)

// ── D. 전각 '＝' 정규화 ──────────────────────────────────────────────────────
[<Fact>]
let ``전각 등호도 동작 시간으로 읽는다`` () =
    let doc, _ = load (csvOf [ "F,W,실린더.전진＝1200MS>실린더.후진" ])
    Assert.Equal<((string * string) * System.TimeSpan) list>(
        [ ("실린더", "전진"), System.TimeSpan.FromMilliseconds 1200.0 ], doc.Durations)

// ── E. 한 셀 안 중복 지정 ───────────────────────────────────────────────────
[<Fact>]
let ``같은 셀에서 값이 달라도 경고만 내고 진행한다`` () =
    let doc, store = load (csvOf [ "F,W,실린더.전진=100MS>A.x;실린더.전진=200MS>B.y" ])
    Assert.Contains(doc.Warnings, fun w -> w.StartsWith "DUR002:")
    Assert.Equal(Some 100.0, msOf store "실린더_Flow.전진")

// ── I. 여러 Flow 가 같은 디바이스를 공유 ────────────────────────────────────
[<Fact>]
let ``다른 Flow 에서 같은 디바이스를 써도 시간이 한 번만 적용된다`` () =
    let _, store = load (csvOf [ "F1,W1,실린더.전진=1500MS>실린더.후진=700MS"
                                 "F2,W2,실린더.전진>실린더.후진" ])
    let systems = store.Systems.Values |> Seq.filter (fun s -> s.Name = "실린더") |> Seq.length
    Assert.Equal(1, systems)
    Assert.Equal(Some 1500.0, msOf store "실린더_Flow.전진")
    Assert.Equal(Some 700.0, msOf store "실린더_Flow.후진")

// ── J. TSV(탭) 입력 ─────────────────────────────────────────────────────────
[<Fact>]
let ``탭 구분 입력에서도 동작한다`` () =
    let content = "FLOW\tWORK\tCALL\nF\tW\t실린더.전진=900MS>실린더.후진"
    let _, store = load content
    Assert.Equal(Some 900.0, msOf store "실린더_Flow.전진")

// ── K. 실제 KA4 공법 (실측 duration) ────────────────────────────────────────
[<Fact>]
let ``KA4 공법 CSV 에 실측 시간을 넣어 불러온다`` () =
    let content =
        csvOf [
            "S507RH,투입체결,RH_정렬_SOL_낙하방지.ADV=972MS>RH_정렬_SOL_KA4_LATCH.ADV=980MS>RH_정렬_SOL_낙하방지.RET=965MS>RH_LOW_SOL_INDEX.언락=979MS>RH_UPPER_SOL_KA4_1차클램프.ADV=1009MS>RH_LOW_SOL_INDEX.락=958MS>RH_UPPER_SOL_KA4_H_SLIDE.ADV=972MS>S507_RH_KA4_런너.START=5.037S"
            "S507RH,복귀,RH_UPPER_SOL_KA4_1차클램프.RET=966MS>RH_UPPER_SOL_KA4_H_SLIDE.RET=989MS>RH_정렬_SOL_KA4_LATCH.RET=980MS"
            "S507RBT,로봇핸드셰이크,S507_5_RT.정렬_취출가능=8729MS>S507_5_RT.셋팅_안착가능=8009MS"
        ]
    let doc, store = load content
    Assert.Equal(13, List.length doc.Durations)
    Assert.Equal(Some 5037.0, msOf store "S507_RH_KA4_런너_Flow.START")
    Assert.Equal(Some 1009.0, msOf store "RH_UPPER_SOL_KA4_1차클램프_Flow.ADV")
    Assert.Equal(Some 966.0,  msOf store "RH_UPPER_SOL_KA4_1차클램프_Flow.RET")
    Assert.Equal(Some 8729.0, msOf store "S507_5_RT_Flow.정렬_취출가능")
    Assert.Equal(Some 958.0,  msOf store "RH_LOW_SOL_INDEX_Flow.락")

// ── 문법 경계값 표 ──────────────────────────────────────────────────────────
// 각 입력이 어떤 코드로 처리되는지 한 곳에 고정한다(회귀 감지용).
[<Theory>]
// 정상
[<InlineData("실린더.전진=1000MS", "OK")>]
[<InlineData("실린더.전진=2.5S", "OK")>]
[<InlineData("실린더.전진=0MS", "OK")>]
[<InlineData("실린더.전진 = 1000 ms", "OK")>]
[<InlineData("실린더.전진=1000Ms", "OK")>]
[<InlineData("밸브(대).열림=500MS", "OK")>]          // 괄호는 이름에 허용
[<InlineData("A-B_C.전진=500MS", "OK")>]
// 시간 지정 의도가 명백한데 값이 틀림
[<InlineData("실린더.전진=300", "DUR001")>]           // 단위 누락
[<InlineData("실린더.전진=MS", "DUR001")>]            // 숫자 없음
[<InlineData("실린더.전진=100XS", "DUR001")>]         // 숫자부 이상
[<InlineData("실린더.전진=-5MS", "DUR001")>]          // 음수
[<InlineData("실린더.전진=90000000MS", "DUR001")>]    // 24시간 초과
[<InlineData("실린더.전진=", "DUR001")>]              // 값 없음
[<InlineData("실린더.전진=1.2.3MS", "DUR001")>]       // 소수점 중복
// 시간이 아님 → 기존 별칭 안내 유지
[<InlineData("s=실린더.전진", "CALL001")>]
[<InlineData("실린더.전진=빠름", "CALL001")>]
// 이름 자체가 잘못됨
[<InlineData("실린더전진=100MS", "CALL001")>]         // '.' 없음
[<InlineData("=1000MS", "CALL001")>]                 // 이름 없음
[<InlineData("BUFFER.전진=100MS", "CALL001")>]        // 예약 디바이스
[<InlineData("실린더.DO=100MS", "CALL001")>]          // 예약 액션
let ``문법 경계값이 기대한 코드로 처리된다`` (token: string, expected: string) =
    let content = csvOf [ sprintf "F,W,%s" token ]
    match BasicCsvParser.parse content with
    | Ok _ -> Assert.Equal("OK", expected)
    | Error errs ->
        let msgs = errs |> List.map (fun e -> e.Message)
        Assert.True(
            msgs |> List.exists (fun m -> m.StartsWith(expected + ":")),
            sprintf "'%s' 기대=%s 실제=%s" token expected (String.concat " | " msgs))

// ── duration 이 전혀 없어도 모델이 완성되어야 한다 ──────────────────────────
[<Fact>]
let ``동작 시간이 하나도 없어도 모든 디바이스 Work 가 기본 500ms 로 만들어진다`` () =
    let doc, store =
        load (csvOf [ "투입,리프트작업,리프트.상승>리프트.투입위치정지>리프트.하강"
                      "가공,고정작업,클램프.전진>클램프.고정확인"
                      "가공,드릴작업,드릴.회전시작>드릴축.하강>드릴축.상승>드릴.회전정지" ])
    Assert.Empty(doc.Durations)
    let deviceWorks =
        store.Works.Values
        |> Seq.filter (fun w -> w.Name.Contains "_Flow.")
        |> List.ofSeq
    Assert.NotEmpty(deviceWorks)
    // DONE 더미만 Duration 이 없고(재기동용), 나머지 API Work 는 전부 500ms.
    for w in deviceWorks do
        if w.LocalName = "DONE" then Assert.True(w.Duration.IsNone, w.Name)
        else Assert.Equal(Some 500.0, w.Duration |> Option.map (fun d -> d.TotalMilliseconds))

[<Fact>]
let ``일부만 지정해도 나머지는 기본 500ms 로 채워진다`` () =
    let _, store = load (csvOf [ "F,W,리프트.상승=2S>리프트.하강" ])
    Assert.Equal(Some 2000.0, msOf store "리프트_Flow.상승")
    Assert.Equal(Some 500.0, msOf store "리프트_Flow.하강")

// ── Start / Clear Work 자동 추가 ────────────────────────────────────────────
module StartClearTests =

    let private loadWith auto content =
        match CsvImporter.parseBasicContent content with
        | Error e -> failwith (String.concat "\n" e)
        | Ok doc ->
            match CsvImporter.loadBasicProjectWith auto doc "P" "S" with
            | Error e -> failwith (String.concat "\n" e)
            | Ok store -> store

    /// Active System(=CSV 로 만든 상위 시스템) 소속 Work 만. Passive device Work 는 제외.
    let private activeWorks (store: DsStore) =
        store.Works.Values
        |> Seq.filter (fun w -> not (w.Name.Contains "_Flow."))
        |> List.ofSeq

    let private arrowsBetween (store: DsStore) (srcId: Guid) (dstId: Guid) =
        store.ArrowWorks.Values
        |> Seq.filter (fun a -> a.SourceId = srcId && a.TargetId = dstId)
        |> Seq.map (fun a -> a.ArrowType)
        |> List.ofSeq
        |> List.sort

    let private sample =
        csvOf [ "투입,W1,실린더.전진=500MS>실린더.후진=500MS"
                "가공,W2,드릴.시작=1S>드릴.정지=500MS"
                "반출,W3,로봇.파지=3S>로봇.해제=1S" ]

    [<Fact>]
    let ``끄면 Start Clear 가 생기지 않는다`` () =
        let store = loadWith false sample
        Assert.Equal(3, List.length (activeWorks store))
        Assert.DoesNotContain(activeWorks store, fun w -> w.LocalName = "Start" || w.LocalName = "Clear")

    // Flow = 동시작업 제품 단위이므로 기동/종료도 Flow 별로 하나씩 붙는다.
    [<Fact>]
    let ``켜면 Flow 마다 Start 와 Clear 가 생긴다`` () =
        let store = loadWith true sample
        let works = activeWorks store
        Assert.Equal(9, List.length works)   // (Work 1 + Start + Clear) x 3 Flow
        for flow in [ "투입"; "가공"; "반출" ] do
            let inFlow = works |> List.filter (fun w -> w.FlowPrefix = flow)
            Assert.Single(inFlow |> List.filter (fun w -> w.LocalName = "Start")) |> ignore
            Assert.Single(inFlow |> List.filter (fun w -> w.LocalName = "Clear")) |> ignore

    [<Fact>]
    let ``Start 는 Source Clear 는 Sink 역할이다`` () =
        let works = activeWorks (loadWith true sample)
        for w in works |> List.filter (fun w -> w.LocalName = "Start") do
            Assert.Equal(TokenRole.Source, w.TokenRole)
        for w in works |> List.filter (fun w -> w.LocalName = "Clear") do
            Assert.Equal(TokenRole.Sink, w.TokenRole)

    [<Fact>]
    let ``Start 는 첫 Work 로 StartReset 1줄만 연결된다`` () =
        let store = loadWith true sample
        let works = activeWorks store
        let w1 = works |> List.find (fun w -> w.LocalName = "W1")
        let start = works |> List.find (fun w -> w.LocalName = "Start" && w.FlowPrefix = w1.FlowPrefix)
        Assert.Equal<ArrowType list>([ ArrowType.StartReset ], arrowsBetween store start.Id w1.Id)
        // 역방향 화살표는 없다
        Assert.Empty(arrowsBetween store w1.Id start.Id)

    [<Fact>]
    let ``Clear 는 이전 Work 에서 StartReset + Reset 2줄로 연결된다`` () =
        let store = loadWith true sample
        let works = activeWorks store
        let w3 = works |> List.find (fun w -> w.LocalName = "W3")
        let clear = works |> List.find (fun w -> w.LocalName = "Clear" && w.FlowPrefix = w3.FlowPrefix)
        Assert.Equal<ArrowType list>(
            List.sort [ ArrowType.Reset; ArrowType.StartReset ],
            arrowsBetween store w3.Id clear.Id)

    // sample 은 Flow 3개 x Work 1개라 Flow 를 넘는 체인은 없어야 한다.
    [<Fact>]
    let ``Flow 를 넘는 Work 체인은 만들지 않는다`` () =
        let store = loadWith true sample
        let works = activeWorks store
        let find n = works |> List.find (fun w -> w.LocalName = n)
        Assert.Empty(arrowsBetween store (find "W1").Id (find "W2").Id)
        Assert.Empty(arrowsBetween store (find "W2").Id (find "W3").Id)

    [<Fact>]
    let ``같은 Flow 안에서는 행 순서대로 StartReset 으로 잇는다`` () =
        let store = loadWith false (csvOf [ "F,A,실린더.전진=100MS"; "F,B,실린더.후진=100MS" ])
        let works = activeWorks store
        let find n = works |> List.find (fun w -> w.LocalName = n)
        Assert.Equal<ArrowType list>([ ArrowType.StartReset ], arrowsBetween store (find "A").Id (find "B").Id)

    [<Fact>]
    let ``Work 가 하나여도 Start 와 Clear 가 붙는다`` () =
        let store = loadWith true (csvOf [ "F,W,실린더.전진=500MS>실린더.후진=500MS" ])
        let works = activeWorks store
        Assert.Equal(3, List.length works)
        let w = works |> List.find (fun x -> x.LocalName = "W")
        let s = works |> List.find (fun x -> x.LocalName = "Start")
        let c = works |> List.find (fun x -> x.LocalName = "Clear")
        Assert.Equal<ArrowType list>([ ArrowType.StartReset ], arrowsBetween store s.Id w.Id)
        Assert.Equal<ArrowType list>(
            List.sort [ ArrowType.Reset; ArrowType.StartReset ], arrowsBetween store w.Id c.Id)

    [<Fact>]
    let ``이름이 겹치면 접미사로 고유화한다`` () =
        let store = loadWith true (csvOf [ "F,Start,실린더.전진=500MS"; "F,Clear,실린더.후진=500MS" ])
        let names = activeWorks store |> List.map (fun w -> w.LocalName) |> List.sort
        Assert.Equal<string list>([ "Clear"; "Clear_1"; "Start"; "Start_1" ], names)

// ── 지침에 실린 예제가 실제로 파싱·변환되는지 ────────────────────────────────
// 지침의 예제가 깨져 있으면 LLM 이 그대로 복제한다. 예제를 회귀 대상으로 고정한다.
module PromptExampleTests =

    [<Fact>]
    let ``예시1 단일 Flow 최소 Work 가 변환된다`` () =
        let content =
            csvOf [ "가공라인,가공,리프트.하강=2S>클램프1.전진=800MS>드릴.회전시작=500MS>드릴축.하강=1.5S>드릴축.상승=1.5S>드릴.회전정지=500MS;리프트.하강=2S>클램프2.전진=800MS>드릴.회전시작=500MS"
                    "가공라인,반출,클램프1.후진=800MS>리프트.상승=2S>로봇.제품파지=3S>로봇.반출=5S;클램프2.후진=800MS>리프트.상승=2S" ]
        let doc, store = load content
        Assert.Equal(2, List.length doc.Works)
        let acts = store.Works.Values |> Seq.filter (fun w -> not (w.Name.Contains "_Flow.")) |> List.ofSeq
        Assert.Equal(2, List.length acts)          // Work 2개 (Start/Clear 끔)
        Assert.Equal(1, acts |> List.map (fun w -> w.FlowPrefix) |> List.distinct |> List.length)
        // 리프트가 하강/상승 양쪽에 나오므로 Work 를 나눈 것이 유일한 분할 근거다.
        Assert.Equal(Some 2000.0, msOf store "리프트_Flow.하강")
        Assert.Equal(Some 2000.0, msOf store "리프트_Flow.상승")

    [<Fact>]
    let ``예시2 Flow 2개 Work 각 1개가 변환된다`` () =
        let one (p: string) =
            $"{p}클램프1.전진=800MS>{p}슬라이드.전진=1S>{p}런너1.체결=5S>{p}슬라이드.후진=1S>{p}클램프1.후진=800MS;"
            + $"{p}클램프2.전진=800MS>{p}슬라이드.전진=1S>{p}런너2.체결=5S>{p}슬라이드.후진=1S>{p}클램프2.후진=800MS"
        let lh = one "LH"
        let rh = one "RH"
        let content = csvOf [ $"LH,LH작업,{lh}"; $"RH,RH작업,{rh}" ]
        let doc, store = load content
        Assert.Equal(2, List.length doc.Works)
        let acts = store.Works.Values |> Seq.filter (fun w -> not (w.Name.Contains "_Flow.")) |> List.ofSeq
        Assert.Equal(2, List.length acts)
        Assert.Equal<string list>([ "LH"; "RH" ], acts |> List.map (fun w -> w.FlowPrefix) |> List.sort)
        // Flow 가 독립이므로 Flow 를 넘는 Work 화살표가 없어야 한다.
        let ids = acts |> List.map (fun w -> w.Id) |> Set.ofList
        let cross =
            store.ArrowWorks.Values
            |> Seq.filter (fun a -> ids.Contains a.SourceId && ids.Contains a.TargetId)
            |> Seq.length
        Assert.Equal(0, cross)
