/// Domain-specific polling pattern library.
/// PLC / 산업 자동화 도메인에서 흔한 polling 패턴 시그니처.
namespace Ds2.Reverse.Core

module PollingPatterns =

    /// 알려진 polling 패턴 사양.
    type PollingPatternSpec = {
        /// 사람-읽기 이름.
        Name: string
        /// 기대 평균 inter-arrival (ms).
        ExpectedPeriodMs: float
        /// 기대 CV (interval std/mean) — 일정 polling 은 작음.
        MaxCv: float
        /// 최소 sample 수.
        MinFires: int
    }

    /// PLC 도메인 알려진 패턴.
    let knownPatterns : PollingPatternSpec list = [
        // 매우 빠른 scan cycle (10-50ms 주기)
        { Name = "scan_10ms"; ExpectedPeriodMs = 10.0; MaxCv = 0.15; MinFires = 10 }
        { Name = "scan_20ms"; ExpectedPeriodMs = 20.0; MaxCv = 0.15; MinFires = 10 }
        { Name = "scan_50ms"; ExpectedPeriodMs = 50.0; MaxCv = 0.15; MinFires = 10 }
        // 일반 polling (100-500ms)
        { Name = "poll_100ms"; ExpectedPeriodMs = 100.0; MaxCv = 0.15; MinFires = 10 }
        { Name = "poll_200ms"; ExpectedPeriodMs = 200.0; MaxCv = 0.15; MinFires = 8 }
        { Name = "poll_500ms"; ExpectedPeriodMs = 500.0; MaxCv = 0.15; MinFires = 6 }
        // 느린 watchdog / heartbeat
        { Name = "watchdog_1s"; ExpectedPeriodMs = 1000.0; MaxCv = 0.10; MinFires = 5 }
        { Name = "heartbeat_5s"; ExpectedPeriodMs = 5000.0; MaxCv = 0.10; MinFires = 5 }
    ]

    /// 시각 list 가 패턴에 매칭되는지 검사.
    let private matchesPattern (times: int64[]) (spec: PollingPatternSpec) : bool =
        if times.Length < spec.MinFires then false
        else
            let intervals =
                [| for i in 1 .. times.Length - 1 ->
                    float (times.[i] - times.[i - 1]) |]
            if intervals.Length < 3 then false
            else
                let m = Array.average intervals
                if m < 1.0 then false
                else
                    let s =
                        sqrt (Array.averageBy (fun x -> (x - m) ** 2.0) intervals)
                    let cv = s / m
                    // mean 이 expected 의 ±20% 이내 + cv 가 spec.MaxCv 이내
                    let periodOk =
                        m >= spec.ExpectedPeriodMs * 0.80
                        && m <= spec.ExpectedPeriodMs * 1.20
                    let cvOk = cv <= spec.MaxCv
                    periodOk && cvOk

    /// 시각 list 가 어떤 known polling 패턴에 매칭되는지 검사.
    /// 결과: Some pattern (가장 잘 매칭) 또는 None.
    let matchPattern (times: int64 seq) : PollingPatternSpec option =
        let arr = Array.ofSeq times |> Array.sort
        knownPatterns
        |> List.tryFind (matchesPattern arr)

    /// 빠른 boolean — 알려진 polling 패턴 매치 여부.
    let isKnownPolling (times: int64 seq) : bool =
        (matchPattern times).IsSome

    /// Cycle-relative offset uniformity 검사.
    /// 한 source 의 events 의 (t mod cycleMs) 분포가 매우 적은 distinct positions
    /// (예: 정확히 3개 위치에 반복) 이면 polling within cycle.
    ///
    /// 결과: (isCyclicPolling, uniqueOffsets, totalFires)
    let detectCyclicPolling (times: int64 seq) (cycleMs: int64) : bool * int * int =
        let arr = Array.ofSeq times |> Array.sort
        if arr.Length < 6 || cycleMs <= 0L then false, 0, arr.Length
        else
            // mod cycle 후 50ms tolerance 로 클러스터 카운트
            let tolMs = 50L
            let offsets = arr |> Array.map (fun t -> t % cycleMs) |> Array.sort
            // 인접 offsets 가 tolMs 이내 → 같은 cluster
            let mutable clusters = 1
            for i in 1 .. offsets.Length - 1 do
                if offsets.[i] - offsets.[i - 1] > tolMs then
                    clusters <- clusters + 1
            // 만약 cycle 끝과 시작이 connected (wraparound) 이면 1 줄임
            let firstOff = offsets.[0]
            let lastOff = offsets.[offsets.Length - 1]
            if cycleMs - lastOff + firstOff <= tolMs then
                clusters <- max 1 (clusters - 1)
            // polling 의심: events 가 cycle 안 적은 (≤ events/cycle × 1.5) 곳에 집중
            // 즉 같은 위치에 반복 발화
            let firesPerCluster =
                if clusters > 0 then float arr.Length / float clusters else 0.0
            // 평균 cluster 당 발화 수 ≥ 5 + clusters ≥ 3 → polling.
            // 진짜 burst causation 은 clusters 적거나 firesPerCluster 작음.
            // 진짜 single-fire source 는 clusters=1.
            let isCyclic = clusters >= 3 && firesPerCluster >= 5.0
            isCyclic, clusters, arr.Length
