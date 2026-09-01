module Ds2.Store.Editor.Tests.BasicCsvTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.CSV

// ds2-basic-csv/v1 (3열 FLOW,WORK,CALL) 파서/매퍼 테스트.
// 계약: '>'=Call Start 엣지, ';'=경로 구분(합집합 DAG), 행 순서=Work StartReset 체인(Flow 경계 무시),
//       별칭 문법 없음(= 등장 시 CALL001), fail-fast 오류 코드 CSV001~006/CALL001~002/DAG001~002.

let private csv (rows: string list) = String.concat "\n" ("FLOW,WORK,CALL" :: rows)

let private drillingCsv =
    csv [
        "투입,리프트작업,리프트.상승>리프트.투입위치정지>리프트.하강"
        "투입,컨베이어작업,스토퍼.해제>컨베이어.이송시작>위치센서.감지대기>컨베이어.이송정지"
        "가공,고정작업,클램프.전진>클램프.고정확인"
        "가공,드릴링작업,드릴.회전시작>드릴축.하강>드릴축.상승>드릴.회전정지"
        "검사,밀착작업,측정헤드.하강>측정헤드.밀착확인"
        "검사,측정작업,측정기.측정시작>측정기.결과판정>측정헤드.상승"
        "반출,로봇추출,로봇.제품파지>로봇.반출위치이동>로봇.제품해제>로봇.원점복귀"
    ]

let private parseOk content =
    match BasicCsvParser.parse content with
    | Ok document -> document
    | Error errors -> failwith (errors |> List.map ParseError.toString |> String.concat "\n")

let private parseErrors content =
    match BasicCsvParser.parse content with
    | Ok _ -> failwith "오류를 기대했지만 파싱에 성공했습니다."
    | Error errors -> errors |> List.map (fun e -> e.Message)

let private hasCode (code: string) (messages: string list) =
    messages |> List.exists (fun m -> m.StartsWith(code + ":"))

module ParserTests =

    [<Fact>]
    let ``드릴링 예제 7개 Work 파싱`` () =
        let doc = parseOk drillingCsv
        Assert.Equal(7, doc.Works.Length)
        // 스토퍼(해제)·위치센서(감지대기)가 단일 API → DEV001 경고 1건
        Assert.Single(doc.Warnings) |> ignore
        Assert.StartsWith("DEV001:", doc.Warnings.Head)
        Assert.Contains("스토퍼", doc.Warnings.Head)
        Assert.Contains("위치센서", doc.Warnings.Head)
        let lift = doc.Works.Head
        Assert.Equal(3, lift.Nodes.Length)
        Assert.Equal(2, lift.Edges.Length)
        let conveyor = doc.Works |> List.item 1
        Assert.Equal(4, conveyor.Nodes.Length)
        Assert.Equal(3, conveyor.Edges.Length)

    [<Fact>]
    let ``분기 합류 경로 합집합 DAG`` () =
        let doc = parseOk (csv [ "투입,분기작업,컨베이어.시작>센서A.감지>컨베이어.정지;컨베이어.시작>센서B.감지>컨베이어.정지" ])
        let work = doc.Works.Head
        Assert.Equal(4, work.Nodes.Length)   // 시작/센서A/정지/센서B 병합
        Assert.Equal(4, work.Edges.Length)   // 시작→A, A→정지, 시작→B, B→정지

    [<Fact>]
    let ``별칭 문법은 거부된다`` () =
        Assert.True(parseErrors (csv [ "투입,분기작업,s=컨베이어.시작>센서A.감지>컨베이어.정지" ]) |> hasCode "CALL001")
        Assert.True(parseErrors (csv [ "투입,분기작업,컨베이어.시작>e=컨베이어.정지" ]) |> hasCode "CALL001")

    [<Fact>]
    let ``DEV001 단일 API 디바이스 경고`` () =
        let doc = parseOk (csv [ "투입,작업,실린더.전진>센서.감지" ])
        let dev001 = doc.Warnings |> List.filter (fun w -> w.StartsWith "DEV001:")
        Assert.Single(dev001) |> ignore
        Assert.Contains("실린더(전진)", dev001.Head)
        Assert.Contains("센서(감지)", dev001.Head)
        // 상보 동작 쌍이 갖춰지면 경고 없음
        let paired = parseOk (csv [ "투입,작업,실린더.전진>실린더.후진" ])
        Assert.Empty(paired.Warnings |> List.filter (fun w -> w.StartsWith "DEV001:"))

    [<Fact>]
    let ``동일 Call 재실행 시도는 병합되어 순환으로 거부`` () =
        Assert.True(parseErrors (csv [ "가공,반복작업,모터.운전>온도센서.확인>모터.운전" ]) |> hasCode "DAG002")

    [<Fact>]
    let ``중복 엣지는 제거`` () =
        let doc = parseOk (csv [ "투입,작업,A장치.동작>B장치.동작;A장치.동작>B장치.동작" ])
        Assert.Equal(1, doc.Works.Head.Edges.Length)

    [<Fact>]
    let ``전각 구분자 폴딩 후 파싱 성공`` () =
        // ＞(U+FF1E) ；(U+FF1B) ，(U+FF0C) 전각 입력
        let content = "FLOW，WORK，CALL\n투입，작업，A장치．동작＞B장치．동작；C장치．동작"
        let doc = parseOk content
        Assert.Equal(3, doc.Works.Head.Nodes.Length)
        Assert.Equal(1, doc.Works.Head.Edges.Length)

    [<Fact>]
    let ``CSV005 비연속 FLOW 경고`` () =
        let doc = parseOk (csv [
            "투입,작업1,A장치.동작"
            "가공,작업2,B장치.동작"
            "투입,작업3,C장치.동작" ])
        let csv005 = doc.Warnings |> List.filter (fun w -> w.StartsWith "CSV005:")
        Assert.Single(csv005) |> ignore

    [<Fact>]
    let ``오류 코드 전수`` () =
        Assert.True(parseErrors "flow,work\n투입,작업" |> hasCode "CSV001")
        Assert.True(parseErrors (csv [ "투입,작업" ]) |> hasCode "CSV002")
        Assert.True(parseErrors (csv [ "투입,,A장치.동작" ]) |> hasCode "CSV003")
        Assert.True(parseErrors (csv [ "투입,작업,A장치.동작"; "투입,작업,B장치.동작" ]) |> hasCode "CSV004")
        Assert.True(parseErrors (csv [ "@투입,작업,A장치.동작" ]) |> hasCode "CSV006")
        Assert.True(parseErrors (csv [ "투입,작업,점없는이름" ]) |> hasCode "CALL001")
        Assert.True(parseErrors (csv [ "투입,작업,A장치.동작>x" ]) |> hasCode "CALL001")
        Assert.True(parseErrors (csv [ "투입,작업,c1=모터.운전>센서.확인>c2=모터.운전" ]) |> hasCode "CALL001")
        Assert.True(parseErrors (csv [ "투입,작업,A장치.동작>A장치.동작" ]) |> hasCode "DAG001")
        Assert.True(parseErrors (csv [ "투입,작업,A장치.동작>B장치.동작;B장치.동작>A장치.동작" ]) |> hasCode "DAG002")

    [<Fact>]
    let ``예약어 디바이스와 액션 거부`` () =
        Assert.True(parseErrors (csv [ "투입,작업,BUFFER.동작" ]) |> hasCode "CALL001")
        Assert.True(parseErrors (csv [ "투입,작업,A장치.DO" ]) |> hasCode "CALL001")

