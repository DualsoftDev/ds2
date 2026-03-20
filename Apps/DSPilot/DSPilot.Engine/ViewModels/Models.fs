namespace DSPilot.Engine

open System

/// View Models 및 DTO
module Models =

    /// Call Heatmap 아이템 (F# record)
    [<CLIMutable>]
    type CallHeatmapItem = {
        CallName: string
        FlowName: string
        WorkName: string
        AverageGoingTime: float
        StdDevGoingTime: float
        GoingCount: int
        mutable PerformanceScore: float
        mutable ColorClass: string
        // 4개 메트릭별 색상 클래스 (매트릭스 히트맵용)
        mutable ColorClassAvg: string
        mutable ColorClassStdDev: string
        mutable ColorClassCV: string
        mutable ColorClassScore: string
    } with
        /// 변동계수 계산
        member this.CoefficientOfVariation =
            if this.AverageGoingTime > 0.0 then
                this.StdDevGoingTime / this.AverageGoingTime
            else
                0.0

        /// 메트릭 값 추출
        member this.GetMetricValue(metric: HeatmapMetric) =
            match metric with
            | HeatmapMetric.AverageTime -> this.AverageGoingTime
            | HeatmapMetric.StdDeviation -> this.StdDevGoingTime
            | HeatmapMetric.CoefficientOfVariation -> this.CoefficientOfVariation
            | HeatmapMetric.PerformanceScore -> this.PerformanceScore

        /// 메트릭별 색상 클래스 추출
        member this.GetColorClass(metric: HeatmapMetric) =
            match metric with
            | HeatmapMetric.AverageTime -> this.ColorClassAvg
            | HeatmapMetric.StdDeviation -> this.ColorClassStdDev
            | HeatmapMetric.CoefficientOfVariation -> this.ColorClassCV
            | HeatmapMetric.PerformanceScore -> this.ColorClassScore

        /// 툴팁 텍스트 생성
        member this.GetTooltipText() =
            let cvStatus =
                if this.CoefficientOfVariation < 0.1 then "매우 안정적"
                elif this.CoefficientOfVariation < 0.2 then "안정적"
                elif this.CoefficientOfVariation < 0.3 then "보통"
                elif this.CoefficientOfVariation < 0.5 then "불안정"
                else "매우 불안정"

            let scoreStatus =
                if this.PerformanceScore >= 90.0 then "우수"
                elif this.PerformanceScore >= 70.0 then "양호"
                elif this.PerformanceScore >= 50.0 then "보통"
                elif this.PerformanceScore >= 30.0 then "개선 필요"
                else "심각"

            sprintf "[%s]\n━━━━━━━━━━━━━━━━━━━━\n평균 실행시간: %.0f ms\n표준편차: %.0f ms\n변동계수: %.2f (%s)\n성능점수: %.0f/100 (%s)\n실행횟수: %d회\n━━━━━━━━━━━━━━━━━━━━\n💡 변동계수가 낮을수록 안정적입니다\n💡 성능점수가 높을수록 우수합니다"
                this.CallName
                this.AverageGoingTime
                this.StdDevGoingTime
                this.CoefficientOfVariation
                cvStatus
                this.PerformanceScore
                scoreStatus
                this.GoingCount

    /// Flow Heatmap 그룹 (F# record)
    [<CLIMutable>]
    type FlowHeatmapGroup = {
        FlowName: string
        Calls: CallHeatmapItem list
        mutable IsExpanded: bool
        // Flow 수준 색상 클래스 (매트릭스 히트맵용)
        mutable FlowColorClassAvg: string
        mutable FlowColorClassStdDev: string
        mutable FlowColorClassCV: string
        mutable FlowColorClassScore: string
    } with
        /// Flow 평균 Going 시간
        member this.FlowAverageTime =
            if this.Calls.IsEmpty then 0.0
            else this.Calls |> List.averageBy (fun c -> c.AverageGoingTime)

        /// Flow 평균 표준편차
        member this.FlowAverageStdDev =
            if this.Calls.IsEmpty then 0.0
            else this.Calls |> List.averageBy (fun c -> c.StdDevGoingTime)

        /// Flow 평균 변동계수
        member this.FlowAverageCV =
            if this.Calls.IsEmpty then 0.0
            else this.Calls |> List.averageBy (fun c -> c.CoefficientOfVariation)

        /// Flow 평균 성능 점수
        member this.FlowAverageScore =
            if this.Calls.IsEmpty then 0.0
            else this.Calls |> List.averageBy (fun c -> c.PerformanceScore)

        /// Call 개수
        member this.CallCount = this.Calls.Length

        /// 이슈 개수 (성능 점수 < 50)
        member this.IssueCount =
            this.Calls |> List.filter (fun c -> c.PerformanceScore < 50.0) |> List.length

    /// Flow Placement (F# record)
    [<CLIMutable>]
    type FlowPlacement = {
        FlowId: Guid
        SystemId: Guid
        FlowName: string
        SystemName: string
        Col: int
        Row: int
        ColSpan: int
        RowSpan: int
    }

    /// Blueprint Layout (F# record)
    [<CLIMutable>]
    type BlueprintLayout = {
        BlueprintImagePath: string option
        CanvasWidth: int
        CanvasHeight: int
        GridColumns: int
        GridRows: int
        OffsetX: int
        OffsetY: int
        OffsetRight: int
        OffsetBottom: int
        FlowPlacements: FlowPlacement list
    } with
        /// Cell 너비 계산
        member this.CellWidth =
            if this.GridColumns > 0 then
                (this.CanvasWidth - this.OffsetX - this.OffsetRight) / this.GridColumns
            else
                200

        /// Cell 높이 계산
        member this.CellHeight =
            if this.GridRows > 0 then
                (this.CanvasHeight - this.OffsetY - this.OffsetBottom) / this.GridRows
            else
                200

    /// 기본 BlueprintLayout 생성
    let createDefaultBlueprintLayout() : BlueprintLayout =
        {
            BlueprintImagePath = None
            CanvasWidth = 1200
            CanvasHeight = 800
            GridColumns = 6
            GridRows = 4
            OffsetX = 0
            OffsetY = 0
            OffsetRight = 0
            OffsetBottom = 0
            FlowPlacements = []
        }

    /// 기본 FlowPlacement 생성
    let createFlowPlacement flowId systemId flowName systemName col row : FlowPlacement =
        {
            FlowId = flowId
            SystemId = systemId
            FlowName = flowName
            SystemName = systemName
            Col = col
            Row = row
            ColSpan = 1
            RowSpan = 1
        }
