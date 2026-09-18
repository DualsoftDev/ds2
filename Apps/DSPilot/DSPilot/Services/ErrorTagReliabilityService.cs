// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using DSPilot.Models;
using DSPilot.Models.UserTagAlerts;
using DSPilot.Repositories;
using static DSPilot.Kpi.ErrorTagReliability;

namespace DSPilot.Services;

/// <summary>
/// 등록 에러 태그 기반 신뢰성 지표(eMTBF · eMTTR) 조회 — doc/31. 판정 규칙은
/// <see cref="ErrorTagReliability"/>(순수 함수)에 있고 여기서는 재료만 모은다.
/// <para>
/// 아무것도 저장하지 않는다. 회복 시각도 상태도 조회할 때 도출하므로, 나중에 디바이스를 묶으면
/// 과거 알람도 함께 살아난다(doc/30 §9.1-③ 과 같은 태도).
/// </para>
/// </summary>
public sealed class ErrorTagReliabilityService
{
    /// <summary>
    /// 재가동 확인 창 = 박제 R × 이 배수. 사이클 두 바퀴 안에 안 돌면 안 고쳐진 것으로 본다.
    /// 후보 flow 가 여럿이면 <b>가장 긴 R</b> 을 쓴다 — 짧은 쪽에 맞추면 느린 설비가 전부 '미확인' 이 된다.
    /// </summary>
    public const double RestartWindowRMultiple = 2.0;

    /// <summary>R 을 못 구했을 때(표본 부족·신규 설치) 쓰는 창. 무한 대기와 즉시 미확인 사이의 절충.</summary>
    public const long RestartWindowFallbackMs = 30 * 60 * 1000;

    private readonly IUserTagAlertRepository _alerts;
    private readonly KpiRepository _kpi;
    private readonly DsProjectService _project;
    private readonly AppSettingsService _settings;
    private readonly ILogger<ErrorTagReliabilityService> _logger;

