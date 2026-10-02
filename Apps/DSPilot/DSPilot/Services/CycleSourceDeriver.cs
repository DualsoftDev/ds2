// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>도출 결과 상태. <see cref="CycleSourceDeriver.DeriveAsync"/>.</summary>
public enum DeriveStatus
{
    /// <summary>시작 경계 신호를 해석하지 못함(태그·Head 미해석, 분기 하나라도 미해석) — 호출측은 아무것도 덮어쓰지 않는다.</summary>
    Unresolved,
    /// <summary>신호는 해석됐으나 구간에 시작 엣지가 0건.</summary>
    NoStarts,
    /// <summary>사이클 1건 이상.</summary>
    Ok,
}

/// <summary>
/// 원시 신호에서 도출한 사이클 1개. 시각은 Local(엣지 Kind 보존).
/// <paramref name="PeriodMs"/> = 다음 시작까지(마지막 열린 사이클은 null). <paramref name="Branch"/> = 판별된 분기(분기 미사용·미분류 null).
/// </summary>
public readonly record struct DerivedCycle(
    DateTime Start,
    DateTime? Complete,
    double? ActiveMs,
    double? PeriodMs,
    string? Branch,
    string? HeadCallName,
    string? TailCallName);

/// <summary>
/// 도출 묶음. <paramref name="UnresolvedLabel"/> = Unresolved 일 때 원인 표시용(태그/Head/분기 이름).
/// <paramref name="Branched"/> = 분기 활성 flow 경로를 탔는지.
/// </summary>
public sealed record DerivedCycleSet(
    DeriveStatus Status,
    IReadOnlyList<DerivedCycle> Cycles,
    bool Branched,
    int Unclassified = 0,
    int MinViolation = 0,
    string? UnresolvedLabel = null)
{
    public static DerivedCycleSet Unresolved(bool branched, string label) => new(DeriveStatus.Unresolved, [], branched, UnresolvedLabel: label);
    public static DerivedCycleSet NoStarts(bool branched) => new(DeriveStatus.NoStarts, [], branched);
}

/// <summary>
/// 사이클 경계 도출의 단일 지점 — plcTagLog(signal) 의 <b>PLC 태그 시각</b>으로 시작·완료·분기를 정한다.
/// <para>
/// 주기 재도출(<see cref="CycleRecomputeService"/> → dspFlowHistory)과 판정 적재(<see cref="Kpi.CycleIngestService"/>
/// → KPI cycle)가 이 한 함수를 공유한다. 간트(<c>CallTestController</c>)도 같은 <see cref="CycleBoundaryEdges"/> ·
/// <see cref="CycleDerivation"/> 로 경계를 만들므로 세 경로의 경계가 같은 시계를 본다(2026-10-02).
/// 종전에는 판정이 라이브 기록(처리 시각 UtcNow)에서 경계를 받아 간트보다 처리 지연만큼 늦었고, 그 결과
/// 다음 사이클 head call 이 이전 사이클에 귀속돼 경계 초과·가짜 MT 비가동·간트 뱃지 한 칸 밀림이 났다.
/// </para>
/// 상태를 갖지 않는다. 로그도 남기지 않는다 — 호출 주기가 다르므로(재도출=수 분, 적재=30초) 호출측이 결정한다.
/// </summary>
public sealed class CycleSourceDeriver
{
    private readonly IPlcRepository _plc;
    private readonly PlcToCallMapperService _mapper;
    private readonly AppSettingsService _settings;
    private readonly DsProjectService _project;

    public CycleSourceDeriver(
        IPlcRepository plc, PlcToCallMapperService mapper, AppSettingsService settings, DsProjectService project)
    {
        _plc = plc;
        _mapper = mapper;
        _settings = settings;
        _project = project;
    }

