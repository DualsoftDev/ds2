// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// 사이클 경계 신호 1개 = <b>주소 + 방향</b>. IN/OUT 구분은 담지 않는다 — 경계는 "어느 주소가 어느 쪽으로 변했나"
/// 가 전부이고, I/O 역할은 후보를 고를 때(화면)와 Call 기준 폴백을 만들 때만 쓰인다.
/// <paramref name="ActiveValue"/> = 엔진 ValueSpec 활성 판정값(null=bool 관용) — <see cref="IPlcRepository.FindActiveEdgesAsync"/> 규약.
/// </summary>
public sealed record BoundarySignal(string Address, bool Falling, string? ActiveValue);

/// <summary>
/// 사용자가 고른 경계 태그 지정 — 저장(<see cref="Models.FlowCycleOverride"/>/<see cref="Models.FlowBranchDef"/>)과
/// 미리보기(요청 DTO) 사이를 오가는 값 묶음. 주소가 비면 그 쪽 경계는 Call 기준 폴백이다.
/// </summary>
public sealed record CycleBoundaryTagSpec(string? StartAddress, string? StartEdge, string? EndAddress, string? EndEdge)
{
    public static readonly CycleBoundaryTagSpec None = new(null, null, null, null);
    public bool IsEmpty => !CycleBoundaryEdges.HasTagSpec(StartAddress) && !CycleBoundaryEdges.HasTagSpec(EndAddress);

    /// <summary>주소는 trim(빈값→null), 에지는 주소가 있을 때만 "rising"/"falling" 으로 정규화.</summary>
    public CycleBoundaryTagSpec Normalized()
    {
        string? s = string.IsNullOrWhiteSpace(StartAddress) ? null : StartAddress!.Trim();
        string? e = string.IsNullOrWhiteSpace(EndAddress) ? null : EndAddress!.Trim();
        return new CycleBoundaryTagSpec(
            s, s is null ? null : CycleBoundaryEdges.NormalizeEdge(StartEdge),
            e, e is null ? null : CycleBoundaryEdges.NormalizeEdge(EndEdge));
    }
}

/// <summary>
/// 사이클 경계의 <b>신호 해석</b> 단일 지점. 경계는 두 가지 방식으로 정의된다(2026-09-17):
/// <list type="number">
///   <item><b>태그 지정</b> — 사용자가 이 flow 간트에 존재하는 주소 하나와 에지(상승/하강)를 직접 고른다.
///     시작·끝 각각 신호 1개. IN/OUT 무관.</item>
///   <item><b>Call 기준(폴백·기존 저장분)</b> — 태그 지정이 없으면 종전 규칙 그대로:
///     시작 = <b>OR(최초 OUT 활성 진입)</b>(엔진이 어느 OUT 이든 상승 시 Going 을 거는 것과 동일),
///     완료 = <b>AND(전 쌍 도달)</b>(엔진 canCompleteCall 의 forall), 쌍별 마커 = InTag 활성 진입(있으면)
///     else OutTag 활성 이탈(OutOnly 추정).</item>
/// </list>
/// 어느 쪽이든 결과는 <see cref="BoundarySignal"/> 목록으로 통일되고, 시작은 union(OR), 끝은 스트림별 AND 합성을
/// <see cref="CycleDerivation.BuildCycles(IReadOnlyList{DateTime}, IReadOnlyList{IReadOnlyList{DateTime}}, DateTime)"/>
/// 이 담당한다. 화면(CallTestController)·재도출(CycleRecomputeService)·기본 경계(CycleAnalysisService)가 이 한 곳을 공유한다.
/// <para>엣지는 원시 전이 그대로다. 채터 필터(SignalDebounce, 2026-09-07~09-18)는 폐기했다 — 현장에 떨림이 없고
/// 필터가 0.23~0.7초 정상 명령 펄스를 지웠다(doc/30 §2.1 · §15-①). 가짜 라이징 방어는 3차의 OUT↑→IN↑ 짝짓기가 맡는다.</para>
/// </summary>
public static class CycleBoundaryEdges
{
    public const string EdgeRising = "rising";
    public const string EdgeFalling = "falling";

