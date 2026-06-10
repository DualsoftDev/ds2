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

    /// <summary>span 목록의 개수/최소/최대/평균(ms). 빈 입력은 (0, null, null, null) — JS apiMeasured 동일.</summary>
    public static (int Count, double? Min, double? Max, double? Mean) Measured(IReadOnlyList<double> spans)
    {
        if (spans is null || spans.Count == 0) return (0, null, null, null);
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity, sum = 0;
        foreach (var x in spans)
        {
            if (x < mn) mn = x;
            if (x > mx) mx = x;
            sum += x;
        }
        return (spans.Count, mn, mx, sum / spans.Count);
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
