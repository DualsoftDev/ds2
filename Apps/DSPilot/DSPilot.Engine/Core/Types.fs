namespace DSPilot.Engine

/// 시간 버킷 크기
type BucketSize =
    | Min5
    | Min10
    | Hour1

/// Heatmap 메트릭
type HeatmapMetric =
    | AverageTime
    | StdDeviation
    | CoefficientOfVariation

/// Trend 데이터 포인트
type TrendPoint = {
    Time: System.DateTime
    Average: float
    StdDev: float
    SampleCount: int
}

/// 성능 메트릭
type PerformanceMetrics = {
    AverageTime: float
    StdDev: float
    CoefficientOfVariation: float
}

/// 색상 클래스
type ColorClass =
    | Excellent
    | Good
    | Fair
    | Poor
    | Critical

/// 색상 클래스를 문자열로 변환
module ColorClass =
    let toString = function
        | Excellent -> "heatmap-excellent"
        | Good -> "heatmap-good"
        | Fair -> "heatmap-fair"
        | Poor -> "heatmap-poor"
        | Critical -> "heatmap-critical"

/// Tag 매칭 모드
type TagMatchMode =
    | ByName      // Tag 이름으로 매칭
    | ByAddress   // Tag 주소로 매칭

/// Call 매핑 정보
[<CLIMutable>]
type CallMappingInfo = {
    Call: Ds2.Core.Call
    ApiCall: Ds2.Core.ApiCall
    IsInTag: bool
    FlowName: string
}