    /// <summary>에지 문자열 → 하강 여부. 미지정/오타는 전부 상승(기본값) — 경계가 조용히 반대로 뒤집히지 않게.</summary>
    public static bool IsFallingEdge(string? edge)
        => string.Equals(edge?.Trim(), EdgeFalling, StringComparison.OrdinalIgnoreCase);

    /// <summary>저장·전송용 정규화 — "rising" | "falling" 둘 중 하나. 기본 "rising".</summary>
    public static string NormalizeEdge(string? edge) => IsFallingEdge(edge) ? EdgeFalling : EdgeRising;

    /// <summary>태그 지정이 유효한가(주소가 비어있지 않은가). false = Call 기준 폴백.</summary>
    public static bool HasTagSpec(string? tagAddress) => !string.IsNullOrWhiteSpace(tagAddress);

    // ── 신호 해석 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 시작 경계 신호. 태그 지정이 있으면 그 주소·에지 단 하나, 없으면 Call 의 전 쌍 OUT 상승(OR).
    /// Call 이름조차 없거나 OUT 이 하나도 없으면 빈 목록 = 해석 불가(호출측이 파괴적 재도출을 건너뛰는 근거).
    /// </summary>
    public static IReadOnlyList<BoundarySignal> StartSignals(
        PlcToCallMapperService mapper, string flowName, string? callName, string? tagAddress, string? tagEdge)
    {
        if (HasTagSpec(tagAddress))
        {
            var addr = tagAddress!.Trim();
            return [new BoundarySignal(addr, IsFallingEdge(tagEdge), mapper.GetActiveValueForAddress(flowName, addr))];
        }
        return StartSignalsFromPairs(ResolvePairs(mapper, flowName, callName));
    }

    /// <summary>주소·에지 → 신호 1개. 활성값(ValueSpec)은 그 주소가 속한 ApiCall 에서 가져온다(미등록이면 bool 관용).</summary>
    public static BoundarySignal TagSignal(
        PlcToCallMapperService mapper, string flowName, string address, string? edge)
    {
        var addr = address.Trim();
        return new BoundarySignal(addr, IsFallingEdge(edge), mapper.GetActiveValueForAddress(flowName, addr));
    }

    /// <summary>Call 기준 시작 = 전 쌍 OUT 활성 진입(OR). 공유 OUT(같은 주소 여러 쌍)은 (주소,활성값) 단위 dedup.</summary>
    public static IReadOnlyList<BoundarySignal> StartSignalsFromPairs(IReadOnlyList<CallTagPair> pairs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var signals = new List<BoundarySignal>();
        foreach (var p in pairs)
        {
            if (string.IsNullOrWhiteSpace(p.OutTag)) continue;
            if (!seen.Add($"{p.OutActiveValue ?? "~"}|{p.OutTag}")) continue;
            signals.Add(new BoundarySignal(p.OutTag!, false, p.OutActiveValue));
        }
        return signals;
    }

    /// <summary>
    /// 완료 경계 신호 + UI 라벨. 태그 지정이면 신호 1개 · 라벨 "Tag"(완료 정의를 사용자가 직접 고른 상태).
    /// Call 기준이면 쌍별로 IN 활성 진입 / OutOnly 쌍은 OUT 활성 이탈, 라벨은 정통(InTag) 쌍이 하나라도 있으면
    /// "InTag", 전부 OutOnly 추정이면 "OutTag", 관측 불가면 null.
    /// </summary>
    public static (IReadOnlyList<BoundarySignal> Signals, string? SourceLabel) EndSignals(
        PlcToCallMapperService mapper, string flowName, string? callName, string? tagAddress, string? tagEdge)
    {
        if (HasTagSpec(tagAddress))
        {
            var addr = tagAddress!.Trim();
            return ([new BoundarySignal(addr, IsFallingEdge(tagEdge), mapper.GetActiveValueForAddress(flowName, addr))], "Tag");
        }
        return EndSignalsFromPairs(ResolvePairs(mapper, flowName, callName));
    }

