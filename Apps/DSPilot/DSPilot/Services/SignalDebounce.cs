// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services;

/// <summary>
/// 신호 채터링(chattering) 필터 — 순수 함수. <b>minStableMs 미만으로 유지된 상태 변화는 없었던 것으로 본다</b>
/// (양방향: 짧은 OFF 끊김도, 짧은 ON 펄스도 무시). 2026-09-07 현장 #121 의 head OUT 이 사이클 인계 시점마다
/// 0.2~0.4초 꺼졌다 켜지며 재상승 → 가짜 사이클 시작(50초 CT) 이 매 사이클 생기던 사례의 해법.
///
/// <para>정의(디바운스 = 안정 시간 기준):</para>
/// <list type="bullet">
///   <item>전이 t_i 의 "유지 시간" = t_{i+1} − t_i (마지막 전이는 무한 = 안정).</item>
///   <item>유지 시간 ≥ minStableMs 인 전이만 후보. 후보가 현재 안정 상태와 같은 값이면(=글리치 후 복귀) 버린다.</item>
///   <item>따라서 채터 버스트 뒤 실제 전이는 <b>안정된 시점</b>(버스트 마지막 전이)으로 잡힌다.</item>
///   <item>minStableMs ≤ 0 = 필터 없음(입력 그대로).</item>
/// </list>
/// 사이클 경계(<see cref="CycleBoundaryEdges"/> → <c>IPlcRepository.FindActiveEdgesAsync</c>)와 간트 신호 구간
/// (<c>CycleAnalysisService.GetActualIoSignalSegmentsInTimeRangeAsync</c>)이 같은 정의를 공유한다.
/// 창(window) 경계 근처의 판정은 호출자가 앞뒤로 minStableMs 만큼 여유를 두고 조회해 해결한다(리포지토리 구현 참조).
/// </summary>
public static class SignalDebounce
{
    /// <summary>상태 전이 1개 — 이 시각부터 <paramref name="Active"/> 상태가 된다.</summary>
    public readonly record struct Transition(DateTime At, bool Active);

    /// <summary>
    /// 전이 목록(시각 오름차순, 값 교대 가정 — 교대가 아니어도 동작하나 중복 값은 무시됨)을 디바운스한다.
    /// 첫 전이 이전의 상태는 첫 전이의 반대값으로 본다(로그 LAG 규약: 첫 전이는 실제 변화).
    /// 반환 = 안정 전이만, 값 교대 보장.
    /// </summary>
    public static List<Transition> Filter(IReadOnlyList<Transition> transitions, int minStableMs)
    {
        var result = new List<Transition>(transitions.Count);
        if (transitions.Count == 0) return result;
        if (minStableMs <= 0)
        {
            // 필터 없음 — 단, 교대 보장만 유지(같은 값 연속은 첫 것만).
            bool? last = null;
            foreach (var t in transitions)
            {
                if (last == t.Active) continue;
                result.Add(t);
                last = t.Active;
            }
            return result;
        }

        var minStable = TimeSpan.FromMilliseconds(minStableMs);
        bool stable = !transitions[0].Active;
        for (int i = 0; i < transitions.Count; i++)
        {
            var t = transitions[i];
            bool isStable = i + 1 >= transitions.Count || (transitions[i + 1].At - t.At) >= minStable;
            if (!isStable) continue;            // 짧게 머문 상태 — 글리치/채터로 무시
            if (t.Active == stable) continue;   // 글리치 뒤 원상 복귀 — 전이 아님
            result.Add(t);
            stable = t.Active;
        }
        return result;
    }

    /// <summary>
    /// ON 구간 목록(시각 오름차순, 겹침 없음)을 디바운스한다 — 짧은 OFF 틈은 병합되고 짧은 ON 펄스는 사라진다.
    /// <paramref name="windowEnd"/> 는 마지막 구간이 창 끝까지 ON 인 경우(끝이 열린 구간)의 끝시각.
    /// 구간의 끝이 창 끝과 같으면 그 OFF 전이는 "관측 불가"라 안정으로 취급되지 않고 ON 이 이어지는 것으로 본다.
    /// </summary>
    public static List<(DateTime Start, DateTime End)> FilterIntervals(
        IReadOnlyList<(DateTime Start, DateTime End)> intervals, int minStableMs, DateTime? windowEnd = null)
    {
        if (minStableMs <= 0 || intervals.Count == 0)
            return intervals.ToList();

        var transitions = new List<Transition>(intervals.Count * 2);
        foreach (var (s, e) in intervals)
        {
            transitions.Add(new Transition(s, true));
            // 창 끝에서 잘린 열린 구간은 OFF 전이를 넣지 않는다(끝을 모름 → ON 지속).
            if (!(windowEnd.HasValue && e >= windowEnd.Value))
                transitions.Add(new Transition(e, false));
        }

        var filtered = Filter(transitions, minStableMs);
        var result = new List<(DateTime, DateTime)>(filtered.Count / 2 + 1);
        DateTime? open = null;
        foreach (var t in filtered)
        {
            if (t.Active) { open ??= t.At; }
            else if (open.HasValue)
            {
                if (t.At > open.Value) result.Add((open.Value, t.At));
                open = null;
            }
        }
        if (open.HasValue)
        {
            var end = windowEnd ?? intervals[^1].End;
            if (end > open.Value) result.Add((open.Value, end));
        }
        return result;
    }
}
