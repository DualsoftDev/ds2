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

// Excel/스프레드시트에서 복사하면 탭 구분(TSV)으로 붙여넣어진다 — 두 모드 모두 자동 인식해야 한다.
module TsvPasteTests =

    let private tsv (rows: string list) = String.concat "\n" rows

    [<Fact>]
    let ``표준 9열 탭 구분 붙여넣기 파싱`` () =
        let content =
            tsv [
                "Flow\tWork\tDevice\tSystem\tApi\tInName\tInAddress\tOutName\tOutAddress"
                "LH\tLOW\tLH_LOW_SOL_INDEX\tLH_LOW_SOL_INDEX\t락\tLH_LOW_RS_INDEX_락\t%IX2.30.0.00\tLH_LOW_SOL_INDEX_락\t%QX2.31.0.00"
                "LH\tLOW\tLH_LOW_SOL_INDEX\tLH_LOW_SOL_INDEX\t언락\tLH_LOW_RS_INDEX_언락\t%IX2.30.0.01\tLH_LOW_SOL_INDEX_언락\t%QX2.31.0.01"
            ]
        match CsvImporter.parseContent content with
        | Error errors -> failwith (String.concat "\n" errors)
        | Ok doc ->
            Assert.Equal(2, doc.Entries.Length)
            let first = doc.Entries.Head
            Assert.Equal("LH", first.FlowName)
            Assert.Equal("LOW", first.WorkName)
            Assert.Equal("LH_LOW_SOL_INDEX", first.DeviceAlias)
            Assert.Equal("락", first.ApiName)
            Assert.Equal<string option>(Some "%IX2.30.0.00", first.InAddress)
            Assert.Equal<string option>(Some "%QX2.31.0.00", first.OutAddress)

    [<Fact>]
    let ``기본 3열 탭 구분 붙여넣기 파싱`` () =
        let doc = parseOk (tsv [ "FLOW\tWORK\tCALL"; "투입\t리프트작업\t리프트.상승>리프트.하강" ])
        Assert.Single(doc.Works) |> ignore
        Assert.Equal(2, doc.Works.Head.Nodes.Length)
        Assert.Equal(1, doc.Works.Head.Edges.Length)

    [<Fact>]
    let ``탭 구분에서 쉼표는 이름의 일부로 보존된다`` () =
        let doc = parseOk (tsv [ "FLOW\tWORK\tCALL"; "투입\t작업A,B\t실린더.전진>실린더.후진" ])
        Assert.Equal("작업A,B", doc.Works.Head.WorkName)

    [<Fact>]
    let ``쉼표 구분은 기존대로 동작한다`` () =
        let doc = parseOk (csv [ "투입,리프트작업,리프트.상승>리프트.하강" ])
        Assert.Single(doc.Works) |> ignore

