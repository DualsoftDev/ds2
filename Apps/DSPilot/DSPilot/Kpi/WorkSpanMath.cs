// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Kpi;

/// <summary>반열린 구간 [S, E). 전부 epoch ms.</summary>
public readonly record struct Span(long S, long E)
{
    public long Length => E > S ? E - S : 0;
}

/// <summary>call 의 응답 유형. doc/30 §2.2.</summary>
public enum CallKind
{
    /// <summary>OUT 이 없다 — IN 만 있는 call. 명령이 없었으니 동작이 시작된 적이 없다. 구간을 만들지 않는다.</summary>
    None = 0,
    /// <summary>o~i — OUT 상승 중 절반 이상이 다음 OUT 상승 전에 IN 상승을 받는다. 구간 = OUT↑ → 첫 IN↑.</summary>
    OutIn = 1,
    /// <summary>o~o — 그 외. 구간 = OUT↑ → OUT↓.</summary>
    OutOnly = 2,
}

/// <summary>
/// 신호에서 call·work 구간을 만드는 순수 함수. doc/30 §2.2 · §2.3 · §3.
/// <para>
/// 시작점은 언제나 OUT 상승이다. IN 상승은 끝점으로만 쓰인다. 채터 필터는 없다 — 떨림이 있어도 이 짝짓기가
/// 버스트의 마지막 라이징을 고르고 구간이 떨림 폭만큼만 짧아진다(doc/30 §15-②).
/// </para>
/// </summary>
public static class WorkSpanMath
{
    /// <summary>o~i 판정 비율 — OUT 상승 중 이 비율 이상이 IN 응답을 받으면 o~i.</summary>
    public const double OutInMatchRate = 0.5;

    /// <summary>call 유형. OUT 이 없으면 None, IN 이 없으면 OutOnly.</summary>
    public static CallKind KindOf(IReadOnlyList<long> outRises, IReadOnlyList<long> inRises)
    {
        if (outRises.Count == 0) return CallKind.None;
        if (inRises.Count == 0) return CallKind.OutOnly;

        var outs = outRises.OrderBy(x => x).ToList();
        var ins = inRises.OrderBy(x => x).ToList();
        int hit = 0, j = 0;
        for (int i = 0; i < outs.Count; i++)
        {
            long o = outs[i];
            long nextO = i + 1 < outs.Count ? outs[i + 1] : long.MaxValue;
            while (j < ins.Count && ins[j] <= o) j++;
            if (j < ins.Count && ins[j] < nextO) hit++;
        }
        return hit >= outs.Count * OutInMatchRate ? CallKind.OutIn : CallKind.OutOnly;
    }

    /// <summary>
    /// call 구간(doc/30 §2.2). o~i 면 <see cref="Pair"/>, o~o 면 OUT ON 구간 그대로, OUT 이 없으면 빈 목록.
    /// </summary>
    /// <param name="outIntervals">OUT 의 ON 구간(상승, 하강). 하강을 모르는 열린 구간은 Fall ≤ Rise 로 넘기면 o~o 에서 버려진다.</param>
    /// <param name="inRises">IN 상승 시각.</param>
    public static List<Span> CallSpans(IReadOnlyList<(long Rise, long Fall)> outIntervals, IReadOnlyList<long> inRises)
    {
        var rises = outIntervals.Select(x => x.Rise).ToList();
        return KindOf(rises, inRises) switch
        {
            CallKind.OutIn => Pair(rises, inRises),
            CallKind.OutOnly => outIntervals
                .Where(x => x.Fall > x.Rise)
                .Select(x => new Span(x.Rise, x.Fall))
                .OrderBy(s => s.S)
                .ToList(),
            _ => [],
        };
    }

    /// <summary>
    /// OUT↑·IN↑ 시각 목록을 짝지어 스팬을 만든다. 각 OUT 은 (그 OUT 이후 ~ 다음 OUT 이전) <b>첫</b> IN 하나에만 맞춘다.
    /// 그 뒤 IN 은 무시하고, IN 이 없으면 그 OUT 은 버린다(신호 누락을 임의 복원하지 않는다). 입력은 정렬되지 않아도 된다.
    /// </summary>
    public static List<Span> Pair(IEnumerable<long> outRises, IEnumerable<long> inRises)
    {
        var outs = outRises.OrderBy(x => x).ToList();
        var ins = inRises.OrderBy(x => x).ToList();
        var spans = new List<Span>();
        if (outs.Count == 0 || ins.Count == 0) return spans;

        int j = 0;
        for (int i = 0; i < outs.Count; i++)
        {
            long o = outs[i];
            long nextO = i + 1 < outs.Count ? outs[i + 1] : long.MaxValue;
            while (j < ins.Count && ins[j] < o) j++;
            if (j < ins.Count && ins[j] < nextO)
            {
                if (ins[j] > o) spans.Add(new Span(o, ins[j]));
                j++;
            }
        }
        return spans;
    }

    /// <summary>겹치거나 맞닿은 구간을 하나로 합친다.</summary>
    public static List<Span> Union(IEnumerable<Span> spans)
    {
        var list = spans.Where(s => s.E > s.S).OrderBy(s => s.S).ToList();
        var merged = new List<Span>();
        foreach (var s in list)
        {
            if (merged.Count > 0 && s.S <= merged[^1].E)
            {
                if (s.E > merged[^1].E) merged[^1] = new Span(merged[^1].S, s.E);
            }
            else merged.Add(s);
        }
        return merged;
    }

    /// <summary>work 구간(doc/30 §2.3) — call 구간 집합의 최소 시작 ~ 최대 끝. 길이를 더하지 않는다. 비었으면 null.</summary>
    public static Span? Envelope(IEnumerable<Span> spans)
    {
        long s = long.MaxValue, e = long.MinValue;
        foreach (var x in spans)
        {
            if (x.E <= x.S) continue;
            if (x.S < s) s = x.S;
            if (x.E > e) e = x.E;
        }
        return e > s ? new Span(s, e) : null;
    }

    /// <summary>구간 목록이 [from, to) 와 겹치는 시간의 합.</summary>
    public static long OverlapMs(IReadOnlyList<Span> spans, long from, long to)
    {
        long total = 0;
        foreach (var s in spans)
        {
            long a = Math.Max(s.S, from);
            long b = Math.Min(s.E, to);
            if (b > a) total += b - a;
        }
        return total;
    }

    /// <summary>
    /// 경계 스냅(doc/30 §3) — 시작이 다음 경계 직전 <paramref name="snapMs"/> 안이면 그 경계 시각을 돌려준다.
    /// 같은 스캔에서 head 보다 2~4ms 먼저 뜨는 형제 call 이 이전 사이클로 잘못 붙는 것을 막는다.
    /// <paramref name="boundariesSorted"/> 는 오름차순이어야 한다.
    /// </summary>
    public static long Snap(long startMs, IReadOnlyList<long> boundariesSorted, long snapMs)
    {
        if (snapMs <= 0 || boundariesSorted.Count == 0) return startMs;
        int lo = 0, hi = boundariesSorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (boundariesSorted[mid] < startMs) lo = mid + 1; else hi = mid;
        }
        return lo < boundariesSorted.Count && boundariesSorted[lo] - startMs <= snapMs
            ? boundariesSorted[lo]
            : startMs;
    }
}
