namespace Ds2.Core

open System
open System.Security.Cryptography
open System.Text

// =============================================================================
// SIMULATION RESULT SNAPSHOT (AAS TechnicalData 박제용 POCO + 변환)
// =============================================================================

/// 시뮬레이션 결과 스냅샷 (TechnicalData 서브모델에 박제되는 표준화된 KPI 컨테이너)
[<AutoOpen>]
module SimulationResultSnapshotTypes =

    /// 시뮬 실행 메타데이터 (검증가능한 박제의 핵심)
    type SimulationMeta() =
        member val SimulatorName    = "Ds2.Runtime.Simulator" with get, set
        member val SimulatorVersion = "" with get, set
        /// ds2 모델 식별자 (디지털 스레드 키 - 같은 모델에서 나온 시뮬결과끼리 묶기 위함)
        member val Ds2ModelHash     = "" with get, set
        member val ScenarioId       = "" with get, set
        member val ScenarioName     = "" with get, set
        member val RunDate          = DateTime.UtcNow with get, set
        member val RunDuration_s    = 0.0 with get, set
        member val Seed             : int option = None with get, set
        member val SignedBy         = "" with get, set

    /// SimCycleTimeAnalysis → AAS 매핑용 POCO
    type KpiCycleTime() =
        member val WorkName                       = "" with get, set
        member val DesignCycleTime_s              = 0.0 with get, set
        member val ActualCycleTime_s              = 0.0 with get, set
        member val MinCycleTime_s                 = 0.0 with get, set
        member val MaxCycleTime_s                 = 0.0 with get, set
        member val StandardDeviation_s            = 0.0 with get, set
        member val VariationCoefficient           = 0.0 with get, set
        member val CycleCount                     = 0 with get, set
        member val ConfidenceInterval95_Lower_s   = 0.0 with get, set
        member val ConfidenceInterval95_Upper_s   = 0.0 with get, set
        member val IsNormalDistribution           = false with get, set
        member val DeviationFromDesign_pct        = 0.0 with get, set
        member val IsExceedingWarning             = false with get, set
        member val IsBottleneck                   = false with get, set
        member val BottleneckSeverity             = "" with get, set
        member val UtilizationRate_pct            = 0.0 with get, set
        member val ImprovementPotential_pct       = 0.0 with get, set
        member val RecommendedTargetCT_s          = 0.0 with get, set
        member val IdleGapBetweenCycles_s         = 0.0 with get, set
        member val EfficiencyRate_pct             = 0.0 with get, set

    /// Call 단위 사이클 분석 — Work 분석 차트 drill-down 용.
    type KpiCallCycleTime() =
        member val CallName               = "" with get, set
        member val ParentWorkName         = "" with get, set
        member val ActualCycleTime_s      = 0.0 with get, set
        member val MinCycleTime_s         = 0.0 with get, set
        member val MaxCycleTime_s         = 0.0 with get, set
        member val CycleCount             = 0 with get, set
        member val IdleGapBetweenCycles_s = 0.0 with get, set
        member val EfficiencyRate_pct     = 0.0 with get, set

    /// SimThroughputResult → AAS 매핑용 POCO
    type KpiThroughput() =
        member val StartTime              = DateTime.MinValue with get, set
        member val EndTime                = DateTime.MinValue with get, set
        member val ElapsedTime_s          = 0.0 with get, set
        member val TotalUnitsProduced     = 0 with get, set
        member val ThroughputPerHour      = 0.0 with get, set
        member val ThroughputPerDay       = 0.0 with get, set
        member val ThroughputPerWeek      = 0.0 with get, set
        member val ThroughputPerMonth     = 0.0 with get, set
        member val TaktTime_s             = 0.0 with get, set
        member val AverageCycleTime_s     = 0.0 with get, set
        member val CycleTimeMargin_pct    = 0.0 with get, set
        member val TargetThroughput       = 0.0 with get, set
        member val AchievementRate_pct    = 0.0 with get, set
        member val EfficiencyRate_pct     = 0.0 with get, set
        member val HourlyThroughputJson   = "" with get, set

    /// SimCapacityAnalysis → AAS 매핑용 POCO
    type KpiCapacity() =
        member val AnalysisDate                = DateTime.MinValue with get, set
        member val DesignCapacity              = 0.0 with get, set
        member val EffectiveCapacity           = 0.0 with get, set
        member val ActualCapacity              = 0.0 with get, set
        member val PlannedCapacity             = 0.0 with get, set
        member val DesignUtilization_pct       = 0.0 with get, set
        member val EffectiveUtilization_pct    = 0.0 with get, set
        member val CapacityUtilization_pct     = 0.0 with get, set
        member val CapacityGap                 = 0.0 with get, set
        member val Bottlenecks                 = ResizeArray<string>() with get, set
        member val RecommendedActions          = ResizeArray<string>() with get, set
        member val CapacityHorizon             = "" with get, set
        member val CapacityStrategy            = "" with get, set

    /// SimConstraintAnalysis 단일 항목 → AAS 매핑용 POCO
    type KpiConstraintItem() =
        member val ResourceName                = "" with get, set
        member val ConstraintType              = "" with get, set
        member val CurrentLoad                 = 0.0 with get, set
        member val MaxCapacity                 = 0.0 with get, set
        member val RemainingCapacity           = 0.0 with get, set
        member val IsConstraining              = false with get, set
        member val Severity                    = "" with get, set
        member val ImpactOnThroughput          = 0.0 with get, set
        member val EstimatedGainIfResolved     = 0.0 with get, set
        member val CurrentTocStep              = "" with get, set
        member val RecommendedActions          = ResizeArray<string>() with get, set

    /// SimResourceUtilization 단일 항목 → AAS 매핑용 POCO
    type KpiResourceItem() =
        member val ResourceName              = "" with get, set
        member val ResourceType              = "" with get, set
        member val AvailableTime_s           = 0.0 with get, set
        member val UsedTime_s                = 0.0 with get, set
        member val ProductionTime_s          = 0.0 with get, set
        member val ChangeoverTime_s          = 0.0 with get, set
        member val IdleTime_s                = 0.0 with get, set
        member val DownTime_s                = 0.0 with get, set
        member val UtilizationRate_pct       = 0.0 with get, set
        member val ProductiveRate_pct        = 0.0 with get, set
        member val AvailabilityRate_pct      = 0.0 with get, set
        member val IdleRate_pct              = 0.0 with get, set
        member val DownRate_pct              = 0.0 with get, set
        member val IndustryBenchmark         = 0.0 with get, set
        member val TargetUtilization         = 0.0 with get, set
        member val PerformanceGap            = 0.0 with get, set

    /// SimOeeTracking 단일 항목 → AAS 매핑용 POCO
    type KpiOeeItem() =
        member val ResourceName              = "" with get, set
        member val CalculationDate           = DateTime.MinValue with get, set
        member val CalculationPeriod_s       = 0.0 with get, set
        member val Availability              = 0.0 with get, set
        member val Performance               = 0.0 with get, set
        member val Quality                   = 0.0 with get, set
        member val OEE                       = 0.0 with get, set
        member val PlannedOperatingTime_s    = 0.0 with get, set
        member val ActualOperatingTime_s     = 0.0 with get, set
        member val PlannedProductionQty      = 0 with get, set
        member val ActualProductionQty       = 0 with get, set
        member val GoodProductQty            = 0 with get, set
        member val DefectQty                 = 0 with get, set
        member val TimeLoss_pct              = 0.0 with get, set
        member val SpeedLoss_pct             = 0.0 with get, set
        member val QualityLoss_pct           = 0.0 with get, set
        member val TargetOEE                 = 0.0 with get, set
        member val OeeGap                    = 0.0 with get, set
        member val OeeClass                  = "" with get, set

    /// 토큰 유형별 Work breakdown
    type KpiPerTokenWorkBreakdown() =
        member val WorkName              = "" with get, set
        member val VisitCount            = 0 with get, set
        member val AvgGoingTime_s        = 0.0 with get, set
        member val AvgFinishTime_s       = 0.0 with get, set

    /// 토큰 유형(혼류 환경에서 originName 기준 그룹) 별 KPI
    type KpiPerToken() =
        member val OriginName            = "" with get, set
        member val SpecLabel             = "" with get, set
        member val InstanceCount         = 0 with get, set
        member val CompletedCount        = 0 with get, set
        member val AvgTraversalTime_s    = 0.0 with get, set
        member val MinTraversalTime_s    = 0.0 with get, set
        member val MaxTraversalTime_s    = 0.0 with get, set
        member val StdDevTraversalTime_s = 0.0 with get, set
        member val ThroughputPerHour     = 0.0 with get, set
        member val FirstSeed             = DateTime.MinValue with get, set
        member val LastComplete          = DateTime.MinValue with get, set
        member val WorkBreakdown         = ResizeArray<KpiPerTokenWorkBreakdown>() with get, set

    /// 시뮬레이션 시나리오 1건의 박제 (Meta + 6 KPI 그룹 + Per-Token)
    type SimulationScenario() =
        member val Meta                  = SimulationMeta() with get, set
        member val CycleTimes            = ResizeArray<KpiCycleTime>() with get, set
        member val CallCycleTimes        = ResizeArray<KpiCallCycleTime>() with get, set
        member val Throughput            : KpiThroughput option = None with get, set
        member val Capacity              : KpiCapacity option = None with get, set
        member val Constraints           = ResizeArray<KpiConstraintItem>() with get, set
        member val ResourceUtilizations  = ResizeArray<KpiResourceItem>() with get, set
        member val OeeItems              = ResizeArray<KpiOeeItem>() with get, set
        member val PerTokenKpis          = ResizeArray<KpiPerToken>() with get, set