// 실설비 패턴 — 솔레노이드 1개가 여러 실린더를 구동하고 실린더마다 개별 센서가 달린 경우.
// CSV 의 System 열이 행마다 다르면 Call 1개에 ApiCall N개(각자 자기 Passive System)가 붙어야 한다.
// (AddCall 다이얼로그의 'ApiCall 복제' / 고급 탭 ApiDef 다중선택과 동일한 구조)
module MultiSystemCallTests =

    let private latchCsv =
        String.concat "\n" [
            "Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress"
            "RH,정렬,LATCH,LATCH1,ADV,RS_ADV1,%IX3.60.0.00,SOL_ADV,%QX3.63.0.00"
            "RH,정렬,LATCH,LATCH1,RET,RS_RET1,%IX3.60.0.01,SOL_RET,%QX3.63.0.01"
            "RH,정렬,LATCH,LATCH2,RET,RS_RET2,%IX3.60.0.02,SOL_RET,%QX3.63.0.01"
            "RH,정렬,LATCH,LATCH3,RET,RS_RET3,%IX3.60.0.03,SOL_RET,%QX3.63.0.01"
            "RH,정렬,LATCH,LATCH4,RET,RS_RET4,%IX3.60.0.04,SOL_RET,%QX3.63.0.01"
            "RH,정렬,LATCH,LATCH5,RET,RS_RET5,%IX3.60.0.05,SOL_RET,%QX3.63.0.01"
        ]

    let private expectedSystems = [ "LATCH1"; "LATCH2"; "LATCH3"; "LATCH4"; "LATCH5" ]

    let private loadLatch () =
        match CsvImporter.parseContent latchCsv with
        | Error errors -> failwith (String.concat "\n" errors)
        | Ok doc ->
            match CsvImporter.loadProject doc "P" "RH_ALIGN" with
            | Error errors -> failwith (String.concat "\n" errors)
            | Ok store -> store

    let private worksOfSystem (store: DsStore) (systemName: string) =
        let system = store.Systems.Values |> Seq.find (fun s -> s.Name = systemName)
        let flow = store.Flows.Values |> Seq.find (fun f -> f.ParentId = system.Id)
        store.Works.Values
        |> Seq.filter (fun w -> w.ParentId = flow.Id)
        |> Seq.map (fun w -> w.LocalName)
        |> Seq.sort
        |> List.ofSeq

    [<Fact>]
    let ``System 열이 다르면 Passive System 이 행 수만큼 생성된다`` () =
        let store = loadLatch ()
        let project = store.Projects.Values |> Seq.head
        let names =
            project.PassiveSystemIds
            |> Seq.map (fun id -> store.Systems.[id].Name)
            |> Seq.sort
            |> List.ofSeq
        Assert.Equal<string list>(expectedSystems, names)

    [<Fact>]
    let ``RET Call 의 ApiCall 은 각자 다른 System 의 ApiDef 를 가리킨다`` () =
        let store = loadLatch ()
        let retCall = store.Calls.Values |> Seq.find (fun c -> c.Name = "LATCH.RET")
        Assert.Equal(5, retCall.ApiCalls.Count)

        let systems =
            retCall.ApiCalls
            |> Seq.map (fun ac ->
                let defId = ac.ApiDefId |> Option.get
                store.Systems.[store.ApiDefs.[defId].ParentId].Name)
            |> List.ofSeq
        Assert.Equal<string list>(expectedSystems, systems)

        // 실린더별 개별 센서 주소가 행 순서대로 보존된다
        let inAddresses =
            retCall.ApiCalls
            |> Seq.map (fun ac -> ac.InTag |> Option.map (fun t -> t.Address) |> Option.defaultValue "")
            |> List.ofSeq
        Assert.Equal<string list>(
            [ "%IX3.60.0.01"; "%IX3.60.0.02"; "%IX3.60.0.03"; "%IX3.60.0.04"; "%IX3.60.0.05" ],
            inAddresses)

    [<Fact>]
    let ``RET 행만 있는 LATCH2 도 device 의 전체 API 집합을 갖는다`` () =
        // ADV 행이 없는 것은 '전진 동작이 없다'가 아니라 '전진 센서를 생략했다'는 뜻이다.
        // 따라서 DONE 더미가 아니라 ADV Work 가 채워져 ADV↔RET 상호 리셋이 성립해야 한다.
        let store = loadLatch ()
        Assert.Equal<string list>([ "ADV"; "RET" ], worksOfSystem store "LATCH2")
        Assert.Equal<string list>([ "ADV"; "RET" ], worksOfSystem store "LATCH1")
        Assert.DoesNotContain("DONE", store.Works.Values |> Seq.map (fun w -> w.LocalName))

    [<Fact>]
    let ``ADV Call 은 센서를 생략한 System 까지 ApiCall 을 갖는다`` () =
        // ApiCall 이 없으면 Active 가 그 Passive Work 를 구동할 수 없어 RET 상태로 고착된다(데드락).
        let store = loadLatch ()
        let advCall = store.Calls.Values |> Seq.find (fun c -> c.Name = "LATCH.ADV")
        Assert.Equal(5, advCall.ApiCalls.Count)
        let systems =
            advCall.ApiCalls
            |> Seq.map (fun ac -> store.Systems.[store.ApiDefs.[ac.ApiDefId |> Option.get].ParentId].Name)
            |> Seq.sort
            |> List.ofSeq
        Assert.Equal<string list>(expectedSystems, systems)

    [<Fact>]
    let ``센서 생략 ApiDef 는 SensingType Virtual 이고 출력은 형제에서 상속한다`` () =
        let store = loadLatch ()
        let advCall = store.Calls.Values |> Seq.find (fun c -> c.Name = "LATCH.ADV")
        let findBySystem name =
            let apiCall =
                advCall.ApiCalls
                |> Seq.find (fun ac ->
                    store.Systems.[store.ApiDefs.[ac.ApiDefId |> Option.get].ParentId].Name = name)
            apiCall, store.ApiDefs.[apiCall.ApiDefId |> Option.get]

        // 센서가 있는 LATCH1 — SensingType 기본(Normal) + 실제 입력 주소 유지
        let latch1Call, latch1Def = findBySystem "LATCH1"
        Assert.True(match latch1Def.SensingType with SensingType.Normal _ -> true | _ -> false)
        Assert.Equal<string option>(Some "%IX3.60.0.00", latch1Call.InTag |> Option.map (fun t -> t.Address))

        // 센서를 생략한 LATCH2 — SensingType Virtual, InTag 없음, OutTag 는 공용 솔레노이드 상속
        let latch2Call, latch2Def = findBySystem "LATCH2"
        Assert.True(match latch2Def.SensingType with SensingType.Virtual _ -> true | _ -> false)
        Assert.True(latch2Call.InTag.IsNone)
        Assert.Equal<string option>(Some "%QX3.63.0.00", latch2Call.OutTag |> Option.map (fun t -> t.Address))
        Assert.True(latch2Def.TxGuid.IsSome && latch2Def.RxGuid.IsSome)

    [<Fact>]
    let ``센서 생략 모델도 V1 V2 검증을 통과한다`` () =
        // V1: ActionType≠Virtual ⇒ OutTag 필수(상속으로 충족)
        // V2: SensingType≠Virtual ⇒ InTag 필수(Virtual 이라 면제)
        let store = loadLatch ()
        let issues =
            [ for call in store.Calls.Values do
                for apiCall in call.ApiCalls do
                    match apiCall.ApiDefId |> Option.bind (fun id -> Queries.getApiDef id store) with
                    | Some apiDef ->
                        yield! (V10Validation.validateApiCallV1 apiDef apiCall |> Option.toList)
                        yield! (V10Validation.validateApiCallV2 apiDef apiCall |> Option.toList)
                    | None -> () ]
        Assert.Empty(issues)

    [<Fact>]
    let ``저장 후 다시 열어도 센서 생략 정보가 보존된다`` () =
        let store = loadLatch ()
        let path =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ds2_sensorless_{System.Guid.NewGuid():N}.json")
        let virtualCount (target: DsStore) =
            target.ApiDefs.Values
            |> Seq.filter (fun d -> match d.SensingType with SensingType.Virtual _ -> true | _ -> false)
            |> Seq.length
        try
            store.SaveToFile path
            let reopened = DsStore()
            reopened.LoadFromFile path
            Assert.Equal(store.ApiCalls.Count, reopened.ApiCalls.Count)
            Assert.Equal(virtualCount store, virtualCount reopened)
            Assert.True(virtualCount reopened > 0)
            let advCall = reopened.Calls.Values |> Seq.find (fun c -> c.Name = "LATCH.ADV")
            Assert.Equal(5, advCall.ApiCalls.Count)
            let sensorless =
                advCall.ApiCalls
                |> Seq.filter (fun ac ->
                    match reopened.ApiDefs.[ac.ApiDefId |> Option.get].SensingType with
                    | SensingType.Virtual _ -> true
                    | _ -> false)
                |> Seq.toList
            Assert.Equal(4, sensorless.Length)
            Assert.All(sensorless, fun ac -> Assert.True(ac.InTag.IsNone && ac.OutTag.IsSome))
        finally
            if System.IO.File.Exists path then System.IO.File.Delete path

    [<Fact>]
    let ``export 라운드트립에서 System 열이 행마다 보존된다`` () =
        let store = loadLatch ()
        let project = store.Projects.Values |> Seq.head
        let exported = CsvExporter.projectToCsv store project.Id
        for name in expectedSystems do
            Assert.Contains($",{name},", exported)

