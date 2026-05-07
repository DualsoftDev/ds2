namespace Ds2.Runtime.Report

open System
open Ds2.Core
open Ds2.Runtime.Report.Model
open Ds2.Runtime.Report.KpiAggregatorHelpers

/// SimulationReport (StateSegment 기반 텔레메트리) → 01_Simulation.fs 의 6종 KPI struct 변환
/// 모든 계산은 best-effort. Planned 값은 SimulationSystemProperties 에서 가져오고 없으면 0.
module KpiAggregator =

    // ── 개별 KPI 빌더 ──────────────────────────────────────────────────────

    /// 작업(Work) 별 SimCycleTimeAnalysis 산출
    let buildCycleTimes
            (designCtFor: string -> float)   // workName → design cycle time (없으면 0)
            (warningPct: float)              // 설계 대비 경고 임계 (%)
            (report: SimulationReport) : SimCycleTimeAnalysis array =
        SimulationReport.getWorks report
        |> List.map (fun entry ->
            let cts = cycleDurations entry
            let count = List.length cts
            let avg   = mean cts
            let stD   = stdDev cts avg
            let cv    = if avg > 0.0 then stD / avg else 0.0
            let mn    = if cts.IsEmpty then 0.0 else List.min cts
            let mx    = if cts.IsEmpty then 0.0 else List.max cts
            let design = designCtFor entry.Name
            let deviation =
                if design > 0.0 then ((avg - design) / design) * 100.0 else 0.0
            // 95% CI: n<31 → t-분포, n≥31 → 정규근사
            let ci95 = confidenceInterval95 avg stD count
            // D'Agostino-Pearson K² 기반 정규성 검정 (n≥8 필요)
            let isNormal = isNormalDAgostinoK2 cts avg stD
            // 활용률: G 시간 / 보고서 전체 시간
            let total = SimulationReport.getTotalDurationSeconds report
            let gTime = secondsOf entry.Segments StateGoing
            let utilFrac = if total > 0.0 then gTime / total else 0.0
            let utilPct  = utilFrac * 100.0
            let severity = bottleneckSeverityOf utilFrac
            let isBottleneck = utilFrac >= 0.90
            let improvement =
                if avg > 0.0 && mn > 0.0 && avg > mn then ((avg - mn) / avg) * 100.0 else 0.0
            let recommendedTarget = if mn > 0.0 then mn + (avg - mn) * 0.2 else avg

            // 사이클 사이 낭비 시간 — 각 Finish 종료 시각과 그 다음 Going 시작 시각의 양수 차 합계.
            //   "Finish 후 다음 Going 전까지의 idle 은 낭비" 의미. R/H 세그먼트가 그 사이에 끼어 있어도 합산.
            let idleGapBetweenCycles = idleGapBetweenCyclesOf entry
            let efficiencyDen = gTime + idleGapBetweenCycles
            let efficiencyPct =
                if efficiencyDen > 0.0 then (gTime / efficiencyDen) * 100.0 else 0.0
            {
                WorkId = (match Guid.TryParse entry.Id with true, g -> g | _ -> Guid.Empty)
                WorkName = entry.Name
                DesignCycleTime = design
                ActualCycleTime = avg
                MinCycleTime = mn
                MaxCycleTime = mx
                StandardDeviation = stD
                VariationCoefficient = cv
                CycleCount = count
                ConfidenceInterval95 = ci95
                IsNormalDistribution = isNormal
                DeviationFromDesign = deviation
                IsExceedingWarning = (deviation > warningPct)
                IsBottleneck = isBottleneck
                BottleneckSeverity = severity
                UtilizationRate = utilPct
                ImprovementPotential = improvement
                RecommendedTargetCT = recommendedTarget
                IdleGapBetweenCycles = idleGapBetweenCycles
                EfficiencyRate = efficiencyPct
            })
        |> List.toArray

    /// Call 단위 사이클 분석 — Work drill-down 차트용.
    /// Type="Call" 인 ReportEntry 들을 Work 사이클과 동일 공식으로 집계.
    ///
    /// ParentWorkName 결정 우선순위:
    ///   1. ReportEntry.ParentWorkId 로 entries 에서 Type="Work" 매칭. (현재 ReportService 는 None 채움.)
    ///   2. 호출자가 제공한 callToWorkName : CallName → ParentWorkName lookup. (store 기반 fallback.)
    let buildCallCycleTimes
            (callToWorkName: string -> string)
            (report: SimulationReport)
            : Ds2.Core.SimulationResultSnapshotTypes.KpiCallCycleTime array =
        let workNameById =
            report.Entries
            |> List.filter (fun e -> e.Type = "Work")
            |> List.map (fun e -> e.Id, e.Name)
            |> Map.ofList
        SimulationReport.getCalls report
        |> List.map (fun entry ->
            let cts = cycleDurations entry
            let count = List.length cts
            let avg   = mean cts
            let mn    = if cts.IsEmpty then 0.0 else List.min cts
            let mx    = if cts.IsEmpty then 0.0 else List.max cts
            let gTime = secondsOf entry.Segments StateGoing

            let idleGap = idleGapBetweenCyclesOf entry

            let den = gTime + idleGap
            let efficiencyPct = if den > 0.0 then (gTime / den) * 100.0 else 0.0

            let parentWorkName =
                match entry.ParentWorkId with
                | Some pid ->
                    match Map.tryFind pid workNameById with
                    | Some n when not (System.String.IsNullOrEmpty n) -> n
                    | _ -> callToWorkName entry.Name
                | None -> callToWorkName entry.Name

            let r = Ds2.Core.SimulationResultSnapshotTypes.KpiCallCycleTime()
            r.CallName               <- entry.Name
            r.ParentWorkName         <- parentWorkName
            r.ActualCycleTime_s      <- avg
            r.MinCycleTime_s         <- mn
            r.MaxCycleTime_s         <- mx
            r.CycleCount             <- count
            r.IdleGapBetweenCycles_s <- idleGap
            r.EfficiencyRate_pct     <- efficiencyPct
            r)
        |> List.toArray

    let buildThroughput = KpiAggregatorBuilders.buildThroughput

    let buildCapacity = KpiAggregatorBuilders.buildCapacity

    let buildConstraints = KpiAggregatorBuilders.buildConstraints

    let buildResourceUtilizations = KpiAggregatorBuilders.buildResourceUtilizations

    let buildOees = KpiAggregatorBuilders.buildOees

    // ── Facade: 한 번에 6종 KPI 산출 ────────────────────────────────────────
    type KpiInputs = {
        /// 시스템-레벨 시뮬 속성 (있으면 Capacity/Throughput 베이스라인으로 사용)
        SimSystemProps: SimulationSystemProperties option
        /// Work 이름 → DesignCycleTime 매핑 (없으면 0)
        DesignCycleTimeFor: string -> float
    }

    let defaultInputs : KpiInputs = {
        SimSystemProps = None
        DesignCycleTimeFor = (fun _ -> 0.0)
    }

    type AggregatedKpis = {
        CycleTimes: SimCycleTimeAnalysis array
        Throughput: SimThroughputResult
        Capacity: SimCapacityAnalysis
        Constraints: SimConstraintAnalysis array
        ResourceUtilizations: SimResourceUtilization array
        OeeItems: SimOeeTracking array
    }

    // ── Per-Token KPI 집계 (혼류 환경) ────────────────────────────────────
    /// 토큰 traversal 1 회의 원시 측정치 — 호출자가 TokenEvent 구독으로 수집
    type TokenTraversal = {
        TokenItem: int
        OriginName: string
        SpecLabel: string
        SeedAt: DateTime
        CompleteAt: DateTime option
        /// 경유 Work 별 G+F 누적시간 (초)
        WorkTimes: (string * float) list
    }

    /// origin/spec 별로 그룹핑하여 KpiPerToken 배열 산출
    let buildPerTokenKpis (traversals: TokenTraversal seq) : KpiPerToken array =
        traversals
        |> Seq.toList
        |> List.groupBy (fun traversal -> traversal.OriginName)
        |> List.map (fun (origin, group) ->
            let completed =
                group
                |> List.choose (fun traversal ->
                    match traversal.CompleteAt with
                    | Some completeAt -> Some (traversal, (completeAt - traversal.SeedAt).TotalSeconds)
                    | None -> None)

            let durations = completed |> List.map snd
            let count = List.length durations
            let avg = if count = 0 then 0.0 else List.average durations
            let mn = if count = 0 then 0.0 else List.min durations
            let mx = if count = 0 then 0.0 else List.max durations
            let stD =
                if count < 2 then 0.0
                else
                    let nf = float count
                    let variance = durations |> List.sumBy (fun x -> (x - avg) ** 2.0) |> fun s -> s / (nf - 1.0)
                    sqrt variance
            let firstSeed =
                if group.IsEmpty then DateTime.MinValue
                else group |> List.map (fun traversal -> traversal.SeedAt) |> List.min
            let lastComplete =
                if completed.IsEmpty then DateTime.MinValue
                else completed |> List.map (fun (traversal, _) -> traversal.CompleteAt.Value) |> List.max
            let elapsedHours =
                if completed.IsEmpty then 0.0
                else (lastComplete - firstSeed).TotalHours
            let throughputPerHour =
                if elapsedHours > 0.0 then float count / elapsedHours else 0.0
            let workBreakdown =
                group
                |> List.collect (fun traversal -> traversal.WorkTimes)
                |> List.groupBy fst
                |> List.map (fun (workName, items) ->
                    let times = items |> List.map snd
                    let visitCount = List.length times
                    let avgTime = if visitCount = 0 then 0.0 else List.average times
                    let breakdown = KpiPerTokenWorkBreakdown()
                    breakdown.WorkName <- workName
                    breakdown.VisitCount <- visitCount
                    breakdown.AvgGoingTime_s <- avgTime
                    breakdown.AvgFinishTime_s <- 0.0
                    breakdown)
                |> List.sortByDescending (fun breakdown -> breakdown.AvgGoingTime_s)
            let specLabel =
                group
                |> List.tryFind (fun traversal -> not (String.IsNullOrEmpty traversal.SpecLabel))
                |> Option.map (fun traversal -> traversal.SpecLabel)
                |> Option.defaultValue origin
            let result = KpiPerToken()
            result.OriginName <- if isNull origin then "" else origin
            result.SpecLabel <- specLabel
            result.InstanceCount <- List.length group
            result.CompletedCount <- count
            result.AvgTraversalTime_s <- avg
            result.MinTraversalTime_s <- mn
            result.MaxTraversalTime_s <- mx
            result.StdDevTraversalTime_s <- stD
            result.ThroughputPerHour <- throughputPerHour
            result.FirstSeed <- firstSeed
            result.LastComplete <- lastComplete
            workBreakdown |> List.iter result.WorkBreakdown.Add
            result)
        |> List.toArray

    /// 6종 KPI 일괄 산출
    let aggregate (inputs: KpiInputs) (report: SimulationReport) : AggregatedKpis =
        let p = inputs.SimSystemProps
        let designPerHour    = p |> Option.map (fun x -> x.DesignCapacityPerHour)    |> Option.defaultValue 0.0
        let effectivePerHour = p |> Option.map (fun x -> x.EffectiveCapacityPerHour) |> Option.defaultValue 0.0
        let plannedPerHour   = p |> Option.map (fun x -> x.PlannedCapacityPerHour)   |> Option.defaultValue 0.0
        let taktTime         = p |> Option.map (fun x -> x.TaktTime)                 |> Option.defaultValue 0.0
        let targetThPerHour  = p |> Option.map (fun x -> x.TargetThroughputPerHour)  |> Option.defaultValue 0.0
        let bottleneck       = p |> Option.map (fun x -> x.BottleneckThreshold)      |> Option.defaultValue 0.90
        let warnPct          = p |> Option.map (fun x -> x.CycleTimeWarningThreshold) |> Option.defaultValue 10.0
        // 산업별 World-Class OEE 벤치마크 (자체 inline — 외부 helper 의존 제거).
        let industryBenchmarkOf (industryType: string) =
            if isNull industryType then 80.0
            else
                match industryType.ToLowerInvariant() with
                | "automotive" -> 87.5
                | "electronics" | "semiconductor" -> 92.5
                | "food" | "beverage" -> 80.0
                | "pharmaceutical" -> 75.0
                | "metal" | "steel" -> 85.0
                | "plastic" -> 80.0
                | "logistics" | "packaging" -> 77.5
                | _ -> 80.0
        let benchmark        =
            p
            |> Option.map (fun x -> industryBenchmarkOf x.IndustryType)
            |> Option.defaultValue 80.0
        let targetUtil       = p |> Option.map (fun x -> x.TargetUtilizationRate)    |> Option.defaultValue 85.0
        let targetOee        = p |> Option.map (fun x -> x.TargetOEE)                |> Option.defaultValue 85.0
        let horizon =
            p
            |> Option.map (fun x ->
                match x.CapacityHorizon with
                | "ShortTerm" -> ShortTerm
                | "LongTerm"  -> LongTerm
                | _ -> MediumTerm)
            |> Option.defaultValue MediumTerm
        let strategy =
            p
            |> Option.map (fun x ->
                match x.CapacityStrategy with
                | "LevelStrategy" -> LevelStrategy
                | "ChaseStrategy" -> ChaseStrategy
                | _ -> MixedStrategy)
            |> Option.defaultValue MixedStrategy

        let cycleTimes = buildCycleTimes inputs.DesignCycleTimeFor warnPct report
        let throughput = buildThroughput taktTime targetThPerHour report
        let capacity   = buildCapacity designPerHour effectivePerHour plannedPerHour horizon strategy throughput
        let constraints = buildConstraints bottleneck report throughput
        let resources  = buildResourceUtilizations benchmark targetUtil report
        let oees       = buildOees inputs.DesignCycleTimeFor targetOee report
        {
            CycleTimes = cycleTimes
            Throughput = throughput
            Capacity = capacity
            Constraints = constraints
            ResourceUtilizations = resources
            OeeItems = oees
        }
