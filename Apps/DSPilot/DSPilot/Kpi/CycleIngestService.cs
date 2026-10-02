// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Infrastructure;
using DSPilot.Models;
using DSPilot.Models.Analysis;
using DSPilot.Repositories;
using DSPilot.Services;

namespace DSPilot.Kpi;

/// <summary>
/// 완료된 사이클을 시간 기반 코어 DB 로 적재한다. doc/30 §2 · §3 · §4 · §13 3차.
/// <para>
/// 하는 일은 넷이다. ① 신호에서 call 구간(§2.2)을 만들고 ② 사이클마다 분기 제외 call 을 뺀 뒤 work 구간(§2.3)·
/// MT(§2.4)·경계 초과(§3)를 재고 ③ 그 시점의 기준선 R·W·MT중앙 과 게이트(§4.1)를 박제하고 ④ 한 트랜잭션으로
/// 저장한다. 상태는 저장하지 않는다 — 조회 시 현재 κ 로 도출한다.
/// </para>
/// <para>
/// 사이클 <b>출처</b> = 원시 신호(signal)의 PLC 태그 시각으로 도출한 경계(<see cref="CycleSourceDeriver"/>) — 간트·주기
/// 재도출과 같은 함수다(2026-10-02). 종전엔 라이브 기록(dspFlowHistory, 끝 = 처리 시각 UtcNow)을 받아 경계가 처리
/// 지연만큼(현장 0.2초~수십 초) 늦었고, 다음 사이클 head call 이 앞 사이클에 귀속돼 경계 초과 대량 제외·MT=CT 가짜
/// 비가동·MT중앙 오염·간트 뱃지 한 칸 밀림이 났다.
/// </para>
/// <para>
/// 처리 상한은 flow 의 <b>시스템(PLC)별</b> 최신 신호 시각 − 정착 여유. 전역 최신을 쓰면 늦게 들어오는 PLC 의 중간
/// 경계가 오기 전에 구간을 닫아 두 사이클이 하나로 합쳐진다. 통신 공백(미계측, 시스템별)과 겹치는 사이클은 경계를
/// 믿을 수 없으므로 <see cref="ExcludeReason.Unknown"/> 으로 박제한다. 미계측 조회가 실패하면 그 flow 는 이번 주기를
/// 미룬다 — 행은 박제라 나중에 고칠 수 없다.
/// </para>
/// </summary>
public sealed class CycleIngestService : BackgroundService
{
    /// <summary>적재 주기.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>기동 후 첫 적재까지의 여유 — 스키마 생성·엔진 초기화가 끝난 뒤 시작한다.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>한 번에 처리할 최대 사이클 수 — 신호 조회 구간이 지나치게 넓어지지 않게 한다.</summary>
    private const int BatchLimit = 400;

    /// <summary>한 번에 기준선을 뒤늦게 찍어 줄 최대 행 수.</summary>
    private const int BackfillLimit = 2000;

    /// <summary>call 신호를 조회할 때 사이클 앞뒤로 두는 여유(ms) — 경계에 걸친 OUT↑/IN↑ 짝을 놓치지 않기 위함.
    /// 경계 엣지 조회의 앞 여유로도 쓴다 — 엣지 판정(LAG)이 구간 첫 행의 직전 값을 알아야 가짜 상승을 안 만든다.</summary>
    private const long SignalPadMs = 60_000;

    /// <summary>정착 여유(ms) — 시스템 최신 신호보다 이만큼 앞선 경계까지만 닫는다. 끝 근처 call 의 하강·응답이
    /// 아직 안 들어와 MT·초과가 덜 재진 채 박제되는 것을 막는다.</summary>
    private const long SettleMs = 10_000;

    /// <summary>한 번에 도출하는 최대 구간(ms). 이 안에 닫힌 사이클이 없으면 상한까지 한 번 넓힌다(장기 정지).</summary>
    private const long MaxSpanMs = 6 * 3_600_000L;

