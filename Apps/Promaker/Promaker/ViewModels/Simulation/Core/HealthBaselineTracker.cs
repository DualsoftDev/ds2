using System;
using System.Collections.Generic;

namespace Promaker.ViewModels;

/// <summary>동결된 건강 기준선 — 설비가 건강하던 시점의 duration 분포 스냅샷.</summary>
internal sealed record FrozenBaseline(
    double MedianMs, double Q1Ms, double Q3Ms, DateTime At, int SampleCount, bool Auto)
{
    public double IqrMs => Math.Max(1.0, Q3Ms - Q1Ms);
}

/// <summary>샘플 1개 반영 결과 — 호출자가 로그/알림으로 옮길 전이만 담는다.</summary>
internal readonly record struct HealthSampleResult(
    FrozenBaseline? JustFrozen, bool FrozenByCap, bool IqrAlarmRaised, bool IqrAlarmCleared, double? DriftPct);

/// <summary>드리프트 선형 외삽 — "현재 추세 지속 시" 상한 도달까지 남은 시간. 날짜 약속이 아니라 권고.</summary>
internal sealed record MaintenanceForecast(
    Guid WorkId, string WorkName,
    double FrozenMedianMs, double CurrentMedianMs, double DriftPct,
    double SlopeMsPerHour, double? HoursToMaxDuration);

/// <summary>
/// device 건강 기준선 동결 + 드리프트 수명 추적기 (work 단위).
/// 기준선은 2개다 — 단기(슬라이딩 윈도우, CallDurationLearning, abnormal 판정용)와
/// 건강 스냅샷(여기서 동결, 드리프트 측정용). 단기 기준선만 있으면 느려지는 설비를
/// 기준선이 따라가며 같이 느려져 노화가 안 보인다 — 건강하던 시점을 동결해 줄자로 삼는다.
///
/// 동결: 자동 수렴(러닝 중앙값이 연속 <see cref="ConvergencePoints"/> 사이클 ±<see cref="ConvergenceBandPct"/>%
/// 밴드 안, 하한 <see cref="MinFreezeSamples"/> 샘플) 또는 상한 <see cref="MaxFreezeSamples"/> 샘플 강제 동결,
/// 또는 수동 <see cref="FreezeNow"/>(SignalHub 브로드캐스트로 전 인스턴스 동시).
/// 동결 후: 단기 중앙값 vs 동결 중앙값 드리프트 % 시계열 + 선형 외삽(MaxDuration 도달 예상),
/// IQR 확대(분포가 넓어짐 = 동작 불안정)는 평균 드리프트보다 먼저 오는 조기 경보.
/// 세션 내 추적 — 영속화는 후속(학습값 파일 저장 인프라에 합류 예정).
/// </summary>
internal sealed class HealthBaselineTracker
{
    /// <summary>수렴 판정에 쓰는 연속 러닝 중앙값 수.</summary>
    internal const int ConvergencePoints = 5;
    /// <summary>수렴 밴드(±%) — 연속 중앙값들이 이 안에 모이면 분포가 자리잡은 것.</summary>
    internal const double ConvergenceBandPct = 2.5;
    /// <summary>자동 동결 하한 샘플 수 — 이전엔 중앙값 자체가 불안정.</summary>
    internal const int MinFreezeSamples = 5;
    /// <summary>자동 동결 상한 샘플 수 — 수렴 못 해도 여기서 강제 동결(영영 안 얼면 드리프트 측정 시작을 못 한다).</summary>
    internal const int MaxFreezeSamples = 30;
    /// <summary>수동 동결 최소 샘플 수 — CallDurationLearning.MinSamples 와 동일 근거(중앙값 최소 의미).</summary>
    internal const int MinManualFreezeSamples = 3;
    /// <summary>동결 후 드리프트 측정용 단기 중앙값 윈도우.</summary>
    internal const int ShortWindowSize = 10;
    /// <summary>IQR 확대 경보 임계(동결 IQR 대비 배율) — 초과 시 경보.</summary>
    internal const double IqrAlarmRatio = 1.5;
    /// <summary>IQR 경보 해제 임계 — 히스테리시스(경보가 경계에서 깜빡이지 않게).</summary>
    internal const double IqrAlarmClearRatio = 1.2;
    /// <summary>외삽 최소 시계열 점 수 — 이하면 기울기가 노이즈.</summary>
    internal const int MinForecastPoints = 10;
    /// <summary>의미 있는 악화 기울기 바닥(기준선 중앙값 대비 시간당 비율) — 동일값 시계열의
    /// 부동소수점 잔차(1e-13 수준)가 "황당한 외삽"으로 새는 것을 막는다.</summary>
    internal const double MinSlopeRatioPerHour = 0.0001;
    /// <summary>드리프트 시계열 보관 상한 — 장기 가동 OOM 방지(오래된 점부터 솎음).</summary>
    internal const int MaxDriftPoints = 5000;

