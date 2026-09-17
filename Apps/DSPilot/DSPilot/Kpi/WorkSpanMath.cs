// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Kpi;

/// <summary>반열린 구간 [S, E). 전부 epoch ms.</summary>
public readonly record struct Span(long S, long E)
{
    public long Length => E > S ? E - S : 0;
}

/// <summary>
/// work 가 "실제로 움직인 시간" 계산. doc/30 §1.
/// <para>
/// work 지속시간 = 그 work 에 속한 call 들의 <b>OUT↑ → IN↑ 스팬 합집합</b>.
/// 합집합이라 한 work 안에서 call 이 겹쳐 돌아도 시간을 두 번 세지 않는다.
/// </para>
/// <para>
/// 짝짓기는 간트 화면(cycle-gantt.js apiSpans)·자동 보정(<see cref="Services.ApiSpanMath"/>)과 같은
/// 그리디 2-포인터다 — 각 OUT 을 (그 OUT 이후 ~ 다음 OUT 이전) 첫 IN 에 맞춘다. IN 이 없으면 그 OUT 은 버린다
/// (신호 누락을 임의 복원하지 않는다는 원본 스펙 §14-6 태도).
/// </para>
/// </summary>
public static class WorkSpanMath
{
    /// <summary>OUT↑·IN↑ 시각 목록을 짝지어 스팬을 만든다. 입력은 정렬되지 않아도 된다.</summary>
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
}