    private readonly KpiRepository _repo;
    private readonly BaselineService _baselines;
    private readonly AppSettingsService _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly PlcToCallMapperService _mapper;
    private readonly CycleSourceDeriver _deriver;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly DsProjectService _project;
    private readonly IPlcRepository _plc;
    private readonly OeeCommHealthService _commHealth;
    private readonly ILogger<CycleIngestService> _logger;

    /// <summary>시작 경계 미해석 경고를 이미 남긴 flow — 30초마다 같은 경고가 쌓이지 않게. 해석되면 지운다.</summary>
    private readonly HashSet<string> _warnedUnresolved = new(StringComparer.Ordinal);

    /// <summary>이번 주기의 시스템별 신호 범위 — 같은 PLC 의 flow 들이 한 번만 잰다. 주기마다 비운다.</summary>
    private readonly Dictionary<Guid, (long? OldestMs, long? LatestMs)> _spanBySystem = new();

    public CycleIngestService(
        KpiRepository repo,
        BaselineService baselines,
        AppSettingsService settings,
        IServiceScopeFactory scopes,
        PlcToCallMapperService mapper,
        CycleSourceDeriver deriver,
        IFlowMetricsService flowMetrics,
        DsProjectService project,
        IPlcRepository plc,
        OeeCommHealthService commHealth,
        ILogger<CycleIngestService> logger)
    {
        _repo = repo;
        _baselines = baselines;
        _settings = settings;
        _scopes = scopes;
        _mapper = mapper;
        _deriver = deriver;
        _flowMetrics = flowMetrics;
        _project = project;
        _plc = plc;
        _commHealth = commHealth;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var n = await IngestOnceAsync(stoppingToken);
                if (n > 0)
                {
                    _baselines.Invalidate();
                    _logger.LogInformation("[Kpi] ingested {Count} cycle(s)", n);
                }

                // 표본이 쌓여 기준선이 생겼으면, 기준선 없이 들어온 행에 뒤늦게 찍어 준다.
                var b = await BackfillBaselinesAsync(stoppingToken);
                if (b > 0) _logger.LogInformation("[Kpi] baseline stamped on {Count} pending cycle(s)", b);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Kpi] cycle ingest failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>한 배치 적재. 반환값은 저장한 사이클 수.</summary>
    public async Task<int> IngestOnceAsync(CancellationToken ct)
    {
        // 추적 flow·경계 Call 이름은 라이브 엔진이 모델에서 해석해 둔 것 — 초기화 전이면 다음 주기에.
        if (!_flowMetrics.IsInitialized) return 0;

        _spanBySystem.Clear();
        var watermarks = await ReadWatermarksAsync(ct);
        var kpi = _settings.LoadSettings().Kpi;
        double gate = kpi.ResolveWorkGate();
        long snapMs = kpi.ResolveBoundarySnapMs();

        int saved = 0;
        foreach (var flow in _flowMetrics.GetTrackedFlowNames())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                saved += await IngestFlowAsync(flow, watermarks.TryGetValue(flow, out var m) ? m : null, gate, snapMs, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Kpi] cycle ingest failed — flow={Flow}", flow);
            }
        }
        return saved;
    }

    /// <summary>flow 하나 — 워터마크 이후 닫힌 사이클을 도출해 측정·박제한다. 반환값은 저장한 사이클 수.</summary>
    private async Task<int> IngestFlowAsync(string flow, long? watermark, double gate, long snapMs, CancellationToken ct)
    {
        var rows = await ReadPendingAsync(flow, watermark, ct);
        if (rows.Count == 0) return 0;

        // 통신 공백(미계측) — 이 flow 의 시스템 기준, 배치 구간당 1회. 실패(비신뢰)면 이번 주기는 미룬다(박제라 못 고친다).
        var systemScope = SystemKeyConvention.Scope(_project.TryGetSystemIdByFlowName(flow));
        var (gaps, trusted) = await _commHealth.TryGetUnmeasuredWindowsAsync(
            KpiTime.ToUtc(rows[0].StartMs), KpiTime.ToUtc(rows[^1].EndMs), ct, systemScope);
        if (!trusted)
        {
            _logger.LogInformation("[Kpi] 미계측 조회 실패 — flow={Flow} 적재를 다음 주기로 미룸", flow);
            return 0;
        }

        var callSpans = await LoadCallSpansAsync(
            flow, rows[0].StartMs - SignalPadMs, rows[^1].EndMs + SignalPadMs, ct);

        // 스냅 대상 경계 = 이 배치의 모든 사이클 시작·끝(오름차순).
        var boundaries = rows.SelectMany(r => new[] { r.StartMs, r.EndMs }).Distinct().OrderBy(x => x).ToList();

        var branchSet = _settings.GetFlowBranchSet(flow);
        bool hasBranches = branchSet is { Branches.Count: > 0 };

        // 경계를 IN 전용 call 의 주소로 고른 분기/flow 만 값이 있다(그 work 의 시작점 시드). 분기마다 경계가 다르므로 캐시한다.
        var seedWorkByBranch = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        int saved = 0;
        foreach (var src in rows)
        {
            // 분기가 있는 flow 에서 어느 분기도 아니면 미분류 — 계산·표본 밖. 구간은 재서 보여 주되 기준선은 박제하지 않는다.
            bool unclassified = hasBranches && string.IsNullOrWhiteSpace(src.Branch);
            // 통신 공백과 겹치면 다음 경계가 공백 뒤로 밀린 것일 수 있다 — 가짜 비생산·비가동을 만들지 않게 제외.
            // 3분 미만 공백은 보고되지 않아(OeeCommHealthService.MinReportGapMs) 사이클로 남는다(의도).
            bool commGap = gaps.Any(g => g.S < src.EndMs && g.E > src.StartMs);
            var excl = unclassified ? EmptyNames : ExcludedCallsOf(branchSet, src.Branch);

            var seedKey = src.Branch ?? "";
            if (!seedWorkByBranch.TryGetValue(seedKey, out var seedWork))
                seedWorkByBranch[seedKey] = seedWork = ResolveBoundarySeedWork(flow, branchSet, src.Branch);

            var (measured, mt, overflow) = MeasureCycle(
                callSpans, excl, src.StartMs, src.EndMs, boundaries, snapMs, seedWork);

            if (commGap || unclassified)
            {
                var works0 = measured.Select(m => new WorkDuration(m.Work, m.DurationMs, 0)).ToList();
                var rec0 = new CycleRecord(flow, src.Branch, src.StartMs, src.EndMs, mt, 0, 0, null, 0, overflow,
                    commGap ? ExcludeReason.Unknown : ExcludeReason.Unclassified);
                if (await _repo.SaveCycleAsync(rec0, works0, ct) > 0) saved++;
                continue;
            }

            var r = await _baselines.GetRAsync(flow, src.Branch, ct);
            var mtMed = await _baselines.GetMtAsync(flow, src.Branch, ct);
            var wBase = await _baselines.GetWAsync(flow, src.Branch, ct);

            var (works, worstWork, worstRatio) = Stamp(measured, wBase, gate);

            var record = new CycleRecord(
                flow,
                src.Branch,
                src.StartMs,
                src.EndMs,
                mt,
                r ?? 0,
                mtMed ?? 0,
                worstWork,
                worstRatio,
                overflow,
                r is null ? ExcludeReason.NoBaseline : ExcludeReason.None);

            if (await _repo.SaveCycleAsync(record, works, ct) > 0) saved++;
        }
        return saved;
    }

    /// <summary>
    /// 기준선 없이 적재된 행에 기준선을 뒤늦게 박제한다. 반환값은 처리한 행 수.
    /// <para>
    /// 설치 직후에는 표본이 없어 첫 사이클들이 전부 '기준 없음' 으로 들어온다. 표본 10건이 쌓이면
    /// 그 행들도 판정 대상이 되어야 한다 — 안 그러면 첫 묶음만 영구히 계산 밖에 남아, 수집 첫날
    /// 지표가 통째로 비게 된다(실측: 첫 적재 400건이 모두 제외).
    /// </para>
    /// </summary>
    public async Task<int> BackfillBaselinesAsync(CancellationToken ct)
    {
        var pending = await _repo.GetPendingBaselineCyclesAsync(BackfillLimit, ct);
        if (pending.Count == 0) return 0;

        double gate = _settings.LoadSettings().Kpi.ResolveWorkGate();

        int done = 0;
        foreach (var (id, flow, branch) in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (await _baselines.GetRAsync(flow, branch, ct) is not double r) continue;
            var mtMed = await _baselines.GetMtAsync(flow, branch, ct) ?? 0;
            var wBase = await _baselines.GetWAsync(flow, branch, ct);

            var works = await _repo.GetCycleWorksAsync(id, ct);
            var (stamped, worstWork, worstRatio) = Stamp(works.Select(w => (w.Work, w.DurationMs)).ToList(), wBase, gate);

            if (await _repo.StampBaselineAsync(id, r, mtMed, worstWork, worstRatio, stamped, ct)) done++;
        }
        return done;
    }

    // ── 측정 ──────────────────────────────────────────────────────────────────

    /// <summary>한 call 의 이름·소속 work·구간 목록(§2.2 규칙으로 만든 것).</summary>
    public sealed record CallSpanSet(string CallName, string Work, List<Span> Spans);

    /// <summary>
    /// 이 flow(분기)의 사이클 경계가 <b>IN 전용 call</b> 의 주소일 때 그 call 이 속한 work 이름, 아니면 null.
    /// <para>
    /// 경계는 주소+에지 하나가 정본이고 IN/OUT 을 가리지 않는다(<see cref="CycleBoundaryEdges"/>). 그런데 §2.2 의
    /// call 구간은 OUT 상승에서만 열리므로, 경계로 지목된 call 에 OUT 이 없으면 그 call 은 구간을 하나도 못 만들고
    /// 소속 work 가 통째로 사라진다. 그 경우에만 <see cref="MeasureCycle"/> 에 시작점을 시드한다 —
    /// OUT 을 가진 call 이면 이미 그 구간이 경계에서 열리므로 시드할 것이 없다(null).
    /// </para>
    /// 모델이 아직 안 섰거나 주소가 이 flow 에 없으면 null — 판단 근거가 없으면 종전 동작을 유지한다.
    /// </summary>
    private string? ResolveBoundarySeedWork(string flow, FlowBranchSet? branchSet, string? branch)
    {
        try
        {
            string? address = null;
            if (!string.IsNullOrWhiteSpace(branch))
            {
                // 분기 행은 그 분기의 경계가 정본. 정의를 못 찾으면(이름 변경 등) 시드하지 않는다.
                address = branchSet?.Branches
                    .FirstOrDefault(b => string.Equals(b.Name, branch, StringComparison.OrdinalIgnoreCase))
                    ?.StartTagAddress;
            }
            else if (branchSet is not { Branches.Count: > 0 })
            {
                address = _settings.GetFlowCycleOverride(flow)?.StartTagAddress;
            }

            if (!CycleBoundaryEdges.HasTagSpec(address)) return null;

            if (!_mapper.IsInitialized) _mapper.Initialize();
            var hit = _mapper.GetFlowTagCatalog(flow)
                .FirstOrDefault(t => string.Equals(t.Address, address, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return null;

            // OUT 이 하나라도 있는 call 이면 §2.2 가 이미 구간을 만든다 — 시드 불필요.
            var pairs = _mapper.GetCallTagPairsByCallId(hit.CallId);
            if (pairs.Any(p => !string.IsNullOrWhiteSpace(p.OutTag))) return null;

            return string.IsNullOrWhiteSpace(hit.WorkName) ? hit.CallName : hit.WorkName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Kpi] 경계 시드 work 해석 실패 — flow={Flow} branch={Branch}", flow, branch);
            return null;
        }
    }

    private static readonly HashSet<string> EmptyNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>이 분기의 제외 call 이름 집합. 분기 정의가 없거나 이름이 안 맞으면 빈 집합(= 아무것도 빼지 않음).</summary>
    private static HashSet<string> ExcludedCallsOf(FlowBranchSet? set, string? branch)
    {
        if (set is null || string.IsNullOrWhiteSpace(branch)) return EmptyNames;
        var def = set.Branches.FirstOrDefault(b => string.Equals(b.Name, branch, StringComparison.OrdinalIgnoreCase));
        if (def is null) return EmptyNames;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in def.ExcludedCallNames)
        {
            // 자기 시작/끝 call 이 제외 목록에 섞여 있으면 무시 — 분기 판별 경로와 같은 방어.
            if (string.Equals(n, def.StartCallName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n, def.EndCallName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
        }
        return names;
    }

    /// <summary>
    /// 사이클 [cs, ce) 안의 work 구간·MT·경계 초과(doc/30 §2.3 · §2.4 · §3).
    /// <list type="bullet">
    ///   <item>제외 call 은 뺀다(call 단위). 남은 call 구간 중 시작이 이 사이클에 속하는 것만 본다 — 시작은 스냅 뒤 시각.</item>
    ///   <item>work 구간 = 그 work 에 속한 call 구간들의 최소 시작 ~ 최대 끝. 더하지 않는다.</item>
    ///   <item>MT = 경계 → 마지막 work 끝(사이클 끝에서 자름). work 사이 공백을 포함한다.</item>
    ///   <item>초과 = call 구간 끝이 사이클 끝을 넘은 최대량. 허용치 비교는 조회 시(κ).</item>
    /// </list>
    /// <para>
    /// <paramref name="seedWork"/> = 사이클 경계를 <b>IN 전용 call</b> 의 주소로 고른 경우 그 call 이 속한 work
    /// (<see cref="ResolveBoundarySeedWork"/>). §2.2 는 "시작점은 언제나 OUT 상승" 이라 IN 전용 call 은 구간을 만들지
    /// 못하는데, 사용자가 그 센서를 사이클 시작으로 지목했다면 적어도 <b>시작점</b>은 인정해야 한다 — 그 work 를
    /// cs 에서 열어 두면 뒤따르는 형제 call 이 붙을 때 work 구간이 사이클 시작부터 측정된다.
    /// 끝(<c>lastEnd</c>)에는 기여하지 않는다: MT 끝은 "마지막 work 끝"(§2.4)이고 맨 IN 상승 하나를 동작 종료로
    /// 인정할지는 별개 결정이다. 그래서 그 work 에 다른 call 이 없으면 폭 0 으로 남아 아래 <c>e &gt; s</c> 에서
    /// 제외된다 — 지속시간이 원리상 없는 관측 전용 work 는 판정 대상이 아니다.
    /// </para>
    /// </summary>
    public static (List<(string Work, long DurationMs)> Works, long? MtMs, long OverflowMs) MeasureCycle(
        IReadOnlyList<CallSpanSet> calls, HashSet<string> excluded, long cs, long ce,
        IReadOnlyList<long> boundaries, long snapMs, string? seedWork = null)
    {
        var env = new Dictionary<string, (long S, long E)>(StringComparer.Ordinal);
        long lastEnd = long.MinValue;
        long overflow = 0;

        if (!string.IsNullOrEmpty(seedWork)) env[seedWork] = (cs, cs);

        foreach (var call in calls)
        {
            if (excluded.Count > 0 && excluded.Contains(call.CallName)) continue;
            foreach (var span in call.Spans)
            {
                long start = WorkSpanMath.Snap(span.S, boundaries, snapMs);
                if (start < cs || start >= ce) continue;

                if (env.TryGetValue(call.Work, out var cur))
                    env[call.Work] = (Math.Min(cur.S, span.S), Math.Max(cur.E, span.E));
                else
                    env[call.Work] = (span.S, span.E);

                if (span.E > lastEnd) lastEnd = span.E;
                if (span.E > ce) overflow = Math.Max(overflow, span.E - ce);
            }
        }

        var works = new List<(string, long)>(env.Count);
        foreach (var (work, (s, e)) in env)
            if (e > s) works.Add((work, e - s));

        long? mt = lastEnd > cs ? Math.Min(lastEnd, ce) - cs : null;
        return (works, mt, overflow);
    }

    /// <summary>
    /// work 별 지속시간에 기준선 W 와 게이트를 박제하고, 게이트를 통과한 work 중 최악 배율을 고른다(doc/30 §4.1).
    /// 기준선이 없는 work 는 W=0 으로 남아 비교에서 빠진다.
    /// </summary>
    private static (List<WorkDuration> Works, string? WorstWork, double WorstRatio) Stamp(
        IReadOnlyList<(string Work, long DurationMs)> measured,
        IReadOnlyDictionary<string, WorkBaseline> wBase, double gate)
    {
        var list = new List<WorkDuration>(measured.Count);
        double worst = 0;
        string? worstWork = null;
        foreach (var (work, dur) in measured)
        {
            WorkDuration wd;
            if (wBase.TryGetValue(work, out var wb))
            {
                bool gated = KpiRules.IsGated(new Quartiles(wb.Q1Ms, wb.MedianMs, wb.Q3Ms, wb.SampleCount), gate);
                wd = new WorkDuration(work, dur, wb.MedianMs, gated);
            }
            else wd = new WorkDuration(work, dur, 0);

            list.Add(wd);
            if (!wd.Gated && wd.Ratio > worst) { worst = wd.Ratio; worstWork = work; }
        }
        return (list, worstWork, worst);
    }

    // ── 출처: 원시 신호에서 도출한 닫힌 사이클 ─────────────────────────────────────

    private sealed record SourceCycle(string Flow, string? Branch, long StartMs, long EndMs);

    /// <summary>
    /// 워터마크(이 flow 의 마지막 적재 사이클 끝) 이후, 시스템 최신 신호 − 정착 여유 안에서 <b>닫힌</b> 사이클.
    /// 마지막 열린 사이클(다음 경계 없음)은 다음 주기로 넘긴다. 최대 <see cref="BatchLimit"/> 건.
    /// <para>워터마크가 없으면(첫 적재) 기준선 창(14일)만큼만 거슬러 올라간다 — 전 이력 재적재는 별도 작업.</para>
    /// </summary>
    private async Task<List<SourceCycle>> ReadPendingAsync(string flow, long? watermark, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var systemId = _project.TryGetSystemIdByFlowName(flow);
        var key = systemId ?? Guid.Empty;
        if (!_spanBySystem.TryGetValue(key, out var span))
            _spanBySystem[key] = span = await _plc.GetSignalSpanMsAsync(systemId);
        var (oldestMs, latestMs) = span;
        if (oldestMs is not long oldest || latestMs is not long latest) return [];

        long upper = latest - SettleMs;
        long from = watermark ?? Math.Max(oldest, latest - KpiRules.BaselineWindowDays * 86_400_000L);
        if (upper <= from) return [];

        // Head/Tail = 간트(CallTestController.ResolveEffectiveHeadTail)와 같은 순서 — 런타임 경계(override 적용) > AASX.
        var (head, tail) = _flowMetrics.GetCycleBoundaryCallNames(flow);
        if (string.IsNullOrEmpty(head) && string.IsNullOrEmpty(tail))
            (head, tail) = _flowMetrics.GetAasxCycleBoundaries(flow);

        long to = Math.Min(upper, from + MaxSpanMs);
        var list = await DeriveClosedAsync(flow, head, tail, from, to);
        if (list is { Count: 0 } && to < upper)
            list = await DeriveClosedAsync(flow, head, tail, from, upper);   // 장기 정지 — 상한까지 한 번 넓힌다
        if (list is null) return [];

        if (list.Count > BatchLimit) list.RemoveRange(BatchLimit, list.Count - BatchLimit);
        return list;
    }

    /// <summary>[from, to) 의 닫힌 사이클(시작 ≥ from). 시작 경계 미해석이면 null(경고는 flow 당 한 번).</summary>
    private async Task<List<SourceCycle>?> DeriveClosedAsync(string flow, string? head, string? tail, long from, long to)
    {
        // 엣지 판정은 구간 첫 행의 직전 값을 알아야 한다 — 앞 여유를 두고 받은 뒤 from 이전 시작은 버린다.
        var fromLocal = KpiTime.ToLocal(from - SignalPadMs);
        var toLocal = KpiTime.ToLocal(to);
        var derived = await _deriver.DeriveAsync(flow, head, tail, fromLocal, toLocal);

        // Head 에 OUT 이 없어 시작을 못 정하면 간트처럼 AASX Head 로 한 번 더(CallTestController.ResolveBoundariesAsync 폴백).
        if (derived.Status == DeriveStatus.Unresolved)
        {
            var (aasxHead, aasxTail) = _flowMetrics.GetAasxCycleBoundaries(flow);
            if (!string.IsNullOrEmpty(aasxHead) && !string.Equals(aasxHead, head, StringComparison.OrdinalIgnoreCase))
                derived = await _deriver.DeriveAsync(flow, aasxHead, aasxTail, fromLocal, toLocal);
        }

        if (derived.Status == DeriveStatus.Unresolved)
        {
            if (_warnedUnresolved.Add(flow))
                _logger.LogWarning("[Kpi] '{Flow}' 시작 경계 미해석 ({What}) — 판정 적재 건너뜀", flow, derived.UnresolvedLabel);
            return null;
        }
        _warnedUnresolved.Remove(flow);

        var list = new List<SourceCycle>();
        foreach (var c in derived.Cycles)
        {
            if (c.PeriodMs is not double period) continue;   // 열린 사이클 — 다음 경계가 와야 닫힌다
            long start = KpiTime.ToMs(c.Start);
            if (start < from) continue;
            list.Add(new SourceCycle(flow, c.Branch, start, start + (long)Math.Round(period)));
        }
        return list;
    }

    private async Task<Dictionary<string, long>> ReadWatermarksAsync(CancellationToken ct)
    {
        try { return await _repo.GetCycleWatermarksAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Kpi] watermark read failed — 전체 재적재로 진행(저장이 멱등이라 무해)");
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// flow 의 call 별 구간(doc/30 §2.2). 신호에서 call 마다 OUT ON 구간과 IN 상승을 모아 유형(o~i / o~o)을 정하고
    /// 구간을 만든다. IN 만 있는 call 은 여기서 빠진다. 채터 필터 없는 원본을 쓴다.
    /// </summary>
    private async Task<List<CallSpanSet>> LoadCallSpansAsync(
        string flow, long fromMs, long toMs, CancellationToken ct)
    {
        var result = new List<CallSpanSet>();
        try
        {
            using var scope = _scopes.CreateScope();
            var analysis = scope.ServiceProvider.GetRequiredService<CycleAnalysisService>();

            var data = await analysis.GetActualIoSignalSegmentsInTimeRangeAsync(
                flow, KpiTime.ToLocal(fromMs), KpiTime.ToLocal(toMs), maxItems: null);

            // call → (이름, work, OUT ON 구간, IN 상승)
            var byCall = new Dictionary<Guid, (string Name, string Work, List<(long Rise, long Fall)> Outs, List<long> Ins)>();
            foreach (var item in data.Items)
            {
                var work = string.IsNullOrWhiteSpace(item.WorkName) ? item.CallName : item.WorkName;
                if (string.IsNullOrWhiteSpace(work)) continue;

                if (!byCall.TryGetValue(item.CallId, out var c))
                    byCall[item.CallId] = c = (item.CallName ?? "", work, [], []);

                long rise = KpiTime.ToMs(item.GoingStartTime);
                if (item.EventType == IOEventType.OutTag)
                {
                    // 하강을 모르는 열린 구간은 Fall ≤ Rise 로 넘겨 o~o 에서 버려지게 한다.
                    long fall = item.FinishTime is DateTime ft ? KpiTime.ToMs(ft) : rise;
                    c.Outs.Add((rise, fall));
                }
                else c.Ins.Add(rise);
            }

            foreach (var (_, c) in byCall)
            {
                var spans = WorkSpanMath.CallSpans(c.Outs, c.Ins);
                if (spans.Count > 0) result.Add(new CallSpanSet(c.Name, c.Work, spans));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Kpi] call span load failed — flow={Flow}", flow);
        }
        return result;
    }
}