    private sealed class WorkHealth
    {
        public readonly Queue<double> Samples = new();          // 동결 전 러닝 윈도우(MaxFreezeSamples)
        public readonly List<double> RecentMedians = new();     // 수렴 판정용 최근 러닝 중앙값
        public int TotalSamples;
        public FrozenBaseline? Frozen;
        public readonly Queue<double> ShortSamples = new();     // 동결 후 단기 윈도우
        public readonly List<(DateTime At, double MedianMs)> DriftSeries = new();
        public bool IqrAlarmActive;
    }

    private readonly Dictionary<Guid, WorkHealth> _works = new();
    private readonly IReadOnlyDictionary<Guid, double> _workMaxDurationMs;
    private readonly IReadOnlyDictionary<Guid, string> _workNames;

    public HealthBaselineTracker(
        IReadOnlyDictionary<Guid, double> workMaxDurationMs,
        IReadOnlyDictionary<Guid, string> workNames)
    {
        _workMaxDurationMs = workMaxDurationMs;
        _workNames = workNames;
    }

    public string NameOf(Guid workId)
        => _workNames.TryGetValue(workId, out var name) ? name : workId.ToString("N")[..8];

    /// <summary>정상 완료 사이클 실측 1건 반영 — CallDurationLearning.SampleRecorded 에서 호출.
    /// abnormal/blackout 오염 샘플은 학습기가 이미 걸렀으므로 여기는 정상 사이클만 들어온다.</summary>
    public HealthSampleResult OnSample(Guid workId, double spanMs, DateTime at)
    {
        if (spanMs <= 0) return default;
        if (!_works.TryGetValue(workId, out var h))
            _works[workId] = h = new WorkHealth();

        return h.Frozen is null ? OnLearningSample(h, spanMs, at) : OnDriftSample(h, spanMs, at);
    }

    private static HealthSampleResult OnLearningSample(WorkHealth h, double spanMs, DateTime at)
    {
        h.Samples.Enqueue(spanMs);
        while (h.Samples.Count > MaxFreezeSamples) h.Samples.Dequeue();
        h.TotalSamples++;

        var median = MedianOf(h.Samples);
        h.RecentMedians.Add(median);
        while (h.RecentMedians.Count > ConvergencePoints) h.RecentMedians.RemoveAt(0);

        var converged = h.TotalSamples >= MinFreezeSamples && IsConverged(h.RecentMedians);
        var capped = !converged && h.TotalSamples >= MaxFreezeSamples;
        if (!converged && !capped) return default;

        h.Frozen = BuildBaseline(h.Samples, at, auto: true);
        return new HealthSampleResult(h.Frozen, FrozenByCap: capped, false, false, null);
    }

    private static HealthSampleResult OnDriftSample(WorkHealth h, double spanMs, DateTime at)
    {
        var frozen = h.Frozen!;
        h.ShortSamples.Enqueue(spanMs);
        while (h.ShortSamples.Count > ShortWindowSize) h.ShortSamples.Dequeue();

        var shortMedian = MedianOf(h.ShortSamples);
        h.DriftSeries.Add((at, shortMedian));
        if (h.DriftSeries.Count > MaxDriftPoints)
            h.DriftSeries.RemoveRange(0, h.DriftSeries.Count - MaxDriftPoints);

        var driftPct = (shortMedian - frozen.MedianMs) / frozen.MedianMs * 100.0;

        bool raised = false, cleared = false;
        if (h.ShortSamples.Count >= MinFreezeSamples)
        {
            var (q1, q3) = QuartilesOf(h.ShortSamples);
            var ratio = Math.Max(1.0, q3 - q1) / frozen.IqrMs;
            if (!h.IqrAlarmActive && ratio >= IqrAlarmRatio) { h.IqrAlarmActive = true; raised = true; }
            else if (h.IqrAlarmActive && ratio < IqrAlarmClearRatio) { h.IqrAlarmActive = false; cleared = true; }
        }
        return new HealthSampleResult(null, false, raised, cleared, driftPct);
    }

    /// <summary>수동 "지금 동결" — 아직 안 언 work 중 표본이 최소치 이상인 것 전부.
    /// 반환 = 이번에 동결된 (workId, baseline) 목록(로그용).</summary>
    public List<(Guid WorkId, FrozenBaseline Baseline)> FreezeNow(DateTime at)
    {
        var frozen = new List<(Guid, FrozenBaseline)>();
        foreach (var (workId, h) in _works)
        {
            if (h.Frozen is not null || h.Samples.Count < MinManualFreezeSamples) continue;
            h.Frozen = BuildBaseline(h.Samples, at, auto: false);
            frozen.Add((workId, h.Frozen));
        }
        return frozen;
    }

