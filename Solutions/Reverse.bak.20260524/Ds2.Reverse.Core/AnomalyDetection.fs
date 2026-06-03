/// B7.1 Anomaly Pattern Learning + B7.2 Per-cycle deviation 측정.
/// 정상 cycle 의 event pattern 학습 → 새 cycle 의 deviation 계산.
namespace Ds2.Reverse.Core

module AnomalyDetection =

    /// 한 cycle 의 패턴 — call name → 평균 offset 시각.
    type CyclePattern = {
        /// 학습 데이터 cycle 수
        NCyclesLearned: int
        /// call name → (mean offset ms, std offset ms)
        Offsets: Map<string, float * float>
        /// 학습 cycle 당 평균 event 수
        EventsPerCycle: float
    }

    /// 한 cycle 의 events 로부터 offset map 추출.
    /// 같은 call 이 여러 번 fire 하면 첫 번째 만 기록.
    let private cycleOffsets (events: (int64 * string) seq) (cycleStartT: int64)
        : Map<string, int64> =
        events
        |> Seq.fold (fun acc (t, n) ->
            let offset = t - cycleStartT
            if Map.containsKey n acc then acc
            else Map.add n offset acc) Map.empty

    /// 첫 N cycle 의 events 로 정상 pattern 학습.
    /// events: (T, Name) — 시간순서 가정.
    /// learnCycles: 학습에 사용할 cycle 수 (e.g. 첫 20 cycles)
    let learn (events: (int64 * string) list) (cycleMs: int64)
              (learnCycles: int) : CyclePattern =
        if List.isEmpty events || cycleMs <= 0L || learnCycles < 1 then
            { NCyclesLearned = 0; Offsets = Map.empty; EventsPerCycle = 0.0 }
        else
            // cycle 별 events 그룹핑
            let groups =
                events
                |> List.groupBy (fun (t, _) -> t / cycleMs)
                |> List.sortBy fst
                |> List.truncate learnCycles
            // call name → offset list
            let nameOffsets = System.Collections.Generic.Dictionary<string, ResizeArray<float>>()
            let mutable totalEvents = 0
            for (cycleIdx, cycleEvents) in groups do
                let t0 = cycleIdx * cycleMs
                let offsetMap = cycleOffsets cycleEvents t0
                totalEvents <- totalEvents + List.length cycleEvents
                for KeyValue(name, off) in offsetMap do
                    match nameOffsets.TryGetValue name with
                    | true, lst -> lst.Add(float off)
                    | _ ->
                        let lst = ResizeArray()
                        lst.Add(float off)
                        nameOffsets.[name] <- lst
            let offsets =
                nameOffsets
                |> Seq.map (fun kv ->
                    let xs = kv.Value |> Array.ofSeq
                    let m = Array.average xs
                    let s = sqrt (Array.averageBy (fun x -> (x - m) ** 2.0) xs)
                    kv.Key, (m, s))
                |> Map.ofSeq
            { NCyclesLearned = List.length groups
              Offsets = offsets
              EventsPerCycle =
                  if List.isEmpty groups then 0.0
                  else float totalEvents / float (List.length groups) }

    /// 한 cycle 의 events 가 정상 pattern 으로부터 얼마나 deviation 인지 측정.
    /// 결과: 0.0 (정확히 정상) ~ 무한대 (매우 비정상).
    /// 측정:
    ///   • offset deviation — 각 event 의 t-offset 이 학습된 (mean, std) 로부터 몇 sigma
    ///   • 누락 events — 학습된 event 가 이 cycle 에 안 나타남
    ///   • 추가 events — 학습 없던 event 가 나타남
    let scoreCycle (pattern: CyclePattern) (cycleEvents: (int64 * string) seq)
                   (cycleStartT: int64) : float =
        if pattern.NCyclesLearned = 0 then 0.0
        else
            let observed = cycleOffsets cycleEvents cycleStartT
            let mutable totalDev = 0.0
            let mutable count = 0
            // 학습된 events 와 비교
            for KeyValue(name, (meanOff, stdOff)) in pattern.Offsets do
                match Map.tryFind name observed with
                | Some offset ->
                    let actualStd = max 10.0 stdOff   // floor for noise
                    let zScore = abs (float offset - meanOff) / actualStd
                    totalDev <- totalDev + zScore
                    count <- count + 1
                | None ->
                    // 누락 — 5 sigma 페널티
                    totalDev <- totalDev + 5.0
                    count <- count + 1
            // 추가 events 페널티
            let extraEvents =
                observed
                |> Map.toSeq
                |> Seq.filter (fun (n, _) -> not (Map.containsKey n pattern.Offsets))
                |> Seq.length
            totalDev <- totalDev + 3.0 * float extraEvents
            if count = 0 then totalDev else totalDev / float count

    /// 모든 cycle 의 deviation score list 산출 + 임계값 초과 cycle 인덱스.
    let analyzeAllCycles (pattern: CyclePattern) (events: (int64 * string) list)
                        (cycleMs: int64) (sigmaThreshold: float)
                        : (int * float) list * int list =
        if pattern.NCyclesLearned = 0 then [], []
        else
            let groups =
                events
                |> List.groupBy (fun (t, _) -> t / cycleMs)
                |> List.sortBy fst
            let scores =
                groups
                |> List.map (fun (cycleIdx, cevents) ->
                    let t0 = cycleIdx * cycleMs
                    int cycleIdx, scoreCycle pattern cevents t0)
            let anomalous =
                scores
                |> List.filter (fun (_, sc) -> sc > sigmaThreshold)
                |> List.map fst
            scores, anomalous
