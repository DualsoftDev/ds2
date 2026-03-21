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
        getColorClassForTime normalized

    /// 메트릭 표시 이름
    let getMetricDisplayName (metric: HeatmapMetric) : string =
        match metric with
        | AverageTime -> "평균 시간 (ms)"
        | StdDeviation -> "표준편차 (ms)"
        | CoefficientOfVariation -> "변동계수 (CV)"

    /// 메트릭 값 포맷
    let formatMetricValue (metric: HeatmapMetric) (value: float) : string =
        match metric with
        | AverageTime -> sprintf "%.0f" value
        | StdDeviation -> sprintf "%.0f" value
        | CoefficientOfVariation -> sprintf "%.2f" value
