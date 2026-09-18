// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Infrastructure;
using DSPilot.Models.Oee;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// OEE 정지(다운타임) — 조회 전용(doc/30 §11.1).
/// <para>수동 분류·전환·마감·되돌리기는 2026-09-18 전면 폐기했다. 상태는 규칙에서만 나온다 —
/// 사람이 행에 원인을 찍는 경로는 v68 의 "원인 어휘 금지" 와 κ 변경 시 자동 재라벨(§12-⑨)과 양립하지 않는다.</para>
/// </summary>
[ApiController]
[Route("api/oee")]
public class OeeDowntimeController : OeeControllerBase
{
    public OeeDowntimeController(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        OeeCtStatsService ctStats,
        OeeAutoShiftInferenceService shiftInfer,
        OeeCommHealthService commHealth,
        OeeNonProdPatternService nonProdPattern,
        HistoryMirrorService mirror,
        ILogger<OeeDowntimeController> logger)
        : base(repo, settings, project, pathResolver, ctStats, shiftInfer, commHealth, nonProdPattern, mirror, logger) { }

    // ── GET /api/oee/downtime?from&to&status&reason&flow[&system][&minDurationMs][&todFrom&todTo] ──
    // system = 시스템 스코프(그 시스템 flow 의 정지만, flow 미상 라인 귀속 행은 보존). flow 지정이 우선.
    // 열람 필터: minDurationMs = 최소 길이(사건 전체 DurationMs), todFrom/todTo = 시작 시각의 로컬 분(0~1439,
    // todFrom > todTo 면 자정 넘김 예 18:00~08:00). 필터는 보는 도구일 뿐 — 판정을 바꾸지 않는다.
    [HttpGet("downtime")]
    public async Task<ActionResult<List<OeeDowntimeDto>>> Downtime(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? status, [FromQuery] string? reason, [FromQuery] string? flow,
        [FromQuery] string? system, [FromQuery] long? minDurationMs, [FromQuery] int? todFrom, [FromQuery] int? todTo,
        CancellationToken ct)
    {
        var (fromUtc, toUtc) = ResolveRange(from, to);
        var flowName = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();
        var flowSet = flowName is null ? ResolveSystemFlowSet(system) : null;
        var rows = await _repo.QueryDowntimeAsync(fromUtc, toUtc, status, reason, flowName, ct);
        // 구 무가동 상태머신(detectSource='nocycle') 자동 행은 정본이 아니다(2026-09-08, doc/26) — 정지 시간은 완료 사이클
        //   행(판정 기준 초과 사이클)에 이미 들어 있고, 부팅 시 정리(OeeRepositoryAdapter)되지만 미러/경합 잔존에 대비해
        //   읽기에서도 걸러낸다. 종전의 '수동 확정 행은 보존' 예외는 수동 라벨 폐기(2026-09-18)로 사라졌다.
        static bool IsLegacyAutoNocycle(OeeDowntimeDto d)
            => string.Equals(d.DetectSource, "nocycle", StringComparison.OrdinalIgnoreCase);
        var merged = (flowSet is null
            ? rows
            : rows.Where(d => d.FlowName is null || flowSet.Contains(d.FlowName)))
            .Where(d => !IsLegacyAutoNocycle(d))
            .ToList();

        // 판정 기준 초과 사이클(로그 테이블에 없는 failureCount 사이클 성분)을 합성해 병합 — 내역이 도넛/바 건수와 정합.
        //   진행 중(열린 사이클, doc/26) 행도 같은 집계에서 합성돼 온다(status=open) — status 필터는 합성 후 적용.
        //   reason 필터가 걸리면 합성 행(고정 reason)은 제외.
        // 합성엔 KPI 와 동일한 집계의 flow 귀속 비생산 구간이 딸려 온다 — DB 이벤트 행의 '구분' 판정에
        //   재사용해 팝업 표시와 KPI 카빙이 같은 판단을 공유한다.
        var nonProdScoped = new List<(string? Flow, double S, double E)>();
        if (string.IsNullOrWhiteSpace(reason))
        {
            var (overCycles, npScoped) = await GetOverThresholdCycleDowntimeAsync(flowName, fromUtc, toUtc, ct, flowSet);
            if (string.Equals(status, "open", StringComparison.OrdinalIgnoreCase))
                overCycles = overCycles.Where(d => string.Equals(d.Status, "open", StringComparison.OrdinalIgnoreCase)).ToList();
            else if (string.Equals(status, "recovered", StringComparison.OrdinalIgnoreCase))
                overCycles = overCycles.Where(d => !string.Equals(d.Status, "open", StringComparison.OrdinalIgnoreCase)).ToList();
            nonProdScoped = npScoped;
            // 과거 수동 라벨로 materialize 됐던 over-cycle 이벤트 행과 겹치는 합성 행 dedup — 같은 사이클이 두 줄로 보이지 않게.
            static bool NearSameStart(DateTime a, DateTime b) => Math.Abs((a - b).TotalSeconds) < 2.0;
            // 같은 정지 이중 표시 흡수(2026-07-16, 사용자 확인) — 하나의 정지가 무가동 이벤트(DB)와 ct 폭주 사이클
            // (합성) 두 소스에 다 잡히면 목록엔 DB 행 하나만 남긴다(감지 이력이 있는 쪽). KPI 는 집계에서 이미
            // 행 단위 라벨 조인으로 처리되므로 표시 전용 정리. 흡수된 DB 행은 감지 칩에 '+이상치초과' 병기(정보 유실 방지).
            static double OverlapRatioOfSynthetic(OeeDowntimeDto db, OeeDowntimeDto sc, DateTime nowL)
            {
                var s = Math.Max(db.StartAt.Ticks, sc.StartAt.Ticks);
                var e = Math.Min((db.EndAt ?? nowL).Ticks, (sc.EndAt ?? nowL).Ticks);
                var dur = Math.Max(1.0, ((sc.EndAt ?? nowL) - sc.StartAt).Ticks);
                return Math.Max(0, e - s) / dur;
            }
            var nowL = DateTime.Now;
            var absorbedIdx = new HashSet<int>();   // 합성 행을 흡수한 DB 행 index — 감지 칩 병기 마킹
            var keptSynthetic = new List<OeeDowntimeDto>();
            foreach (var sc in overCycles)
            {
                // ① 이미 materialize 된 over-cycle DB 행과 같은 사이클 → 제외(종전 dedup). 합성 행의 축·확인 필요 정보는 DB 행에 승계.
                var twin = merged.FindIndex(d => string.Equals(d.DetectSource, "over-cycle", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(d.FlowName, sc.FlowName, StringComparison.Ordinal)
                        && NearSameStart(d.StartAt, sc.StartAt));
                if (twin >= 0)
                {
                    if (merged[twin].Axis is null) merged[twin] = merged[twin] with { Axis = sc.Axis };
                    continue;
                }
                // ② 같은 flow 의 무가동 DB 행과 크게 겹침(≥60%) → 흡수(같은 정지의 이중 표시).
                var host = -1;
                for (var i = 0; i < merged.Count; i++)
                {
                    var d = merged[i];
                    if (d.Id <= 0 || !string.Equals(d.FlowName, sc.FlowName, StringComparison.Ordinal)) continue;
                    if (OverlapRatioOfSynthetic(d, sc, nowL) >= 0.6) { host = i; break; }
                }
                if (host >= 0) { absorbedIdx.Add(host); continue; }
                keptSynthetic.Add(sc);
            }
            foreach (var i in absorbedIdx)
                merged[i] = merged[i] with
                {
                    DetectSource = merged[i].DetectSource + "+over-cycle",
                    Note = string.IsNullOrEmpty(merged[i].Note)
                        ? "무가동 이벤트 + 판정 기준 초과 사이클 동시 감지(같은 정지 — 한 줄로 병합)"
                        : merged[i].Note + " · 판정 기준 초과 사이클 동시 감지(병합)",
                };
            merged.AddRange(keptSynthetic);
            merged = merged.OrderByDescending(d => d.StartAt).ThenByDescending(d => d.Id).ToList();
        }

        // DB 이벤트 행 구분 판정: KPI 비생산 구간과의 <b>과반</b> 겹침(OeeMath.IsMajorityCovered)으로만 정한다 —
        // 반드시 **그 행의 flow 구간만** 본다. (구 '대기' 구분·수동 라벨 우선 분기는 폐기.)
        var nowLocal = DateTime.Now;
        for (var i = 0; i < merged.Count; i++)
        {
            var d = merged[i];
            if (d.Id <= 0) continue;   // 합성 행은 이미 IsNonProd 세팅됨
            bool isNp;
            {
                var sMs = new DateTimeOffset(DateTime.SpecifyKind(d.StartAt, DateTimeKind.Local)).ToUnixTimeMilliseconds();
                var eMs = new DateTimeOffset(DateTime.SpecifyKind(d.EndAt ?? nowLocal, DateTimeKind.Local)).ToUnixTimeMilliseconds();
                var dur = Math.Max(1.0, eMs - sMs);
                double overlap = 0;
                foreach (var (fl, s, e) in nonProdScoped)
                {
                    if (fl is not null && d.FlowName is not null
                        && !string.Equals(fl, d.FlowName, StringComparison.OrdinalIgnoreCase)) continue;
                    var o = Math.Min(e, eMs) - Math.Max(s, sMs);
                    if (o > 0) overlap += o;
                }
                isNp = OeeMath.IsMajorityCovered(dur, overlap);
            }
            if (isNp != d.IsNonProd) merged[i] = d with { IsNonProd = isNp };
        }
        var classified = await AttachCluesAsync(merged, fromUtc, toUtc, ct);
        LogClassifyTransitions(classified);   // 판정 전이 로그 — 프로세스 수명 내 구분 변화 계측

        // 열람 필터 — 서버에서 걸러 목록과 건수가 같은 집합을 본다.
        IEnumerable<OeeDowntimeDto> filtered = classified;
        var nowMsL = ToMs(DateTime.UtcNow);
        if (minDurationMs is long md && md > 0)
            filtered = filtered.Where(d => (d.DurationMs ?? (long)Math.Max(0, nowMsL - ToMs(DateTime.SpecifyKind(d.StartAt, DateTimeKind.Local)))) >= md);
        if (todFrom is int tf && todTo is int tt && tf != tt)
            filtered = filtered.Where(d => InTimeOfDay(d.StartAt, tf, tt));

        // 기간 내 클립 지속시간(2026-08-27) — 목록 필터가 '구간 겹침'이 되며 기간 경계를 걸친 정지가 들어온다.
        //   표시는 InRangeMs(기간과 겹친 몫)로 하고 사건 전체 길이(DurationMs)는 병기 — 합계가 KPI(정지시간)와 맞도록.
        //   open(endAt=null) 은 now 로 캡(미래 시간을 정지로 계상하지 않는다).
        var fromMs = ToMs(fromUtc);
        var toMs = Math.Min(ToMs(toUtc), nowMsL);
        return filtered.Select(d =>
        {
            var s0 = ToMs(DateTime.SpecifyKind(d.StartAt, DateTimeKind.Local));
            var e0 = d.EndAt.HasValue ? ToMs(DateTime.SpecifyKind(d.EndAt.Value, DateTimeKind.Local)) : nowMsL;
            var inRange = (long)Math.Max(0, Math.Min(e0, toMs) - Math.Max(s0, fromMs));
            return d with { InRangeMs = inRange };
        }).ToList();
    }

    /// <summary>시작 시각(로컬)이 [fromMin, toMin) 시간대에 드는가 — fromMin &gt; toMin 이면 자정 넘김(예 18:00~08:00).</summary>
    private static bool InTimeOfDay(DateTime localStart, int fromMin, int toMin)
    {
        var m = localStart.Hour * 60 + localStart.Minute;
        return fromMin <= toMin ? (m >= fromMin && m < toMin) : (m >= fromMin || m < toMin);
    }

    // ── 판정 전이 로그 — 같은 정지 행의 구분(고장/비생산)이 이전 조회와 달라지면 1줄 기록.
    //    스키마 없이 프로세스 수명 캐시로 계측 — "언제 왜 뒤집혔나" 를 다음 테스트에서 즉시 확정하기 위한 진단용.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_lastClassify = new();

    private void LogClassifyTransitions(List<OeeDowntimeDto> rows)
    {
        foreach (var d in rows)
        {
            var key = d.Id > 0 ? $"id:{d.Id}" : $"{d.FlowName}|{d.StartAt.Ticks}";
            var cur = d.IsNonProd;
            if (s_lastClassify.TryGetValue(key, out var prev) && prev != cur)
            {
                static string Label(bool np) => np ? "비생산" : "고장";
                _logger.LogInformation(
                    "[OEE-CLASSIFY] 정지 구분 전이 flow={Flow} ev={Key} {Prev}→{Cur} (시작 {Start:MM-dd HH:mm:ss}, 지속 {Dur}s, 단서={Clue})",
                    d.FlowName, key, Label(prev), Label(cur), d.StartAt,
                    (d.DurationMs ?? 0) / 1000, d.Clue?.Label ?? "-");
            }
            s_lastClassify[key] = cur;
            if (s_lastClassify.Count > 4096) s_lastClassify.Clear();   // 진단 캐시 폭주 방지(정확성 무관)
        }
    }
}
