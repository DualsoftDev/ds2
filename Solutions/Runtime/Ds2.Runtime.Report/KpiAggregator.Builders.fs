namespace Ds2.Runtime.Report

open System
open Ds2.Core
open Ds2.Runtime.Report.Model
open Ds2.Runtime.Report.KpiAggregatorHelpers

module KpiAggregatorBuilders =

    /// 시스템 전체 SimThroughputResult 산출 (단위 = Work F-카운트 합계)
    let buildThroughput
            (taktTime: float)
            (targetThroughputPerHour: float)
            (report: SimulationReport) : SimThroughputResult =
        let elapsed = report.Metadata.TotalDuration
        let elapsedHours = elapsed.TotalSeconds / 3600.0
        let totalUnits =
            SimulationReport.getWorks report
            |> List.sumBy finishCount
        let perHour = if elapsedHours > 0.0 then float totalUnits / elapsedHours else 0.0
        let perDay = perHour * 24.0
        let perWeek = perDay * 7.0
        let perMonth = perDay * 30.0
        let avgCt =
            let cts =
                SimulationReport.getWorks report
                |> List.collect cycleDurations
            mean cts
        let margin = if taktTime > 0.0 then ((taktTime - avgCt) / taktTime) * 100.0 else 0.0
        let achievement =
            if targetThroughputPerHour > 0.0 then (perHour / targetThroughputPerHour) * 100.0 else 0.0
        let hourly =
            if elapsedHours <= 0.0 then [||]
            else
                let buckets = max 1 (int (ceil elapsedHours))
                [| for i in 0 .. buckets - 1 ->
                    let lo = report.Metadata.StartTime.AddHours(float i)
                    let hi = report.Metadata.StartTime.AddHours(float (i + 1))
                    let count =
                        SimulationReport.getWorks report
                        |> List.sumBy (fun entry ->
                            entry.Segments
                            |> List.filter (fun segment -> segment.State = StateFinish && segment.StartTime >= lo && segment.StartTime < hi)
                            |> List.length)
                    (i, float count) |]
        {
            StartTime = report.Metadata.StartTime
            EndTime = report.Metadata.EndTime
            ElapsedTime = elapsed
            TotalUnitsProduced = totalUnits
            ThroughputPerHour = perHour
            ThroughputPerDay = perDay
            ThroughputPerWeek = perWeek
            ThroughputPerMonth = perMonth
            TaktTime = taktTime
            AverageCycleTime = avgCt
            CycleTimeMargin = margin
            TargetThroughput = targetThroughputPerHour
            AchievementRate = achievement
            EfficiencyRate = achievement
            HourlyThroughput = hourly
        }

    /// SimCapacityAnalysis (시스템 단일)
    let buildCapacity
            (designPerHour: float)
            (effectivePerHour: float)
            (plannedPerHour: float)
            (horizon: CapacityHorizon)
            (strategy: CapacityStrategy)
            (throughput: SimThroughputResult) : SimCapacityAnalysis =
        let actual = throughput.ThroughputPerHour
        let designUtil = if designPerHour > 0.0 then (actual / designPerHour) * 100.0 else 0.0
        let effectiveUtil = if effectivePerHour > 0.0 then (actual / effectivePerHour) * 100.0 else 0.0
        let capacityUtil = if plannedPerHour > 0.0 then (actual / plannedPerHour) * 100.0 else 0.0
        let gap = plannedPerHour - actual
        {
            AnalysisId = Guid.NewGuid()
            AnalysisDate = DateTime.UtcNow
            DesignCapacity = designPerHour
            EffectiveCapacity = effectivePerHour
            ActualCapacity = actual
            PlannedCapacity = plannedPerHour
            DesignUtilization = designUtil
            EffectiveUtilization = effectiveUtil
            CapacityUtilization = capacityUtil
            CapacityGap = gap
            Bottlenecks = [||]
            RecommendedActions = [||]
            CapacityHorizon = horizon
            CapacityStrategy = strategy
        }

    /// 작업별 SimConstraintAnalysis (high-utilization Work 만 제약 후보)
    let buildConstraints
            (bottleneckThreshold: float)
            (report: SimulationReport)
            (throughput: SimThroughputResult) : SimConstraintAnalysis array =
        let total = SimulationReport.getTotalDurationSeconds report
        SimulationReport.getWorks report
        |> List.choose (fun entry ->
            if total <= 0.0 then None
            else
                let g = secondsOf entry.Segments StateGoing
                let frac = g / total
                if frac < bottleneckThreshold then None
                else
                    let severity = bottleneckSeverityOf frac
                    Some {
                        ConstraintId = Guid.NewGuid()
                        AnalysisDate = DateTime.UtcNow
                        ResourceName = entry.Name
                        ConstraintType = TimeConstraint
                        CurrentLoad = frac * 100.0
                        MaxCapacity = 100.0
                        RemainingCapacity = (1.0 - frac) * 100.0
                        IsConstraining = true
                        Severity = severity
                        ImpactOnThroughput = (frac - bottleneckThreshold) * throughput.ThroughputPerHour
                        EstimatedGainIfResolved =
                            if frac > 0.0 then throughput.ThroughputPerHour * ((frac - 0.80) / frac) else 0.0
                        CurrentTocStep = Identify
                        RecommendedActions = [| "검토 권장: 사이클 타임 단축 또는 병렬화" |]
                    })
        |> List.toArray

    /// 작업별 SimResourceUtilization
    let buildResourceUtilizations
            (industryBenchmark: float)
            (targetUtilization: float)
            (report: SimulationReport) : SimResourceUtilization array =
        let total = SimulationReport.getTotalDurationSeconds report
        let totalSpan = TimeSpan.FromSeconds(total)
        SimulationReport.getWorks report
        |> List.map (fun entry ->
            let gTime = secondsOf entry.Segments StateGoing
            let fTime = secondsOf entry.Segments StateFinish
            let hTime = secondsOf entry.Segments StateHoming
            let rTime = secondsOf entry.Segments StateReady
            let used = gTime + fTime
            let utilPct =
                if total > 0.0 then (used / total) * 100.0 else 0.0
            let prodPct =
                if total > 0.0 then (gTime / total) * 100.0 else 0.0
            let availPct =
                if total > 0.0 then ((total - hTime) / total) * 100.0 else 0.0
            let idlePct =
                if total > 0.0 then (rTime / total) * 100.0 else 0.0
            let downPct =
                if total > 0.0 then (hTime / total) * 100.0 else 0.0
            {
                ResourceId = (match Guid.TryParse entry.Id with true, guid -> guid | _ -> Guid.Empty)
                ResourceName = entry.Name
                ResourceType = "Work"
                AvailableTime = totalSpan
                UsedTime = TimeSpan.FromSeconds used
                ProductionTime = TimeSpan.FromSeconds gTime
                ChangeoverTime = TimeSpan.FromSeconds fTime
                IdleTime = TimeSpan.FromSeconds rTime
                DownTime = TimeSpan.FromSeconds hTime
                UtilizationRate = utilPct
                ProductiveRate = prodPct
                AvailabilityRate = availPct
                IdleRate = idlePct
                DownRate = downPct
                IndustryBenchmark = industryBenchmark
                TargetUtilization = targetUtilization
                PerformanceGap = targetUtilization - utilPct
                HourlyUtilization = [||]
            })
        |> List.toArray

    /// 작업별 SimOeeTracking — 시뮬레이션 가정.
    let buildOees
            (designCtFor: string -> float)
            (targetOee: float)
            (report: SimulationReport) : SimOeeTracking array =
        let plannedSpan = report.Metadata.TotalDuration
        SimulationReport.getWorks report
        |> List.map (fun entry ->
            let gTime = secondsOf entry.Segments StateGoing
            let actualSpan = TimeSpan.FromSeconds gTime
            let producedQty = finishCount entry
            let idleGapBetweenCycles = idleGapBetweenCyclesOf entry
            let availability = 1.0
            let performance =
                let den = gTime + idleGapBetweenCycles
                if den > 0.0 then min 1.0 (max 0.0 (gTime / den))
                elif producedQty > 0 then 1.0
                else 0.0
            let quality = 1.0
            let oee = availability * performance * quality
            let standardCt =
                let d = designCtFor entry.Name
                if d > 0.0 then d
                elif producedQty > 0 && gTime > 0.0 then gTime / float producedQty
                else 0.0
            let timeLoss =
                if plannedSpan.TotalSeconds > 0.0 then
                    ((plannedSpan.TotalSeconds - actualSpan.TotalSeconds) / plannedSpan.TotalSeconds) * 100.0
                else 0.0
            let speedLoss =
                if standardCt > 0.0 && producedQty > 0 then
                    let actualCt = if producedQty > 0 then actualSpan.TotalSeconds / float producedQty else 0.0
                    if actualCt > 0.0 then ((actualCt - standardCt) / actualCt) * 100.0 else 0.0
                else 0.0
            let oeeClass =
                let pct = oee * 100.0
                if pct >= 85.0 then "World Class"
                elif pct >= 75.0 then "Excellent"
                elif pct >= 65.0 then "Good"
                elif pct >= 50.0 then "Fair"
                else "Poor"
            {
                ResourceId = (match Guid.TryParse entry.Id with true, guid -> guid | _ -> Guid.Empty)
                ResourceName = entry.Name
                CalculationDate = DateTime.UtcNow
                CalculationPeriod = plannedSpan
                Availability = availability
                Performance = performance
                Quality = quality
                OEE = oee
                PlannedOperatingTime = plannedSpan
                ActualOperatingTime = actualSpan
                PlannedProductionQty = 0
                ActualProductionQty = producedQty
                GoodProductQty = producedQty
                DefectQty = 0
                TimeLoss = timeLoss
                SpeedLoss = speedLoss
                QualityLoss = 0.0
                TargetOEE = targetOee
                OeeGap = (targetOee - oee * 100.0)
                OeeClass = oeeClass
            })
        |> List.toArray
