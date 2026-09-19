namespace Ds2.CSV

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store


/// ds2-basic-csv/v1 → ImportPlan 매퍼.
/// - '>' 인접쌍 → ArrowBetweenCalls(workId, src, dst, Start)
/// - 데이터 행 순서 → 모든 Work 를 StartReset 체인 (Flow 경계 무시, ArrowBetweenWorks.ParentId=systemId).
///   Flow = 제품 1개가 머무는 자리이고, 체인은 그 자리 사이의 이송이다.
///   StartReset 은 다음 Work 가 시작할 때 이전 Work 를 리셋하므로 이전 자리가 곧바로
///   다음 제품을 받는다 = 파이프라인. 끊으면 스테이션끼리 이어지지 않는다.
/// - 디바이스 캐스케이드는 기존 linkCallsToDevicesMultiFlow 재사용 (systemNameHint=Some devAlias
///   → 여러 Flow 가 같은 디바이스를 공유해도 단일 Passive System 으로 병합)
/// - SequenceLabel/Position 은 부여하지 않는다(AutoLayout 이 배치).
/// - autoStartClear=true 면 첫 Flow 앞에 Start(TokenRole.Source), 마지막 Flow 뒤에 Clear(TokenRole.Sink) 를
///   하나씩 자동 추가한다. 라인 전체가 하나의 체인이므로 기동/종료도 양 끝에 하나씩이다.
/// - store 는 변경하지 않는다(plan 생성 계약 — 기존 CsvMapper 와 동일).
module internal BasicCsvMapper =

    /// 자동 추가되는 시작/종료 Work 이름. 같은 Flow 에 동명 Work 가 이미 있으면 접미사로 고유화한다.
    let [<Literal>] internal startWorkName = "Start"
    let [<Literal>] internal clearWorkName = "Clear"

    /// flow 안에서 겹치지 않는 Work 이름을 만든다 (Start, Start_1, Start_2 ...).
    let private uniqueWorkName (taken: HashSet<string>) (flowName: string) (baseName: string) =
        let key (n: string) = $"{flowName}\u0000{n}"
        if not (taken.Contains(key baseName)) then baseName
        else
            let mutable i = 1
            while taken.Contains(key $"{baseName}_{i}") do i <- i + 1
            $"{baseName}_{i}"

    let mapToSystemPlanWith
        (autoStartClear: bool)
        (store: DsStore)
        (projectId: Guid)
        (systemId: Guid)
        (document: BasicCsvDocument)
        : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let flows = Dictionary<string, Flow>()
        let flowOrder = ResizeArray<string>()
        let callsByFlow = Dictionary<string, ResizeArray<Call * string * string option>>()
        let mutable prevWork: Work option = None
        let mutable firstWork: Work option = None
        // 자동 추가 Work 이름 충돌 방지용 — CSV 가 만든 (Flow, Work) 조합.
        let takenWorkNames = HashSet<string>()
        for w in document.Works do takenWorkNames.Add($"{w.FlowName}\u0000{w.WorkName}") |> ignore

        for basicWork in document.Works do
            let flow =
                match flows.TryGetValue(basicWork.FlowName) with
                | true, existing -> existing
                | false, _ ->
                    let created = Flow(basicWork.FlowName, systemId)
                    operations.Add(AddFlow created)
                    flows.[basicWork.FlowName] <- created
                    flowOrder.Add basicWork.FlowName
                    created

            let work = Work(flow.Name, basicWork.WorkName, flow.Id)
            operations.Add(AddWork work)

            // CALL 노드 → Call 엔티티 (동일 이름 노드는 파서가 이미 병합)
            let callByKey = Dictionary<string, Call>()
            for (key, deviceAlias, apiName) in basicWork.Nodes do
                let call = Call(deviceAlias, apiName, work.Id)
                operations.Add(AddCall call)
                callByKey.[key] <- call
                let bucket =
                    match callsByFlow.TryGetValue(basicWork.FlowName) with
                    | true, existing -> existing
                    | false, _ ->
                        let created = ResizeArray()
                        callsByFlow.[basicWork.FlowName] <- created
                        created
                // systemNameHint = Some deviceAlias: Flow 간 동일 디바이스 → 단일 Passive 병합
                bucket.Add(call, $"{deviceAlias}.{apiName}", Some deviceAlias)

            // 선행·후행이 완전히 같은 Call 들은 한 묶음으로 동시에 시작/리셋된다 → Group 으로 연결.
            // Group 확장(SimIndexGroupExpansion)은 멤버 전원에 외부 선행 합집합을 재분배하고
            // 후행은 멤버 전원 AND-join 으로 재구성하므로, Start 엣지는 그룹의 **대표 멤버 하나**에만
            // 넣고 나가게 하면 된다 — 실행 의미는 동일하고 캔버스에는 선이 한 가닥만 남는다.
            // (시그니처가 같으므로 대표의 선행→대표 / 대표→후행 엣지는 CSV 에 반드시 존재한다.)
            let predsOf = Dictionary<string, SortedSet<string>>()
            let succsOf = Dictionary<string, SortedSet<string>>()
            for (key, _, _) in basicWork.Nodes do
                predsOf.[key] <- SortedSet<string>(StringComparer.Ordinal)
                succsOf.[key] <- SortedSet<string>(StringComparer.Ordinal)
            for (srcKey, dstKey) in basicWork.Edges do
                predsOf.[dstKey].Add srcKey |> ignore
                succsOf.[srcKey].Add dstKey |> ignore

            let groupOrder = ResizeArray<string>()
            let groupMembers = Dictionary<string, ResizeArray<string>>()
            for (key, _, _) in basicWork.Nodes do
                // 선행/후행 집합을 그대로 키로 — 같은 키면 서로 교체 가능한 병렬 형제다.
                let sig_ =
                    String.concat "|" [ String.concat "," predsOf.[key]; ">"; String.concat "," succsOf.[key] ]
                match groupMembers.TryGetValue sig_ with
                | true, members -> members.Add key
                | false, _ ->
                    let members = ResizeArray<string>()
                    members.Add key
                    groupMembers.[sig_] <- members
                    groupOrder.Add sig_

            // 대표(첫 멤버)가 아닌 그룹 멤버 — 이들의 들어오는/나가는 Start 엣지는 생략한다.
            let nonRepresentative = HashSet<string>()
            for sig_ in groupOrder do
                let members = groupMembers.[sig_]
                for i in 1 .. members.Count - 1 do
                    nonRepresentative.Add members.[i] |> ignore

            // '>' 엣지 → ArrowBetweenCalls(Start). 비대표 멤버 몫은 Group 확장이 대신한다.
            for (srcKey, dstKey) in basicWork.Edges do
                if not (nonRepresentative.Contains srcKey) && not (nonRepresentative.Contains dstKey) then
                    let arrow = ArrowBetweenCalls(work.Id, callByKey.[srcKey].Id, callByKey.[dstKey].Id, ArrowType.Start)
                    operations.Add(AddArrowCall arrow)

            for sig_ in groupOrder do
                let members = groupMembers.[sig_]
                // 2개 이상일 때만. 인접쌍으로 이으면 union-find 가 하나의 그룹으로 합친다.
                for i in 0 .. members.Count - 2 do
                    let arrow =
                        ArrowBetweenCalls(work.Id, callByKey.[members.[i]].Id, callByKey.[members.[i + 1]].Id, ArrowType.Group)
                    operations.Add(AddArrowCall arrow)

            // 행 순서 Work StartReset 체인 — Flow 경계를 넘어 끝까지 잇는다(스테이션 간 이송).
            match prevWork with
            | Some prev ->
                operations.Add(AddArrowWork (ArrowBetweenWorks(systemId, prev.Id, work.Id, ArrowType.StartReset)))
            | None -> firstWork <- Some work
            prevWork <- Some work

        // Start / Clear 자동 추가 — 라인 전체 체인의 양 끝에 하나씩.
        // Start : 첫 Work 로 StartReset 1줄만 연결한다.
        // Clear : 마지막 Work 에서 StartReset + Reset 2줄로 연결해 끝나면 항상 리셋되게 한다.
        if autoStartClear then
            match firstWork, prevWork with
            | Some first, Some last ->
                let firstFlow = flows.[(List.head document.Works).FlowName]
                let lastFlow = flows.[(List.last document.Works).FlowName]

                let startName = uniqueWorkName takenWorkNames firstFlow.Name startWorkName
                takenWorkNames.Add($"{firstFlow.Name}\u0000{startName}") |> ignore
                let startWork = Work(firstFlow.Name, startName, firstFlow.Id)
                startWork.TokenRole <- TokenRole.Source
                operations.Add(AddWork startWork)
                operations.Add(AddArrowWork (ArrowBetweenWorks(systemId, startWork.Id, first.Id, ArrowType.StartReset)))

                let clearName = uniqueWorkName takenWorkNames lastFlow.Name clearWorkName
                takenWorkNames.Add($"{lastFlow.Name}\u0000{clearName}") |> ignore
                let clearWork = Work(lastFlow.Name, clearName, lastFlow.Id)
                clearWork.TokenRole <- TokenRole.Sink
                operations.Add(AddWork clearWork)
                operations.Add(AddArrowWork (ArrowBetweenWorks(systemId, last.Id, clearWork.Id, ArrowType.StartReset)))
                operations.Add(AddArrowWork (ArrowBetweenWorks(systemId, last.Id, clearWork.Id, ArrowType.Reset)))
            | _ -> ()

        // 디바이스 캐스케이드: Passive System/Flow/Work + ApiDef(Tx/Rx) + ApiCall + pairwise ResetReset
        let allFlowCalls =
            [ for flowName in flowOrder do
                match callsByFlow.TryGetValue(flowName) with
                | true, bucket -> yield flowName, List.ofSeq bucket
                | false, _ -> () ]
        // CALL 토큰의 '(1000MS)' 접미사 → 디바이스 API Work.Duration.
        // 미지정 API 는 None 을 돌려 기존 기본값(500ms) 경로를 타게 한다.
        let durationMap = dict document.Durations
        let durationOf (alias: string) (api: string) =
            match durationMap.TryGetValue((alias, api)) with
            | true, ts -> Some ts
            | false, _ -> Some (TimeSpan.FromMilliseconds 500.)
        ImportPlanDeviceOps.linkCallsToDevicesMultiFlowWithDurations durationOf store projectId allFlowCalls operations

        ImportPlan.ofSeq operations

    /// 기존 호출자 호환 — Start/Clear 자동 추가 없음.
    let mapToSystemPlan (store: DsStore) (projectId: Guid) (systemId: Guid) (document: BasicCsvDocument) : ImportPlan =
        mapToSystemPlanWith false store projectId systemId document
