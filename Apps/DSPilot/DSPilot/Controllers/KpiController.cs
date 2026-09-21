// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 시간 기반 코어 v68 의 HTTP 표면. doc/30 §9.
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
    private readonly DsProjectService _project;
    private readonly ILogger<KpiController> _logger;

    public KpiController(KpiRepository repo, AppSettingsService settings, DsProjectService project, ILogger<KpiController> logger)
    {
        _repo = repo;
        _settings = settings;
        _project = project;
        _logger = logger;
    }

    // ── GET /api/kpi/timeline?from&to&flow&branch ─────────────────────────────

    /// <summary>
    /// 구간 안의 사이클 개수·상태·KPI. 유효 행(구간에 완전히 들어온 행)만 계산에 들어가고,
    /// 잘림·미상·진행 중·미분류·기준 없음·경계 초과는 연표에만 표시된다.
    /// </summary>
    [HttpGet("timeline")]
    public async Task<ActionResult<KpiTimelineDto>> Timeline(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? flow,
        [FromQuery] string? branch,
        [FromQuery] string? system,
        CancellationToken ct)
    {
        var (fromMs, toMs) = ResolveRange(from, to);
        var kappa = _settings.LoadSettings().Kpi.Resolve();

        // 스코프 — 설비(flow) 가 우선, 없으면 시스템(그 시스템의 설비 집합). 둘 다 없으면 라인 전체.
        // 종전엔 system 을 아예 받지 않아, 시스템 페이지가 "시스템 X" 라고 써 놓고 라인 전체를 그렸다.
        var systemFlows = string.IsNullOrWhiteSpace(flow) ? ResolveSystemFlowSet(system) : null;
        var rows = await _repo.QueryCyclesAsync(fromMs, toMs, flow, branch, ct, systemFlows);
        var facts = rows.Select(r => r.ToFact()).ToList();

        var totals = KpiRules.Compute(facts, kappa);
        // 세그먼트는 (설비, 분기) 계열마다 따로 만든다. 사이클 행은 한 계열 안에서만 시간축을 타일링하므로
        // 섞어서 병합하면 서로 겹친 구간이 이름표 없이 쏟아진다(화면이 덮어 그릴 수밖에 없다).
        var segments = rows
            .GroupBy(r => (r.Flow, r.Branch))
            .SelectMany(g => KpiRules
                .BuildSegments(g.Select(r => r.ToFact()).ToList(), kappa)
                .Select(s => (Seg: s, g.Key.Flow, g.Key.Branch)))
            .OrderBy(x => x.Seg.StartMs)
            .ToList();
        var links = await _repo.QueryLinkEventsAsync(fromMs, toMs, ct);
        var gatedWorks = await _repo.GetGatedWorksAsync(fromMs, toMs, flow, ct);
        var snapMs = _settings.LoadSettings().Kpi.ResolveBoundarySnapMs();

        // 사이클 단위 행 — 간트가 사이클마다 MT/WT 경계선과 축(work | MT)을 그릴 때 쓴다(세그먼트는 인접 병합이라 사이클을 못 가른다).
        var cycles = facts.Select(f =>
        {
            var st = KpiRules.Classify(f, kappa);
            return new KpiCycleDto(
                f.Id, f.StartMs, f.EndMs, st.ToString(),
                st == CycleState.Excluded ? KpiRules.ResolveExclude(f, kappa).ToString() : null,
                f.MtMs,
                st == CycleState.Down ? KpiRules.Axis(f, kappa).ToString() : null,
                f.WorstWork, f.WorstRatio, f.MtRatio, f.OverflowMs);
        }).ToList();

        var excluded = new List<KpiExcludedDto>();
        int cut = 0, unknown = 0, inProgress = 0, noBaseline = 0, unclassified = 0, overflow = 0;
        foreach (var f in facts)
        {
            if (KpiRules.Classify(f, kappa) != CycleState.Excluded) continue;
            var reason = KpiRules.ResolveExclude(f, kappa);
            switch (reason)
            {
                case ExcludeReason.Cut: cut++; break;
                case ExcludeReason.InProgress: inProgress++; break;
                case ExcludeReason.NoBaseline: noBaseline++; break;
                case ExcludeReason.Unclassified: unclassified++; break;
                case ExcludeReason.Overflow: overflow++; break;
                default: unknown++; break;
            }
            excluded.Add(new KpiExcludedDto(
                Math.Max(f.StartMs, fromMs), Math.Min(f.EndMs, toMs), reason.ToString()));
        }

        // 구간 시작부터 첫 행까지는 이전 경계를 모르는 구간(UNK). 행이 하나도 없으면 구간 전체가 UNK.
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
                new KpiExcludedCountsDto(cut, unknown, inProgress, noBaseline, unclassified, overflow)),
            KpiMetricsDto.From(totals),
            segments.Select(x => new KpiSegmentDto(
                KpiTime.ToIso(x.Seg.StartMs), KpiTime.ToIso(x.Seg.EndMs),
                x.Seg.StartMs, x.Seg.EndMs,
                x.Seg.State.ToString(),
                x.Seg.State == CycleState.Excluded ? x.Seg.Reason.ToString() : null,
                x.Seg.Cycles, x.Seg.CtMs, x.Seg.MtMs, x.Seg.RMs, x.Seg.MtMedianMs,
                x.Seg.State == CycleState.Down ? x.Seg.Axis.ToString() : null,
                x.Seg.WorstWork, x.Seg.WorstRatio, x.Seg.MtRatio,
                x.Flow, x.Branch)).ToList(),
            excluded,
            links.Select(l => new KpiLinkDto(
                l.System, KpiTime.ToIso(l.AtMs), l.EndMs is long e ? KpiTime.ToIso(e) : null,
                l.IsConnected, l.Kind, l.Detail)).ToList(),
            new KpiKappaDto(kappa.NonProd, kappa.Work, kappa.Mt, kappa.Quality, kappa.OverflowMs),
            cycles,
            gatedWorks,
            snapMs));
    }

    // ── GET /api/kpi/cycle/{id}/works ─────────────────────────────────────────

    /// <summary>
    /// 한 사이클의 work 지속시간 — 세그먼트 툴팁·간트 상세가 "왜 비가동인가"를 보일 때.
    /// 게이트에 걸린 work 는 <c>gated</c> 로 표시되고 <c>over</c> 는 항상 false 다(판정 근거가 아니므로).
    /// </summary>
    [HttpGet("cycle/{id:long}/works")]
    public async Task<ActionResult<List<KpiWorkDto>>> CycleWorks(long id, CancellationToken ct)
    {
        var works = await _repo.GetCycleWorksAsync(id, ct);
        var kappa = _settings.LoadSettings().Kpi.Resolve();
        return Ok(works
            .Select(w => new KpiWorkDto(w.Work, w.DurationMs, w.WUsedMs, w.Ratio, w.Gated, !w.Gated && w.Ratio > kappa.Work))
            .ToList());
    }

    // ── GET/PUT /api/kpi/settings ─────────────────────────────────────────────

    /// <summary>현재 계수·품질과 허용 범위.</summary>
    [HttpGet("settings")]
    public ActionResult<KpiSettingsDto> GetSettings()
    {
        var s = _settings.LoadSettings().Kpi;
        return Ok(new KpiSettingsDto(
            s.NonProdKappa, s.DownKappa, s.MtKappa, s.QualityPercent,
            s.WorkGate, s.BoundarySnapMs, s.OverflowToleranceMs, s.RawRetentionDays,
            KpiKappa.NonProdMin, KpiKappa.NonProdMax,
            KpiKappa.WorkMin, KpiKappa.WorkMax,
            KpiKappa.MtMin, KpiKappa.MtMax,
            KpiRules.WorkGateMin, KpiRules.WorkGateMax,
            0, KpiRules.BoundarySnapMsMax,
            KpiKappa.OverflowMsMin, KpiKappa.OverflowMsMax,
            KpiRules.MinBaselineSamples, KpiRules.BaselineWindowDays));
    }

    /// <summary>
    /// 계수·품질 저장. κ 와 초과 허용치는 저장 즉시 전 구간이 재라벨된다 — 상태를 저장하지 않고 조회 시 도출하기
    /// 때문이다(doc/30 §5). 게이트와 스냅은 적재 시점 값이라 다음 적재부터 반영된다.
    /// </summary>
    [HttpPut("settings")]
    public ActionResult<KpiSettingsDto> PutSettings([FromBody] KpiSettingsRequest req)
    {
        _settings.Update(m =>
        {
            if (req.NonProdKappa is double np) m.Kpi.NonProdKappa = Math.Clamp(np, KpiKappa.NonProdMin, KpiKappa.NonProdMax);
            if (req.DownKappa is double dn) m.Kpi.DownKappa = Math.Clamp(dn, KpiKappa.WorkMin, KpiKappa.WorkMax);
            if (req.MtKappa is double mk) m.Kpi.MtKappa = Math.Clamp(mk, KpiKappa.MtMin, KpiKappa.MtMax);
            if (req.QualityPercent is double q) m.Kpi.QualityPercent = Math.Clamp(q, 0, 100);
            if (req.WorkGate is double g) m.Kpi.WorkGate = Math.Clamp(g, KpiRules.WorkGateMin, KpiRules.WorkGateMax);
            if (req.BoundarySnapMs is long sn) m.Kpi.BoundarySnapMs = Math.Clamp(sn, 0, KpiRules.BoundarySnapMsMax);
            if (req.OverflowToleranceMs is long ov) m.Kpi.OverflowToleranceMs = Math.Clamp(ov, KpiKappa.OverflowMsMin, KpiKappa.OverflowMsMax);
            if (req.RawRetentionDays is int d) m.Kpi.RawRetentionDays = Math.Max(0, d);
        });
        _logger.LogInformation(
            "[Kpi] settings saved — nonProd={Np} work={Wk} mt={Mt} quality={Q} gate={G} snap={S}ms overflow={O}ms",
            req.NonProdKappa, req.DownKappa, req.MtKappa, req.QualityPercent, req.WorkGate, req.BoundarySnapMs, req.OverflowToleranceMs);
        return GetSettings();
    }

    // ── 공통 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 시스템 이름 → 그 시스템의 설비 이름 집합. 미지정이면 null(= 라인 전체, 종전 동작).
    /// 지정했는데 시스템이 없거나 모델 미로드면 <b>빈 집합</b>이다 — 전체로 폴백하면 라인 수치가 그 시스템
    /// 것처럼 보인다(가장 위험한 오해). 구 OEE 컨트롤러와 같은 태도.
    /// </summary>
    private HashSet<string>? ResolveSystemFlowSet(string? system)
    {
        if (string.IsNullOrWhiteSpace(system)) return null;
        var name = system.Trim();
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var sys in _project.GetActiveSystems())
            {
                if (!string.Equals(sys.Name, name, StringComparison.Ordinal)) continue;
                foreach (var f in _project.GetFlows(sys.Id)) set.Add(f.Name);
                break;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Kpi] system→flow 집합 해석 실패: {System}", name); }
        return set;
    }

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
/// <param name="Cycles">사이클 단위 행(연표 세그먼트는 인접 병합이라 사이클을 못 가른다) — 간트의 MT/WT 선·축 표시용.</param>
/// <param name="GatedWorks">이 구간·flow 에서 게이트에 걸려 판정에서 빠진 work 이름(doc/30 §4.1).</param>
/// <param name="BoundarySnapMs">경계 스냅(ms) — 간트가 work 구간을 사이클에 귀속할 때 서버와 같은 값을 쓴다(doc/30 §3).</param>
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
    KpiKappaDto Kappa,
    List<KpiCycleDto> Cycles,
    List<string> GatedWorks,
    long BoundarySnapMs);

