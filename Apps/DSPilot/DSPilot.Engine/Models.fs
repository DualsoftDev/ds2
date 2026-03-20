namespace DSPilot.Engine

open System

/// View Models 및 DTO
module Models =

    /// Call Heatmap 아이템 (F# record) - 매트릭스 히트맵용 per-metric 색상
    type CallHeatmapItem = {
        CallName: string
        FlowName: string
        WorkName: string
        AverageGoingTime: float
        StdDevGoingTime: float
        GoingCount: int
        PerformanceScore: float
        ColorClassAvg: string
        ColorClassStdDev: string
        ColorClassCV: string
        ColorClassScore: string
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

            sprintf "[%s]\n평균: %.0f ms | 표준편차: %.0f ms\n변동계수: %.2f (%s)\n성능점수: %.0f/100 (%s)\n실행횟수: %d회"
                this.CallName
                this.AverageGoingTime
                this.StdDevGoingTime
                this.CoefficientOfVariation
                cvStatus
                this.PerformanceScore
                scoreStatus
                this.GoingCount

    /// Flow Heatmap 그룹 (F# record) - Flow 수준 집계 색상 포함
    type FlowHeatmapGroup = {
        FlowName: string
        Calls: CallHeatmapItem list
        mutable IsExpanded: bool
        FlowColorClassAvg: string
        FlowColorClassStdDev: string
        FlowColorClassCV: string
        FlowColorClassScore: string
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

        /// 이슈 Call 개수 (개선필요 또는 심각)
        member this.IssueCount =
            this.Calls |> List.filter (fun c ->
                c.ColorClassScore = "heatmap-poor" || c.ColorClassScore = "heatmap-critical") |> List.length

    /// Tag Edge State (F# record)
    type TagEdgeState = {
        TagName: string
        PreviousValue: string
        CurrentValue: string
        LastUpdateTime: DateTime
        EdgeType: EdgeType
    } with
        /// 라이징 엣지 여부
        member this.IsRisingEdge() = EdgeDetection.isRising this.EdgeType

        /// 폴링 엣지 여부
        member this.IsFallingEdge() = EdgeDetection.isFalling this.EdgeType

        /// ToString
        override this.ToString() =
            let edgeStr =
                if EdgeDetection.isRising this.EdgeType then "Rising"
                elif EdgeDetection.isFalling this.EdgeType then "Falling"
                else "None"
            sprintf "%s: %s → %s (%s)" this.TagName this.PreviousValue this.CurrentValue edgeStr

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

    /// 초기 TagEdgeState 생성
    let createTagEdgeState tagName currentValue =
        let edgeType = EdgeDetection.detectEdge None currentValue
        {
            TagName = tagName
            PreviousValue = "0"
            CurrentValue = currentValue
            LastUpdateTime = DateTime.Now
            EdgeType = edgeType
        }

    /// TagEdgeState 업데이트
    let updateTagEdgeState (state: TagEdgeState) newValue =
        let edgeType = EdgeDetection.detectEdge (Some state.CurrentValue) newValue
        {
            state with
                PreviousValue = state.CurrentValue
                CurrentValue = newValue
                LastUpdateTime = DateTime.Now
                EdgeType = edgeType
        }

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
