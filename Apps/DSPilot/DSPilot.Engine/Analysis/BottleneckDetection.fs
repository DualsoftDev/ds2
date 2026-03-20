namespace DSPilot.Engine

/// Bottleneck Identification
module BottleneckDetection =

    /// 병목 타입
    type BottleneckType =
        | CriticalPath      // 임계 경로 (순차 실행이 긴 경우)
        | LongDuration      // 긴 실행 시간
        | FrequentExecution // 빈번한 실행

    /// 병목 정보
    type BottleneckInfo = {
        CallName: string
        BottleneckType: BottleneckType
        Value: float           // 해당 메트릭 값 (ms 또는 횟수)
        Impact: float          // 영향도 (0.0 ~ 1.0)
    }

    /// 임계 경로 식별 (가장 긴 순차 실행 경로)
    let findCriticalPath (timelines: GanttLayout.TimelineItem list) : string list =
        // 시작 시간 순으로 정렬
        let sorted = timelines |> List.sortBy (fun t -> t.RelativeStart)

        // 각 항목의 종료 시간 기준으로 다음 시작 가능한 항목 찾기
        let rec buildPath (current: GanttLayout.TimelineItem option) (remaining: GanttLayout.TimelineItem list) (path: string list) : string list =
            match current with
            | None ->
                // 시작 항목이 없으면 첫 번째 항목부터 시작
                match remaining with
                | [] -> path
                | first :: rest -> buildPath (Some first) rest [first.CallName]
            | Some curr ->
                match curr.RelativeEnd with
                | None -> path  // 진행 중인 항목이면 여기서 종료
                | Some currEnd ->
                    // 현재 항목 종료 이후 시작하는 항목 찾기
                    let nextItem =
                        remaining
                        |> List.filter (fun item -> item.RelativeStart >= currEnd)
                        |> List.tryHead

                    match nextItem with
                    | Some next ->
                        let newRemaining = remaining |> List.filter (fun x -> x.CallName <> next.CallName)
                        buildPath (Some next) newRemaining (path @ [next.CallName])
                    | None -> path

        buildPath None sorted []

    /// 실행 시간 기준 병목 식별
    let identifyLongDurationBottlenecks (timelines: GanttLayout.TimelineItem list) (threshold: float) : BottleneckInfo list =
        timelines
        |> List.choose (fun item ->
            match item.RelativeEnd with
            | Some endTime ->
                let duration = float (endTime - item.RelativeStart)
                if duration >= threshold then
                    Some {
                        CallName = item.CallName
                        BottleneckType = LongDuration
                        Value = duration
                        Impact = duration / threshold  // 임계값 대비 비율
                    }
                else
                    None
            | None -> None
        )
        |> List.sortByDescending (fun b -> b.Impact)

    /// 여러 사이클에서 병목 종합 분석
    let analyzeMultiCycleBottlenecks
        (cycleTimelines: (string * GanttLayout.TimelineItem list) list)  // (CycleId, Timelines) 리스트
        (durationThreshold: float)
        : BottleneckInfo list =

        // 모든 사이클의 타임라인을 Call별로 그룹화
        let callDurations =
            cycleTimelines
            |> List.collect snd
            |> List.choose (fun item ->
                match item.RelativeEnd with
                | Some endTime ->
                    Some (item.CallName, float (endTime - item.RelativeStart))
                | None -> None
            )
            |> List.groupBy fst
            |> List.map (fun (callName, durations) ->
                let avgDuration = durations |> List.map snd |> List.average
                let count = durations.Length
                (callName, avgDuration, count)
            )

        // 긴 실행 시간 병목
        let durationBottlenecks =
            callDurations
            |> List.filter (fun (_, avgDuration, _) -> avgDuration >= durationThreshold)
            |> List.map (fun (callName, avgDuration, _) ->
                {
                    CallName = callName
                    BottleneckType = LongDuration
                    Value = avgDuration
                    Impact = avgDuration / durationThreshold
                }
            )

        // 빈번한 실행 병목 (사이클 수 대비 실행 횟수)
        let cycleCount = float (cycleTimelines.Length)
        let frequentBottlenecks =
            callDurations
            |> List.filter (fun (_, _, count) -> float count > cycleCount * 1.5)
            |> List.map (fun (callName, _, count) ->
                {
                    CallName = callName
                    BottleneckType = FrequentExecution
                    Value = float count
                    Impact = float count / cycleCount
                }
            )

        durationBottlenecks @ frequentBottlenecks
        |> List.sortByDescending (fun b -> b.Impact)
