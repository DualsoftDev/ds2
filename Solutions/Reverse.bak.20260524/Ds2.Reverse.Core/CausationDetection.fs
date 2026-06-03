/// Causation 검출 — sufficiency + necessity + lag stability gates.
/// 검증된 알고리즘 (VLINE 합성 모델에서 P/R/F1 = 1.000).
namespace Ds2.Reverse.Core

module CausationDetection =

    /// A → B 인과 점수 계산.
    ///
    /// 정의:
    ///   sufficiency = P[A 발화 직후 [-parallel_lag, window] 안에 B 발화]
    ///   necessity   = P[B 발화 직전 [-parallel_lag, window] 안에 A 발화]
    ///   lag stats   = sufficiency 매칭 페어의 (t_B - t_A)
    ///   is_parallel = |lag_mean| < parallel_lag AND lag_std < parallel_lag
    ///   passes_grp  = is_parallel AND suff/necc >= suff_min
    ///   passes_seq  = lag_mean > 0 AND suff >= suff_min AND necc >= necc_min
    ///                 AND (lag_cv <= cv_max OR lag_std <= std_abs_ms)
    let score (cfg: CausationConfig) (aTimes: int64 seq) (bTimes: int64 seq) : CausationScore =
        let a = Array.ofSeq aTimes |> Array.sort
        let b = Array.ofSeq bTimes |> Array.sort
        let nA = a.Length
        let nB = b.Length
        let parallelLag = int64 cfg.ParallelLagMs
        // Effective window — cycle hint 있으면 cycle * 0.7 로 자동 축소 (cross-cycle 차단)
        let win =
            match cfg.CycleHintMs with
            | Some c -> min cfg.WindowMs (int64 (float c * 0.7))
            | None -> cfg.WindowMs

        if nA < cfg.MinFires || nB < cfg.MinFires then
            { NA = nA; NB = nB
              Sufficiency = 0.0; Necessity = 0.0
              LagMean = 0.0; LagStd = 0.0; LagCv = 999.0
              AbsLagMean = 999.0
              IsParallel = false
              PassesSeq = false; PassesGrp = false
              Reason = Some (sprintf "low_n(a=%d,b=%d)" nA nB) }
        else
            // sufficiency — A 발화 이후 가장 가까운 B 가 window 안인지.
            // burst-friendly: 다중 A 가 같은 B 매칭 허용 (단일 cause → multiple effects).
            let mutable hitsA = 0
            let lags = ResizeArray<int64>()
            let mutable bi = 0
            for ta in a do
                while bi < nB && b.[bi] < ta - parallelLag do
                    bi <- bi + 1
                if bi < nB && b.[bi] - ta >= -parallelLag && b.[bi] - ta <= win then
                    hitsA <- hitsA + 1
                    lags.Add(b.[bi] - ta)
            let suff = float hitsA / float nA

            // necessity — B 발화 직전 win 안에 A 있는지.
            // burst-friendly: 다중 B 가 같은 A 매칭 허용.
            // Cross-cycle 차단은 win 축소 (cycle hint) 로 해결.
            let mutable hitsB = 0
            let mutable ai = 0
            for tb in b do
                while ai < nA && a.[ai] < tb - win do
                    ai <- ai + 1
                if ai < nA && tb - a.[ai] >= -parallelLag && tb - a.[ai] <= win then
                    hitsB <- hitsB + 1
            let necc = float hitsB / float nB

            // B4.2 Outlier filtering — Tukey IQR (Q1-1.5*IQR ~ Q3+1.5*IQR).
            // 작은 표본 (< 8) 에는 skip. lags 자체를 트리밍 — stats 재계산.
            if lags.Count >= 8 then
                let sortedL = lags |> Seq.sort |> Array.ofSeq
                let q index =
                    let pos = float index * (float sortedL.Length - 1.0)
                    let i = int pos
                    if i + 1 < sortedL.Length then
                        let frac = pos - float i
                        float sortedL.[i] * (1.0 - frac) + float sortedL.[i + 1] * frac
                    else float sortedL.[i]
                let q1 = q (1.0 / 4.0)
                let q3 = q (3.0 / 4.0)
                let iqr = q3 - q1
                if iqr > 0.0 then
                    let lo = q1 - 1.5 * iqr
                    let hi = q3 + 1.5 * iqr
                    let filtered = lags |> Seq.filter (fun l -> float l >= lo && float l <= hi)
                                        |> Array.ofSeq
                    // 최소 표본 보호: 50% 이상 남으면 적용
                    if filtered.Length >= lags.Count / 2 && filtered.Length >= 5 then
                        lags.Clear()
                        for l in filtered do lags.Add l

            // lag stats (sufficiency 매칭 페어 기반)
            let lagMean, lagStd, lagCv, absLagMean =
                if lags.Count = 0 then
                    0.0, 0.0, 999.0, 999.0
                else
                    let xs = lags |> Seq.map float |> Array.ofSeq
                    let m = Array.average xs
                    let s = sqrt (Array.averageBy (fun x -> (x - m) ** 2.0) xs)
                    let cv = if m > 0.0 then s / m elif abs m < 1.0 then 0.0 else 999.0
                    m, s, cv, abs m

            let isParallel =
                absLagMean < cfg.ParallelLagMs && lagStd < cfg.ParallelLagMs

            let passesGrp =
                isParallel && suff >= cfg.SufficiencyMin && necc >= cfg.SufficiencyMin

            // Stability:
            //   (a) CV ≤ cv_max (정상 stable jitter)
            //   (b) std ≤ abs_ms AND lagMean < 200ms (작은 lag 의 jitter 흡수)
            //   (c) bimodal-stable (bottleneck / queue 등 lag 두 peak)
            //   (d) drift-stable — lag 가 시간 따라 단조 변화 (linear drift)
            //       linear regression 의 residual std 가 평균의 15% 이하 AND |slope| > 0.5
            // 모드 개수 — 75ms 폭 bin histogram 기반.
            // 5% 이상 인구의 bin 을 mode 로 인정. 3+ 면 multi-modal → 거부.
            // 빠른 분리: bin 사이 빈 bin (>=1) 존재 시 그 사이를 mode 경계로 인정.
            let modeCount =
                if lags.Count < 6 then 1
                else
                    let binW = 75L
                    let sorted = lags |> Seq.sort |> Array.ofSeq
                    let minL = sorted.[0]
                    let bins = System.Collections.Generic.Dictionary<int, int>()
                    for l in sorted do
                        let idx = int ((l - minL) / binW)
                        match bins.TryGetValue idx with
                        | true, v -> bins.[idx] <- v + 1
                        | _ -> bins.[idx] <- 1
                    let threshold = max 3 (sorted.Length / 20)   // 5%
                    let modeBins =
                        bins
                        |> Seq.filter (fun kv -> kv.Value >= threshold)
                        |> Seq.map (fun kv -> kv.Key)
                        |> Seq.toList
                        |> List.sort
                    // 연속된 mode bin 들은 한 mode 로 합치기 (bin 간격 ≤ 1)
                    let mutable n = 0
                    let mutable prevOpt = None
                    for idx in modeBins do
                        match prevOpt with
                        | None -> n <- 1
                        | Some prev when idx - prev > 1 -> n <- n + 1
                        | _ -> ()
                        prevOpt <- Some idx
                    max 1 n

            let bimodalStable =
                if lags.Count < 6 || modeCount > 2 then false
                else
                    let sorted = lags |> Seq.sort |> Array.ofSeq
                    let gaps =
                        [| for i in 0 .. sorted.Length - 2 -> i, sorted.[i + 1] - sorted.[i] |]
                    let maxGapIdx, maxGap = gaps |> Array.maxBy snd
                    // Bimodal 인정 조건 (모두 통과해야):
                    //   (a) 두 mode 간 gap ≥ 100ms — 명확히 분리 (jitter 흡수 위해 완화)
                    //   (b) 각 mode 의 std < 60ms — mode 내부 안정 (jitter 흡수)
                    //   (c) 두 mode 크기 비율 ≥ 25% — outlier 가 아닌 진짜 modal
                    if maxGap < 100L then false
                    else
                        let mode1 = sorted.[..maxGapIdx]
                        let mode2 = sorted.[maxGapIdx + 1..]
                        if mode1.Length < 2 || mode2.Length < 2 then false
                        else
                            let total = mode1.Length + mode2.Length
                            let smaller = min mode1.Length mode2.Length
                            if smaller * 4 < total then false   // < 25% → outlier 분포
                            else
                                let stdOf (arr: int64[]) =
                                    let xs = arr |> Array.map float
                                    let m = Array.average xs
                                    sqrt (Array.averageBy (fun x -> (x - m) ** 2.0) xs)
                                stdOf mode1 < 60.0 && stdOf mode2 < 60.0
            // smallLagFallback — 작은 lag 의 jitter 흡수.
            // 조건: lag mean 자체가 작고 (< 150ms) std 가 mean 의 80% 이내 OR std < 50ms.
            // 너무 느슨하면 multi-modal 이 통과하므로 mean 과 std 모두 제한.
            let smallLagFallback =
                lagMean < 150.0
                && (lagStd < 50.0 || (lagMean > 0.0 && lagStd < lagMean * 0.8))

            // B1.2 Multi-modal k-means stability — 3+ peaks 도 각 cluster 가 좁고
            // 균등 분포면 인정 (3+ mode 가 강제 거부되는 것 보다 관대).
            //
            // 방법: 1-D k-means with k=3,4. 각 cluster 의 std < 60ms, 가장 작은
            //       cluster 가 전체의 12% 이상이면 안정으로 봄.
            let kmeansStable =
                if lags.Count < 12 || modeCount < 3 || modeCount > 5 then false
                else
                    let sorted = lags |> Seq.sort |> Array.ofSeq |> Array.map float
                    let n = sorted.Length
                    let runKmeans (k: int) =
                        // 초기 centroids: 균등 분할 quantile.
                        let centroids =
                            [| for i in 0 .. k - 1 ->
                                sorted.[(i * 2 + 1) * n / (2 * k)] |]
                        let assign = Array.zeroCreate<int> n
                        let mutable changed = true
                        let mutable iter = 0
                        while changed && iter < 15 do
                            changed <- false
                            iter <- iter + 1
                            for i in 0 .. n - 1 do
                                let mutable bestK = 0
                                let mutable bestDist = abs (sorted.[i] - centroids.[0])
                                for j in 1 .. k - 1 do
                                    let d = abs (sorted.[i] - centroids.[j])
                                    if d < bestDist then bestDist <- d; bestK <- j
                                if assign.[i] <> bestK then changed <- true
                                assign.[i] <- bestK
                            // recompute centroids
                            for j in 0 .. k - 1 do
                                let members =
                                    [| for i in 0 .. n - 1 do
                                        if assign.[i] = j then yield sorted.[i] |]
                                if members.Length > 0 then
                                    centroids.[j] <- Array.average members
                        // 각 cluster size + std
                        let clusters =
                            [| for j in 0 .. k - 1 ->
                                let mems =
                                    [| for i in 0 .. n - 1 do
                                        if assign.[i] = j then yield sorted.[i] |]
                                if mems.Length = 0 then 0, 999.0
                                else
                                    let m = Array.average mems
                                    let s = sqrt (Array.averageBy (fun x -> (x - m) ** 2.0) mems)
                                    mems.Length, s |]
                        clusters
                    // try k=3, k=4. 최소 cluster size >= 12%, 모든 std < 60.
                    let kCandidates = [ 3; 4 ]
                    kCandidates
                    |> List.exists (fun k ->
                        let clusters = runKmeans k
                        let minSize = clusters |> Array.map fst |> Array.min
                        let maxStd = clusters |> Array.map snd |> Array.max
                        let pct = float minSize * 100.0 / float n
                        pct >= 12.0 && maxStd < 60.0)

            // Drift detection — linear regression
            let driftStable =
                if lags.Count < 10 || lagMean <= 0.0 then false
                else
                    let xs = [| for i in 0 .. lags.Count - 1 -> float i |]
                    let ys = lags |> Seq.map float |> Array.ofSeq
                    let xMean = Array.average xs
                    let yMean = Array.average ys
                    let num = (xs, ys) ||> Array.map2 (fun x y -> (x - xMean) * (y - yMean))
                                       |> Array.sum
                    let den = xs |> Array.sumBy (fun x -> (x - xMean) ** 2.0)
                    if den < 1e-9 then false
                    else
                        let slope = num / den
                        let intercept = yMean - slope * xMean
                        let residuals =
                            (xs, ys) ||> Array.map2 (fun x y -> y - (slope * x + intercept))
                        let residualStd = sqrt (Array.averageBy (fun r -> r ** 2.0) residuals)
                        // 인정: residual std 가 mean lag 의 15% 이하 AND slope 의미 있음
                        residualStd < lagMean * 0.15 && abs slope > 0.5

            // Cyclic drift detection — lag 가 주기적 (cosine 등) 으로 변동.
            // autocorrelation 기반: linear detrend 후 ACF 의 비-0 lag peak 검사.
            // peak ACF >= 0.5 이면 cyclic pattern 인정, 그 주기의 model fit 으로 잔차 < mean lag 의 30%.
            let cyclicStable =
                if lags.Count < 20 || lagMean <= 0.0 then false
                else
                    let xs = [| for i in 0 .. lags.Count - 1 -> float i |]
                    let ys = lags |> Seq.map float |> Array.ofSeq
                    // linear detrend
                    let xMean = Array.average xs
                    let yMean = Array.average ys
                    let num = (xs, ys) ||> Array.map2 (fun x y -> (x - xMean) * (y - yMean)) |> Array.sum
                    let den = xs |> Array.sumBy (fun x -> (x - xMean) ** 2.0)
                    let slope = if den < 1e-9 then 0.0 else num / den
                    let intercept = yMean - slope * xMean
                    let detrended =
                        (xs, ys) ||> Array.map2 (fun x y -> y - (slope * x + intercept))
                    // autocorrelation (normalized)
                    let n = detrended.Length
                    let dMean = Array.average detrended
                    let centered = detrended |> Array.map (fun v -> v - dMean)
                    let var0 = centered |> Array.sumBy (fun v -> v * v)
                    if var0 < 1e-9 then false
                    else
                        // 시도할 주기 범위: 2 ~ n/3
                        let maxK = min (n / 3) 30
                        let mutable bestK = 0
                        let mutable bestAcf = 0.0
                        for k in 2 .. maxK do
                            let mutable s = 0.0
                            for i in 0 .. n - k - 1 do
                                s <- s + centered.[i] * centered.[i + k]
                            let acf = s / var0
                            if acf > bestAcf then
                                bestAcf <- acf
                                bestK <- k
                        // 강한 주기성 인정: peak ACF >= 0.5
                        if bestAcf < 0.5 || bestK < 2 then false
                        else
                            // 주기 모델 fit (cos / sin amplitude)
                            let omega = 2.0 * System.Math.PI / float bestK
                            let cosTerms = xs |> Array.map (fun x -> cos (omega * x))
                            let sinTerms = xs |> Array.map (fun x -> sin (omega * x))
                            let dotC = Array.map2 (*) centered cosTerms |> Array.sum
                            let dotS = Array.map2 (*) centered sinTerms |> Array.sum
                            let normC = cosTerms |> Array.sumBy (fun v -> v * v)
                            let normS = sinTerms |> Array.sumBy (fun v -> v * v)
                            let aC = if normC < 1e-9 then 0.0 else dotC / normC
                            let aS = if normS < 1e-9 then 0.0 else dotS / normS
                            let cyclicPred =
                                Array.init n (fun i -> aC * cosTerms.[i] + aS * sinTerms.[i])
                            let residuals = Array.map2 (-) centered cyclicPred
                            let residualStd = sqrt (Array.averageBy (fun r -> r * r) residuals)
                            // cyclic + linear residual 가 작아야 인정
                            residualStd < lagMean * 0.30 && residualStd < 200.0

            // Multi-modal (3+) 인 경우 기본적으로 거부, 단 kmeans 가 명확한 cluster
            // 구조를 발견하면 인정 (B1.2).
            let stable =
                (modeCount <= 2
                 && (lagCv <= cfg.LagCvMax || smallLagFallback || bimodalStable
                     || driftStable || cyclicStable))
                || kmeansStable
            let passesSeq =
                lagMean > 0.0 && suff >= cfg.SufficiencyMin
                && necc >= cfg.NecessityMin && stable

            { NA = nA; NB = nB
              Sufficiency = round (suff * 1000.0) / 1000.0
              Necessity = round (necc * 1000.0) / 1000.0
              LagMean = round (lagMean * 10.0) / 10.0
              LagStd = round (lagStd * 10.0) / 10.0
              LagCv = round (lagCv * 1000.0) / 1000.0
              AbsLagMean = round (absLagMean * 10.0) / 10.0
              IsParallel = isParallel
              PassesSeq = passesSeq
              PassesGrp = passesGrp
              Reason = None }

    /// B4.3 Bayesian-style aggregation — 한 arrow 에 대한 여러 증거 소스 (logic strength,
    /// capture confidence, cluster fallback) 를 결합해 posterior 산출.
    ///
    /// 가정: 각 증거는 독립 sample. 확률 형태로 변환 후 log-odds 합산 (Bayesian fusion).
    /// 결과: 0~1 의 posterior probability.
    let bayesianAggregate (evidences: float list) : float =
        if List.isEmpty evidences then 0.5
        else
            // 각 evidence p_i 를 log-odds 로 변환: logit = log(p / (1-p))
            // 합산 후 sigmoid 로 환원.
            // 안전: p 가 0 또는 1 에 너무 가까우면 clamp.
            let clamp p = max 0.01 (min 0.99 p)
            let logits =
                evidences |> List.map (fun p ->
                    let pc = clamp p
                    log (pc / (1.0 - pc)))
            // Prior = 0.5 (logit=0). Posterior logit = sum of evidence logits.
            let totalLogit = List.sum logits
            // Convert back to probability
            1.0 / (1.0 + exp (- totalLogit))

    /// B4.1 Background Noise Estimation — events 의 전체 발화 분포로 noise level 추정.
    ///
    /// 방법:
    ///   1. 모든 event 시각을 합쳐서 cycle-relative offset 계산.
    ///   2. 같은 call 이름 끼리의 cycle 내 발화 시각 std 측정.
    ///   3. std 가 큰 (jitter 가 심한) call 이 많을수록 noise level 높음.
    ///
    /// 결과: 0.0 (clean) ~ 1.0 (very noisy)
    let estimateNoiseLevel (events: CapturedEvent list) (cycleMs: int64) : float =
        if List.isEmpty events || cycleMs <= 0L then 0.0
        else
            // event 를 call name 별로 그룹핑
            let byName =
                events
                |> List.groupBy (fun e -> e.Name)
                |> List.filter (fun (_, evs) -> List.length evs >= 5)
            if List.isEmpty byName then 0.0
            else
                // 각 call 의 cycle-relative offset std (mod cycleMs)
                let stds =
                    byName
                    |> List.map (fun (_, evs) ->
                        let offsets =
                            evs
                            |> List.map (fun e -> float (e.T % cycleMs))
                            |> Array.ofList
                        let m = Array.average offsets
                        sqrt (Array.averageBy (fun x -> (x - m) ** 2.0) offsets))
                // 평균 std → noise 척도. 200ms 이상이면 매우 noisy (≈1.0)
                let avgStd = List.average stds
                max 0.0 (min 1.0 (avgStd / 200.0))

    /// Per-arrow confidence — CausationScore + 옵션 logic strength 결합.
    ///
    /// 가중:
    ///   primary  = (suff + necc) / 2 * stabilityWeight    (0~1)
    ///   logic    = 0.5 if None, else logicStrength         (0~1)
    ///   nReliability: NA < 10 → 0.5, < 30 → 0.8, else 1.0
    ///   raw = primary * 0.7 + logic * 0.3
    ///   scaled = raw * nReliability
    let confidence (sco: CausationScore) (logicStrength: float option) : ArrowConfidence =
        let stabilityWeight =
            if sco.PassesSeq || sco.PassesGrp then 1.0
            elif sco.Sufficiency >= 0.5 then 0.6
            else 0.3
        let primary = (sco.Sufficiency + sco.Necessity) / 2.0 * stabilityWeight
        let nReliability =
            if sco.NA < 10 then 0.5
            elif sco.NA < 30 then 0.8
            else 1.0
        let raw =
            match logicStrength with
            | None -> primary                                  // capture-only
            | Some s -> primary * 0.7 + s * 0.3                // hybrid
        let scaled = max 0.0 (min 1.0 (raw * nReliability))
        let tier =
            if scaled >= 0.9 then High
            elif scaled >= 0.7 then Medium
            elif scaled >= 0.5 then Low
            else Reject
        let evidence =
            [
                if sco.PassesSeq then "passes_seq"
                if sco.PassesGrp then "passes_grp"
                if sco.Sufficiency >= 0.85 then "high_suff"
                if sco.Necessity >= 0.85 then "high_necc"
                if sco.LagCv <= 0.30 then "low_cv"
                match logicStrength with
                | Some s -> sprintf "logic=%.2f" s
                | None -> ()
                sprintf "n=%d" sco.NA
            ]
        { Score = round (scaled * 1000.0) / 1000.0
          Tier = tier
          Evidence = evidence
          NReliability = nReliability }

    /// Mutex 인정 점수.
    /// A 와 B 가 상호 배타적으로 발화 — 한 cycle 안에서 둘 다 발화하지 않음.
    /// 측정 방법: cycle 기준 ±cycleHint*0.4 window 안에서 A 와 B 가 같이 발화한 횟수가 적은지.
    /// 결과: (passes, coOccurrenceRate, nA, nB)
    let mutexScore (cfg: CausationConfig) (aTimes: int64 seq) (bTimes: int64 seq)
        : bool * float * int * int =
        let a = Array.ofSeq aTimes |> Array.sort
        let b = Array.ofSeq bTimes |> Array.sort
        let nA, nB = a.Length, b.Length
        if nA < cfg.MinFires || nB < cfg.MinFires then
            false, 0.0, nA, nB
        else
            // co-occurrence window — cycle hint 의 절반 (한 cycle 안 거리)
            let win =
                match cfg.CycleHintMs with
                | Some c -> int64 (float c * 0.5)
                | None -> cfg.WindowMs / 2L
            // 각 A 발화에 대해 ±win 안에 B 가 있는지 카운트.
            let mutable coHits = 0
            let mutable bi = 0
            for ta in a do
                while bi < nB && b.[bi] < ta - win do
                    bi <- bi + 1
                let mutable j = bi
                let mutable hit = false
                while j < nB && b.[j] - ta <= win do
                    if abs (b.[j] - ta) <= win then hit <- true
                    j <- j + 1
                if hit then coHits <- coHits + 1
            let rate = float coHits / float nA
            // mutex 인정: co-occurrence rate < 10% AND 두 측 모두 충분히 발화
            let passes = rate < 0.10 && nA >= cfg.MinFires && nB >= cfg.MinFires
            passes, rate, nA, nB

    /// declared_kind 우선 게이팅. ArrowType code:
    ///   1=Start  2=Reset  3=StartReset  4=ResetReset  5=Group
    /// 주의: mutex (ResetReset) 는 별도 mutexScore 로 평가 — gate 에 도달하면 score 가 fail 일 수 있으나
    /// 그 자체로 reject 하지 않음. mutex 검출은 ReverseEngine 에서 직접 처리.
    let gate (declaredKind: string) (sco: CausationScore) : GatingDecision =
        let kindLower = declaredKind.ToLowerInvariant()
        if kindLower = "group" then
            if sco.PassesGrp then EmitGroup sco
            else Dropped("declared_group_but_lag_too_large", sco)
        else
            // declared_kind 별 ArrowType code 결정
            let arrowTypeCode =
                match kindLower with
                | "reset" -> 2
                | "trigger_reset" | "startreset" -> 3
                | "mutex" | "resetreset" -> 4
                | _ -> 1   // trigger / start / default
            if sco.PassesSeq then EmitSequential(arrowTypeCode, sco)
            else Dropped("causation_gate_fail", sco)

    /// Multi-source cluster causation — 한 sink (tgt) 에 multiple sources 가 있을 때.
    /// 각 B 발화는 가장 가까운 preceding source 의 cluster 에 할당.
    /// 각 source 의 score = (cluster B 수 / source 발화 수).
    ///
    /// 입력: (srcName, srcTimes) list + tgtTimes
    /// 출력: srcName → ClusterScore
    let clusterScore (cfg: CausationConfig)
                     (srcsTimes: (string * int64 seq) list)
                     (tgtTimes: int64 seq) : Map<string, ClusterScore> =
        let parallelLag = int64 cfg.ParallelLagMs
        let win =
            match cfg.CycleHintMs with
            | Some c -> min cfg.WindowMs (int64 (float c * 0.7))
            | None -> cfg.WindowMs
        let bArr = Array.ofSeq tgtTimes |> Array.sort
        let nB = bArr.Length
        let srcArrs =
            srcsTimes
            |> List.map (fun (n, ts) -> n, Array.ofSeq ts |> Array.sort)

        // Each B 의 closest preceding source assignment
        let bClusterAssign : string [] = Array.create nB null
        let bClusterLag : int64 [] = Array.zeroCreate nB
        for bi in 0 .. nB - 1 do
            let tb = bArr.[bi]
            let mutable best : (string * int64) option = None
            for (srcName, srcTimes) in srcArrs do
                let mutable found : int64 option = None
                for ta in srcTimes do
                    if ta <= tb + parallelLag && tb - ta <= win then
                        match found with
                        | None -> found <- Some ta
                        | Some prev when ta > prev -> found <- Some ta
                        | _ -> ()
                match found with
                | Some ta ->
                    let lag = tb - ta
                    match best with
                    | None -> best <- Some (srcName, lag)
                    | Some (_, bl) when lag < bl -> best <- Some (srcName, lag)
                    | _ -> ()
                | None -> ()
            match best with
            | Some (sn, lag) ->
                bClusterAssign.[bi] <- sn
                bClusterLag.[bi] <- lag
            | None -> ()

        // 각 source 의 cluster stats
        let result = System.Collections.Generic.Dictionary<string, ClusterScore>()
        for (srcName, srcTimes) in srcArrs do
            let clusterLags = ResizeArray<int64>()
            for bi in 0 .. nB - 1 do
                if bClusterAssign.[bi] = srcName then
                    clusterLags.Add bClusterLag.[bi]
            let nA = srcTimes.Length
            let clusterSize = clusterLags.Count
            let suff = if nA = 0 then 0.0 else float clusterSize / float nA
            let coverage = if nB = 0 then 0.0 else float clusterSize / float nB
            let lagMean, lagStd, lagCv =
                if clusterLags.Count = 0 then 0.0, 0.0, 999.0
                else
                    let xs = clusterLags |> Seq.map float |> Array.ofSeq
                    let m = Array.average xs
                    let s = sqrt (Array.averageBy (fun x -> (x - m) ** 2.0) xs)
                    let cv = if m > 0.0 then s / m else 999.0
                    m, s, cv
            let stable =
                lagCv <= cfg.LagCvMax
                || (lagStd <= cfg.LagStdAbsMs && lagMean < 200.0)
            let passes =
                clusterSize >= cfg.MinFires
                && nA >= cfg.MinFires
                && suff >= cfg.SufficiencyMin
                && lagMean > 0.0
                && stable
            result.[srcName] <- {
                SrcName = srcName
                NA = nA; NB = nB
                ClusterSize = clusterSize
                Suff = round (suff * 1000.0) / 1000.0
                Coverage = round (coverage * 1000.0) / 1000.0
                LagMean = round (lagMean * 10.0) / 10.0
                LagStd = round (lagStd * 10.0) / 10.0
                LagCv = round (lagCv * 1000.0) / 1000.0
                PassesSeq = passes
            }
        result |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
