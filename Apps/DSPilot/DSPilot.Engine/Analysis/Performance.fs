namespace DSPilot.Engine

/// 성능 분석 모듈
module Performance =

    /// 성능 메트릭 계산
    let calculateMetrics (average: float) (stdDev: float) : PerformanceMetrics =
        let cv =
            if average = 0.0 then 0.0
            else (stdDev / average) * 100.0

        {
            AverageTime = average
            StdDev = stdDev
            CoefficientOfVariation = cv
        }

    /// 정규화 (0-1 범위)
    let normalizeValue (value: float) (minValue: float) (maxValue: float) : float =
        if maxValue > minValue then
            (value - minValue) / (maxValue - minValue)
        else
            0.5

    /// 역정규화 점수 계산 (낮을수록 높은 점수)
    let inverseNormalizeScore (value: float) (minValue: float) (maxValue: float) : float =
        if maxValue > minValue then
            (1.0 - (value - minValue) / (maxValue - minValue)) * 100.0
        else
            100.0

    /// Heatmap 아이템 리스트의 성능 점수 계산
    /// 가중치: 평균 시간 70%, 변동계수 30%
    let calculatePerformanceScores
        (items: (float * float) list) : float list =

        if items.IsEmpty then []
        else
            let avgTimes = items |> List.map fst
            let cvs = items |> List.map snd

            let minAvg = avgTimes |> List.min
            let maxAvg = avgTimes |> List.max
            let minCV = cvs |> List.min
            let maxCV = cvs |> List.max

            items
            |> List.map (fun (avgTime, cv) ->
                let avgScore = inverseNormalizeScore avgTime minAvg maxAvg
                let cvScore = inverseNormalizeScore cv minCV maxCV
                avgScore * 0.7 + cvScore * 0.3
            )

    /// 정규화된 성능 점수 계산 (0-100 스케일)
    let calculatePerformanceScore (metrics: PerformanceMetrics) (maxAvg: float) : float =
        if maxAvg = 0.0 then 0.0
        else
            let timeScore = ((maxAvg - metrics.AverageTime) / maxAvg) * 70.0
            let stabilityScore =
                if metrics.CoefficientOfVariation <= 10.0 then 30.0
                elif metrics.CoefficientOfVariation <= 20.0 then 20.0
                elif metrics.CoefficientOfVariation <= 30.0 then 10.0
                else 0.0
            max 0.0 (min 100.0 (timeScore + stabilityScore))

    /// Heatmap 색상 클래스 결정
    let determineColorClass (metric: HeatmapMetric) (value: float) : ColorClass =
        match metric with
        | AverageTime ->
            if value < 100.0 then Excellent
            elif value < 500.0 then Good
            elif value < 1000.0 then Fair
            elif value < 2000.0 then Poor
            else Critical

        | StdDeviation ->
            if value < 50.0 then Excellent
            elif value < 100.0 then Good
            elif value < 200.0 then Fair
            elif value < 400.0 then Poor
            else Critical

        | CoefficientOfVariation ->
            if value < 10.0 then Excellent
            elif value < 20.0 then Good
            elif value < 30.0 then Fair
            elif value < 50.0 then Poor
            else Critical

        | PerformanceScore ->
            if value >= 80.0 then Excellent
            elif value >= 60.0 then Good
            elif value >= 40.0 then Fair
            elif value >= 20.0 then Poor
            else Critical

    /// 정규화된 값에 따른 색상 클래스 (성능 점수용, 높을수록 녹색)
    let getColorClassForPerformance (normalized: float) : string =
        if normalized >= 0.8 then "heatmap-excellent"
        elif normalized >= 0.6 then "heatmap-good"
        elif normalized >= 0.4 then "heatmap-fair"
        elif normalized >= 0.2 then "heatmap-poor"
        else "heatmap-critical"

    /// 정규화된 값에 따른 색상 클래스 (시간/변동성용, 낮을수록 녹색)
    let getColorClassForTime (normalized: float) : string =
        if normalized <= 0.2 then "heatmap-excellent"
        elif normalized <= 0.4 then "heatmap-good"
        elif normalized <= 0.6 then "heatmap-fair"
        elif normalized <= 0.8 then "heatmap-poor"
        else "heatmap-critical"

    /// Heatmap 값에 따른 CSS 클래스 이름 반환
    let getColorClass (metric: HeatmapMetric) (value: float) : string =
        determineColorClass metric value
        |> ColorClass.toString

    /// 메트릭 값과 정규화를 통한 색상 클래스 결정
    let assignColorClass (metric: HeatmapMetric) (value: float) (minValue: float) (maxValue: float) : string =
        let normalized = normalizeValue value minValue maxValue

        match metric with
        | PerformanceScore -> getColorClassForPerformance normalized
        | _ -> getColorClassForTime normalized

    /// 메트릭 표시 이름
    let getMetricDisplayName (metric: HeatmapMetric) : string =
        match metric with
        | AverageTime -> "평균 시간 (ms)"
        | StdDeviation -> "표준편차 (ms)"
        | CoefficientOfVariation -> "변동계수 (CV)"
        | PerformanceScore -> "성능 점수"

    /// 메트릭 값 포맷
    let formatMetricValue (metric: HeatmapMetric) (value: float) : string =
        match metric with
        | AverageTime -> sprintf "%.0f" value
        | StdDeviation -> sprintf "%.0f" value
        | CoefficientOfVariation -> sprintf "%.2f" value
        | PerformanceScore -> sprintf "%.0f" value