    /// <summary>
    /// [fromLocal, toLocal) 의 사이클. 분기 활성 flow 는 분기 정의로(호출측 Head/Tail 무시), 아니면
    /// 경계 태그 지정(override) &gt; <paramref name="headCallName"/>/<paramref name="tailCallName"/> 순.
    /// 멀티 PLC: 엣지 조회는 이 flow 의 PLC 로 한정한다.
    /// </summary>
    public async Task<DerivedCycleSet> DeriveAsync(
        string flowName, string? headCallName, string? tailCallName, DateTime fromLocal, DateTime toLocal)
    {
        if (string.IsNullOrWhiteSpace(flowName) || toLocal <= fromLocal)
            return DerivedCycleSet.NoStarts(false);

        var branchSet = _settings.GetFlowBranchSet(flowName);
        if (branchSet is not null && branchSet.Branches.Count > 0)
            return await DeriveBranchesAsync(flowName, branchSet, fromLocal, toLocal);

        // 경계 신호 해석은 CycleBoundaryEdges 한 곳 (2026-09-17):
        //   · 사용자가 경계 태그를 고른 flow = 그 주소·에지 하나가 시작/끝(IN/OUT 무관).
        //   · 고르지 않았으면 종전 Call 기준 — 시작 = OR(전 쌍 OUT 활성 진입 union),
        //     완료 = AND(쌍별 마커 전부 도달) — 엔진(canCompleteCall forall)·라이브 기록과 정렬.
        if (!_mapper.IsInitialized) _mapper.Initialize();
        var ov = _settings.GetFlowCycleOverride(flowName);
        var startSignals = CycleBoundaryEdges.StartSignals(
            _mapper, flowName, headCallName, ov?.StartTagAddress, ov?.StartTagEdge);
        var (endSignals, _) = CycleBoundaryEdges.EndSignals(
            _mapper, flowName, tailCallName, ov?.EndTagAddress, ov?.EndTagEdge);
        if (startSignals.Count == 0)
            return DerivedCycleSet.Unresolved(false, $"태그 '{ov?.StartTagAddress ?? "-"}' / Head '{headCallName}'");

        // head==tail(단일 신호 Call)도 자기 OutTag↑→완료(InTag↑/OutTag↓)로 분해 — 화면(CallTestController)과 동일 규칙.
        var systemId = _project.TryGetSystemIdByFlowName(flowName);

        var starts = await CycleBoundaryEdges.StartEdgesAsync(_plc, startSignals, fromLocal, toLocal, systemId);
        if (starts.Count == 0) return DerivedCycleSet.NoStarts(false);

        var tailStreams = await CycleBoundaryEdges.EndStreamsAsync(_plc, endSignals, fromLocal, toLocal, systemId);
        var cycles = CycleDerivation.BuildCycles(starts, tailStreams, toLocal);

        var list = new List<DerivedCycle>(cycles.Count);
        foreach (var c in cycles)
            list.Add(new DerivedCycle(c.Start, c.Complete, c.ActiveMs, c.PeriodMs, null, headCallName, tailCallName));
        return new DerivedCycleSet(DeriveStatus.Ok, list, false);
    }

