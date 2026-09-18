// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using Dapper;
using DSPilot.Models.Analysis;
using DSPilot.Services;
using Microsoft.Data.Sqlite;

namespace DSPilot.Kpi;

/// <summary>
/// 완료된 사이클을 시간 기반 코어 DB 로 적재한다. doc/30 §13 2~3단계.
/// <para>
/// 하는 일은 셋이다. ① 사이클 구간 안에서 work 별 지속시간을 재고 ② 그 시점의 기준선 R·W 를 박제하고
/// ③ 한 트랜잭션으로 저장한다. 상태는 저장하지 않는다 — 조회 시 현재 κ 로 도출한다.
/// </para>
/// <para>
/// ★ 전환 기간의 사이클 <b>출처</b>는 구 파이프라인의 산출물(dspFlowHistory)이다. head→head 경계 도출은
/// 이미 검증된 경로라 그대로 재사용하고, 교체 완료(doc/30 §13 7단계) 시 출처만 새 수집 경로로 바꾼다.
/// 여기서 하는 계산(work 지속시간·박제·저장)은 그때도 그대로 남는다.
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

    /// <summary>work 신호를 조회할 때 사이클 앞뒤로 두는 여유(ms) — 경계에 걸친 OUT↑/IN↑ 짝을 놓치지 않기 위함.</summary>
    private const long SignalPadMs = 60_000;

    private readonly KpiRepository _repo;
    private readonly BaselineService _baselines;
    private readonly IDatabasePathResolver _paths;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CycleIngestService> _logger;

    public CycleIngestService(
        KpiRepository repo,
        BaselineService baselines,
        IDatabasePathResolver paths,
        IServiceScopeFactory scopes,
        ILogger<CycleIngestService> logger)
    {
        _repo = repo;
        _baselines = baselines;
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

        int saved = 0;
        foreach (var group in pending.GroupBy(c => c.Flow, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var flow = group.Key;
            var rows = group.OrderBy(c => c.StartMs).ToList();

            var workSpans = await LoadWorkSpansAsync(
                flow, rows[0].StartMs - SignalPadMs, rows[^1].EndMs + SignalPadMs, ct);

            var wBaseline = await _baselines.GetWAsync(flow, ct);

            foreach (var src in rows)
            {
                var r = await _baselines.GetRAsync(flow, src.Branch, ct);

                var works = new List<WorkDuration>();
                double worstRatio = 0;
                string? worstWork = null;

                foreach (var (work, spans) in workSpans)
                {
                    long dur = WorkSpanMath.OverlapMs(spans, src.StartMs, src.EndMs);
                    if (dur <= 0) continue;
                    double wUsed = wBaseline.TryGetValue(work, out var wv) ? wv : 0;
                    var wd = new WorkDuration(work, dur, wUsed);
                    works.Add(wd);
                    if (wd.Ratio > worstRatio) { worstRatio = wd.Ratio; worstWork = work; }
                }

                var record = new CycleRecord(
                    flow,
                    src.Branch,
                    src.StartMs,
                    src.EndMs,
                    src.MtMs,
                    r ?? 0,
                    worstWork,
                    worstRatio,
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

        int done = 0;
        foreach (var group in pending.GroupBy(p => p.Flow, StringComparer.Ordinal))
        {
            var wBaseline = await _baselines.GetWAsync(group.Key, ct);
            foreach (var (id, flow, branch) in group)
            {
                ct.ThrowIfCancellationRequested();
                if (await _baselines.GetRAsync(flow, branch, ct) is not double r) continue;

                var works = await _repo.GetCycleWorksAsync(id, ct);
                var stamped = new List<WorkDuration>(works.Count);
                double worstRatio = 0;
                string? worstWork = null;
                foreach (var w in works)
                {
                    double wUsed = wBaseline.TryGetValue(w.Work, out var wv) ? wv : 0;
                    var wd = new WorkDuration(w.Work, w.DurationMs, wUsed);
                    stamped.Add(wd);
                    if (wd.Ratio > worstRatio) { worstRatio = wd.Ratio; worstWork = w.Work; }
                }

                if (await _repo.StampBaselineAsync(id, r, worstWork, worstRatio, stamped, ct)) done++;
            }
        }
        return done;
    }

    // ── 출처(전환 기간): 구 파이프라인의 완료 사이클 ─────────────────────────────

    private sealed record SourceCycle(string Flow, string? Branch, long StartMs, long EndMs, long? MtMs);

    private async Task<List<SourceCycle>> ReadPendingAsync(CancellationToken ct)
    {
        var watermarks = await ReadWatermarksAsync(ct);

        var legacyPath = _paths.GetPlcDbPath();
        if (!File.Exists(legacyPath)) return [];

        await using var conn = new SqliteConnection($"Data Source={legacyPath};Mode=ReadOnly;Default Timeout=20");
        await conn.OpenAsync(ct);

        // recordedAt 은 사이클의 <b>끝</b>(= 다음 head). 시작은 끝 − ct.
        var raw = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT flowName AS FlowName, branchName AS BranchName, ct AS Ct, mt AS Mt, recordedAt AS RecordedAt
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

            list.Add(new SourceCycle(
                flow,
                r.BranchName as string,
                end - cts,
                end,
                r.Mt is null ? null : Convert.ToInt64(r.Mt)));

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
    /// flow 의 work 별 "실제로 움직인" 구간. 신호에서 call 의 OUT↑→IN↑ 을 짝지어 work 단위로 합집합한다.
    /// </summary>
    private async Task<Dictionary<string, List<Span>>> LoadWorkSpansAsync(
        string flow, long fromMs, long toMs, CancellationToken ct)
    {
        var result = new Dictionary<string, List<Span>>(StringComparer.Ordinal);
        try
        {
            using var scope = _scopes.CreateScope();
            var analysis = scope.ServiceProvider.GetRequiredService<CycleAnalysisService>();

            var data = await analysis.GetActualIoSignalSegmentsInTimeRangeAsync(
                flow, KpiTime.ToLocal(fromMs), KpiTime.ToLocal(toMs), maxItems: null);

            // work → call → (OUT↑ 목록, IN↑ 목록)
            var byWork = new Dictionary<string, Dictionary<Guid, (List<long> Out, List<long> In)>>(StringComparer.Ordinal);
            foreach (var item in data.Items)
            {
                var work = string.IsNullOrWhiteSpace(item.WorkName) ? item.CallName : item.WorkName;
                if (string.IsNullOrWhiteSpace(work)) continue;

                if (!byWork.TryGetValue(work, out var calls))
                    byWork[work] = calls = new Dictionary<Guid, (List<long>, List<long>)>();
                if (!calls.TryGetValue(item.CallId, out var pair))
                    calls[item.CallId] = pair = ([], []);

                long at = KpiTime.ToMs(item.GoingStartTime);
                if (item.EventType == IOEventType.OutTag) pair.Out.Add(at);
                else pair.In.Add(at);
            }

            foreach (var (work, calls) in byWork)
            {
                var spans = new List<Span>();
                foreach (var (_, pair) in calls) spans.AddRange(WorkSpanMath.Pair(pair.Out, pair.In));
                var merged = WorkSpanMath.Union(spans);
                if (merged.Count > 0) result[work] = merged;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Kpi] work span load failed — flow={Flow}", flow);
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