// Excel 에서 편집한 표는 열 이름이 'IN Name', 'IN_ADDR', 'Out Addr' 처럼 흔히 달라진다.
// 헤더 이름은 대소문자·공백·언더스코어·addr↔address 축약을 흡수해 인식해야 한다.
module HeaderVariantTests =

    let private row =
        "LH\tUPPER\tSOL_1차클램프\tSOL_1차클램프1\tADV\tRS_ADV1\t%IX5.27.0.12\tSOL_ADV\t%QX5.29.0.06"

    let private parseWithHeader (header: string) =
        match CsvImporter.parseContent (header + "\n" + row) with
        | Error errors -> failwith (String.concat "\n" errors)
        | Ok doc -> doc

    [<Fact>]
    let ``공백 포함 축약 헤더를 인식한다`` () =
        let doc = parseWithHeader "Flow\tWork\tDevice\tSystem\tApi\tIN Name\tIN Addr\tOUT Name\tOUT Addr"
        Assert.Single(doc.Entries) |> ignore
        let entry = doc.Entries.Head
        Assert.Equal<string option>(Some "RS_ADV1", entry.InName)
        Assert.Equal<string option>(Some "%IX5.27.0.12", entry.InAddress)
        Assert.Equal<string option>(Some "%QX5.29.0.06", entry.OutAddress)

    [<Fact>]
    let ``언더스코어 대문자 헤더를 인식한다`` () =
        let doc = parseWithHeader "FLOW\tWORK\tDEVICE\tSYSTEM\tAPI\tIN_NAME\tIN_ADDRESS\tOUT_NAME\tOUT_ADDR"
        Assert.Single(doc.Entries) |> ignore

    [<Fact>]
    let ``기본 3열도 공백 헤더를 인식한다`` () =
        let doc = parseOk "FLOW , WORK , CALL\n투입,작업,실린더.전진>실린더.후진"
        Assert.Single(doc.Works) |> ignore

    [<Fact>]
    let ``알 수 없는 헤더는 여전히 거부한다`` () =
        match CsvImporter.parseContent ("Flow\tWork\tSequence\n" + row) with
        | Ok _ -> failwith "잘못된 헤더가 통과했습니다."
        | Error errors -> Assert.Contains(errors, fun e -> e.Contains "invalid header")
