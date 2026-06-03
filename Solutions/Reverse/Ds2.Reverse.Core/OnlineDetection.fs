/// B6 Online / Incremental Detection — events 가 stream 으로 들어오는 경우.
/// Welford's online algorithm 으로 mean/variance 를 incremental 업데이트.
/// Anytime: 언제든지 snapshot 호출 → 현재까지의 CausationScore 산출.
namespace Ds2.Reverse.Core

open System.Collections.Generic

module OnlineDetection =

    /// 한 (A → B) 페어의 streaming 상태.
    /// Event 가 들어올 때마다 update; snapshot 으로 현재 score 산출.
    type OnlineScore() =
        let mutable nA = 0
        let mutable nB = 0
        let mutable hitsA = 0
        let mutable hitsB = 0
        let mutable lagN = 0
        // Welford accumulators for lag mean / M2 (sum of squared deviations)
        let mutable lagMean = 0.0
        let mutable lagM2 = 0.0
        // 최근 A 의 시각 (lag matching 용)
        let recentA = Queue<int64>()
        let recentB = Queue<int64>()
        let mutable windowMs = 3000L
        let mutable parallelLagMs = 50L

        member _.SetWindow (w: int64) = windowMs <- w
        member _.SetParallelLag (p: int64) = parallelLagMs <- p

        // Online 검출은 event arrival 순서대로:
        //   AddA(t): A 카운트 + recentA 에 enqueue (미래 B 매칭용 대기)
        //   AddB(t): B 카운트 + 가장 가까운 이전 A 찾기 (sufficiency for that A; necessity for this B)
        // 결과적으로 한 (A,B) 페어가 같이 매칭 → hitsA + hitsB 동시 증가.
        member this.AddA (t: int64) =
            nA <- nA + 1
            recentA.Enqueue t
            this.PruneOld ()

        member this.AddB (t: int64) =
            nB <- nB + 1
            recentB.Enqueue t
            this.PruneOld ()
            // 가장 가까운 이전 A — recentA 의 마지막 entry 가 가장 큰 (가장 최근) A
            let mutable bestA : int64 option = None
            for ta in recentA do
                if t - ta >= -parallelLagMs && t - ta <= windowMs then
                    match bestA with
                    | None -> bestA <- Some ta
                    | Some prev when ta > prev -> bestA <- Some ta
                    | _ -> ()
            match bestA with
            | Some ta ->
                hitsB <- hitsB + 1                 // necessity (B 직전 A 있음)
                hitsA <- hitsA + 1                 // sufficiency (A 직후 B 있음)
                this.UpdateLagStats (float (t - ta))
            | None -> ()

        member private this.PruneOld () =
            // window 보다 오래된 entries 제거 (memory 보호)
            let cutoff =
                let latest =
                    [
                        if recentA.Count > 0 then yield recentA |> Seq.last
                        if recentB.Count > 0 then yield recentB |> Seq.last
                    ]
                if List.isEmpty latest then 0L
                else List.max latest - windowMs
            while recentA.Count > 0 && recentA.Peek() < cutoff do
                recentA.Dequeue() |> ignore
            while recentB.Count > 0 && recentB.Peek() < cutoff do
                recentB.Dequeue() |> ignore

        member private _.UpdateLagStats (x: float) =
            lagN <- lagN + 1
            let delta = x - lagMean
            lagMean <- lagMean + delta / float lagN
            let delta2 = x - lagMean
            lagM2 <- lagM2 + delta * delta2

        /// 현재 시점 snapshot — CausationScore 형식 산출.
        member _.Snapshot () : CausationScore =
            let suff = if nA = 0 then 0.0 else float hitsA / float nA
            let necc = if nB = 0 then 0.0 else float hitsB / float nB
            let lagStd =
                if lagN < 2 then 0.0
                else sqrt (lagM2 / float lagN)
            let lagCv =
                if lagMean > 0.0 then lagStd / lagMean
                elif abs lagMean < 1.0 then 0.0
                else 999.0
            let absLagMean = abs lagMean
            let isParallel =
                absLagMean < float parallelLagMs
                && lagStd < float parallelLagMs
            let passesSeq =
                lagMean > 0.0 && suff >= 0.85 && necc >= 0.85
                && (lagCv <= 0.30 || (lagStd <= 150.0 && lagMean < 200.0))
            let passesGrp = isParallel && suff >= 0.85 && necc >= 0.85
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

        /// Confidence 표시 (snapshot 의 confidence)
        member this.SnapshotConfidence () : ArrowConfidence =
            CausationDetection.confidence (this.Snapshot()) None

    // ── B7.2 Causation Drift Alert ──────────────────────────────────────
    // confidence 가 시간에 따라 drift (drop / pickup) 하면 라인 상태 변화 알림.

    /// Drift 분석 결과.
    type DriftAlert =
        /// 안정 — confidence 변화 적음
        | Stable
        /// 하락 — confidence 감소 추세 (drop, drop 폭)
        | Dropping of slope: float * recentScore: float
        /// 상승 — confidence 증가 추세 (pickup, pickup 폭)
        | Picking of slope: float * recentScore: float

    /// Snapshot history 의 confidence drift 분석.
    /// 입력: confidence list (시간 순서, 최소 5개)
    /// 방법: linear regression — slope.
    let analyzeDrift (history: ArrowConfidence list) : DriftAlert =
        if List.length history < 5 then Stable
        else
            let arr = history |> List.toArray
            let n = arr.Length
            let xs = [| for i in 0 .. n - 1 -> float i |]
            let ys = arr |> Array.map (fun c -> c.Score)
            let xMean = Array.average xs
            let yMean = Array.average ys
            let num = (xs, ys) ||> Array.map2 (fun x y -> (x - xMean) * (y - yMean)) |> Array.sum
            let den = xs |> Array.sumBy (fun x -> (x - xMean) ** 2.0)
            let slope = if den < 1e-9 then 0.0 else num / den
            let recent = ys.[n - 1]
            // slope 의미 임계: 한 sample 당 0.01 (즉 10 sample 당 0.1)
            if abs slope < 0.01 then Stable
            elif slope < 0.0 then Dropping(slope, recent)
            else Picking(slope, recent)

    /// 전체 events stream 을 처리 + 매 N event 마다 snapshot history 저장.
    /// 결과: snapshot list — confidence convergence 분석 용.
    let runStream (events: (int64 * string * string) seq)   // (t, srcName, tgtName)
                  (windowMs: int64)
                  (snapshotEveryN: int) : OnlineScore * ArrowConfidence list =
        let state = OnlineScore()
        state.SetWindow windowMs
        let history = ResizeArray<ArrowConfidence>()
        let mutable counter = 0
        // 이 helper 는 단순 (A → B) 페어만 처리 — multi-pair 는 외부에서 분기
        for (t, src, _tgt) in events do
            // src: "A" → AddA, src: "B" → AddB
            if src = "A" then state.AddA t
            elif src = "B" then state.AddB t
            counter <- counter + 1
            if counter % snapshotEveryN = 0 then
                history.Add (state.SnapshotConfidence())
        state, List.ofSeq history
