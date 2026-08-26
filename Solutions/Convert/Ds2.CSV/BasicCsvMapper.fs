namespace Ds2.CSV

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store


/// ds2-basic-csv/v1 → ImportPlan 매퍼.
/// - '>' 인접쌍 → ArrowBetweenCalls(workId, src, dst, Start)
/// - 데이터 행 순서 → 모든 Work 를 StartReset 체인(Flow 경계 무시, ArrowBetweenWorks.ParentId=systemId)
/// - 디바이스 캐스케이드는 기존 linkCallsToDevicesMultiFlow 재사용 (systemNameHint=Some devAlias
///   → 여러 Flow 가 같은 디바이스를 공유해도 단일 Passive System 으로 병합)
/// - TokenRole/SequenceLabel/Position 은 부여하지 않는다(수동 시작 전용, AutoLayout 이 배치).
/// - store 는 변경하지 않는다(plan 생성 계약 — 기존 CsvMapper 와 동일).
module internal BasicCsvMapper =

    let mapToSystemPlan (store: DsStore) (projectId: Guid) (systemId: Guid) (document: BasicCsvDocument) : ImportPlan =
        let operations = ResizeArray<ImportPlanOperation>()
        let flows = Dictionary<string, Flow>()
        let flowOrder = ResizeArray<string>()
        let callsByFlow = Dictionary<string, ResizeArray<Call * string * string option>>()
        let mutable prevWork: Work option = None

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

            // '>' 엣지 → ArrowBetweenCalls(Start)
            for (srcKey, dstKey) in basicWork.Edges do
                let arrow = ArrowBetweenCalls(work.Id, callByKey.[srcKey].Id, callByKey.[dstKey].Id, ArrowType.Start)
                operations.Add(AddArrowCall arrow)

            // 행 순서 Work StartReset 체인 (Flow 경계 무시)
            match prevWork with
            | Some prev ->
                let arrow = ArrowBetweenWorks(systemId, prev.Id, work.Id, ArrowType.StartReset)
                operations.Add(AddArrowWork arrow)
            | None -> ()
            prevWork <- Some work

        // 디바이스 캐스케이드: Passive System/Flow/Work + ApiDef(Tx/Rx) + ApiCall + pairwise ResetReset
        let allFlowCalls =
            [ for flowName in flowOrder do
                match callsByFlow.TryGetValue(flowName) with
                | true, bucket -> yield flowName, List.ofSeq bucket
                | false, _ -> () ]
        ImportPlanDeviceOps.linkCallsToDevicesMultiFlow store projectId allFlowCalls operations

        ImportPlan.ofSeq operations
