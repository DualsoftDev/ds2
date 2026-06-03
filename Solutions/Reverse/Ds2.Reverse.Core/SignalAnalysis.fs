/// Signal-domain analysis — FFT / autocorrelation 기반 periodicity 검출.
/// Polling vs random fire 구분에 사용.
namespace Ds2.Reverse.Core

module SignalAnalysis =

    /// 1-D DFT — N small (∼ < 200) 일 때 직접 O(N²).
    /// 입력: signal (length N), 출력: power spectrum (length N/2).
    let dft (signal: float[]) : float[] =
        let n = signal.Length
        if n < 4 then [||]
        else
            let halfN = n / 2
            let power = Array.zeroCreate<float> halfN
            for k in 1 .. halfN - 1 do
                let mutable re = 0.0
                let mutable im = 0.0
                for j in 0 .. n - 1 do
                    let theta = 2.0 * System.Math.PI * float k * float j / float n
                    re <- re + signal.[j] * cos theta
                    im <- im - signal.[j] * sin theta
                power.[k] <- (re * re + im * im) / float n
            power

    /// Periodicity 측정 결과.
    type PeriodicityScore = {
        /// 가장 강한 power peak 의 power
        MaxPeakPower: float
        /// 평균 power (non-zero bins)
        MeanPower: float
        /// peak/mean ratio — > 5 면 강한 주기성
        PeakRatio: float
        /// 가장 강한 peak 의 frequency bin index
        PeakBin: int
        /// signal 길이
        N: int
    }

    /// Time series 의 periodicity score.
    /// signal: inter-arrival times 또는 binned event counts.
    let periodicityScore (signal: float[]) : PeriodicityScore =
        let n = signal.Length
        if n < 4 then
            { MaxPeakPower = 0.0; MeanPower = 0.0; PeakRatio = 0.0
              PeakBin = -1; N = n }
        else
            let power = dft signal
            if power.Length < 2 then
                { MaxPeakPower = 0.0; MeanPower = 0.0; PeakRatio = 0.0
                  PeakBin = -1; N = n }
            else
                // 0-th bin (DC) 제외
                let nonZero = power |> Array.skip 1
                let maxPow = nonZero |> Array.max
                let meanPow = nonZero |> Array.average
                let peakBin =
                    nonZero
                    |> Array.mapi (fun i p -> i + 1, p)
                    |> Array.maxBy snd
                    |> fst
                let ratio = if meanPow < 1e-9 then 0.0 else maxPow / meanPow
                {
                    MaxPeakPower = maxPow
                    MeanPower = meanPow
                    PeakRatio = ratio
                    PeakBin = peakBin
                    N = n
                }

    /// Event time series 가 polling 인지 판정.
    /// times: 정렬된 event 시각 list.
    /// binMs: histogram bin 크기.
    /// 결과: (isPolling, peakRatio, peakBin)
    let detectPollingFromTimes (times: int64 seq) (binMs: int64) : bool * float * int =
        let arr = Array.ofSeq times |> Array.sort
        if arr.Length < 8 then false, 0.0, -1
        else
            let span = arr.[arr.Length - 1] - arr.[0]
            if span <= 0L then false, 0.0, -1
            else
                let nBins = int (span / binMs) + 1
                if nBins < 8 || nBins > 4096 then false, 0.0, -1
                else
                    let hist = Array.zeroCreate<float> nBins
                    for t in arr do
                        let idx = int ((t - arr.[0]) / binMs)
                        if idx < nBins then
                            hist.[idx] <- hist.[idx] + 1.0
                    let score = periodicityScore hist
                    // peak ratio > 5 면 강한 주기성 → polling 의심
                    let isPolling = score.PeakRatio > 5.0
                    isPolling, score.PeakRatio, score.PeakBin

    /// Inter-arrival time 시계열 (events 의 t_i+1 - t_i).
    let interArrivals (times: int64 seq) : float[] =
        let arr = Array.ofSeq times |> Array.sort
        if arr.Length < 2 then [||]
        else
            [| for i in 1 .. arr.Length - 1 -> float (arr.[i] - arr.[i - 1]) |]

    /// Inter-arrival CV — 일정 간격이면 매우 작음 (polling 시그니처).
    let interArrivalCV (times: int64 seq) : float =
        let intervals = interArrivals times
        if intervals.Length < 3 then 999.0
        else
            let m = Array.average intervals
            if m < 1.0 then 999.0
            else
                let s =
                    sqrt (Array.averageBy (fun x -> (x - m) ** 2.0) intervals)
                s / m