    // ── 분기(branch) — 분기 활성 flow 전용 경로 (2026-08-27) ─────────────────────
    /// <summary>
    /// 분기 정의(자기 Head/Tail + 제외 call)별 시작 엣지를 <b>시간순 병합 스트림</b>으로 합쳐 사이클을
    /// 만든다. ct = 다음 시작(분기 무관) — 분기 미사용과 동일한 부모 축이라 TEEP·평균·임계 소비자가
    /// 분기 도입 전후로 흔들리지 않는다(설계 규약: ct 부모 의미 불변).
    /// <para>분류: 제외 call OutTag↑ 발화 = 그 분기 아님(반증). 반증 창은 병합 스팬 전체가 아니라
    /// <b>[시작, 끝 call 동작 종료)</b> = 완료(tail 마커) 직후 끝 call OutTag↓ 까지(2026-09-07). MT 뒤 WT 구간에
    /// 들어온 다음 차종 준비 동작(분기 전환 시 다음 head 8~9s 전 UNIT up 등)은 다음 사이클 몫이라 반증이 아니다.
    /// 완료가 없으면(MT 미확정) 종전처럼 스팬 전체가 반증 창. 화면 판별기(CycleGantt.classifyBranches)와 같은 규칙.
    /// 같은 시작 시각에 후보 분기가 여럿이고(공유 Head) 복수가 통과하면 정의 순서 첫 매칭 승.
    /// <b>최소 위반(2026-09-08)</b>: 후보 전멸(모든 분기가 제외 call 에 걸림)이면 발화한 제외 call <b>종류 수</b>가 가장 적은 분기가
    /// 유일할 때 그 분기로 판별한다. 차종 전환 사이클은 옛 차종 유닛 뒷정리(down 좌우 2개)만 새 차종 분기에 걸리고
    /// 새 차종 작업(up·lock·unlock 6개)이 옛 차종 분기에 걸려, "가장 덜 틀린" 분기 = 실제 작업 차종이다(현장 #137 6/6 검증).
    /// 통과(위반 0) 분기가 하나라도 있으면 종전과 완전히 같고, 최소가 동률이면 여전히 미분류. CT 중복(위반 0 복수)에는 적용하지
    /// 않는다 — 증거 부재라 계산으로 가를 수 없고 사용자가 정의를 고쳐야 한다.
    /// 전멸 = 미분류(Branch=null) — 행은 보존하되 무결성 카드 계수 대상.</para>
    /// <para>MT(tail 완료)도 <b>병합 스팬</b> 안에서만 찾는다 — 분기 자체 주기(다음 동일분기 시작)로
    /// 찾으면 형제 사이클 너머의 tail 을 집어 MT 가 형제 구동시간을 삼킨다.</para>
    /// </summary>
    private async Task<DerivedCycleSet> DeriveBranchesAsync(
        string flowName, FlowBranchSet set, DateTime fromLocal, DateTime toLocal)
    {
        var systemId = _project.TryGetSystemIdByFlowName(flowName);

        // (태그, 활성값, 방향) → 엣지 목록 캐시 — Head/제외 call 이 분기 간에 겹칠 때 재조회 방지.
        var edgeCache = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        async Task<List<DateTime>> EdgesAsync(string tag, string? activeValue, bool falling)
        {
            var key = $"{(falling ? "F" : "R")}|{activeValue ?? "~"}|{tag}";
            if (edgeCache.TryGetValue(key, out var hit)) return hit;
            var edges = await _plc.FindActiveEdgesAsync(tag, activeValue, falling, fromLocal, toLocal, systemId);
            edgeCache[key] = edges; // FindActiveEdges 는 이미 오름차순
            return edges;
        }

        // 복수 I/O 쌍 대응 — 시작/제외 = OUT 활성 진입 union, 완료 = 쌍별 스트림 AND(단일 경로와 동일 규칙).
        async Task<List<DateTime>> UnionOutEdgesAsync(IReadOnlyList<CallTagPair> callPairs, bool falling)
        {
            var merged = new SortedSet<DateTime>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in callPairs)
            {
                if (string.IsNullOrWhiteSpace(p.OutTag)) continue;
                if (!seen.Add($"{p.OutActiveValue ?? "~"}|{p.OutTag}")) continue;
                foreach (var t in await EdgesAsync(p.OutTag!, p.OutActiveValue, falling)) merged.Add(t);
            }
            return merged.ToList();
        }

        // 경계 신호(주소+에지) 단위 — 태그 지정 분기와 Call 기준 분기가 같은 캐시를 탄다.
        async Task<List<DateTime>> SignalUnionAsync(IReadOnlyList<BoundarySignal> signals)
        {
            var merged = new SortedSet<DateTime>();
            foreach (var s in signals)
                foreach (var t in await EdgesAsync(s.Address, s.ActiveValue, s.Falling)) merged.Add(t);
            return merged.ToList();
        }

        async Task<List<List<DateTime>>> SignalStreamsAsync(IReadOnlyList<BoundarySignal> signals)
        {
            var streams = new List<List<DateTime>>(signals.Count);
            foreach (var s in signals)
                streams.Add(await EdgesAsync(s.Address, s.ActiveValue, s.Falling));
            return streams;
        }

        if (!_mapper.IsInitialized) _mapper.Initialize();

