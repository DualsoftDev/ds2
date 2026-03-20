namespace DSPilot.Engine

/// Gantt Chart Lane Assignment
module GanttLayout =

    /// Call 타임라인 항목 (레인 할당을 위한 간소화 모델)
    type TimelineItem = {
        CallName: string
        RelativeStart: int
        RelativeEnd: int option
        Lane: int
    }

    /// 두 타임라인 항목이 시간적으로 겹치는지 확인
    let isOverlapping (item1: TimelineItem) (item2: TimelineItem) : bool =
        match item1.RelativeEnd, item2.RelativeEnd with
        | Some end1, Some end2 ->
            // 두 항목 모두 종료 시간이 있는 경우
            not (end1 <= item2.RelativeStart || end2 <= item1.RelativeStart)
        | Some end1, None ->
            // item1만 종료, item2는 진행 중
            end1 > item2.RelativeStart
        | None, Some end2 ->
            // item2만 종료, item1은 진행 중
            end2 > item1.RelativeStart
        | None, None ->
            // 둘 다 진행 중 - 겹침
            true

    /// 레인 할당 알고리즘 (시간 기반 겹침 감지)
    let assignLanes (timelines: TimelineItem list) : TimelineItem list =
        // 시작 시간 순으로 정렬
        let sorted = timelines |> List.sortBy (fun t -> t.RelativeStart)

        // 각 타임라인에 레인 할당
        sorted
        |> List.fold (fun (assigned: TimelineItem list) (item: TimelineItem) ->
            // 현재 항목과 겹치지 않는 레인 찾기
            let usedLanes =
                assigned
                |> List.filter (fun prev -> isOverlapping prev item)
                |> List.map (fun prev -> prev.Lane)
                |> Set.ofList

            // 사용 가능한 가장 작은 레인 번호 찾기
            let availableLane =
                Seq.initInfinite id
                |> Seq.find (fun lane -> not (usedLanes.Contains lane))

            let assignedItem = { item with Lane = availableLane }
            assigned @ [assignedItem]
        ) []