    /// <summary>Call 기준 완료 = 쌍별 마커(IN↑, 없으면 OUT↓). 둘 다 없는 쌍은 관측 불가라 제외(AND 영구 미완료 방지).</summary>
    public static (IReadOnlyList<BoundarySignal> Signals, string? SourceLabel) EndSignalsFromPairs(
        IReadOnlyList<CallTagPair> pairs)
    {
        var signals = new List<BoundarySignal>(pairs.Count);
        bool anyIn = false, anyOut = false;
        foreach (var p in pairs)
        {
            if (!string.IsNullOrWhiteSpace(p.InTag))
            {
                signals.Add(new BoundarySignal(p.InTag!, false, p.InActiveValue));
                anyIn = true;
            }
            else if (!string.IsNullOrWhiteSpace(p.OutTag))
            {
                signals.Add(new BoundarySignal(p.OutTag!, true, p.OutActiveValue));
                anyOut = true;
            }
        }
        return (signals, anyIn ? "InTag" : anyOut ? "OutTag" : null);
    }

    private static IReadOnlyList<CallTagPair> ResolvePairs(
        PlcToCallMapperService mapper, string flowName, string? callName)
        => string.IsNullOrWhiteSpace(callName)
            ? Array.Empty<CallTagPair>()
            : mapper.GetCallTagPairsByName(flowName, callName);

    // ── 엣지 조회 ──────────────────────────────────────────────────────────────

    /// <summary>시작 경계 = 전 신호 엣지의 union(오름차순, 동시각 dedup). 신호 0개면 빈 목록.</summary>
    public static async Task<List<DateTime>> StartEdgesAsync(
        IPlcRepository plc, IReadOnlyList<BoundarySignal> signals,
        DateTime from, DateTime to, Guid? systemId)
    {
        var merged = new SortedSet<DateTime>();
        foreach (var s in signals)
            foreach (var t in await plc.FindActiveEdgesAsync(s.Address, s.ActiveValue, s.Falling, from, to, systemId))
                merged.Add(t);
        return merged.ToList();
    }

    /// <summary>완료 마커 스트림(신호별, 각 오름차순) — AND 합성은 <see cref="CycleDerivation"/>.</summary>
    public static async Task<List<List<DateTime>>> EndStreamsAsync(
        IPlcRepository plc, IReadOnlyList<BoundarySignal> signals,
        DateTime from, DateTime to, Guid? systemId)
    {
        var streams = new List<List<DateTime>>(signals.Count);
        foreach (var s in signals)
            streams.Add(await plc.FindActiveEdgesAsync(s.Address, s.ActiveValue, s.Falling, from, to, systemId));
        return streams;
    }

    /// <summary>Call 기준 시작 경계(구 API 유지) — <see cref="StartSignalsFromPairs"/> + <see cref="StartEdgesAsync"/>.</summary>
    public static Task<List<DateTime>> HeadStartsAsync(
        IPlcRepository plc, IReadOnlyList<CallTagPair> pairs,
        DateTime from, DateTime to, Guid? systemId)
        => StartEdgesAsync(plc, StartSignalsFromPairs(pairs), from, to, systemId);

    /// <summary>Call 기준 완료 마커(구 API 유지) — <see cref="EndSignalsFromPairs"/> + <see cref="EndStreamsAsync"/>.</summary>
    public static async Task<(List<List<DateTime>> Streams, string? SourceLabel)> TailStreamsAsync(
        IPlcRepository plc, IReadOnlyList<CallTagPair> pairs,
        DateTime from, DateTime to, Guid? systemId)
    {
        var (signals, label) = EndSignalsFromPairs(pairs);
        return (await EndStreamsAsync(plc, signals, from, to, systemId), label);
    }

    /// <summary>완료 마커 엣지 전체 union(표시용) — 간트 tail 마커 틱은 모든 신호의 도달을 보여준다.</summary>
    public static List<DateTime> UnionSorted(IReadOnlyList<IReadOnlyList<DateTime>> streams)
    {
        var merged = new SortedSet<DateTime>();
        foreach (var s in streams)
            foreach (var t in s)
                merged.Add(t);
        return merged.ToList();
    }
}
