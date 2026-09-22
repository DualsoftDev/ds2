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
/// 아무것도 저장하지 않는다. 사건도 회복 시각도 상태도 조회할 때 도출하므로, 나중에 디바이스를 묶으면
/// 과거 알람도 함께 살아난다(doc/30 §9.1-③ 과 같은 태도).
/// </para>
/// <para>
/// 2026-09-22 개정 — 집계 단위가 <b>태그에서 디바이스</b>로 올라갔고, 정지 게이트와 재접속 스냅샷 제외가
/// 들어왔다. 현장 실측에서 종전 숫자의 82%가 고장이 아니었다(경고 21 · 스냅샷 6 · 진짜 고장 2).
/// </para>
/// </summary>
public sealed class ErrorTagReliabilityService
{
    /// <summary>
    /// 재가동 확인 창 = 리듬 R × 이 배수. 후보 flow 가 여럿이면 <b>가장 긴 R</b> 을 쓴다 —
    /// 짧은 쪽에 맞추면 느린 설비가 전부 '미확인' 이 된다.
    /// </summary>
    public const double RestartWindowRMultiple = 2.0;

    /// <summary>
    /// 창의 하한 — R 을 못 구했을 때의 폴백이자 사이클이 빠른 설비의 바닥값이다.
    /// <para>
    /// R 은 사이클 주기(흔히 분 단위)라 사람이 고치고 다시 돌리는 리듬과 자릿수가 다르다. R×2 를 그대로
    /// 쓰면 CT 2분짜리 설비의 창이 4분이 되어, 정상 수리 건이 줄줄이 '재가동 미확인' 으로 떨어진다.
    /// 잘못 경고하는 쪽이 조금 길게 재는 쪽보다 나쁘다.
    /// </para>
    /// </summary>
    public const long RestartWindowFloorMs = 30 * 60 * 1000;

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
    /// <param name="EventNo">묶인 사건 번호. 같은 번호 = 같은 정지에서 울린 알람들이다.</param>
    /// <param name="Skip">집계에서 빠진 이유(<c>None</c> = 집계 대상). 화면 분해의 근거.</param>
    /// <param name="RepairMs">사건의 발생 → 재가동 그대로(차감 없음). 복구 완료일 때만.</param>
    public sealed record AlertVerdict(
        DateTime OccurredAtUtc,
        DateTime? ClearedAtUtc,
        DateTime? RestartedAtUtc,
        string SystemName,
        string Name,
        string TagAddress,
        string Device,
        RecoveryState State,
        StopVerdict Stop,
        SkipCause Skip,
        int EventNo,
        long? RepairMs,
        string? RestartFlow);

