// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 시간 기반 코어 v68 의 HTTP 표면. doc/30 §8.
/// <para>
/// 설비효율(OEE) · 생산효율(TEEP) · 가동시간 분석 세 페이지가 <b>이 엔드포인트 하나</b>를 공유한다.
/// 구간을 정하면 그 안의 CT 개수와 상태를 한줄 연표로 그릴 재료를 돌려준다. 페이지마다 다른 것은
/// 그 아래 어떤 KPI 를 강조하느냐뿐이다.
/// </para>
/// </summary>
[ApiController]
[Route("api/kpi")]
public sealed class KpiController : ControllerBase
{
    private readonly KpiRepository _repo;
    private readonly AppSettingsService _settings;
    private readonly ILogger<KpiController> _logger;

    public KpiController(KpiRepository repo, AppSettingsService settings, ILogger<KpiController> logger)
    {
        _repo = repo;
        _settings = settings;
        _logger = logger;
    }

    // ── GET /api/kpi/timeline?from&to&flow&branch ─────────────────────────────

    /// <summary>
    /// 구간 안의 사이클 개수·상태·KPI. 유효 행(구간에 완전히 들어온 행)만 계산에 들어가고,
    /// 잘림·미상·진행 중·기준 없음은 연표에만 표시된다.
    /// </summary>
    [HttpGet("timeline")]
    public async Task<ActionResult<KpiTimelineDto>> Timeline(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? flow,
        [FromQuery] string? branch,
        CancellationToken ct)
    {
        var (fromMs, toMs) = ResolveRange(from, to);
        var kappa = _settings.LoadSettings().Kpi.Resolve();

        var rows = await _repo.QueryCyclesAsync(fromMs, toMs, flow, branch, ct);
        var facts = rows.Select(r => r.ToFact()).ToList();

        var totals = KpiRules.Compute(facts, kappa);
        var segments = KpiRules.BuildSegments(facts, kappa);
        var links = await _repo.QueryLinkEventsAsync(fromMs, toMs, ct);

        var excluded = new List<KpiExcludedDto>();
        int cut = 0, unknown = 0, inProgress = 0, noBaseline = 0;
        foreach (var f in facts)
        {
            if (KpiRules.Classify(f, kappa) != CycleState.Excluded) continue;
            var reason = KpiRules.ResolveExclude(f);
            switch (reason)
            {
                case ExcludeReason.Cut: cut++; break;
                case ExcludeReason.InProgress: inProgress++; break;
                case ExcludeReason.NoBaseline: noBaseline++; break;
                default: unknown++; break;
            }
            excluded.Add(new KpiExcludedDto(
                Math.Max(f.StartMs, fromMs), Math.Min(f.EndMs, toMs), reason.ToString()));
        }

        // 구간 시작부터 첫 행까지는 이전 head 를 모르는 구간(UNK). 행이 하나도 없으면 구간 전체가 UNK.
        long firstStart = rows.Count > 0 ? rows.Min(r => r.StartMs) : toMs;
        if (firstStart > fromMs)
        {
            excluded.Insert(0, new KpiExcludedDto(fromMs, firstStart, ExcludeReason.Unknown.ToString()));
            unknown++;
        }

        // 마지막 행 이후 ~ 지금은 열린 사이클(진행 중). 과거 구간에는 생기지 않는다.
        long lastEnd = rows.Count > 0 ? rows.Max(r => r.EndMs) : fromMs;
        long liveEnd = Math.Min(toMs, KpiTime.NowMs());
        if (rows.Count > 0 && liveEnd > lastEnd)
        {
            excluded.Add(new KpiExcludedDto(lastEnd, liveEnd, ExcludeReason.InProgress.ToString()));
            inProgress++;
        }

        return Ok(new KpiTimelineDto(
            KpiTime.ToIso(fromMs),
            KpiTime.ToIso(toMs),
            flow,
            branch,
            new KpiCountsDto(
                totals.RunCount, totals.DownCount, totals.NonProdCount,
                new KpiExcludedCountsDto(cut, unknown, inProgress, noBaseline)),
            KpiMetricsDto.From(totals),
            segments.Select(s => new KpiSegmentDto(
                KpiTime.ToIso(s.StartMs), KpiTime.ToIso(s.EndMs),
                s.StartMs, s.EndMs,
                s.State.ToString(),
                s.State == CycleState.Excluded ? s.Reason.ToString() : null,
                s.Cycles, s.CtMs, s.RMs, s.WorstWork, s.WorstRatio)).ToList(),
            excluded,
            links.Select(l => new KpiLinkDto(
                l.System, KpiTime.ToIso(l.AtMs), l.EndMs is long e ? KpiTime.ToIso(e) : null,
                l.IsConnected, l.Kind, l.Detail)).ToList(),
            new KpiKappaDto(kappa.NonProd, kappa.Down, kappa.Quality)));
    }

    // ── GET /api/kpi/cycle/{id}/works ─────────────────────────────────────────

    /// <summary>한 사이클의 work 지속시간 — 세그먼트 툴팁·간트 상세가 "왜 비가동인가"를 보일 때.</summary>
    [HttpGet("cycle/{id:long}/works")]
    public async Task<ActionResult<List<KpiWorkDto>>> CycleWorks(long id, CancellationToken ct)
    {
        var works = await _repo.GetCycleWorksAsync(id, ct);
        var kappa = _settings.LoadSettings().Kpi.Resolve();
        return Ok(works
            .Select(w => new KpiWorkDto(w.Work, w.DurationMs, w.WUsedMs, w.Ratio, w.Ratio > kappa.Down))
            .ToList());
    }

