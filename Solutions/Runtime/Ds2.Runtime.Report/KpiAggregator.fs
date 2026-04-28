namespace Ds2.Runtime.Report

open System
open Ds2.Core
open Ds2.Runtime.Report.Model

/// SimulationReport (StateSegment 기반 텔레메트리) → 01_Simulation.fs 의 6종 KPI struct 변환
/// 모든 계산은 best-effort. Planned 값은 SimulationSystemProperties 에서 가져오고 없으면 0.
module KpiAggregator =

    // 상태 코드 (StateSegment.State)
    let [<Literal>] private StateGoing  = "G"
    let [<Literal>] private StateFinish = "F"
    let [<Literal>] private StateHoming = "H"
    let [<Literal>] private StateReady  = "R"

    let private secondsOf (segs: StateSegment list) (state: string) : float =
        segs |> List.filter (fun s -> s.State = state) |> List.sumBy (fun s -> s.DurationSeconds)

    let private cycleDurations (entry: ReportEntry) : float list =
        // G-세그먼트 시작에서 다음 G-세그먼트 시작까지를 한 사이클로 본다.
        // 마지막 G 는 종료 사이클이 없으므로 제외.
        let gStarts =
            entry.Segments
            |> List.filter (fun s -> s.State = StateGoing)
            |> List.map (fun s -> s.StartTime)
        match gStarts with
        | [] | [_] -> []
        | _ ->
            gStarts
            |> List.pairwise
            |> List.map (fun (a, b) -> (b - a).TotalSeconds)

    let private finishCount (entry: ReportEntry) : int =
        entry.Segments |> List.filter (fun s -> s.State = StateFinish) |> List.length

    let private mean xs =
        match xs with [] -> 0.0 | _ -> List.average xs

    let private stdDev (xs: float list) (avg: float) =
        match xs with
        | [] | [_] -> 0.0
        | _ ->
            let n = float (List.length xs)
            let variance = xs |> List.sumBy (fun x -> (x - avg) ** 2.0) |> fun s -> s / (n - 1.0)
            sqrt variance

    // ── 통계 헬퍼 (CI95 with t-distribution + D'Agostino K² normality test) ────
    /// Student's t critical values at α=0.025 (two-sided 95% CI), df=1..30.
    /// df ≥ 30 일 경우 z=1.96 사용.
    let private tCritical95 = [|
        12.706; 4.303; 3.182; 2.776; 2.571; 2.447; 2.365; 2.306; 2.262; 2.228   // df 1..10
        2.201; 2.179; 2.160; 2.145; 2.131; 2.120; 2.110; 2.101; 2.093; 2.086    // df 11..20
        2.080; 2.074; 2.069; 2.064; 2.060; 2.056; 2.052; 2.048; 2.045; 2.042    // df 21..30
    |]

    let private tCriticalAt95 (df: int) : float =
        if df < 1 then 12.706
        elif df <= 30 then tCritical95.[df - 1]
        else 1.96  // 정규근사

    /// 95% 신뢰구간 (n=2..30 은 t-분포, n>=31 은 정규근사)
    let private confidenceInterval95 (avg: float) (stD: float) (n: int) : float * float =
        if n < 2 then (avg, avg)
        else
            let df = n - 1
            let tCrit = tCriticalAt95 df
            let sem = stD / sqrt (float n)
            let margin = tCrit * sem
            (avg - margin, avg + margin)

    /// 표본 왜도(skewness) — biased sample 추정
    let private skewness (xs: float list) (avg: float) (stD: float) : float =
        match xs with
        | [] | [_] | [_; _] -> 0.0
        | _ when stD <= 0.0 -> 0.0
        | _ ->
            let n = float (List.length xs)
            let m3 = xs |> List.sumBy (fun x -> (x - avg) ** 3.0) |> fun s -> s / n
            m3 / (stD ** 3.0)

    /// 표본 첨도(kurtosis, excess) — biased sample 추정
    let private excessKurtosis (xs: float list) (avg: float) (stD: float) : float =
        match xs with
        | [] | [_] | [_; _] | [_; _; _] -> 0.0
        | _ when stD <= 0.0 -> 0.0
        | _ ->
            let n = float (List.length xs)
            let m4 = xs |> List.sumBy (fun x -> (x - avg) ** 4.0) |> fun s -> s / n
            (m4 / (stD ** 4.0)) - 3.0

    /// D'Agostino-Pearson K² omnibus 정규성 검정 (간이판).
    /// n ≥ 8 에서 z(skew)² + z(kurt)² 가 χ²(2)의 5% 임계값(=5.991)을 초과하면 비정규.
    /// (참고: 정확한 K² 는 더 복잡한 변환을 거치지만, 여기서는 √(n/6)·skew, √(n/24)·kurt 의
    ///  점근 정규근사를 사용한 단순화 버전 — 표본의 정규성 신호로 충분히 유용.)
    let private isNormalDAgostinoK2 (xs: float list) (avg: float) (stD: float) : bool =
        let n = List.length xs
        if n < 8 then false  // 표본 부족 — 보수적으로 비정규
        elif stD <= 0.0 then false
        else
            let s = skewness xs avg stD
            let k = excessKurtosis xs avg stD
            let nf = float n
            let zs = s * sqrt (nf / 6.0)
            let zk = k * sqrt (nf / 24.0)
            let k2 = zs * zs + zk * zk
            k2 < 5.991  // χ²(2) 5% upper critical

    let private bottleneckSeverityOf (utilizationFraction: float) : BottleneckSeverity =
        if utilizationFraction >= 0.95 then CriticalBottleneck
        elif utilizationFraction >= 0.90 then MajorBottleneck
        elif utilizationFraction >= 0.80 then ModerateBottleneck
        elif utilizationFraction >= 0.70 then MinorBottleneck
        else NoBottleneck

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
            })
        |> List.toArray

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
        let perDay  = perHour * 24.0
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
        // 시간대별 (단순화): elapsed 시간을 1시간 단위로 분할하여 per-hour bucket 생성
        let hourly =
            if elapsedHours <= 0.0 then [||]
            else
                let buckets = max 1 (int (ceil elapsedHours))
                [| for i in 0 .. buckets - 1 ->
                    let lo = report.Metadata.StartTime.AddHours(float i)
                    let hi = report.Metadata.StartTime.AddHours(float (i + 1))
                    let count =
                        SimulationReport.getWorks report
                        |> List.sumBy (fun e ->
                            e.Segments
                            |> List.filter (fun s -> s.State = StateFinish && s.StartTime >= lo && s.StartTime < hi)
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
        let designUtil    = if designPerHour > 0.0 then (actual / designPerHour) * 100.0 else 0.0
        let effectiveUtil = if effectivePerHour > 0.0 then (actual / effectivePerHour) * 100.0 else 0.0
        let capacityUtil  = if plannedPerHour > 0.0 then (actual / plannedPerHour) * 100.0 else 0.0
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
            (bottleneckThreshold: float)  // 0..1 (예: 0.90)
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
                ResourceId = (match Guid.TryParse entry.Id with true, g -> g | _ -> Guid.Empty)
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

    /// 작업별 SimOeeTracking (Quality 정보 부재 시 1.0 가정)
    let buildOees
            (designCtFor: string -> float)
            (targetOee: float)
            (report: SimulationReport) : SimOeeTracking array =
        let plannedSpan = report.Metadata.TotalDuration
        SimulationReport.getWorks report
        |> List.map (fun entry ->
            let gTime = secondsOf entry.Segments StateGoing
            let actualSpan = TimeSpan.FromSeconds gTime
            let availability =
                if plannedSpan.TotalSeconds > 0.0 then actualSpan.TotalSeconds / plannedSpan.TotalSeconds else 0.0
            let producedQty = finishCount entry
            let standardCt = designCtFor entry.Name
            let performance =
                if actualSpan.TotalSeconds > 0.0 && standardCt > 0.0 then
                    (float producedQty * standardCt) / actualSpan.TotalSeconds
                else 0.0
            let quality = 1.0       // 텔레메트리에 양/불량 데이터 없음 — 100% 가정
            let oee = availability * performance * quality
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
                ResourceId = (match Guid.TryParse entry.Id with true, g -> g | _ -> Guid.Empty)
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
        CompleteAt: DateTime option   // None = 진행 중 (집계에서 제외)
        /// 경유 Work 별 G+F 누적시간 (초)
        WorkTimes: (string * float) list
    }

    /// origin/spec 별로 그룹핑하여 KpiPerToken 배열 산출
    let buildPerTokenKpis (traversals: TokenTraversal seq) : KpiPerToken array =
        traversals
        |> Seq.toList
        |> List.groupBy (fun t -> t.OriginName)
        |> List.map (fun (origin, group) ->
            let completed =
                group
                |> List.choose (fun t ->
                    match t.CompleteAt with
                    | Some c -> Some (t, (c - t.SeedAt).TotalSeconds)
                    | None -> None)
            let durations = completed |> List.map snd
            let n = List.length durations
            let avg = if n = 0 then 0.0 else List.average durations
            let mn = if n = 0 then 0.0 else List.min durations
            let mx = if n = 0 then 0.0 else List.max durations
            let stD =
                if n < 2 then 0.0
                else
                    let nf = float n
                    let v = durations |> List.sumBy (fun x -> (x - avg) ** 2.0) |> fun s -> s / (nf - 1.0)
                    sqrt v
            let firstSeed = if group.IsEmpty then DateTime.MinValue else group |> List.map (fun t -> t.SeedAt) |> List.min
            let lastComplete =
                if completed.IsEmpty then DateTime.MinValue
                else completed |> List.map (fun (t, _) -> t.CompleteAt.Value) |> List.max
            let elapsedHours =
                if completed.IsEmpty then 0.0
                else (lastComplete - firstSeed).TotalHours
            let throughputPerHour =
                if elapsedHours > 0.0 then float n / elapsedHours else 0.0
            // Work breakdown — origin 그룹 전체 토큰의 Work별 평균 시간
            let workBreakdown =
                group
                |> List.collect (fun t -> t.WorkTimes)
                |> List.groupBy fst
                |> List.map (fun (workName, items) ->
                    let times = items |> List.map snd
                    let cnt = List.length times
                    let avgT = if cnt = 0 then 0.0 else List.average times
                    let b = KpiPerTokenWorkBreakdown()
                    b.WorkName <- workName
                    b.VisitCount <- cnt
                    b.AvgGoingTime_s <- avgT
                    b.AvgFinishTime_s <- 0.0
                    b)
                |> List.sortByDescending (fun b -> b.AvgGoingTime_s)
            let specLabel =
                group |> List.tryFind (fun t -> not (System.String.IsNullOrEmpty t.SpecLabel))
                      |> Option.map (fun t -> t.SpecLabel)
                      |> Option.defaultValue origin
            let r = KpiPerToken()
            r.OriginName            <- if isNull origin then "" else origin
            r.SpecLabel             <- specLabel
            r.InstanceCount         <- List.length group
            r.CompletedCount        <- n
            r.AvgTraversalTime_s    <- avg
            r.MinTraversalTime_s    <- mn
            r.MaxTraversalTime_s    <- mx
            r.StdDevTraversalTime_s <- stD
            r.ThroughputPerHour     <- throughputPerHour
            r.FirstSeed             <- firstSeed
            r.LastComplete          <- lastComplete
            workBreakdown |> List.iter r.WorkBreakdown.Add
            r)
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
        let benchmark        =
            p
            |> Option.map (fun x -> SimulationHelpers.getIndustryBenchmark x.IndustryType)
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