/// <summary>사이클 한 행의 판정 결과. Axis 는 비가동일 때만("Work" | "Mt"), Reason 은 제외일 때만.</summary>
public sealed record KpiCycleDto(
    long Id, long StartMs, long EndMs, string State, string? Reason,
    long? MtMs, string? Axis, string? WorstWork, double WorstRatio, double MtRatio, long OverflowMs);

public sealed record KpiCountsDto(int Run, int Down, int NonProd, KpiExcludedCountsDto Excluded);

public sealed record KpiExcludedCountsDto(
    int Cut, int Unknown, int InProgress, int NoBaseline, int Unclassified, int Overflow)
{
    public int Total => Cut + Unknown + InProgress + NoBaseline + Unclassified + Overflow;
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

/// <summary>연표 세그먼트. 인접한 같은 상태의 사이클이 하나로 묶여 있다. Axis 는 비가동일 때만("Work" | "Mt").</summary>
/// <summary>
/// 연표 세그먼트. <b>Flow·Branch 를 함께 싣는다</b> — 사이클 행은 (설비, 분기) 하나에 대해서만 시간축을
/// 빈틈없이 타일링한다. 여러 계열을 한 배열에 이름표 없이 섞어 보내면 화면은 겹쳐 그리는 수밖에 없고,
/// 나중에 시작한 것이 앞선 것을 덮어 정상 구간이 제외에 가려진다(현장 2026-09-21: 라인 226개 중 225개 겹침).
/// 계열을 알면 화면이 픽셀 구간마다 상태별 면적을 합산할 수 있다(doc/30 §9.1 "구간 비례").
/// </summary>
public sealed record KpiSegmentDto(
    string Start, string End, long StartMs, long EndMs,
    string State, string? Reason,
    int Cycles, long CtMs, long MtMs, double RMs, double MtMedianMs,
    string? Axis, string? WorstWork, double WorstRatio, double MtRatio,
    string Flow, string? Branch);

public sealed record KpiExcludedDto(long StartMs, long EndMs, string Reason);

/// <summary>접속 전이·심박 공백. 계산 인자가 아니라 대조용 오버레이다.</summary>
public sealed record KpiLinkDto(
    string System, string At, string? End, bool IsConnected, string Kind, string? Detail);

public sealed record KpiKappaDto(double NonProd, double Work, double Mt, double Quality, long OverflowMs);

public sealed record KpiWorkDto(
    string Work, long DurationMs, double WUsedMs, double Ratio, bool Gated, bool Over);

public sealed record KpiSettingsDto(
    double NonProdKappa, double DownKappa, double MtKappa, double QualityPercent,
    double WorkGate, long BoundarySnapMs, long OverflowToleranceMs, int RawRetentionDays,
    double NonProdMin, double NonProdMax,
    double DownMin, double DownMax,
    double MtMin, double MtMax,
    double WorkGateMin, double WorkGateMax,
    long BoundarySnapMin, long BoundarySnapMax,
    long OverflowMin, long OverflowMax,
    int MinBaselineSamples, int BaselineWindowDays);

public sealed record KpiSettingsRequest(
    double? NonProdKappa, double? DownKappa, double? MtKappa, double? QualityPercent,
    double? WorkGate, long? BoundarySnapMs, long? OverflowToleranceMs, int? RawRetentionDays);