    /// <summary>조회 결과 — 라인 지표 + 디바이스별 + 건별 판정 + 커버리지.</summary>
    /// <param name="UnboundTagCount">묶이지 않아 계산에서 빠진 에러 태그 수(전역 제외).</param>
    /// <param name="StaleSystems">
    /// 알람에는 나오는데 현재 모델에 없는 System 이름 — <b>리네임으로 과거가 끊겼다는 신호</b>다.
    /// 비어 있지 않으면 화면이 조용히 넘어가지 말고 말해야 한다.
    /// </param>
    public sealed record Result(
        Summary Summary,
        List<DeviceSummary> Devices,
        List<AlertVerdict> Alerts,
        int UnboundTagCount,
        int GlobalTagCount,
        int SkippedChangedCount,
        List<string> StaleSystems,
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
            return new Result(RollUp([]), [], [], 0, globalCount, 0, [], _project.IsLoaded);

        var currentSystems = CurrentSystems();
        var deviceIndex = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex(
            bindings, currentSystems, settings.AbnormalAlarm.SystemAliases);

        // (System, 디바이스) → flow 집합. 한 디바이스가 여러 flow 에 걸치는 것이 Ds2 모델의 정상이라 집합으로 받는다.
        // ★System 까지 키에 넣는 이유 — 디바이스 별칭은 PLC 가 둘이면 겹칠 수 있다(실측 주소 충돌 12%).
        var (flowsByKey, flowsByDevice) = BuildFlowsByDevice();

        var fromMs = KpiTime.ToMs(fromUtc);
        var toMs = KpiTime.ToMs(toUtc);

        // 알람 — 사용자정의(usertag) 구분만. 자동감지(abnormal)는 사용자가 선언한 고장이 아니다.
        var records = await _alerts.QueryAlertsAsync(
            fromUtc, toUtc, nameFilter: null, levelFilter: null, systemFilter: systemFilter,
            categoryFilter: "usertag", limit: int.MaxValue, offset: 0, ct: ct,
            sortColumn: null, sortDesc: false);

        // 태그를 디바이스로 접는다 — 여기서부터 계산 단위가 디바이스다.
        var byDevice = new Dictionary<(string System, string Device), List<UserTagAlertRecord>>();
        var unboundAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentNames = currentSystems.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staleSystems = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in records)
        {
            var sysName = r.SystemName ?? string.Empty;
            var address = r.TagAddress ?? string.Empty;

            if (!AbnormalDeviceFilterHelpers.TryGetBoundDevice(
                    deviceIndex, r.SystemId.ToString(), sysName, address, out var device)
                || device.Length == 0)
            {
                // 미지정·전역 — 계산에서 빠진다. 커버리지 안내를 위해 태그 수만 센다.
                if (!string.IsNullOrWhiteSpace(address)) unboundAddresses.Add(sysName + "|" + address);
                // 현재 모델에 없는 System 이름이면 리네임으로 끊긴 것이다 — 조용히 넘기지 않는다.
                if (sysName.Length > 0 && !currentNames.Contains(sysName)) staleSystems.Add(sysName);
                continue;
            }

            var key = (System: sysName, Device: device);
            if (!byDevice.TryGetValue(key, out var list)) byDevice[key] = list = [];
            list.Add(r);
        }

