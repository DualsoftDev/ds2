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
