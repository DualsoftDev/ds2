namespace DSPilot.Engine

open Ds2.Core
open Ds2.Core.LoggingHelpers

/// 성능 분석 모듈 — Core 함수 위임 + DSPilot UI 헬퍼
module Performance =

    /// 성능 메트릭 계산
    let calculateMetrics (average: float) (stdDev: float) : PerformanceMetrics =
        calculatePerformanceMetrics average stdDev

    /// 정규화 (0-1 범위)
    let normalizeValue = LoggingHelpers.normalizeValue

    /// Heatmap 색상 클래스 결정
    let determineColorClass = LoggingHelpers.determineColorClass

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
        |> colorClassToString

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
