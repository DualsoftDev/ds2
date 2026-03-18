namespace DSPilot.Engine

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.UI.Core

/// <summary>
/// Flow 분석 모듈 - 대표 Work 선택, DAG 분석, MT/WT/CT 계산
/// </summary>
module FlowAnalysis =

    // =========================================================================
    // Phase 1: 대표 Work 선택 및 DAG 분석
    // =========================================================================

    /// <summary>
    /// Flow의 대표 Work 선택
    /// - Call 수가 가장 많은 Work 선택
    /// - 동률이면 Work.Name 오름차순 첫 번째 선택
    /// </summary>
    let selectRepresentativeWork (works: Work list) (store: DsStore) : Work option =
        if works.IsEmpty then None
        else
            let worksWithCallCount =
                works
                |> List.map (fun work ->
                    let calls = DsQuery.callsOf work.Id store
                    (work, calls.Length))
                |> List.sortByDescending (fun (_, count) -> count)
                |> List.groupBy (fun (_, count) -> count)
                |> List.head // 가장 Call 수가 많은 그룹
                |> snd

            // 동률이면 Name 오름차순
            worksWithCallCount
            |> List.map fst
            |> List.sortBy (fun w -> w.Name)
            |> List.tryHead

    /// <summary>
    /// Call DAG 노드 정보 (Call, in-degree, out-degree)
    /// </summary>
    type CallDagNode = {
        Call: Call
        InDegree: int
        OutDegree: int
    }

    /// <summary>
    /// Call DAG 구성
    /// - arrows: ArrowBetweenCalls list
    /// - calls: Call list
    /// - 각 Call의 in-degree, out-degree 계산
    /// </summary>
    let buildCallDag (calls: Call list) (arrows: ArrowBetweenCalls list) : CallDagNode list =
        let callIds = calls |> List.map (fun c -> c.Id) |> Set.ofList

        let inDegrees =
            arrows
            |> List.filter (fun a -> callIds.Contains a.TargetId)
            |> List.groupBy (fun a -> a.TargetId)
            |> List.map (fun (targetId, group) -> (targetId, group.Length))
            |> Map.ofList

        let outDegrees =
            arrows
            |> List.filter (fun a -> callIds.Contains a.SourceId)
            |> List.groupBy (fun a -> a.SourceId)
            |> List.map (fun (sourceId, group) -> (sourceId, group.Length))
            |> Map.ofList

        calls
        |> List.map (fun call ->
            {
                Call = call
                InDegree = inDegrees |> Map.tryFind call.Id |> Option.defaultValue 0
                OutDegree = outDegrees |> Map.tryFind call.Id |> Option.defaultValue 0
            })

    /// <summary>
    /// DAG 순환 검증 (Kahn's Algorithm for Topological Sort)
    /// - 순환이 있으면 InvalidOperationException 발생
    /// </summary>
    let detectCycle (dag: CallDagNode list) (arrows: ArrowBetweenCalls list) : unit =
        let callIdSet = dag |> List.map (fun n -> n.Call.Id) |> Set.ofList

        // in-degree 카운트 초기화
        let inDegreeCounts =
            dag
            |> List.map (fun n -> (n.Call.Id, n.InDegree))
            |> dict
            |> Dictionary

        // in-degree가 0인 노드들로 큐 초기화
        let queue = Queue<Guid>()
        for node in dag do
            if node.InDegree = 0 then
                queue.Enqueue(node.Call.Id)

        let mutable visitedCount = 0

        while queue.Count > 0 do
            let currentId = queue.Dequeue()
            visitedCount <- visitedCount + 1

            // 현재 노드에서 나가는 엣지들 처리
            for arrow in arrows do
                if arrow.SourceId = currentId && callIdSet.Contains arrow.TargetId then
                    inDegreeCounts.[arrow.TargetId] <- inDegreeCounts.[arrow.TargetId] - 1
                    if inDegreeCounts.[arrow.TargetId] = 0 then
                        queue.Enqueue(arrow.TargetId)

        // 모든 노드를 방문하지 못했으면 순환이 존재
        if visitedCount <> dag.Length then
            invalidOp $"Cycle detected in Call DAG. Visited {visitedCount} out of {dag.Length} nodes."

    /// <summary>
    /// Head Call 찾기 (in-degree = 0)
    /// - 여러 개면 Name 오름차순 정렬
    /// </summary>
    let findHeadCalls (dag: CallDagNode list) : Call list =
        dag
        |> List.filter (fun n -> n.InDegree = 0)
        |> List.map (fun n -> n.Call)
        |> List.sortBy (fun c -> c.Name)

    /// <summary>
    /// Tail Call 찾기 (out-degree = 0)
    /// - 여러 개면 Name 오름차순 정렬
    /// </summary>
    let findTailCalls (dag: CallDagNode list) : Call list =
        dag
        |> List.filter (fun n -> n.OutDegree = 0)
        |> List.map (fun n -> n.Call)
        |> List.sortBy (fun c -> c.Name)

    /// <summary>
    /// Flow 분석 결과
    /// </summary>
    type FlowAnalysisResult = {
        FlowName: string
        FlowId: Guid
        RepresentativeWorkId: Guid option
        RepresentativeWorkName: string option
        HeadCalls: Call list
        TailCalls: Call list
        MovingStartName: string option
        MovingEndName: string option
    }

    /// <summary>
    /// Flow 전체 분석
    /// - 대표 Work 선택
    /// - DAG 구성 및 순환 검증
    /// - Head/Tail Call 찾기
    /// - MovingStartName/MovingEndName 결정
    /// </summary>
    let analyzeFlow (flow: Flow) (store: DsStore) : FlowAnalysisResult =
        let works = DsQuery.worksOf flow.Id store

        // 모든 Work의 모든 Call을 수집 (대표 Work뿐만 아니라 전체)
        let allCalls =
            works
            |> List.collect (fun work -> DsQuery.callsOf work.Id store)

        let allArrows =
            works
            |> List.collect (fun work -> DsQuery.arrowCallsOf work.Id store)

        match selectRepresentativeWork works store with
        | None ->
            // Work가 없는 경우
            {
                FlowName = flow.Name
                FlowId = flow.Id
                RepresentativeWorkId = None
                RepresentativeWorkName = None
                HeadCalls = []
                TailCalls = []
                MovingStartName = None
                MovingEndName = None
            }
        | Some repWork ->
            let calls = DsQuery.callsOf repWork.Id store
            let arrows = DsQuery.arrowCallsOf repWork.Id store

            if calls.IsEmpty then
                // 대표 Work에 Call이 없지만, 다른 Work에 Call이 있을 수 있음
                if allCalls.IsEmpty then
                    {
                        FlowName = flow.Name
                        FlowId = flow.Id
                        RepresentativeWorkId = Some repWork.Id
                        RepresentativeWorkName = Some repWork.Name
                        HeadCalls = []
                        TailCalls = []
                        MovingStartName = None
                        MovingEndName = None
                    }
                else
                    // 전체 Call/Arrow로 DAG 구성
                    let dag = buildCallDag allCalls allArrows
                    detectCycle dag allArrows

                    let headCalls = findHeadCalls dag
                    let tailCalls = findTailCalls dag

                    let movingStartName = headCalls |> List.tryHead |> Option.map (fun c -> c.Name)
                    let movingEndName = tailCalls |> List.tryHead |> Option.map (fun c -> c.Name)

                    {
                        FlowName = flow.Name
                        FlowId = flow.Id
                        RepresentativeWorkId = Some repWork.Id
                        RepresentativeWorkName = Some repWork.Name
                        HeadCalls = headCalls
                        TailCalls = tailCalls
                        MovingStartName = movingStartName
                        MovingEndName = movingEndName
                    }
            else
                // DAG 구성
                let dag = buildCallDag calls arrows

                // 순환 검증 (예외 발생 가능)
                detectCycle dag arrows

                // Head/Tail 찾기
                let headCalls = findHeadCalls dag
                let tailCalls = findTailCalls dag

                // MovingStartName/MovingEndName 결정
                let movingStartName = headCalls |> List.tryHead |> Option.map (fun c -> c.Name)
                let movingEndName = tailCalls |> List.tryHead |> Option.map (fun c -> c.Name)

                {
                    FlowName = flow.Name
                    FlowId = flow.Id
                    RepresentativeWorkId = Some repWork.Id
                    RepresentativeWorkName = Some repWork.Name
                    HeadCalls = headCalls
                    TailCalls = tailCalls
                    MovingStartName = movingStartName
                    MovingEndName = movingEndName
                }