    public ErrorTagReliabilityService(
        IUserTagAlertRepository alerts, KpiRepository kpi, DsProjectService project,
        AppSettingsService settings, ILogger<ErrorTagReliabilityService> logger)
    {
        _alerts = alerts;
        _kpi = kpi;
        _project = project;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>알람 1건의 판정 결과 — 목록 표시용.</summary>
    /// <param name="RepairMs">발생 → 재가동, 비생산 차감. 복구 완료일 때만.</param>
    public sealed record AlertVerdict(
        DateTime OccurredAtUtc,
        DateTime? ClearedAtUtc,
        DateTime? RestartedAtUtc,
        string SystemName,
        string Name,
        string TagAddress,
        string Device,
        RecoveryState State,
        long? RepairMs,
        string? RestartFlow);

    /// <summary>조회 결과 — 지표 + 건별 판정 + 커버리지.</summary>
    /// <param name="UnboundTagCount">묶이지 않아 계산에서 빠진 에러 태그 수(전역 제외).</param>
    /// <param name="SkippedChangedCount">Changed 매치옵이라 뺀 알람 수.</param>
    public sealed record Result(
        Summary Summary,
        List<AlertVerdict> Alerts,
        int UnboundTagCount,
        int GlobalTagCount,
        int SkippedChangedCount,
        bool ProjectLoaded);

    /// <summary>
    /// 구간 [fromUtc, toUtc) 의 지표를 낸다. 대상은 <b>디바이스가 묶인 이상알람TAG</b> 뿐이다 —
    /// 묶이지 않으면 재가동을 확인할 flow 집합이 없어 회복을 판정할 수 없다(doc/31 §4.1).
    /// </summary>
    public async Task<Result> AnalyzeAsync(
        DateTime fromUtc, DateTime toUtc, string? systemFilter = null, CancellationToken ct = default)
    {
        var settings = _settings.LoadSettings();
        var bindings = AbnormalDeviceFilterHelpers.NormalizeUserTagDeviceBindings(
            settings.AbnormalAlarm.UserTagDeviceBindings);

        var bound = bindings.Where(b => b.Device.Length > 0).ToList();
        var globalCount = bindings.Count - bound.Count;

        if (!_project.IsLoaded || bound.Count == 0)
            return new Result(Aggregate([], []), [], UnboundTagCount: 0, globalCount, 0, _project.IsLoaded);

        // 디바이스 → flow 집합. 한 디바이스가 여러 flow 에 걸치는 것이 Ds2 모델의 정상이라 집합으로 받는다.
        var flowsByDevice = BuildFlowsByDevice();

        // 이 창에서 쓸 flow 전체 — 사이클을 flow 별로 한 번씩만 읽는다.
        var neededFlows = bound
            .Select(b => b.Device)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(d => flowsByDevice.TryGetValue(d, out var f) ? f : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fromMs = KpiTime.ToMs(fromUtc);
        var toMs = KpiTime.ToMs(toUtc);
        var kappa = settings.Kpi.Resolve();

        // 재가동 근거와 비생산 구간은 같은 사이클 행에서 나온다 — flow 당 한 번만 읽는다.
        // 창 밖의 재가동도 근거가 되어야 하므로(구간 끝에 걸친 알람) 뒤쪽을 넉넉히 연다.
        var cycleToMs = Math.Max(toMs, KpiTime.NowMs());
        var flowFacts = new Dictionary<string, FlowFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in neededFlows)
            flowFacts[flow] = await LoadFlowFactsAsync(flow, fromMs, cycleToMs, kappa, ct);

        // 알람 — 사용자정의(usertag) 구분만. 자동감지(abnormal)는 사용자가 선언한 고장이 아니다.
        var records = await _alerts.QueryAlertsAsync(
            fromUtc, toUtc, nameFilter: null, levelFilter: null, systemFilter: systemFilter,
            categoryFilter: "usertag", limit: int.MaxValue, offset: 0, ct: ct,
            sortColumn: null, sortDesc: false);

        var deviceIndex = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex(bindings);
        var nowMs = KpiTime.NowMs();

        var verdicts = new List<AlertVerdict>();
        var resolvedAll = new List<(AlertInput Alert, Recovery Recovery)>();
        var nonProdAll = new List<Span>();
        var skippedChanged = 0;
        var unboundAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 태그 단위로 묶어 처리한다 — 재발화 병합과 eMTBF 간격이 태그별 계열 위에서만 뜻을 갖는다.
        foreach (var group in records
            .GroupBy(r => (r.SystemName ?? string.Empty, r.TagAddress ?? string.Empty))
            .OrderBy(g => g.Key.Item1, StringComparer.OrdinalIgnoreCase))
        {
            var (sysName, address) = group.Key;
            if (!AbnormalDeviceFilterHelpers.TryGetBoundDevice(deviceIndex, sysName, address, out var device)
                || device.Length == 0)
            {
                // 미지정·전역 — 계산에서 빠진다. 커버리지 안내를 위해 태그 수만 센다.
                if (!string.IsNullOrWhiteSpace(address)) unboundAddresses.Add(sysName + "|" + address);
                continue;
            }

            var flows = flowsByDevice.TryGetValue(device, out var f) ? f : [];
            var restarts = MergeRestarts(flows, flowFacts);
            var nonProd = IntersectSpans([.. flows.Select(fl =>
                flowFacts.TryGetValue(fl, out var ff) ? ff.NonProd : (IReadOnlyList<Span>)[])]);
            nonProdAll.AddRange(nonProd);

            var windowMs = RestartWindowFor(flows, flowFacts);
            var ordered = group.OrderBy(r => r.OccurredAt).ToList();

            // 1패스: 창·재가동으로 각 건을 판정한다(재발화 병합 전이라 여기서는 근거만 만든다).
            var resolved = ordered
                .Select(r => Resolve(ToInput(r, windowMs), restarts, nowMs))
                .ToList();

            // 2패스: 회복을 사이에 두지 않은 연속 발생을 한 고장으로 묶는다.
            var kept = DedupeReignitions(
                [.. ordered.Select((r, i) => (KpiTime.ToMs(r.OccurredAt), resolved[i].RestartMs))]);
            var keptSet = kept.ToHashSet();

            for (var i = 0; i < ordered.Count; i++)
            {
                var rec = ordered[i];
                var rv = resolved[i];

                // Changed 는 정상 상태 개념이 없어 발화 즉시 해소된다 — 지속시간이 뜻을 갖지 못한다.
                var isChanged = string.Equals(rec.MatchOp, "Changed", StringComparison.OrdinalIgnoreCase);
                if (isChanged) skippedChanged++;

                var counts = keptSet.Contains(i) && !isChanged;
                if (counts)
                {
                    resolvedAll.Add((ToInput(rec, windowMs), rv));
                    if (rv is { State: RecoveryState.Recovered, RestartMs: { } restartMs })
                        verdicts.Add(Verdict(rec, device, rv, restartMs, nonProd, flows, flowFacts));
                    else
                        verdicts.Add(Verdict(rec, device, rv, null, nonProd, flows, flowFacts));
                }
            }
        }

        // 집계는 전체 태그를 발생 순으로 합쳐서 낸다 — 라인 단위 지표다.
        resolvedAll.Sort((a, b) => a.Alert.OccurredMs.CompareTo(b.Alert.OccurredMs));
        verdicts.Sort((a, b) => b.OccurredAtUtc.CompareTo(a.OccurredAtUtc));

        var summary = Aggregate(resolvedAll, WorkSpanMath.Union(nonProdAll));

        return new Result(summary, verdicts, unboundAddresses.Count, globalCount, skippedChanged, true);
    }

    private static AlertInput ToInput(UserTagAlertRecord r, long windowMs) =>
        new(KpiTime.ToMs(r.OccurredAt), r.ClearedAt is { } c ? KpiTime.ToMs(c) : null, windowMs);

    private AlertVerdict Verdict(
        UserTagAlertRecord r, string device, Recovery rv, long? restartMs,
        IReadOnlyList<Span> nonProd, IReadOnlyList<string> flows, Dictionary<string, FlowFacts> facts)
    {
        var occurredMs = KpiTime.ToMs(r.OccurredAt);
        long? repair = restartMs is { } rm ? ElapsedExcludingNonProduction(occurredMs, rm, nonProd) : null;
        return new AlertVerdict(
            OccurredAtUtc: r.OccurredAt,
            ClearedAtUtc: r.ClearedAt,
            RestartedAtUtc: restartMs is { } ms ? KpiTime.ToUtc(ms) : null,
            SystemName: r.SystemName ?? string.Empty,
            Name: r.Name ?? string.Empty,
            TagAddress: r.TagAddress ?? string.Empty,
            Device: device,
            State: rv.State,
            RepairMs: repair,
            // 어느 flow 가 회복 근거였는지 남긴다 — 분기 우회로 오판이 났을 때 사후 추적의 유일한 실마리다.
            RestartFlow: restartMs is { } r2 ? FlowOfRestart(r2, flows, facts) : null);
    }

    private static string? FlowOfRestart(long restartMs, IReadOnlyList<string> flows, Dictionary<string, FlowFacts> facts)
    {
        foreach (var flow in flows)
            if (facts.TryGetValue(flow, out var f) && f.Restarts.BinarySearch(restartMs) >= 0)
                return flow;
        return null;
    }

    /// <summary>후보 flow 들의 사이클 시작 시각 합집합(오름차순). "하나라도 돌면" 이 OR 이므로 합집합이다.</summary>
    private static List<long> MergeRestarts(IReadOnlyList<string> flows, Dictionary<string, FlowFacts> facts)
    {
        var all = new List<long>();
        foreach (var flow in flows)
            if (facts.TryGetValue(flow, out var f))
                all.AddRange(f.Restarts);
        all.Sort();
        return all;
    }

    /// <summary>후보 flow 중 가장 긴 R 의 배수. R 이 없으면 폴백.</summary>
    private static long RestartWindowFor(IReadOnlyList<string> flows, Dictionary<string, FlowFacts> facts)
    {
        double maxR = 0;
        foreach (var flow in flows)
            if (facts.TryGetValue(flow, out var f) && f.MedianRMs > maxR)
                maxR = f.MedianRMs;
        return maxR > 0 ? (long)(maxR * RestartWindowRMultiple) : RestartWindowFallbackMs;
    }

    /// <summary>flow 1개에서 뽑은 재료 — 재가동 후보, 비생산 구간, 박제 R 대표값.</summary>
    private sealed record FlowFacts(List<long> Restarts, List<Span> NonProd, double MedianRMs);

    private async Task<FlowFacts> LoadFlowFactsAsync(
        string flow, long fromMs, long toMs, KpiKappa kappa, CancellationToken ct)
    {
        var cycles = await _kpi.QueryCyclesAsync(fromMs, toMs, flow, branch: null, ct);

        // 재가동 = 사이클 시작. 제외 행(잘림·UNK·진행 중)도 시작 자체는 진짜 head 신호이므로 근거로 쓴다.
        var restarts = cycles.Select(c => c.StartMs).Distinct().OrderBy(x => x).ToList();

        // 비생산 = 현재 κ 로 도출. 상태를 저장하지 않으므로 κ 를 바꾸면 과거도 함께 다시 라벨된다.
        var nonProd = WorkSpanMath.Union(cycles
            .Where(c => KpiRules.Classify(c.ToFact(), kappa) == CycleState.NonProd)
            .Select(c => new Span(c.StartMs, c.EndMs)));

        var rs = cycles.Where(c => c.RUsedMs > 0).Select(c => c.RUsedMs).OrderBy(x => x).ToList();
        var medianR = rs.Count == 0 ? 0 : rs[rs.Count / 2];

        return new FlowFacts(restarts, nonProd, medianR);
    }

    /// <summary>
    /// 디바이스 → 그 디바이스를 쓰는 Call 이 속한 flow 이름 집합. AASX 를 한 번 훑어 만든다.
    /// </summary>
    private Dictionary<string, List<string>> BuildFlowsByDevice()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var flow in _project.GetAllFlows())
                foreach (var work in _project.GetWorks(flow.Id))
                    foreach (var call in _project.GetCalls(work.Id))
                    {
                        if (string.IsNullOrWhiteSpace(call.DevicesAlias)) continue;
                        var device = call.DevicesAlias.Trim();
                        if (!map.TryGetValue(device, out var set))
                            map[device] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(flow.Name);
                    }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[eMTBF] 디바이스→flow 수집 실패 (non-critical)");
        }
        return map.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }
}