    // ── GET/PUT /api/kpi/settings ─────────────────────────────────────────────

    /// <summary>현재 계수·품질과 허용 범위.</summary>
    [HttpGet("settings")]
    public ActionResult<KpiSettingsDto> GetSettings()
    {
        var s = _settings.LoadSettings().Kpi;
        return Ok(new KpiSettingsDto(
            s.NonProdKappa, s.DownKappa, s.QualityPercent, s.RawRetentionDays,
            KpiKappa.NonProdMin, KpiKappa.NonProdMax, KpiKappa.DownMin, KpiKappa.DownMax,
            KpiRules.MinBaselineSamples, KpiRules.BaselineWindowDays));
    }

    /// <summary>
    /// 계수·품질 저장. 저장 즉시 전 구간이 재라벨된다 — 상태를 저장하지 않고 조회 시 도출하기 때문이다(doc/30 §4).
    /// </summary>
    [HttpPut("settings")]
    public ActionResult<KpiSettingsDto> PutSettings([FromBody] KpiSettingsRequest req)
    {
        _settings.Update(m =>
        {
            if (req.NonProdKappa is double np) m.Kpi.NonProdKappa = Math.Clamp(np, KpiKappa.NonProdMin, KpiKappa.NonProdMax);
            if (req.DownKappa is double dn) m.Kpi.DownKappa = Math.Clamp(dn, KpiKappa.DownMin, KpiKappa.DownMax);
            if (req.QualityPercent is double q) m.Kpi.QualityPercent = Math.Clamp(q, 0, 100);
            if (req.RawRetentionDays is int d) m.Kpi.RawRetentionDays = Math.Max(0, d);
        });
        _logger.LogInformation(
            "[Kpi] settings saved — nonProd={Np} down={Dn} quality={Q}",
            req.NonProdKappa, req.DownKappa, req.QualityPercent);
        return GetSettings();
    }

    // ── 공통 ──────────────────────────────────────────────────────────────────

    /// <summary>기본 구간은 오늘 0시 ~ 지금. 들어온 값은 로컬로 보고 epoch ms 로 바꾼다.</summary>
    private static (long FromMs, long ToMs) ResolveRange(DateTime? from, DateTime? to)
    {
        var now = DateTime.Now;
        var f = from ?? now.Date;
        var t = to ?? now;
        if (f.Kind == DateTimeKind.Unspecified) f = DateTime.SpecifyKind(f, DateTimeKind.Local);
        if (t.Kind == DateTimeKind.Unspecified) t = DateTime.SpecifyKind(t, DateTimeKind.Local);
        long fm = KpiTime.ToMs(f.ToUniversalTime());
        long tm = KpiTime.ToMs(t.ToUniversalTime());
        if (tm <= fm) tm = fm + 1;
        return (fm, tm);
    }
}

// ── DTO ───────────────────────────────────────────────────────────────────────

/// <summary>한줄 연표 한 벌. 세 페이지가 공유한다.</summary>
public sealed record KpiTimelineDto(
    string From,
    string To,
    string? Flow,
    string? Branch,
    KpiCountsDto Counts,
    KpiMetricsDto Kpi,
    List<KpiSegmentDto> Segments,
    List<KpiExcludedDto> Excluded,
    List<KpiLinkDto> Links,
    KpiKappaDto Kappa);

public sealed record KpiCountsDto(int Run, int Down, int NonProd, KpiExcludedCountsDto Excluded);

public sealed record KpiExcludedCountsDto(int Cut, int Unknown, int InProgress, int NoBaseline)
{
    public int Total => Cut + Unknown + InProgress + NoBaseline;
}

/// <summary>지표. T 는 캘린더가 아니라 유효 행 CT 의 합이다.</summary>
public sealed record KpiMetricsDto(
    long TMs, long TRunMs, long TDownMs, long TNonProdMs, double SumRMs,
    double? Availability, double? Performance, double Quality,
    double? Oee, double? Utilization, double? Teep,
    int ToCount, double? TtrMs, double? TbfMs, bool Verified)
{
    public static KpiMetricsDto From(KpiTotals t) => new(
        t.TMs, t.TRunMs, t.TDownMs, t.TNonProdMs, t.SumRMs,
        t.A, t.P, t.Q, t.Oee, t.U, t.Teep,
        t.ToCount, t.TtrMs, t.TbfMs, KpiRules.Verify(t));
}

/// <summary>연표 세그먼트. 인접한 같은 상태의 사이클이 하나로 묶여 있다.</summary>
public sealed record KpiSegmentDto(
    string Start, string End, long StartMs, long EndMs,
    string State, string? Reason,
    int Cycles, long CtMs, double RMs, string? WorstWork, double WorstRatio);

public sealed record KpiExcludedDto(long StartMs, long EndMs, string Reason);

/// <summary>접속 전이·심박 공백. 계산 인자가 아니라 대조용 오버레이다.</summary>
public sealed record KpiLinkDto(
    string System, string At, string? End, bool IsConnected, string Kind, string? Detail);

public sealed record KpiKappaDto(double NonProd, double Down, double Quality);

public sealed record KpiWorkDto(
    string Work, long DurationMs, double WUsedMs, double Ratio, bool Over);

public sealed record KpiSettingsDto(
    double NonProdKappa, double DownKappa, double QualityPercent, int RawRetentionDays,
    double NonProdMin, double NonProdMax, double DownMin, double DownMax,
    int MinBaselineSamples, int BaselineWindowDays);

public sealed record KpiSettingsRequest(
    double? NonProdKappa, double? DownKappa, double? QualityPercent, int? RawRetentionDays);
