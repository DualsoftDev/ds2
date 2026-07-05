// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using DSPilot.Controllers;

namespace DSPilot.Services;

/// <summary>
/// 실측 duration 페어링 — cycle-gantt.js 의 apiSpans()/apiMeasured() (DSPilot/wwwroot/app/cycle-gantt.js:101-121)
/// 의 순수 C# 포팅. OutTag(명령) ON 시작과 InTag(응답) ON 시작을 그리디 2-포인터로 짝지어 latency(ms) span 을
/// 만들고 count/min/max/mean 으로 집계한다. CallTest 화면이 클라에서 하던 계산을 자동 보정(서버)에서 동일하게 쓴다.
/// </summary>
public static class ApiSpanMath
{
    /// <summary>
    /// 명령(out) 시작 시각과 응답(in) 시작 시각을 짝지어 latency(ms) 목록을 만든다.
    /// JS apiSpans 동일 — 양쪽을 ms 로 변환·정렬한 뒤, 각 OUT 을 (그 OUT 이후 ~ 다음 OUT 이전) 첫 IN 에 매칭.
    /// IN 이 부족하면 후행 OUT 은 매칭 실패로 조용히 누락(원본 동작 보존).
    /// </summary>
    public static List<double> Spans(IReadOnlyList<CtIntervalDto> outIntervals, IReadOnlyList<CtIntervalDto> inIntervals)
    {
        var outs = (outIntervals ?? Array.Empty<CtIntervalDto>()).Select(iv => ParseMs(iv.Start)).OrderBy(x => x).ToList();
        var ins = (inIntervals ?? Array.Empty<CtIntervalDto>()).Select(iv => ParseMs(iv.Start)).OrderBy(x => x).ToList();
        var spans = new List<double>();
        if (outs.Count == 0 || ins.Count == 0) return spans;

        int j = 0;
        for (int i = 0; i < outs.Count; i++)
        {
            var o = outs[i];
            var nextO = (i + 1 < outs.Count) ? outs[i + 1] : double.PositiveInfinity;
            while (j < ins.Count && ins[j] < o) j++;
            if (j < ins.Count && ins[j] < nextO) { spans.Add(ins[j] - o); j++; }
        }
        return spans;
    }

    /// <summary>
    /// span 목록의 개수/pMin/pMax/평균/실측최솟값/실측최댓값(ms). 빈 입력은 (0, null, null, null, null, null).
    /// pMin/pMax 는 정렬 후 분위수(OeeCtStatsService 와 동일 공식) — 이상치 단 1개가 임계를 왜곡하는
    /// measMax/mean+k·σ 방식보다 고변동(수작업·조립) 공정에서 안정적이다.
    /// percentileMax/percentileMin 을 지정하면 p95/p05 대신 해당 백분위수를 계산한다(기본값 95/5).
    /// RawMin/RawMax 는 sorted 첫/마지막 값(백분위수 무관한 순수 최솟값/최댓값).
    /// </summary>
    public static (int Count, double? PMin, double? PMax, double? Mean, double? RawMin, double? RawMax) Measured(
        IReadOnlyList<double> spans, double percentileMax = 95.0, double percentileMin = 5.0)
    {
        if (spans is null || spans.Count == 0) return (0, null, null, null, null, null);
        double sum = 0;
        foreach (var x in spans) sum += x;
        double mean = sum / spans.Count;

        var sorted = spans.OrderBy(x => x).ToList();
        return (spans.Count, Percentile(sorted, percentileMin), Percentile(sorted, percentileMax), mean, sorted[0], sorted[^1]);
    }

    private static double Percentile(List<double> sorted, double pct)
    {
        int idx = Math.Clamp((int)Math.Floor(pct / 100.0 * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[idx];
    }

    /// <summary>
    /// 중앙값과 클린 실측최대(중앙값 × 3 초과 span 은 통신 오염 단발 이상치로 보고 제외한 최댓값). 빈 입력은 (null, null).
    /// 3× 울타리는 DeviceDurationLearner.robustAvg 의 [med/3, med×3] 상한과 동일 규약 — 정상 이중모드(느린 정상 경로가
    /// 중앙값의 3배 이내)는 클램프로 보호하고, 재연결 burst 로 부풀려진 span 은 Max 임계에 반영하지 않는다.
    /// </summary>
    public static (double? Median, double? CleanMax) MedianAndCleanMax(IReadOnlyList<double> spans)
    {
        if (spans is null || spans.Count == 0) return (null, null);
        var sorted = spans.OrderBy(x => x).ToList();
        double median = Percentile(sorted, 50.0);
        double cleanMax = median;
        foreach (var x in sorted)
            if (x <= median * 3.0 && x > cleanMax) cleanMax = x;
        return (median, cleanMax);
    }

    /// <summary>
    /// 로컬 ISO("o", offset 포함) 문자열 → 절대 epoch ms. DateTimeOffset 으로 파싱하므로 tz/offset 을
    /// 올바르게 흡수한다(JS new Date(iso).getTime() 과 동일 절대값). 파싱 실패 시 0.
    /// </summary>
    private static double ParseMs(string iso)
        => DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToUnixTimeMilliseconds()
            : 0d;
}
