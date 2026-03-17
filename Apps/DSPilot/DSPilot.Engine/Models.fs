namespace DSPilot.Engine

open System

/// View Models 및 DTO
module Models =

    /// Call Heatmap 아이템 (F# record)
    type CallHeatmapItem = {
        CallName: string
        FlowName: string
        WorkName: string
        AverageGoingTime: float
        StdDevGoingTime: float
        GoingCount: int
        PerformanceScore: float
        ColorClass: string
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

        /// 툴팁 텍스트 생성
        member this.GetTooltipText() =
            sprintf "%s\n평균: %.0fms\n표준편차: %.0fms\n변동계수: %.2f\n성능점수: %.0f\n실행횟수: %d"
                this.CallName
                this.AverageGoingTime
                this.StdDevGoingTime
                this.CoefficientOfVariation
                this.PerformanceScore
                this.GoingCount

    /// Flow Heatmap 그룹 (F# record)
    type FlowHeatmapGroup = {
        FlowName: string
        Calls: CallHeatmapItem list
        mutable IsExpanded: bool
    } with
        /// Flow 평균 Going 시간
        member this.FlowAverageTime =
            if this.Calls.IsEmpty then 0.0
            else this.Calls |> List.averageBy (fun c -> c.AverageGoingTime)

        /// Flow 평균 표준편차
        member this.FlowAverageStdDev =
            if this.Calls.IsEmpty then 0.0
            else this.Calls |> List.averageBy (fun c -> c.StdDevGoingTime)

        /// Flow 평균 성능 점수
        member this.FlowAverageScore =
            if this.Calls.IsEmpty then 0.0
            else this.Calls |> List.averageBy (fun c -> c.PerformanceScore)

        /// Call 개수
        member this.CallCount = this.Calls.Length

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