module MapperTests =

    let private loadOk content projectName systemName =
        match CsvImporter.parseBasicContent content with
        | Error errors -> failwith (String.concat "\n" errors)
        | Ok document ->
            match CsvImporter.loadBasicProject document projectName systemName with
            | Error errors -> failwith (String.concat "\n" errors)
            | Ok store -> store

    [<Fact>]
    let ``buildBasicSystemImportPlan 은 store 를 변경하지 않는다`` () =
        let store = DsStore()
        let projectId = store.AddProject("Project")
        let systemId = store.AddSystem("System", projectId, true)
        let before = store.Flows.Count, store.Works.Count, store.Calls.Count, store.ArrowWorks.Count
        let document = parseOk drillingCsv
        match CsvImporter.buildBasicSystemImportPlan store document systemId with
        | Error errors -> Assert.Fail(String.concat "\n" errors)
        | Ok plan ->
            Assert.NotEmpty(plan.Operations)
            Assert.Equal(before, (store.Flows.Count, store.Works.Count, store.Calls.Count, store.ArrowWorks.Count))

    [<Fact>]
    let ``드릴링 예제 전체 변환 — Work StartReset 체인과 Call Start 엣지`` () =
        let store = loadOk drillingCsv "드릴링라인" "드릴링공정"
        let project = store.Projects.Values |> Seq.head
        let activeSystemId = project.ActiveSystemIds |> Seq.head

        // Active Flow 4개 (투입/가공/검사/반출)
        let activeFlows =
            store.Flows.Values |> Seq.filter (fun f -> f.ParentId = activeSystemId) |> Seq.toList
        Assert.Equal(4, activeFlows.Length)

        // Active Work 7개
        let activeFlowIds = activeFlows |> List.map (fun f -> f.Id) |> Set.ofList
        let activeWorks =
            store.Works.Values |> Seq.filter (fun w -> activeFlowIds.Contains w.ParentId) |> Seq.toList
        Assert.Equal(7, activeWorks.Length)

        // Work StartReset 체인 6개 (Flow 경계 무시, ParentId=activeSystemId)
        let chainArrows =
            store.ArrowWorks.Values
            |> Seq.filter (fun a -> a.ParentId = activeSystemId && a.ArrowType = ArrowType.StartReset)
            |> Seq.toList
        Assert.Equal(6, chainArrows.Length)

        // Call Start 엣지 = 각 Work 노드수-1 합 = 2+3+1+3+1+2+3 = 15
        let callStartArrows =
            store.ArrowCalls.Values
            |> Seq.filter (fun a -> a.ArrowType = ArrowType.Start)
            |> Seq.toList
        Assert.Equal(15, callStartArrows.Length)

        // Passive 디바이스: 리프트/스토퍼/컨베이어/위치센서/클램프/드릴/드릴축/측정헤드/측정기/로봇 = 10
        Assert.Equal(10, project.PassiveSystemIds.Count)

        // ApiDef Tx/Rx 배선 (디바이스 Work 참조)
        let apiDefs = store.ApiDefs.Values |> Seq.toList
        Assert.NotEmpty(apiDefs)
        Assert.All(apiDefs, fun apiDef ->
            Assert.True(apiDef.TxGuid.IsSome && apiDef.RxGuid.IsSome))

    [<Fact>]
    let ``여러 Flow 가 공유하는 디바이스는 단일 Passive 시스템`` () =
        let content = csv [
            "투입,작업1,컨베이어.이송시작>컨베이어.이송정지"
            "반출,작업2,컨베이어.역송시작>컨베이어.역송정지" ]
        let store = loadOk content "P" "S"
        let project = store.Projects.Values |> Seq.head
        Assert.Equal(1, project.PassiveSystemIds.Count)
        // 디바이스 API 4개가 한 시스템에 모임
        let passiveId = project.PassiveSystemIds |> Seq.head
        let defs = store.ApiDefs.Values |> Seq.filter (fun d -> d.ParentId = passiveId) |> Seq.toList
        Assert.Equal(4, defs.Length)

    [<Fact>]
    let ``단일 API 디바이스는 DONE 더미 Work 로 재기동 가능해진다`` () =
        // 센서.감지 = API 1개 device, 실린더.전진/후진 = API 2개 device
        let store = loadOk (csv [ "투입,작업,실린더.전진>센서.감지>실린더.후진" ]) "P" "S"
        let project = store.Projects.Values |> Seq.head
        let passiveIds = project.PassiveSystemIds |> Set.ofSeq

        let sensorSystem =
            store.Systems.Values |> Seq.find (fun s -> passiveIds.Contains s.Id && s.Name = "센서")
        let sensorFlow = store.Flows.Values |> Seq.find (fun f -> f.ParentId = sensorSystem.Id)
        let sensorWorks = store.Works.Values |> Seq.filter (fun w -> w.ParentId = sensorFlow.Id) |> Seq.toList
        let apiWork = sensorWorks |> List.find (fun w -> w.LocalName = "감지")
        let doneWork = sensorWorks |> List.find (fun w -> w.LocalName = "DONE")
        Assert.Equal(2, sensorWorks.Length)

        // API -Start-> DONE + API <-ResetReset-> DONE
        let arrowsOf t =
            store.ArrowWorks.Values
            |> Seq.filter (fun a ->
                a.ParentId = sensorSystem.Id && a.ArrowType = t
                && a.SourceId = apiWork.Id && a.TargetId = doneWork.Id)
            |> Seq.length
        Assert.Equal(1, arrowsOf ArrowType.Start)
        Assert.Equal(1, arrowsOf ArrowType.ResetReset)

        // ApiDef: Tx = API Work, Rx = DONE
        let apiDef = store.ApiDefs.Values |> Seq.find (fun d -> d.ParentId = sensorSystem.Id)
        Assert.Equal(Some apiWork.Id, apiDef.TxGuid)
        Assert.Equal(Some doneWork.Id, apiDef.RxGuid)

        // API 2개 device 는 DONE 없이 기존 pairwise ResetReset 유지
        let cylSystem =
            store.Systems.Values |> Seq.find (fun s -> passiveIds.Contains s.Id && s.Name = "실린더")
        let cylFlow = store.Flows.Values |> Seq.find (fun f -> f.ParentId = cylSystem.Id)
        let cylWorks = store.Works.Values |> Seq.filter (fun w -> w.ParentId = cylFlow.Id) |> Seq.toList
        Assert.Equal(2, cylWorks.Length)
        Assert.DoesNotContain("DONE", cylWorks |> List.map (fun w -> w.LocalName))

    [<Fact>]
    let ``합류 노드는 선행 2개의 Start 엣지를 받는다`` () =
        let store = loadOk (csv [ "투입,분기작업,컨베이어.시작>센서A.감지>컨베이어.정지;컨베이어.시작>센서B.감지>컨베이어.정지" ]) "P" "S"
        let stopCall =
            store.Calls.Values |> Seq.find (fun c -> c.Name = "컨베이어.정지")
        let incoming =
            store.ArrowCalls.Values
            |> Seq.filter (fun a -> a.TargetId = stopCall.Id && a.ArrowType = ArrowType.Start)
            |> Seq.length
        Assert.Equal(2, incoming)