        var resolved = new List<BranchRuntime>(set.Branches.Count);
        foreach (var def in set.Branches)
        {
            var startSignals = CycleBoundaryEdges.StartSignals(
                _mapper, flowName, def.StartCallName, def.StartTagAddress, def.StartTagEdge);
            if (startSignals.Count == 0)
            {
                // 시작 경계 미해석 분기가 하나라도 있으면 병합 스트림 자체가 불완전 — 호출측이 아무것도 쓰지 않게 한다.
                return DerivedCycleSet.Unresolved(true,
                    $"분기 '{def.Name}' (태그 '{def.StartTagAddress ?? "-"}' / Head '{def.StartCallName}')");
            }

            var exclEdges = new List<List<DateTime>>();
            foreach (var callName in def.ExcludedCallNames)
            {
                // 자기 Head/Tail 이 제외 목록에 섞이면 모든 자기 사이클을 스스로 반증 → 방어적으로 무시.
                if (string.Equals(callName, def.StartCallName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(callName, def.EndCallName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var exclPairs = ResolvePairs(flowName, callName);
                // 관측 불가 call(OUT 없음)은 "미발화"로 간주하고 필터에서만 빠진다 — 도출 전체를 막지 않는다.
                if (!exclPairs.Any(p => !string.IsNullOrWhiteSpace(p.OutTag))) continue;
                exclEdges.Add(await UnionOutEdgesAsync(exclPairs, falling: false));
            }

            var starts = await SignalUnionAsync(startSignals);
            var (endSignals, _) = CycleBoundaryEdges.EndSignals(
                _mapper, flowName, def.EndCallName, def.EndTagAddress, def.EndTagEdge);
            var tailStreams = await SignalStreamsAsync(endSignals);
            // 반증 창 상한 = 끝 call 의 동작 종료(OutTag↓). 완료 마커(InTag↑)보다 0.4~0.5s 늦어, 완료와 거의 동시에
            // 움직이는 차종별 call(공유 head/tail 분기의 유일한 구분 근거)이 스캔 순서로 창 밖에 밀리는 일을 막는다.
            // 끝을 태그로 직접 고른 분기는 "그 call 의 동작 종료" 라는 개념이 없으므로(주소 하나가 곧 완료), 끝 call
            // 이름이 남아 있으면 그것으로, 없으면 빈 목록 → 반증 창이 병합 스팬 전체가 된다(2026-09-07 이전의 보수적 동작).
            var tailFalls = await UnionOutEdgesAsync(ResolvePairs(flowName, def.EndCallName), falling: true);
            resolved.Add(new BranchRuntime(def, starts, tailStreams, tailFalls, exclEdges));
        }

        // 시작 시각 병합 — 같은 시각에 여러 분기(공유 Head)면 정의 순서대로 후보 적재.
        var byStart = new SortedDictionary<DateTime, List<BranchRuntime>>();
        foreach (var rt in resolved)
            foreach (var s in rt.Starts)
            {
                if (!byStart.TryGetValue(s, out var list)) byStart[s] = list = new List<BranchRuntime>(1);
                if (!list.Contains(rt)) list.Add(rt);
            }

        if (byStart.Count == 0) return DerivedCycleSet.NoStarts(true);

        var mergedStarts = new List<DateTime>(byStart.Keys);
        var cycles = new List<DerivedCycle>(mergedStarts.Count);
        int unclassified = 0, minViolation = 0;

        for (int i = 0; i < mergedStarts.Count; i++)
        {
            var s = mergedStarts[i];
            bool hasNext = i + 1 < mergedStarts.Count;
            var end = hasNext ? mergedStarts[i + 1] : toLocal;
            double? periodMs = hasNext ? (mergedStarts[i + 1] - s).TotalMilliseconds : (double?)null;

            var candidates = byStart[s];
            BranchRuntime? winner = null;
            var completeBy = new Dictionary<BranchRuntime, DateTime?>(candidates.Count);
            // 후보별 위반 수 = 반증 창 안에서 발화한 제외 call 종류 수(같은 call 의 반복 발화는 1 — 채터링이 판정을 흔들지 않게).
            var violations = new int[candidates.Count];
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                var cand = candidates[ci];
                // 후보별 완료 → 반증 창 [s, 끝 call OutTag↓) (완료 없으면 스팬 전체, OUT 이 다음 시작까지 유지되면 스팬 끝).
                var candComplete = AndCompleteInRange(cand.TailStreams, s, end);
                completeBy[cand] = candComplete;
                var refuteEnd = end;
                if (candComplete.HasValue)
                {
                    var fall = FirstEdgeAtOrAfter(cand.TailOutFalls, candComplete.Value, end);
                    if (fall.HasValue) refuteEnd = fall.Value;
                }
                var viol = 0;
                foreach (var edges in cand.ExclusionEdges)
                    if (HasEdgeInRange(edges, s, refuteEnd)) viol++;
                violations[ci] = viol;
                if (viol == 0 && winner is null) winner = cand;   // 위반 0 = 정상 통과, 정의 순서 첫 통과 승(종전 규칙)
            }
            if (winner is null && hasNext)
            {
                // 최소 위반 — 전멸(모두 위반 ≥1)이고 완결 스팬일 때만. 최소가 유일하면 그 분기, 동률이면 미분류 유지.
                // 진행 중(마지막) 스팬은 다음 시작 전이라 증거가 덜 쌓였으므로 적용하지 않는다(화면 판별기와 동일).
                var min = int.MaxValue; var minIdx = -1; var minTies = 0;
                for (int ci = 0; ci < candidates.Count; ci++)
                {
                    if (violations[ci] < min) { min = violations[ci]; minIdx = ci; minTies = 1; }
                    else if (violations[ci] == min) minTies++;
                }
                if (minIdx >= 0 && minTies == 1) { winner = candidates[minIdx]; minViolation++; }
            }
            var basis = winner ?? candidates[0]; // 미분류 행도 측정 경계(Head/Tail)는 첫 후보 것으로 박제
            if (winner is null) unclassified++;

            var complete = completeBy[basis];
            double? activeMs = complete.HasValue ? (complete.Value - s).TotalMilliseconds : (double?)null;

            cycles.Add(new DerivedCycle(s, complete, activeMs, periodMs,
                winner?.Def.Name, basis.Def.StartCallName, basis.Def.EndCallName));
        }

        return new DerivedCycleSet(DeriveStatus.Ok, cycles, true, unclassified, minViolation);
    }

    /// <summary>분기 1개의 도출 재료 — 정의 + 시작/완료(쌍별 스트림)/제외 엣지 목록(전부 오름차순).</summary>
    private sealed record BranchRuntime(
        FlowBranchDef Def,
        List<DateTime> Starts,
        List<List<DateTime>> TailStreams,
        List<DateTime> TailOutFalls,
        List<List<DateTime>> ExclusionEdges);

    /// <summary>[from, to) 안의 첫 엣지 — from 포함(완료 시각과 동시각 OutTag↓ 도 동작 종료로 인정).</summary>
    private static DateTime? FirstEdgeAtOrAfter(List<DateTime> edges, DateTime fromInclusive, DateTime toExclusive)
    {
        var i = edges.BinarySearch(fromInclusive);
        if (i < 0) i = ~i;
        else while (i > 0 && edges[i - 1] == fromInclusive) i--;
        return i < edges.Count && edges[i] < toExclusive ? edges[i] : (DateTime?)null;
    }

    /// <summary>[from, to) 안에 엣지 존재 여부 — from 포함(사이클 시작 시각 동시 발화도 그 사이클 소속).</summary>
    private static bool HasEdgeInRange(List<DateTime> edges, DateTime fromInclusive, DateTime toExclusive)
    {
        var i = edges.BinarySearch(fromInclusive);
        if (i < 0) i = ~i;
        else while (i > 0 && edges[i - 1] == fromInclusive) i--; // 중복 시 첫 항목까지 후퇴
        return i < edges.Count && edges[i] < toExclusive;
    }

    /// <summary>(from, to) 안의 첫 엣지 — from 초과(BuildCycles 의 tail 매칭 '&lt;= cStart 스킵' 과 동일 규약).</summary>
    private static DateTime? FirstEdgeInRange(List<DateTime> edges, DateTime fromExclusive, DateTime toExclusive)
    {
        var i = edges.BinarySearch(fromExclusive);
        if (i < 0) i = ~i;
        else { do { i++; } while (i < edges.Count && edges[i] == fromExclusive); } // 동시각 전부 스킵(초과 조건)
        return i < edges.Count && edges[i] < toExclusive ? edges[i] : (DateTime?)null;
    }

    /// <summary>
    /// 복수 I/O 쌍 완료 = AND — 스팬 (from, to) 안에서 스트림별 첫 엣지가 <b>전부</b> 존재할 때
    /// 그 최댓값(마지막 응답)을 완료 시각으로. 하나라도 없으면 미완료(null). 스트림 0개 = 관측 불가 = null.
    /// CycleDerivation.BuildCycles(AND 오버로드)와 같은 정의 — 분기 경로는 병합 스팬이라 포인터 대신 이 범위검색을 쓴다.
    /// </summary>
    private static DateTime? AndCompleteInRange(List<List<DateTime>> streams, DateTime fromExclusive, DateTime toExclusive)
    {
        if (streams.Count == 0) return null;
        var worst = DateTime.MinValue;
        foreach (var edges in streams)
        {
            var e = FirstEdgeInRange(edges, fromExclusive, toExclusive);
            if (!e.HasValue) return null;
            if (e.Value > worst) worst = e.Value;
        }
        return worst;
    }

    /// <summary>flow + Call 이름 → 전체 ApiCall(I/O 쌍) 목록.</summary>
    private IReadOnlyList<CallTagPair> ResolvePairs(string flowName, string? callName)
    {
        if (!_mapper.IsInitialized) _mapper.Initialize();
        return callName is null
            ? Array.Empty<CallTagPair>()
            : _mapper.GetCallTagPairsByName(flowName, callName);
    }
}