/// 시뮬 결과 변환 유틸 (ds2 런타임 struct → AAS 박제용 POCO)
module SimulationResultSnapshot =

    /// SHA-256 해시 헬퍼
    let computeModelHash (canonicalRepresentation: string) : string =
        if String.IsNullOrEmpty canonicalRepresentation then ""
        else
            use sha = SHA256.Create()
            let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalRepresentation))
            "sha256:" + (bytes |> Array.map (sprintf "%02x") |> String.concat "")

    let private hourlyToJson (hourly: (int * float) array) : string =
        if isNull (box hourly) || hourly.Length = 0 then ""
        else
            hourly
            |> Array.map (fun (h, v) ->
                sprintf "[%d,%s]" h (v.ToString("G", System.Globalization.CultureInfo.InvariantCulture)))
            |> String.concat ","
            |> sprintf "[%s]"

    let fromCycleTime (a: SimCycleTimeAnalysis) : KpiCycleTime =
        let r = KpiCycleTime()
        r.WorkName                     <- a.WorkName
        r.DesignCycleTime_s            <- a.DesignCycleTime
        r.ActualCycleTime_s            <- a.ActualCycleTime
        r.MinCycleTime_s               <- a.MinCycleTime
        r.MaxCycleTime_s               <- a.MaxCycleTime
        r.StandardDeviation_s          <- a.StandardDeviation
        r.VariationCoefficient         <- a.VariationCoefficient
        r.CycleCount                   <- a.CycleCount
        let lo, hi                     = a.ConfidenceInterval95
        r.ConfidenceInterval95_Lower_s <- lo
        r.ConfidenceInterval95_Upper_s <- hi
        r.IsNormalDistribution         <- a.IsNormalDistribution
        r.DeviationFromDesign_pct      <- a.DeviationFromDesign
        r.IsExceedingWarning           <- a.IsExceedingWarning
        r.IsBottleneck                 <- a.IsBottleneck
        r.BottleneckSeverity           <- a.BottleneckSeverity.ToString()
        r.UtilizationRate_pct          <- a.UtilizationRate
        r.ImprovementPotential_pct     <- a.ImprovementPotential
        r.RecommendedTargetCT_s        <- a.RecommendedTargetCT
        r.IdleGapBetweenCycles_s       <- a.IdleGapBetweenCycles
        r.EfficiencyRate_pct           <- a.EfficiencyRate
        r

    let fromThroughput (t: SimThroughputResult) : KpiThroughput =
        let r = KpiThroughput()
        r.StartTime            <- t.StartTime
        r.EndTime              <- t.EndTime
        r.ElapsedTime_s        <- t.ElapsedTime.TotalSeconds
        r.TotalUnitsProduced   <- t.TotalUnitsProduced
        r.ThroughputPerHour    <- t.ThroughputPerHour
        r.ThroughputPerDay     <- t.ThroughputPerDay
        r.ThroughputPerWeek    <- t.ThroughputPerWeek
        r.ThroughputPerMonth   <- t.ThroughputPerMonth
        r.TaktTime_s           <- t.TaktTime
        r.AverageCycleTime_s   <- t.AverageCycleTime
        r.CycleTimeMargin_pct  <- t.CycleTimeMargin
        r.TargetThroughput     <- t.TargetThroughput
        r.AchievementRate_pct  <- t.AchievementRate
        r.EfficiencyRate_pct   <- t.EfficiencyRate
        r.HourlyThroughputJson <- hourlyToJson t.HourlyThroughput
        r

    let fromCapacity (c: SimCapacityAnalysis) : KpiCapacity =
        let r = KpiCapacity()
        r.AnalysisDate             <- c.AnalysisDate
        r.DesignCapacity           <- c.DesignCapacity
        r.EffectiveCapacity        <- c.EffectiveCapacity
        r.ActualCapacity           <- c.ActualCapacity
        r.PlannedCapacity          <- c.PlannedCapacity
        r.DesignUtilization_pct    <- c.DesignUtilization
        r.EffectiveUtilization_pct <- c.EffectiveUtilization
        r.CapacityUtilization_pct  <- c.CapacityUtilization
        r.CapacityGap              <- c.CapacityGap
        if not (isNull (box c.Bottlenecks)) then
            for b in c.Bottlenecks do
                r.Bottlenecks.Add(b)
        if not (isNull (box c.RecommendedActions)) then
            for a in c.RecommendedActions do
                r.RecommendedActions.Add(a)
        r.CapacityHorizon          <- c.CapacityHorizon.ToString()
        r.CapacityStrategy         <- c.CapacityStrategy.ToString()
        r

    let fromConstraint (k: SimConstraintAnalysis) : KpiConstraintItem =
        let r = KpiConstraintItem()
        r.ResourceName            <- k.ResourceName
        r.ConstraintType          <- k.ConstraintType.ToString()
        r.CurrentLoad             <- k.CurrentLoad
        r.MaxCapacity             <- k.MaxCapacity
        r.RemainingCapacity       <- k.RemainingCapacity
        r.IsConstraining          <- k.IsConstraining
        r.Severity                <- k.Severity.ToString()
        r.ImpactOnThroughput      <- k.ImpactOnThroughput
        r.EstimatedGainIfResolved <- k.EstimatedGainIfResolved
        r.CurrentTocStep          <- k.CurrentTocStep.ToString()
        if not (isNull (box k.RecommendedActions)) then
            for a in k.RecommendedActions do
                r.RecommendedActions.Add(a)
        r

    let fromResource (u: SimResourceUtilization) : KpiResourceItem =
        let r = KpiResourceItem()
        r.ResourceName         <- u.ResourceName
        r.ResourceType         <- u.ResourceType
        r.AvailableTime_s      <- u.AvailableTime.TotalSeconds
        r.UsedTime_s           <- u.UsedTime.TotalSeconds
        r.ProductionTime_s     <- u.ProductionTime.TotalSeconds
        r.ChangeoverTime_s     <- u.ChangeoverTime.TotalSeconds
        r.IdleTime_s           <- u.IdleTime.TotalSeconds
        r.DownTime_s           <- u.DownTime.TotalSeconds
        r.UtilizationRate_pct  <- u.UtilizationRate
        r.ProductiveRate_pct   <- u.ProductiveRate
        r.AvailabilityRate_pct <- u.AvailabilityRate
        r.IdleRate_pct         <- u.IdleRate
        r.DownRate_pct         <- u.DownRate
        r.IndustryBenchmark    <- u.IndustryBenchmark
        r.TargetUtilization    <- u.TargetUtilization
        r.PerformanceGap       <- u.PerformanceGap
        r

    let fromOee (o: SimOeeTracking) : KpiOeeItem =
        let r = KpiOeeItem()
        r.ResourceName           <- o.ResourceName
        r.CalculationDate        <- o.CalculationDate
        r.CalculationPeriod_s    <- o.CalculationPeriod.TotalSeconds
        r.Availability           <- o.Availability
        r.Performance            <- o.Performance
        r.Quality                <- o.Quality
        r.OEE                    <- o.OEE
        r.PlannedOperatingTime_s <- o.PlannedOperatingTime.TotalSeconds
        r.ActualOperatingTime_s  <- o.ActualOperatingTime.TotalSeconds
        r.PlannedProductionQty   <- o.PlannedProductionQty
        r.ActualProductionQty    <- o.ActualProductionQty
        r.GoodProductQty         <- o.GoodProductQty
        r.DefectQty              <- o.DefectQty
        r.TimeLoss_pct           <- o.TimeLoss
        r.SpeedLoss_pct          <- o.SpeedLoss
        r.QualityLoss_pct        <- o.QualityLoss
        r.TargetOEE              <- o.TargetOEE
        r.OeeGap                 <- o.OeeGap
        r.OeeClass               <- o.OeeClass
        r

    /// 6종 KPI + 토큰별 KPI 를 단일 시나리오 스냅샷으로 합성
    let buildScenario
        (meta: SimulationMeta)
        (cycleTimes: SimCycleTimeAnalysis seq)
        (throughput: SimThroughputResult option)
        (capacity: SimCapacityAnalysis option)
        (constraints: SimConstraintAnalysis seq)
        (resources: SimResourceUtilization seq)
        (oeeItems: SimOeeTracking seq)
        (perTokenKpis: KpiPerToken seq) : SimulationScenario =
        let s = SimulationScenario()
        s.Meta <- meta
        cycleTimes |> Seq.iter (fun a -> s.CycleTimes.Add(fromCycleTime a))
        s.Throughput <- throughput |> Option.map fromThroughput
        s.Capacity <- capacity |> Option.map fromCapacity
        constraints |> Seq.iter (fun a -> s.Constraints.Add(fromConstraint a))
        resources |> Seq.iter (fun a -> s.ResourceUtilizations.Add(fromResource a))
        oeeItems |> Seq.iter (fun a -> s.OeeItems.Add(fromOee a))
        perTokenKpis |> Seq.iter (fun p -> s.PerTokenKpis.Add p)
        s
