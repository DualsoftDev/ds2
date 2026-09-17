// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Kpi;

/// <summary>
/// 기준선 R·W 의 14일 이동 중앙값. doc/30 §3.
/// <para>
/// R 은 flow(분기가 있으면 분기별) 완료 CT 의 중앙값, W 는 work 별 지속시간의 중앙값이다.
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

    /// <summary>flow 의 work 별 현재 W(ms). 표본 부족한 work 는 빠진다.</summary>
    public async Task<IReadOnlyDictionary<string, double>> GetWAsync(string flow, CancellationToken ct = default)
    {
        var snap = await EnsureAsync(ct);
        return snap.W.TryGetValue(flow, out var m) ? m : EmptyW;
    }

    /// <summary>캐시를 즉시 무효화한다 — 사이클을 대량 적재한 직후 등.</summary>
    public void Invalidate() => _snapshot = _snapshot with { ExpiresAt = DateTime.MinValue };

    private static readonly IReadOnlyDictionary<string, double> EmptyW =
        new Dictionary<string, double>(StringComparer.Ordinal);

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
        var w = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        var rows = new List<BaselineRow>();

        try
        {
            long since = KpiTime.NowMs() - (long)TimeSpan.FromDays(KpiRules.BaselineWindowDays).TotalMilliseconds;
            var today = KpiTime.LocalDate(KpiTime.NowMs());
            var scopes = await _repo.GetActiveScopesAsync(since, ct);

            foreach (var (flow, branch) in scopes)
            {
                var cts = await _repo.GetCtSamplesAsync(flow, branch, since, ct);
                if (KpiRules.Median(cts) is double med)
                {
                    r[Key(flow, branch)] = med;
                    rows.Add(new BaselineRow(BaselineRow.ScopeR, flow, branch ?? "", "", today, med, cts.Count));
                }
            }

            // W 는 분기와 무관하게 flow 단위로 모은다 — work 는 물리 설비의 동작 회로다.
            foreach (var flow in scopes.Select(s => s.Flow).Distinct(StringComparer.Ordinal))
            {
                var samples = await _repo.GetWorkSamplesAsync(flow, since, ct);
                if (samples.Count == 0) continue;
                var map = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (work, list) in samples)
                {
                    if (KpiRules.Median(list) is not double med) continue;
                    map[work] = med;
                    rows.Add(new BaselineRow(BaselineRow.ScopeW, flow, "", work, today, med, list.Count));
                }
                if (map.Count > 0) w[flow] = map;
            }

            if (rows.Count > 0) await _repo.UpsertBaselineAsync(rows, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Kpi] baseline compute failed — 기존 캐시를 유지한다");
            return _snapshot with { ExpiresAt = DateTime.UtcNow.Add(CacheTtl) };
        }

        return new Snapshot(
            r,
            w.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, double>)kv.Value, StringComparer.Ordinal),
            DateTime.UtcNow.Add(CacheTtl));
    }

    private sealed record Snapshot(
        IReadOnlyDictionary<string, double> R,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> W,
        DateTime ExpiresAt)
    {
        public static Snapshot Empty => new(
            new Dictionary<string, double>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal),
            DateTime.MinValue);
    }
}
