// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using Dapper;
using DSPilot.Models;
using DSPilot.Models.Analysis;
using DSPilot.Services;
using Microsoft.Data.Sqlite;

namespace DSPilot.Kpi;

/// <summary>
/// 완료된 사이클을 시간 기반 코어 DB 로 적재한다. doc/30 §2 · §3 · §4 · §13 3차.
/// <para>
/// 하는 일은 넷이다. ① 신호에서 call 구간(§2.2)을 만들고 ② 사이클마다 분기 제외 call 을 뺀 뒤 work 구간(§2.3)·
/// MT(§2.4)·경계 초과(§3)를 재고 ③ 그 시점의 기준선 R·W·MT중앙 과 게이트(§4.1)를 박제하고 ④ 한 트랜잭션으로
/// 저장한다. 상태는 저장하지 않는다 — 조회 시 현재 κ 로 도출한다.
/// </para>
/// <para>
/// ★ 전환 기간의 사이클 <b>출처</b>는 구 파이프라인의 산출물(dspFlowHistory)이다. 경계 도출과 분기 판별은 이미
/// 검증된 경로라 그대로 재사용하고, 3차 완료 시 출처만 새 수집 경로로 바꾼다. 여기서 하는 계산은 그때도 그대로 남는다.
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

    /// <summary>call 신호를 조회할 때 사이클 앞뒤로 두는 여유(ms) — 경계에 걸친 OUT↑/IN↑ 짝을 놓치지 않기 위함.</summary>
    private const long SignalPadMs = 60_000;

    private readonly KpiRepository _repo;
    private readonly BaselineService _baselines;
    private readonly AppSettingsService _settings;
    private readonly IDatabasePathResolver _paths;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CycleIngestService> _logger;

    public CycleIngestService(
        KpiRepository repo,
        BaselineService baselines,
        AppSettingsService settings,
        IDatabasePathResolver paths,
        IServiceScopeFactory scopes,
        ILogger<CycleIngestService> logger)
    {
        _repo = repo;
        _baselines = baselines;
        _settings = settings;
        _paths = paths;
        _scopes = scopes;
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
        var pending = await ReadPendingAsync(ct);
        if (pending.Count == 0) return 0;

        var kpi = _settings.LoadSettings().Kpi;
        double gate = kpi.ResolveWorkGate();
        long snapMs = kpi.ResolveBoundarySnapMs();

        int saved = 0;
        foreach (var group in pending.GroupBy(c => c.Flow, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var flow = group.Key;
            var rows = group.OrderBy(c => c.StartMs).ToList();

            var callSpans = await LoadCallSpansAsync(
                flow, rows[0].StartMs - SignalPadMs, rows[^1].EndMs + SignalPadMs, ct);

            // 스냅 대상 경계 = 이 배치의 모든 사이클 시작·끝(오름차순).
            var boundaries = rows.SelectMany(r => new[] { r.StartMs, r.EndMs }).Distinct().OrderBy(x => x).ToList();

            var branchSet = _settings.GetFlowBranchSet(flow);
            bool hasBranches = branchSet is { Branches.Count: > 0 };

            foreach (var src in rows)
            {
                // 분기가 있는 flow 에서 어느 분기도 아니면 미분류 — 계산·표본 밖. 구간은 재서 보여 주되 기준선은 박제하지 않는다.
                bool unclassified = hasBranches && string.IsNullOrWhiteSpace(src.Branch);
                var excl = unclassified ? EmptyNames : ExcludedCallsOf(branchSet, src.Branch);

                var (measured, mt, overflow) = MeasureCycle(callSpans, excl, src.StartMs, src.EndMs, boundaries, snapMs);

                if (unclassified)
                {
                    var works0 = measured.Select(m => new WorkDuration(m.Work, m.DurationMs, 0)).ToList();
                    var rec0 = new CycleRecord(flow, null, src.StartMs, src.EndMs, mt, 0, 0, null, 0, overflow, ExcludeReason.Unclassified);
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
    private sealed record CallSpanSet(string CallName, string Work, List<Span> Spans);

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
    /// </summary>
    private static (List<(string Work, long DurationMs)> Works, long? MtMs, long OverflowMs) MeasureCycle(
        IReadOnlyList<CallSpanSet> calls, HashSet<string> excluded, long cs, long ce,
        IReadOnlyList<long> boundaries, long snapMs)
    {
        var env = new Dictionary<string, (long S, long E)>(StringComparer.Ordinal);
        long lastEnd = long.MinValue;
        long overflow = 0;

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

    // ── 출처(전환 기간): 구 파이프라인의 완료 사이클 ─────────────────────────────

    private sealed record SourceCycle(string Flow, string? Branch, long StartMs, long EndMs);

    private async Task<List<SourceCycle>> ReadPendingAsync(CancellationToken ct)
    {
        var watermarks = await ReadWatermarksAsync(ct);

        var legacyPath = _paths.GetPlcDbPath();
        if (!File.Exists(legacyPath)) return [];

        await using var conn = new SqliteConnection($"Data Source={legacyPath};Mode=ReadOnly;Default Timeout=20");
        await conn.OpenAsync(ct);

        // recordedAt 은 사이클의 <b>끝</b>(= 다음 경계). 시작은 끝 − ct. mt 는 tail 기준이라 쓰지 않는다 — 여기서 work 로 다시 잰다.
        var raw = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT flowName AS FlowName, branchName AS BranchName, ct AS Ct, recordedAt AS RecordedAt
            FROM dspFlowHistory
            WHERE ct > 0 AND recordedAt IS NOT NULL
            ORDER BY recordedAt DESC
            LIMIT 20000
            """,
            cancellationToken: ct));

        var list = new List<SourceCycle>();
        foreach (var r in raw)
        {
            string? flow = r.FlowName as string;
            if (string.IsNullOrWhiteSpace(flow)) continue;
            if (ParseUtcMs(r.RecordedAt as string) is not long end) continue;
            long cts = Convert.ToInt64(r.Ct);
            if (cts <= 0) continue;

            long mark = watermarks.TryGetValue(flow, out var m) ? m : 0;
            if (end <= mark) continue;

            list.Add(new SourceCycle(flow, r.BranchName as string, end - cts, end));

            if (list.Count >= BatchLimit) break;
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

    private static long? ParseUtcMs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return (long)(dt - DateTime.UnixEpoch).TotalMilliseconds;
        return null;
    }
}