    /// <summary>드리프트 추세 선형 외삽 — 시계열 부족/악화 추세 아님이면 null.</summary>
    public MaintenanceForecast? TryForecast(Guid workId)
    {
        if (!_works.TryGetValue(workId, out var h) || h.Frozen is not { } frozen) return null;
        if (h.DriftSeries.Count < MinForecastPoints) return null;

        // 최소제곱 — x=시간(h, 첫 점 기준), y=단기 중앙값(ms).
        var t0 = h.DriftSeries[0].At;
        double n = h.DriftSeries.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (at, med) in h.DriftSeries)
        {
            var x = (at - t0).TotalHours;
            sx += x; sy += med; sxx += x * x; sxy += x * med;
        }
        var denom = n * sxx - sx * sx;
        if (denom <= 0) return null;
        var slope = (n * sxy - sx * sy) / denom;   // ms/hour

        var current = h.DriftSeries[^1].MedianMs;
        var driftPct = (current - frozen.MedianMs) / frozen.MedianMs * 100.0;

        double? hoursToMax = null;
        if (slope >= frozen.MedianMs * MinSlopeRatioPerHour
            && _workMaxDurationMs.TryGetValue(workId, out var maxMs)
            && maxMs > current)
            hoursToMax = (maxMs - current) / slope;

        return new MaintenanceForecast(workId, NameOf(workId), frozen.MedianMs, current, driftPct, slope, hoursToMax);
    }

    /// <summary>정지 시 요약 — work 별 동결 상태/드리프트/외삽 한 줄씩(로그용). 동결된 work 가 없으면 빈 목록.</summary>
    public List<string> SummaryLines()
    {
        var lines = new List<string>();
        foreach (var (workId, h) in _works)
        {
            if (h.Frozen is not { } frozen) continue;
            var name = NameOf(workId);
            var mode = frozen.Auto ? "자동" : "수동";
            if (h.DriftSeries.Count == 0)
            {
                lines.Add($"{name}: 기준선 {frozen.MedianMs:F0}ms ({mode} 동결, 표본 {frozen.SampleCount}) — 드리프트 표본 없음");
                continue;
            }
            var current = h.DriftSeries[^1].MedianMs;
            var driftPct = (current - frozen.MedianMs) / frozen.MedianMs * 100.0;
            var alarm = h.IqrAlarmActive ? ", IQR 확대 경보 중" : "";
            var forecast = TryForecast(workId);
            var tail = forecast?.HoursToMaxDuration is { } hours
                ? $", 추세 지속 시 약 {hours:F0}h 후 상한 도달 — 정비 권고"
                : "";
            lines.Add($"{name}: 기준선 {frozen.MedianMs:F0}ms ({mode}) → 현재 {current:F0}ms ({driftPct:+0.0;-0.0}%){alarm}{tail}");
        }
        return lines;
    }

    public bool HasFrozenBaseline
    {
        get
        {
            foreach (var h in _works.Values)
                if (h.Frozen is not null) return true;
            return false;
        }
    }

    /// <summary>연속 러닝 중앙값들이 중심 ±밴드 안에 전부 들어왔는가.</summary>
    internal static bool IsConverged(IReadOnlyList<double> medians)
    {
        if (medians.Count < ConvergencePoints) return false;
        double min = double.MaxValue, max = double.MinValue;
        foreach (var m in medians)
        {
            if (m < min) min = m;
            if (m > max) max = m;
        }
        var center = (min + max) / 2.0;
        return center > 0 && (max - min) <= center * (ConvergenceBandPct * 2 / 100.0);
    }

    private static FrozenBaseline BuildBaseline(IReadOnlyCollection<double> samples, DateTime at, bool auto)
    {
        var (q1, q3) = QuartilesOf(samples);
        return new FrozenBaseline(MedianOf(samples), q1, q3, at, samples.Count, auto);
    }

    private static double[] SortedArrayOf(IReadOnlyCollection<double> values)
    {
        var sorted = new double[values.Count];
        var i = 0;
        foreach (var v in values) sorted[i++] = v;
        Array.Sort(sorted);
        return sorted;
    }

    private static double MedianOf(IReadOnlyCollection<double> values)
    {
        var sorted = SortedArrayOf(values);
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
    }

    private static (double Q1, double Q3) QuartilesOf(IReadOnlyCollection<double> values)
    {
        var sorted = SortedArrayOf(values);
        return (PercentileOf(sorted, 0.25), PercentileOf(sorted, 0.75));
    }

    /// <summary>선형 보간 백분위 — 정렬 배열 기준.</summary>
    private static double PercentileOf(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
