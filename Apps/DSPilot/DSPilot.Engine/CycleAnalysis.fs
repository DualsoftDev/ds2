namespace DSPilot.Engine

open System

/// Cycle 분석 모듈
module CycleAnalysis =

    /// 버킷 크기를 TimeSpan으로 변환
    let bucketSizeToTimeSpan (size: BucketSize) : TimeSpan =
        match size with
        | Min5 -> TimeSpan.FromMinutes(5.0)
        | Min10 -> TimeSpan.FromMinutes(10.0)
        | Hour1 -> TimeSpan.FromHours(1.0)

    /// 시간을 버킷으로 그룹화 (시작 시간 기준)
    let getBucketKey (time: DateTime) (bucketSize: BucketSize) : DateTime =
        let span = bucketSizeToTimeSpan bucketSize
        let ticks = time.Ticks / span.Ticks
        DateTime(ticks * span.Ticks)

    /// 실행 시간 목록에서 통계 계산
    let calculateBucketStats (executionTimes: int list) : float * float * int =
        if executionTimes.IsEmpty then
            (0.0, 0.0, 0)
        else
            let count = executionTimes.Length
            let avg = executionTimes |> List.averageBy float
            let variance =
                executionTimes
                |> List.map (fun x ->
                    let diff = float x - avg
                    diff * diff)
                |> List.average
            let stdDev = sqrt variance
            (avg, stdDev, count)

    /// Trend 포인트 생성
    let createTrendPoint (bucketTime: DateTime) (executionTimes: int list) : TrendPoint =
        let (avg, stdDev, count) = calculateBucketStats executionTimes
        {
            Time = bucketTime
            Average = avg
            StdDev = stdDev
            SampleCount = count
        }

    /// 백분위수 계산 (50th, 95th, 99th)
    let calculatePercentiles (values: int list) : float * float * float =
        if values.IsEmpty then
            (0.0, 0.0, 0.0)
        else
            let sorted = values |> List.sort |> List.toArray
            let count = sorted.Length

            let getPercentile (p: float) =
                let index = int (float count * p / 100.0)
                let clampedIndex = max 0 (min (count - 1) index)
                float sorted.[clampedIndex]

            let p50 = getPercentile 50.0
            let p95 = getPercentile 95.0
            let p99 = getPercentile 99.0

            (p50, p95, p99)

    /// 실행 시간 분포 계산 (히스토그램용)
    let calculateDistribution (values: int list) (binCount: int) : (float * float * int) list =
        if values.IsEmpty || binCount <= 0 then
            []
        else
            let minVal = values |> List.min |> float
            let maxVal = values |> List.max |> float
            let binWidth = (maxVal - minVal) / float binCount

            if binWidth = 0.0 then
                [(minVal, maxVal, values.Length)]
            else
                [0 .. binCount - 1]
                |> List.map (fun i ->
                    let binStart = minVal + float i * binWidth
                    let binEnd = binStart + binWidth
                    let count =
                        values
                        |> List.filter (fun v ->
                            let fv = float v
                            fv >= binStart && (i = binCount - 1 || fv < binEnd))
                        |> List.length
                    (binStart, binEnd, count))

    // ===============================
    // Gantt Chart Lane Assignment
    // ===============================

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

    // ===============================
    // Bottleneck Identification
    // ===============================

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
    let findCriticalPath (timelines: TimelineItem list) : string list =
        // 시작 시간 순으로 정렬
        let sorted = timelines |> List.sortBy (fun t -> t.RelativeStart)

        // 각 항목의 종료 시간 기준으로 다음 시작 가능한 항목 찾기
        let rec buildPath (current: TimelineItem option) (remaining: TimelineItem list) (path: string list) : string list =
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
    let identifyLongDurationBottlenecks (timelines: TimelineItem list) (threshold: float) : BottleneckInfo list =
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
        (cycleTimelines: (string * TimelineItem list) list)  // (CycleId, Timelines) 리스트
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
