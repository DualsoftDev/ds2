// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Kpi;

/// <summary>
/// 기준선 R · W · MT중앙 의 14일 이동 중앙값. doc/30 §4.
/// <para>
/// 셋 다 flow(분기가 있으면 분기별) 키다. R 은 완료 CT, MT중앙 은 경계→마지막 work 끝, W 는 work 별 지속시간의
/// 중앙값이다. W 에는 사분위(Q1·Q3)를 함께 두어 게이트(<see cref="KpiRules.IsGated"/>)의 근거로 쓴다.
/// 표본이 <see cref="KpiRules.MinBaselineSamples"/> 미만이면 기준선을 만들지 않는다 — 그 사이클은
/// 기준 없음으로 제외된다. 설치 직후 첫 10 사이클이 여기에 해당한다(자가 부팅).
/// </para>
/// <para>
/// 기준선은 계속 움직이지만 <b>사이클 완료 시점 값이 그 행에 박제</b>되므로 과거 판정은 변하지 않는다.
/// 여기서 캐시하는 것은 "지금 완료되는 사이클에 찍어 줄 값"이다.
/// </para>
/// </summary>
public sealed class BaselineService
{
    /// <summary>캐시 수명 — 기준선은 천천히 움직이므로 짧게 잡을 이유가 없다.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    private readonly KpiRepository _repo;
    private readonly ILogger<BaselineService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot _snapshot = Snapshot.Empty;

    public BaselineService(KpiRepository repo, ILogger<BaselineService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>flow·분기의 현재 R(ms). 표본 부족이면 null.</summary>
    public async Task<double?> GetRAsync(string flow, string? branch, CancellationToken ct = default)
    {
        var snap = await EnsureAsync(ct);
        return snap.R.TryGetValue(Key(flow, branch), out var v) ? v : null;
    }

    /// <summary>flow·분기의 현재 MT중앙(ms). 표본 부족이면 null.</summary>
    public async Task<double?> GetMtAsync(string flow, string? branch, CancellationToken ct = default)
    {
        var snap = await EnsureAsync(ct);
        return snap.Mt.TryGetValue(Key(flow, branch), out var v) ? v : null;
    }

    /// <summary>flow·분기의 work 별 현재 W 와 사분위. 표본 부족한 work 는 빠진다.</summary>
    public async Task<IReadOnlyDictionary<string, WorkBaseline>> GetWAsync(
        string flow, string? branch, CancellationToken ct = default)
    {
        var snap = await EnsureAsync(ct);
        return snap.W.TryGetValue(Key(flow, branch), out var m) ? m : EmptyW;
    }

    /// <summary>캐시를 즉시 무효화한다 — 사이클을 대량 적재한 직후 등.</summary>
    public void Invalidate() => _snapshot = _snapshot with { ExpiresAt = DateTime.MinValue };

    private static readonly IReadOnlyDictionary<string, WorkBaseline> EmptyW =
        new Dictionary<string, WorkBaseline>(StringComparer.Ordinal);

    // 구분자를 넣어 (flow "A", 분기 "B") 와 (flow "AB", 분기 없음) 이 한 키로 겹치지 않게 한다.
    private static string Key(string flow, string? branch) => $"{flow}{branch ?? ""}";

    private async Task<Snapshot> EnsureAsync(CancellationToken ct)
    {
        var cur = _snapshot;
        if (cur.ExpiresAt > DateTime.UtcNow) return cur;

        await _gate.WaitAsync(ct);
        try
        {
            cur = _snapshot;
            if (cur.ExpiresAt > DateTime.UtcNow) return cur;
            var fresh = await ComputeAsync(ct);
            _snapshot = fresh;
            return fresh;
        }
        finally { _gate.Release(); }
    }

    private async Task<Snapshot> ComputeAsync(CancellationToken ct)
    {
        var r = new Dictionary<string, double>(StringComparer.Ordinal);
        var mt = new Dictionary<string, double>(StringComparer.Ordinal);
        var w = new Dictionary<string, IReadOnlyDictionary<string, WorkBaseline>>(StringComparer.Ordinal);
        var rows = new List<BaselineRow>();

        try
        {
            long since = KpiTime.NowMs() - (long)TimeSpan.FromDays(KpiRules.BaselineWindowDays).TotalMilliseconds;
            var today = KpiTime.LocalDate(KpiTime.NowMs());
            var scopes = await _repo.GetActiveScopesAsync(since, ct);

            foreach (var (flow, branch) in scopes)
            {
                var key = Key(flow, branch);
                var br = branch ?? "";

                var cts = await _repo.GetCtSamplesAsync(flow, branch, since, ct);
                if (KpiRules.Median(cts) is double medCt)
                {
                    r[key] = medCt;
                    rows.Add(new BaselineRow(BaselineRow.ScopeR, flow, br, "", today, medCt, cts.Count));
                }

                var mts = await _repo.GetMtSamplesAsync(flow, branch, since, ct);
                if (KpiRules.Median(mts) is double medMt)
                {
                    mt[key] = medMt;
                    rows.Add(new BaselineRow(BaselineRow.ScopeMt, flow, br, "", today, medMt, mts.Count));
                }

                // W 는 분기별 — 같은 이름의 work 라도 기종에 따라 다른 call 집합이 남는다(doc/30 §2.3).
                var samples = await _repo.GetWorkSamplesAsync(flow, branch, since, ct);
                if (samples.Count == 0) continue;
                var map = new Dictionary<string, WorkBaseline>(StringComparer.Ordinal);
                foreach (var (work, list) in samples)
                {
                    if (KpiRules.QuartilesOf(list) is not Quartiles q) continue;
                    map[work] = new WorkBaseline(q.Median, q.Q1, q.Q3, q.Count);
                    rows.Add(new BaselineRow(BaselineRow.ScopeW, flow, br, work, today, q.Median, q.Count, q.Q1, q.Q3));
                }
                if (map.Count > 0) w[key] = map;
            }

            if (rows.Count > 0) await _repo.UpsertBaselineAsync(rows, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Kpi] baseline compute failed — 기존 캐시를 유지한다");
            return _snapshot with { ExpiresAt = DateTime.UtcNow.Add(CacheTtl) };
        }

        return new Snapshot(r, mt, w, DateTime.UtcNow.Add(CacheTtl));
    }

    private sealed record Snapshot(
        IReadOnlyDictionary<string, double> R,
        IReadOnlyDictionary<string, double> Mt,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, WorkBaseline>> W,
        DateTime ExpiresAt)
    {
        public static Snapshot Empty => new(
            new Dictionary<string, double>(StringComparer.Ordinal),
            new Dictionary<string, double>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyDictionary<string, WorkBaseline>>(StringComparer.Ordinal),
            DateTime.MinValue);
    }
}
