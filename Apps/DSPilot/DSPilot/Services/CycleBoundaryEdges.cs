// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// 사이클 경계 엣지의 <b>복수 I/O 쌍</b> 해석 — 엔진 의미론과 정렬(2026-09-02):
/// <list type="bullet">
///   <item>시작 = <b>OR(최초 OUT 활성 진입)</b> — 엔진이 어느 OUT 이든 상승 시 Going 을 거는 것과 동일.
///     전체 쌍의 OUT 엣지 union(중복 시각 제거).</item>
///   <item>완료 = <b>AND(전 쌍 도달)</b> — 엔진 canCompleteCall 의 forall 과 동일. 쌍별 완료 마커 스트림을
///     따로 반환하고, <see cref="CycleDerivation.BuildCycles(IReadOnlyList{DateTime}, IReadOnlyList{IReadOnlyList{DateTime}}, DateTime)"/>
///     이 사이클 창 안에서 "스트림별 첫 엣지의 최댓값(마지막 응답)"으로 합성한다.</item>
///   <item>쌍별 완료 마커 = InTag 활성 진입(있으면) else OutTag 활성 이탈(OutOnly 추정) —
///     <see cref="CycleCompletionResolver"/> 의 단일 쌍 규칙을 쌍 단위로 그대로 적용.</item>
/// </list>
/// 활성 판정은 <see cref="CallTagPair"/> 의 ActiveValue(모델 ValueSpec, 엔진 RuntimeSemantics 공유 정의)를 쓴다.
/// 화면(CallTestController)·재도출(CycleRecomputeService)·기본 경계(CycleAnalysisService)가 이 한 곳을 공유한다.
/// </summary>
public static class CycleBoundaryEdges
{
    /// <summary>시작 경계 = 전체 쌍 OUT 활성 진입 엣지의 union(오름차순, 동시각 dedup). OUT 없는 쌍은 제외.</summary>
    public static async Task<List<DateTime>> HeadStartsAsync(
        IPlcRepository plc, IReadOnlyList<CallTagPair> pairs,
        DateTime from, DateTime to, Guid? systemId)
    {
        // 공유 OUT(같은 주소 여러 쌍)은 중복 조회를 피한다 — (주소, 활성값) 단위 dedup.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new SortedSet<DateTime>();
        foreach (var p in pairs)
        {
            if (string.IsNullOrWhiteSpace(p.OutTag)) continue;
            if (!seen.Add($"{p.OutActiveValue ?? "~"}|{p.OutTag}")) continue;
            foreach (var t in await plc.FindActiveEdgesAsync(p.OutTag!, p.OutActiveValue, falling: false, from, to, systemId))
                merged.Add(t);
        }
        return merged.ToList();
    }

    /// <summary>
    /// 완료 마커 스트림(쌍별, 각 오름차순) + UI 라벨. IN 있는 쌍 = IN 활성 진입, OutOnly 쌍 = OUT 활성 이탈.
    /// 라벨은 정통(InTag) 쌍이 하나라도 있으면 "InTag", 전부 OutOnly 추정이면 "OutTag", 관측 불가면 null —
    /// <see cref="CycleCompletionResolver.SourceLabel"/> 의 단일 쌍 의미를 다수 쌍으로 확장한 것.
    /// </summary>
    public static async Task<(List<List<DateTime>> Streams, string? SourceLabel)> TailStreamsAsync(
        IPlcRepository plc, IReadOnlyList<CallTagPair> pairs,
        DateTime from, DateTime to, Guid? systemId)
    {
        var streams = new List<List<DateTime>>(pairs.Count);
        bool anyIn = false, anyOut = false;
        foreach (var p in pairs)
        {
            if (!string.IsNullOrWhiteSpace(p.InTag))
            {
                streams.Add(await plc.FindActiveEdgesAsync(p.InTag!, p.InActiveValue, falling: false, from, to, systemId));
                anyIn = true;
            }
            else if (!string.IsNullOrWhiteSpace(p.OutTag))
            {
                streams.Add(await plc.FindActiveEdgesAsync(p.OutTag!, p.OutActiveValue, falling: true, from, to, systemId));
                anyOut = true;
            }
            // 둘 다 없는 쌍 = 관측 불가 — AND 에 넣으면 모든 사이클이 영구 미완료가 되므로 제외.
        }
        var label = anyIn ? "InTag" : anyOut ? "OutTag" : null;
        return (streams, label);
    }

    /// <summary>완료 마커 엣지 전체 union(표시용) — 간트 tail 마커 틱은 모든 쌍의 도달 신호를 보여준다.</summary>
    public static List<DateTime> UnionSorted(IReadOnlyList<IReadOnlyList<DateTime>> streams)
    {
        var merged = new SortedSet<DateTime>();
        foreach (var s in streams)
            foreach (var t in s)
                merged.Add(t);
        return merged.ToList();
    }
}