        // 필요한 flow 만 한 번씩 읽는다. 창 밖의 재가동도 근거가 되어야 하므로 뒤쪽을 넉넉히 연다.
        var neededFlows = byDevice.Keys
            .SelectMany(k => FlowsOf(flowsByKey, flowsByDevice, k.System, k.Device))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cycleToMs = Math.Max(toMs, KpiTime.NowMs());
        var flowFacts = new Dictionary<string, FlowFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in neededFlows)
            flowFacts[flow] = await LoadFlowFactsAsync(flow, fromMs, cycleToMs, ct);

        // 통신 재접속 시각 — 붙는 순간 이미 켜져 있던 조건이 한꺼번에 발화한 것을 걸러낸다.
        var linkAt = await LoadLinkConnectsAsync(fromMs, cycleToMs, ct);

        var nowMs = KpiTime.NowMs();
        var devices = new List<DeviceSummary>();
        var verdicts = new List<AlertVerdict>();
        var skippedChanged = 0;
        var eventSeq = 0;

        foreach (var ((sysName, device), group) in byDevice.OrderBy(kv => kv.Key.System, StringComparer.OrdinalIgnoreCase)
                                                           .ThenBy(kv => kv.Key.Device, StringComparer.OrdinalIgnoreCase)
                                                           .Select(kv => (kv.Key, kv.Value)))
        {
            var flows = FlowsOf(flowsByKey, flowsByDevice, sysName, device);
            var starts = MergeStarts(flows, flowFacts);
            var rhythm = RhythmMs(starts);
            var windowMs = RestartWindowFor(flows, flowFacts);

            var ordered = group.OrderBy(r => r.OccurredAt).ToList();
            var inputs = ordered.Select(r => ToInput(r, windowMs)).ToList();

            // ① 사건으로 묶고 ② 집계 진입 여부를 판정한다.
            var events = BuildEvents(inputs, starts, nowMs);
            foreach (var e in events)
            {
                var changed = ordered[e.Members[0]].MatchOp is { } op
                    && string.Equals(op, "Changed", StringComparison.OrdinalIgnoreCase);
                if (changed) skippedChanged += e.Members.Count;
                Classify([e], starts, rhythm, linkAt, changed);
            }

            // ③ 분모 = 리듬이 유지된 구간(조회 창으로 자름).
            var operating = OperatingMs(starts, rhythm, fromMs, toMs);
            devices.Add(AggregateDevice(sysName, device, events, operating));

            foreach (var e in events)
            {
                eventSeq++;
                foreach (var i in e.Members)
                    verdicts.Add(Verdict(ordered[i], device, e, eventSeq, flows, flowFacts));
            }
        }

        devices.Sort((a, b) => b.TotalDownMs.CompareTo(a.TotalDownMs));   // 보전 우선순위 = 총 정지시간
        verdicts.Sort((a, b) => b.OccurredAtUtc.CompareTo(a.OccurredAtUtc));

        return new Result(
            RollUp(devices), devices, verdicts,
            unboundAddresses.Count, globalCount, skippedChanged, [.. staleSystems], true);
    }

    private static AlertInput ToInput(UserTagAlertRecord r, long windowMs) =>
        new(KpiTime.ToMs(r.OccurredAt), r.ClearedAt is { } c ? KpiTime.ToMs(c) : null, windowMs);

    private static AlertVerdict Verdict(
        UserTagAlertRecord r, string device, FaultEvent e, int eventNo,
        IReadOnlyList<string> flows, Dictionary<string, FlowFacts> facts)
    {
        var restartMs = e.Recovery.RestartMs;
        return new AlertVerdict(
            OccurredAtUtc: r.OccurredAt,
            ClearedAtUtc: r.ClearedAt,
            RestartedAtUtc: restartMs is { } ms ? KpiTime.ToUtc(ms) : null,
            SystemName: r.SystemName ?? string.Empty,
            Name: r.Name ?? string.Empty,
            TagAddress: r.TagAddress ?? string.Empty,
            Device: device,
            State: e.Recovery.State,
            Stop: e.Stop,
            Skip: e.Skip,
            EventNo: eventNo,
            RepairMs: e.DownMs,
            // 어느 flow 가 회복 근거였는지 남긴다 — 분기 우회로 오판이 났을 때 사후 추적의 유일한 실마리다.
            RestartFlow: restartMs is { } r2 ? FlowOfRestart(r2, flows, facts) : null);
    }

    private static string? FlowOfRestart(long restartMs, IReadOnlyList<string> flows, Dictionary<string, FlowFacts> facts)
    {
        foreach (var flow in flows)
            if (facts.TryGetValue(flow, out var f) && f.Starts.BinarySearch(restartMs) >= 0)
                return flow;
        return null;
    }

    /// <summary>후보 flow 들의 사이클 시작 시각 합집합(오름차순). "하나라도 돌면" 이 OR 이므로 합집합이다.</summary>
    private static List<long> MergeStarts(IReadOnlyList<string> flows, Dictionary<string, FlowFacts> facts)
    {
        var all = new List<long>();
        foreach (var flow in flows)
            if (facts.TryGetValue(flow, out var f))
                all.AddRange(f.Starts);
        all.Sort();
        return all;
    }

    /// <summary>후보 flow 중 가장 긴 R 의 배수, 하한 적용.</summary>
    private static long RestartWindowFor(IReadOnlyList<string> flows, Dictionary<string, FlowFacts> facts)
    {
        double maxR = 0;
        foreach (var flow in flows)
            if (facts.TryGetValue(flow, out var f) && f.RhythmMs > maxR)
                maxR = f.RhythmMs;
        return Math.Max((long)(maxR * RestartWindowRMultiple), RestartWindowFloorMs);
    }

    /// <summary>flow 1개에서 뽑은 재료 — 사이클 시작 시각과 그 간격의 중앙값뿐이다.</summary>
    private sealed record FlowFacts(List<long> Starts, double RhythmMs);

    private async Task<FlowFacts> LoadFlowFactsAsync(
        string flow, long fromMs, long toMs, CancellationToken ct)
    {
        var cycles = await _kpi.QueryCyclesAsync(fromMs, toMs, flow, branch: null, ct);

        // 시작 시각만 쓴다. 그 사이클의 판정(가동·비가동·비생산)도, 구간 길이도 보지 않는다 —
        // 분기 행이 서로 겹쳐(실측 #135 는 37행 중 28행) 구간을 쓰면 한 시각의 상태가 여럿이 된다.
        // 제외 행(UNK·진행 중 등)도 시작 자체는 진짜 head 신호라 근거로 쓴다.
        var starts = cycles.Select(c => c.StartMs).Distinct().OrderBy(x => x).ToList();

        // ★박제 R(rUsedMs)이 아니라 실측 시작 간격의 중앙값이다 — 박제 R 은 v68 기준선 기계의 산물이라
        // 표본 게이트에 걸리면 0 이 되고, 새 DB 에서 기준선이 안 잡히는 교착 전례가 있다(2026-09-21).
        return new FlowFacts(starts, RhythmMs(starts));
    }

    /// <summary>접속이 붙은 시각(오름차순). 끊김·gap 은 보지 않는다 — 스냅샷은 '붙는 순간' 에 생긴다.</summary>
    private async Task<List<long>> LoadLinkConnectsAsync(long fromMs, long toMs, CancellationToken ct)
    {
        try
        {
            var events = await _kpi.QueryLinkEventsAsync(fromMs, toMs, ct);
            return events
                .Where(e => e.IsConnected && e.Kind is LinkEventRecord.KindLink or LinkEventRecord.KindBoot)
                .Select(e => e.AtMs)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }
        catch (Exception ex)
        {
            // 링크 이력을 못 읽으면 스냅샷 제외만 못 할 뿐 지표는 나와야 한다.
            _logger.LogDebug(ex, "[eMTBF] 접속 이력 조회 실패 (non-critical)");
            return [];
        }
    }

    /// <summary>현재 모델의 활성 System (GUID, 이름) — 리네임 해석과 '끊긴 이름' 판정의 기준.</summary>
    private List<(string Id, string Name)> CurrentSystems()
    {
        try
        {
            return [.. _project.GetActiveSystems().Select(s => (s.Id.ToString(), s.Name ?? string.Empty))];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[eMTBF] 활성 System 수집 실패 (non-critical)");
            return [];
        }
    }

    private static List<string> FlowsOf(
        Dictionary<(string, string), List<string>> byKey,
        Dictionary<string, List<string>> byDevice,
        string system, string device)
    {
        if (byKey.TryGetValue((system, device), out var scoped) && scoped.Count > 0) return scoped;
        // System 이 안 맞는 경우(리네임 직후 등)는 디바이스 이름만으로 폴백한다 — 없는 것보다 낫다.
        return byDevice.TryGetValue(device, out var any) ? any : [];
    }

    /// <summary>
    /// (System, 디바이스) → 그 디바이스를 쓰는 Call 이 속한 flow 이름 집합. AASX 를 한 번 훑어 만든다.
    /// System 없는 디바이스 단독 색인도 같이 만들어 폴백에 쓴다.
    /// </summary>
    private (Dictionary<(string, string), List<string>>, Dictionary<string, List<string>>) BuildFlowsByDevice()
    {
        var byKey = new Dictionary<(string, string), HashSet<string>>();
        var byDevice = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sysNameById = _project.GetActiveSystems().ToDictionary(s => s.Id, s => s.Name ?? string.Empty);

            foreach (var flow in _project.GetAllFlows())
            {
                var sysName = sysNameById.TryGetValue(flow.ParentId, out var n) ? n : string.Empty;
                foreach (var work in _project.GetWorks(flow.Id))
                    foreach (var call in _project.GetCalls(work.Id))
                    {
                        if (string.IsNullOrWhiteSpace(call.DevicesAlias)) continue;
                        var device = call.DevicesAlias.Trim();

                        var key = (sysName, device);
                        if (!byKey.TryGetValue(key, out var set))
                            byKey[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(flow.Name);

                        if (!byDevice.TryGetValue(device, out var all))
                            byDevice[device] = all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        all.Add(flow.Name);
                    }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[eMTBF] 디바이스→flow 수집 실패 (non-critical)");
        }

        static List<string> Sorted(HashSet<string> s) => [.. s.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
        return (byKey.ToDictionary(kv => kv.Key, kv => Sorted(kv.Value)),
                byDevice.ToDictionary(kv => kv.Key, kv => Sorted(kv.Value), StringComparer.OrdinalIgnoreCase));
    }
}
