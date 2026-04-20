namespace DSPilot.Engine

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store

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
                    let calls = Queries.callsOf work.Id store
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
    /// Group arrow 평탄화 (Union-Find로 그룹 병합)
    /// 예: A→B, B→C (Group), C→D (Group), D→E이면
    /// - Group: [B, C, D] (Union-Find로 병합)
    /// - 결과: A→B, A→C, A→D, B→E, C→E, D→E
    /// </summary>
    let flattenGroupArrows (arrows: ArrowBetweenCalls list) : (Guid * Guid) list =
        // Start arrow만 사용 (Group arrow는 그룹 관계 정의만 사용)
        let startArrows = arrows |> List.filter (fun a -> a.ArrowType = enum 1)
        let groupArrows = arrows |> List.filter (fun a -> a.ArrowType = enum 5)

        if groupArrows.IsEmpty then
            startArrows |> List.map (fun a -> (a.SourceId, a.TargetId))
        else
            // Union-Find로 그룹 병합
            let parent = Dictionary<Guid, Guid>()

            let rec find (x: Guid) =
                if not (parent.ContainsKey x) then
                    parent.[x] <- x
                    x
                elif parent.[x] = x then
                    x
                else
                    let root = find parent.[x]
                    parent.[x] <- root  // Path compression
                    root

            let union (x: Guid) (y: Guid) =
                let rootX = find x
                let rootY = find y
                if rootX <> rootY then
                    parent.[rootY] <- rootX

            // Group arrow로 연결된 모든 노드를 같은 그룹으로 병합
            for arrow in groupArrows do
                union arrow.SourceId arrow.TargetId

            // 각 루트별로 그룹 멤버 수집
            let groupsByRoot =
                parent.Keys
                |> Seq.groupBy find
                |> Seq.map (fun (root, members) -> (root, members |> Seq.toList))
                |> dict

            // 노드 → 그룹 멤버 리스트 매핑
            let nodeToGroup =
                groupsByRoot
                |> Seq.collect (fun kvp ->
                    kvp.Value |> List.map (fun node -> (node, kvp.Value)))
                |> Map.ofSeq

            // Start arrow 평탄화
            startArrows
            |> List.collect (fun arrow ->
                let src = arrow.SourceId
                let tgt = arrow.TargetId

                // src가 그룹에 속하면 그룹 멤버 모두, 아니면 src만
                let sources =
                    match nodeToGroup.TryFind src with
                    | Some members -> members
                    | None -> [src]

                // tgt가 그룹에 속하면 그룹 멤버 모두, 아니면 tgt만
                let targets =
                    match nodeToGroup.TryFind tgt with
                    | Some members -> members
                    | None -> [tgt]

                // 모든 조합
                [for s in sources do
                    for t in targets do
                        (s, t)])
            |> List.distinct

    /// <summary>
    /// Call DAG 구성
    /// - arrows: ArrowBetweenCalls list
    /// - calls: Call list
    /// - 각 Call의 in-degree, out-degree 계산
    /// - Group arrow를 평탄화하여 처리
    /// </summary>
    let buildCallDag (calls: Call list) (arrows: ArrowBetweenCalls list) : CallDagNode list =
        let callIds = calls |> List.map (fun c -> c.Id) |> Set.ofList

        // Group arrow 평탄화
        let flattenedEdges = flattenGroupArrows arrows

        let inDegrees =
            flattenedEdges
            |> List.filter (fun (_, targetId) -> callIds.Contains targetId)
            |> List.groupBy snd
            |> List.map (fun (targetId, group) -> (targetId, group.Length))
            |> Map.ofList

        let outDegrees =
            flattenedEdges
            |> List.filter (fun (sourceId, _) -> callIds.Contains sourceId)
            |> List.groupBy fst
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
    /// - 평탄화된 엣지 리스트 사용
    /// </summary>
    let detectCycle (dag: CallDagNode list) (flattenedEdges: (Guid * Guid) list) : unit =
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

            // 현재 노드에서 나가는 엣지들 처리 (평탄화된 엣지 사용)
            for (sourceId, targetId) in flattenedEdges do
                if sourceId = currentId && callIdSet.Contains targetId then
                    inDegreeCounts.[targetId] <- inDegreeCounts.[targetId] - 1
                    if inDegreeCounts.[targetId] = 0 then
                        queue.Enqueue(targetId)

        // 모든 노드를 방문하지 못했으면 순환이 존재
        if visitedCount <> dag.Length then
            invalidOp $"Cycle detected in Call DAG. Visited {visitedCount} out of {dag.Length} nodes."

    /// <summary>
    /// Head Call 찾기 (in-degree = 0인 노드 중 Name 오름차순 첫 번째)
    /// - 복수 Head가 있으면 첫 번째만 선택 (병렬 시작점은 지원 안 함)
    /// - 반환: (선택된 Head Call option, 전체 Head 수)
    /// </summary>
    let findHeadCall (dag: CallDagNode list) : Call option * int =
        let heads =
            dag
            |> List.filter (fun n -> n.InDegree = 0)
            |> List.map (fun n -> n.Call)
            |> List.sortBy (fun c -> c.Name)

        let headCall = heads |> List.tryHead
        (headCall, heads.Length)

    /// <summary>
    /// Tail Call 찾기 (out-degree = 0인 노드 중 Name 오름차순 첫 번째)
    /// - 복수 Tail이 있으면 첫 번째만 선택 (병렬 종료점은 지원 안 함)
    /// - 반환: (선택된 Tail Call option, 전체 Tail 수)
    /// </summary>
    let findTailCall (dag: CallDagNode list) : Call option * int =
        let tails =
            dag
            |> List.filter (fun n -> n.OutDegree = 0)
            |> List.map (fun n -> n.Call)
            |> List.sortBy (fun c -> c.Name)

        let tailCall = tails |> List.tryHead
        (tailCall, tails.Length)

    /// <summary>
    /// Flow 분석 결과
    /// </summary>
    type FlowAnalysisResult = {
        FlowName: string
        FlowId: Guid
        RepresentativeWorkId: Guid option
        RepresentativeWorkName: string option
        HeadCall: Call option
        TailCall: Call option
        HeadCount: int
        TailCount: int
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
        let works = Queries.worksOf flow.Id store

        // 모든 Work의 모든 Call을 수집 (대표 Work뿐만 아니라 전체)
        let allCalls =
            works
            |> List.collect (fun work -> Queries.callsOf work.Id store)

        let allArrows =
            works
            |> List.collect (fun work -> Queries.arrowCallsOf work.Id store)

        match selectRepresentativeWork works store with
        | None ->
            // Work가 없는 경우
            {
                FlowName = flow.Name
                FlowId = flow.Id
                RepresentativeWorkId = None
                RepresentativeWorkName = None
                HeadCall = None
                TailCall = None
                HeadCount = 0
                TailCount = 0
                MovingStartName = None
                MovingEndName = None
            }
        | Some repWork ->
            // 전체 Flow의 모든 Call과 Arrow를 사용하여 DAG 구성
            // (대표 Work뿐만 아니라 모든 Work의 Call/Arrow 포함)
            if allCalls.IsEmpty then
                {
                    FlowName = flow.Name
                    FlowId = flow.Id
                    RepresentativeWorkId = Some repWork.Id
                    RepresentativeWorkName = Some repWork.Name
                    HeadCall = None
                    TailCall = None
                    HeadCount = 0
                    TailCount = 0
                    MovingStartName = None
                    MovingEndName = None
                }
            else
                // 전체 Call/Arrow로 DAG 구성
                let dag = buildCallDag allCalls allArrows

                // Group arrow 평탄화 (순환 검증에 사용)
                let flattenedEdges = flattenGroupArrows allArrows
                detectCycle dag flattenedEdges

                // Head/Tail Call 찾기 (단일 선택 + 전체 개수)
                let (headCall, headCount) = findHeadCall dag
                let (tailCall, tailCount) = findTailCall dag

                // 복수 Head/Tail 경고 (콘솔 출력)
                if headCount > 1 then
                    printfn "[WARNING] Flow '%s': %d heads found, using first '%s'"
                        flow.Name headCount (headCall |> Option.map (fun c -> c.Name) |> Option.defaultValue "N/A")
                if tailCount > 1 then
                    printfn "[WARNING] Flow '%s': %d tails found, using first '%s'"
                        flow.Name tailCount (tailCall |> Option.map (fun c -> c.Name) |> Option.defaultValue "N/A")

                let movingStartName = headCall |> Option.map (fun c -> c.Name)
                let movingEndName = tailCall |> Option.map (fun c -> c.Name)

                {
                    FlowName = flow.Name
                    FlowId = flow.Id
                    RepresentativeWorkId = Some repWork.Id
                    RepresentativeWorkName = Some repWork.Name
                    HeadCall = headCall
                    TailCall = tailCall
                    HeadCount = headCount
                    TailCount = tailCount
                    MovingStartName = movingStartName
                    MovingEndName = movingEndName
                }
