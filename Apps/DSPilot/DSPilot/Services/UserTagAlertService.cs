// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Ds2.Core;
using Ds2.Editor;
using DSPilot.Hubs;
using DSPilot.Models.UserTagAlerts;
using DSPilot.Repositories;
using LoggingHelpers = Ds2.Core.LoggingHelpers;

namespace DSPilot.Services;

/// <summary>
/// 프로젝트에 정의된 UserTag(LoggingSystemProperties.UserTags)들을 모니터링.
/// plcTagLog 신규 행을 폴링 → AASX UserTag 정의(MatchOp/MatchValue)와 매칭 → 알림 큐 + DB 저장.
/// UI(/user-tags) 가 실시간으로 표시.
///
/// v2: F# UserTagHelpers.shouldFire 를 호출하여 "사용자가 등록한 임의 조건" 으로 매칭.
///     알림은 메모리 큐(최근 N개, 빠른 UI 푸시) + userTagAlertLog 테이블 (장기 보관) 동시 적재.
/// </summary>
public sealed class UserTagAlertService : BackgroundService
{
    private const int PollIntervalMs = 750;
    private const int MaxAlerts = 500;

    private readonly DsProjectService _projectService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SimulationEngineService _engineService;
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly AppSettingsService _appSettings;
    private readonly ILogger<UserTagAlertService> _logger;

    private readonly object _stateLock = new();
    private Dictionary<string, UserTagDefinition> _definitionsByAddress =
        new(StringComparer.OrdinalIgnoreCase);
    // (System, 주소) → 정의. 멀티 PLC 에서 두 System 이 같은 주소를 정의해도 둘 다 살아 있는 정본 인덱스.
    private Dictionary<string, UserTagDefinition> _definitionsBySystemAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private List<UserTagDefinition> _definitions = new();
    private DateTime? _projectLoadedAt;

    // 직전 값 — edge / 임계치 전이 평가에 필요. ★키는 (System, 주소) — 주소만으로 묶으면
    // 두 PLC 의 값이 번갈아 덮여 없는 전이가 만들어지거나 진짜 전이가 삼켜진다.
    private readonly Dictionary<string, string> _lastValueByAddress =
        new(StringComparer.OrdinalIgnoreCase);

    // 라이브 활성 알람(대시보드/전체화면 배너용) — (System, 주소)별 1건. fire 시 등록, 조건 풀림 시 제거.
    // 히스토리 큐(_alerts)/DB 로그와 별개: 표시 목록에서 자동 해소되는 "현재 걸려 있는 알람" 집합.
    // ★주소만으로 묶으면 한 PLC 의 해제가 다른 PLC 의 알람 배너를 지운다.
    private readonly Dictionary<string, ActiveUserAlarm> _activeUserAlarms =
        new(StringComparer.OrdinalIgnoreCase);

    /// System 키 — plc.systemId 컬럼 표기(소문자 "D")와 같아야 로그 행과 매칭된다.
    private static string SysKey(Guid systemId) =>
        systemId == Guid.Empty ? "" : systemId.ToString("D").ToLowerInvariant();

    private static string SysKey(string? systemId) =>
        string.IsNullOrWhiteSpace(systemId) ? ""
        : Guid.TryParse(systemId, out var g) ? SysKey(g) : "";

    /// (System, 주소) 복합키. System 이 미상이면 주소만 — 귀속 미상 로그의 폴백 경로와 키가 일치한다.
    private static string UserTagKey(string sysKey, string address) =>
        sysKey.Length == 0 ? address : sysKey + "|" + address;

    private readonly LinkedList<UserTagAlert> _alerts = new();
    private long _lastCheckedLogId;
    private bool _initialized;

    public event Action? AlertsChanged;

    public UserTagAlertService(
        DsProjectService projectService,
        IServiceScopeFactory scopeFactory,
        SimulationEngineService engineService,
        IHubContext<MonitoringHub> hubContext,
        AppSettingsService appSettings,
        ILogger<UserTagAlertService> logger)
    {
        _projectService = projectService;
        _scopeFactory = scopeFactory;
        _engineService = engineService;
        _hubContext = hubContext;
        _appSettings = appSettings;
        _logger = logger;
    }

    public IReadOnlyList<UserTagDefinition> GetDefinitions()
    {
        lock (_stateLock) return _definitions.ToList();
    }

    /// <summary>현재 차단된 UserTag TagAddress 집합(대소문자 무시). 소스 차단·라이브 큐 필터 공용.</summary>
    private HashSet<string> GetBlockedAddresses()
    {
        try
        {
            var list = _appSettings.LoadSettings().AbnormalAlarm.UserTagFilters;
            return list is { Count: > 0 }
                ? new HashSet<string>(list.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>최신순 메모리 큐 — 빠른 UI 푸시용. 장기 조회는 Repository 사용. 차단된 UserTag 는 읽기 시 제외(해제하면 다시 표시).</summary>
    public IReadOnlyList<UserTagAlert> GetAlerts(int? maxCount = null)
    {
        var blocked = GetBlockedAddresses();
        lock (_stateLock)
        {
            var list = _alerts.AsEnumerable();
            if (blocked.Count > 0) list = list.Where(a => !blocked.Contains(a.TagAddress));
            var result = list.ToList();
            if (maxCount.HasValue && result.Count > maxCount.Value)
                return result.Take(maxCount.Value).ToList();
            return result;
        }
    }

    /// <summary>현재 조건이 걸려 있는 활성 알람(시각 내림차순). 대시보드/전체화면 배너용 — 조건 풀리면 자동 제외됨. 차단된 UserTag 는 제외.</summary>
    public IReadOnlyList<ActiveUserAlarm> GetActiveAlarms()
    {
        var blocked = GetBlockedAddresses();
        lock (_stateLock)
            return _activeUserAlarms.Values
                .Where(a => blocked.Count == 0 || !blocked.Contains(a.TagAddress))
                .OrderByDescending(a => a.OccurredAt)
                .ToList();
    }

    public void ClearAlerts()
    {
        lock (_stateLock) { _alerts.Clear(); _activeUserAlarms.Clear(); }
        AlertsChanged?.Invoke();
        // 정적 /uptime 페이지(SignalR 구독)에도 즉시 비움 반영 (issue #176).
        _ = BroadcastAlertsChangedAsync(0, CancellationToken.None);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UserTagAlertService starting (poll={Ms}ms, max={Max})",
            PollIntervalMs, MaxAlerts);

        // 최근 알림 히드레이션 — 재시작 후에도 UI 가 비어있지 않도록 DB 최신 N개 부터 큐 시드.
        await HydrateRecentAlertsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RefreshDefinitionsIfChanged();

                if (_definitionsByAddress.Count > 0)
                {
                    await PollOnceAsync(stoppingToken);
                }

                await Task.Delay(PollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UserTagAlert] poll loop error");
                try { await Task.Delay(1500, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("UserTagAlertService stopped");
    }

    private async Task HydrateRecentAlertsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var alertRepo = scope.ServiceProvider.GetRequiredService<IUserTagAlertRepository>();
            var recent = await alertRepo.GetLatestAlertsAsync(MaxAlerts, ct);
            lock (_stateLock)
            {
                _alerts.Clear();
                foreach (var r in recent)
                {
                    _alerts.AddLast(new UserTagAlert(
                        Id: r.Id,
                        Timestamp: r.OccurredAt,
                        SystemName: r.SystemName,
                        Name: r.Name,
                        LogLevel: r.LogLevel,
                        TagAddress: r.TagAddress,
                        ValueType: r.ValueType,
                        Value: r.ActualValue,
                        MatchOp: r.MatchOp,
                        MatchValue: r.MatchValue ?? string.Empty));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UserTagAlert] hydration skipped");
        }
    }

    private void RefreshDefinitionsIfChanged()
    {
        var loadedAt = _projectService.LastLoadedUtc;
        if (loadedAt == _projectLoadedAt && _initialized) return;

        if (!_projectService.IsLoaded)
        {
            lock (_stateLock)
            {
                _definitionsByAddress = new(StringComparer.OrdinalIgnoreCase);
                _definitions = new();
                _projectLoadedAt = loadedAt;
                _initialized = true;
            }
            return;
        }

        var store = _projectService.GetStore();
        var rows = store.GetAllUserTagsForProject();
        var defs = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.TagAddress))
            .Select(r => new UserTagDefinition(
                r.SystemId, r.SystemName, r.Name,
                r.LogLevel ?? "Info", r.TagAddress, r.ValueType ?? "Bit",
                r.MatchOp ?? string.Empty, r.MatchValue ?? string.Empty))
            .ToList();

        // 주소 단독 인덱스 — systemId 를 모르는 로그(레거시 행·귀속 미상)의 폴백.
        // ★TryAdd first-wins 라 두 System 이 같은 주소를 정의하면 한쪽이 조용히 사라진다.
        //   그래서 아래 (System,주소) 인덱스를 정본으로 두고, 이건 폴백 겸 진단용으로만 쓴다.
        var byAddr = new Dictionary<string, UserTagDefinition>(StringComparer.OrdinalIgnoreCase);
        var bySysAddr = new Dictionary<string, UserTagDefinition>(StringComparer.OrdinalIgnoreCase);
        var shadowed = new List<UserTagDefinition>();
        foreach (var d in defs)
        {
            if (!byAddr.TryAdd(d.TagAddress, d)) shadowed.Add(d);
            bySysAddr[UserTagKey(SysKey(d.SystemId), d.TagAddress)] = d;
        }
        if (shadowed.Count > 0)
            _logger.LogWarning(
                "[UserTagAlert] 주소가 겹치는 UserTag 정의 {Count}건 — 로그에 systemId 가 실려 오면 정확히 " +
                "구분되지만, 귀속 미상 로그는 먼저 등록된 정의로만 매칭된다 (예: {Sample})",
                shadowed.Count,
                string.Join(", ", shadowed.Take(3).Select(d => $"{d.SystemName}/{d.TagAddress}")));

        lock (_stateLock)
        {
            _definitions = defs;
            _definitionsByAddress = byAddr;
            _definitionsBySystemAddress = bySysAddr;
            _projectLoadedAt = loadedAt;
            _initialized = true;
        }

        _logger.LogInformation(
            "[UserTagAlert] definitions loaded: {Count} tag(s) across {SysCount} system(s)",
            defs.Count, defs.Select(d => d.SystemName).Distinct().Count());

        // 새로 추가된 UserTag 주소를 plcTag/캐시에 반영.
        // 부재 시 Hub 신호가 plcTagLog 에 기록되지 않아 폴링이 매칭할 행을 찾지 못함.
        _engineService.EnsureUserTagAddressesRegistered();
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();
        var alertRepo = scope.ServiceProvider.GetRequiredService<IUserTagAlertRepository>();

        // 최초 1회: 현재 최대 log ID 부터 — 과거 로그까지 한꺼번에 알림 폭주 방지.
        if (_lastCheckedLogId == 0)
        {
            try
            {
                var (_, maxId) = await plcRepo.GetLatestValuePerTagAsync();
                _lastCheckedLogId = maxId;
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[UserTagAlert] initial maxId fetch failed");
                return;
            }
        }

        var newLogs = await plcRepo.GetLogsAfterIdAsync(_lastCheckedLogId);
        if (newLogs.Count == 0) return;

        _lastCheckedLogId = newLogs.Max(l => l.Id);

        Dictionary<string, UserTagDefinition> defsSnap, defsBySysSnap;
        lock (_stateLock) { defsSnap = _definitionsByAddress; defsBySysSnap = _definitionsBySystemAddress; }

        // 사용자정의 알람 차단 — 차단된 UserTag 는 이번 폴링에서 아예 발화시키지 않는다(라이브 큐/DB/SignalR 미기록).
        // 디바이스 차단(AbnormalEventService.ProcessAsync skip)의 UserTag 대응물. 해제하면 이후 발생분부터 다시 기록된다.
        var blockedSnap = GetBlockedAddresses();

        // 진단 — UserTag 정의된 주소가 newLogs 에 몇 건 들어왔는지 / fire 결정 추적용.
        // 정의된 주소가 plcTagLog 에 전혀 안 들어오면(A 케이스) 본 카운터가 항상 0.
        var matchedCount = 0;
        var firedCount = 0;

        var newAlerts = new List<UserTagAlertRecord>();
        var newUiAlerts = new List<UserTagAlert>();

        foreach (var log in newLogs)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(log.Address)) continue;
            // 멀티 PLC: 로그에 실려온 System 으로 정확히 매칭하고, 귀속 미상(구버전 행)만 주소 폴백.
            // 주소만으로 매칭하면 두 System 이 같은 주소를 정의했을 때 한쪽 정의가 통째로 죽는다.
            var logSysKey = SysKey(log.SystemId);
            if (!defsBySysSnap.TryGetValue(UserTagKey(logSysKey, log.Address), out var def)
                && !defsSnap.TryGetValue(log.Address, out def)) continue;
            var stateKey = UserTagKey(logSysKey, def.TagAddress);

            matchedCount++;

            // 차단된 UserTag 는 소스에서 완전 배제(기록·활성 알람·SignalR 생략).
            if (blockedSnap.Count > 0 && blockedSnap.Contains(def.TagAddress)) continue;

            var newValue = log.Value ?? string.Empty;

            // 직전 값 lookup — 첫 샘플이면 prev = None.
            string? prevValue;
            lock (_stateLock)
            {
                _lastValueByAddress.TryGetValue(stateKey, out var p);
                prevValue = p; // null 이면 첫 샘플 (== F# 의 None 와 동치)
                _lastValueByAddress[stateKey] = newValue;
            }

            // F# 매칭 평가 — ValueType / MatchOp / MatchValue 에 따라 fire 여부 결정.
            var vt = LoggingHelpers.UserTagHelpers.parseValueType(def.ValueType);
            var op = LoggingHelpers.UserTagHelpers.parseMatchOp(
                string.IsNullOrWhiteSpace(def.MatchOp)
                    ? LoggingHelpers.UserTagHelpers.matchOpToString(LoggingHelpers.UserTagHelpers.defaultMatchOpFor(vt))
                    : def.MatchOp);

            var prevOpt = prevValue is null
                ? Microsoft.FSharp.Core.FSharpOption<string>.None
                : Microsoft.FSharp.Core.FSharpOption<string>.Some(prevValue);

            var fire = LoggingHelpers.UserTagHelpers.shouldFire(
                vt, op, def.MatchValue ?? string.Empty, prevOpt, newValue);

            _logger.LogInformation(
                "[UserTagAlert] sample {Addr} prev={Prev} new={New} op={Op} → fire={Fire}",
                log.Address, prevValue ?? "<none>", newValue,
                LoggingHelpers.UserTagHelpers.matchOpToString(op), fire);

            if (!fire) continue;
            firedCount++;

            var record = new UserTagAlertRecord(
                Id: 0,
                OccurredAt: log.DateTime,
                SystemId: def.SystemId,
                SystemName: def.SystemName,
                Name: def.Name,
                LogLevel: def.LogLevel,
                TagAddress: def.TagAddress,
                ValueType: def.ValueType,
                MatchOp: LoggingHelpers.UserTagHelpers.matchOpToString(op),
                MatchValue: def.MatchValue,
                ActualValue: newValue,
                SourceLogId: log.Id);
            newAlerts.Add(record);

            // 라이브 활성 알람 등록/갱신 — 이 주소의 조건이 지금 걸렸다(배너 표시 대상).
            // Changed 류는 정상상태가 없어 직후 해소 패스에서 곧 제거됨(배너 비표시).
            var active = new ActiveUserAlarm(
                OccurredAt: log.DateTime,
                SystemName: def.SystemName,
                Name: def.Name,
                LogLevel: def.LogLevel,
                TagAddress: def.TagAddress,
                ValueType: def.ValueType,
                MatchOp: LoggingHelpers.UserTagHelpers.matchOpToString(op),
                MatchValue: def.MatchValue ?? string.Empty,
                Value: newValue);
            lock (_stateLock) _activeUserAlarms[stateKey] = active;
        }

        // DB INSERT — 한 건씩 (트랜잭션 누적은 단순화. 폴링 1회 분량은 보통 ≤ 수십건).
        foreach (var rec in newAlerts)
        {
            try
            {
                var insertedId = await alertRepo.InsertAlertAsync(rec, ct);
                newUiAlerts.Add(new UserTagAlert(
                    Id: insertedId,
                    Timestamp: rec.OccurredAt,
                    SystemName: rec.SystemName,
                    Name: rec.Name,
                    LogLevel: rec.LogLevel,
                    TagAddress: rec.TagAddress,
                    ValueType: rec.ValueType,
                    Value: rec.ActualValue,
                    MatchOp: rec.MatchOp,
                    MatchValue: rec.MatchValue ?? string.Empty));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[UserTagAlert] DB insert failed for {Name}/{Addr}", rec.Name, rec.TagAddress);
            }
        }

        if (newUiAlerts.Count > 0)
        {
            lock (_stateLock)
            {
                foreach (var a in newUiAlerts)
                {
                    _alerts.AddFirst(a);
                }
                while (_alerts.Count > MaxAlerts) _alerts.RemoveLast();
            }
            AlertsChanged?.Invoke();
        }

        // ── 활성 알람 조건 해소 — 현재 값이 더 이상 매칭 조건을 만족하지 않으면 배너 표시 목록에서 제거 ──
        // 값이 정상으로 돌아오면 그 주소의 plcTagLog 행이 들어와 _lastValueByAddress 가 갱신되므로 여기서 잡힌다.
        var resolvedCount = 0;
        List<string> clearedAddrs = [];
        lock (_stateLock)
        {
            if (_activeUserAlarms.Count > 0)
            {
                var toRemove = new List<string>();
                foreach (var (addr, a) in _activeUserAlarms)
                {
                    if (!_lastValueByAddress.TryGetValue(addr, out var cur)) continue;
                    var vt = LoggingHelpers.UserTagHelpers.parseValueType(a.ValueType);
                    var op = LoggingHelpers.UserTagHelpers.parseMatchOp(a.MatchOp);
                    if (!LoggingHelpers.UserTagHelpers.isConditionActive(vt, op, a.MatchValue ?? string.Empty, cur))
                        toRemove.Add(addr);
                }
                foreach (var addr in toRemove) _activeUserAlarms.Remove(addr);
                resolvedCount = toRemove.Count;
                clearedAddrs = toRemove;
            }
        }

        // 해소 시각을 DB 에 남긴다 — 정지 분류(doc/25)가 "미해소 usertag = 라인 고장"을 과거 기간
        // 조회에서도 재현하려면 이 값이 유일한 근거다(발생 시점만으론 실시간 외엔 알 수 없다).
        if (clearedAddrs.Count > 0)
        {
            try { await alertRepo.MarkClearedAsync(clearedAddrs, DateTime.UtcNow, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "[UserTagAlert] 해소 시각 기록 스킵"); }
        }

        // 신규 발화 또는 활성 알람 해소가 있으면 클라이언트에 통지(배너/uptime 재조회 트리거).
        // 정적 /uptime 페이지(SignalR 구독)에 신규 UserTag 알림을 실시간 통지.
        // 상단바 배지는 /api/nav/summary 4초 폴링으로 갱신되지만, 탭의 집계
        // (총알림·시계열 추이)는 SignalR 신호가 없으면 10초 폴링까지 지연됨 (issue #176).
        if (newUiAlerts.Count > 0 || resolvedCount > 0)
            await BroadcastAlertsChangedAsync(newUiAlerts.Count, ct);

        // 진단 — newLogs 중 정의된 주소 행이 0 이면 plcTagLog INSERT 가 안 일어남 (A 케이스).
        if (newLogs.Count > 0)
        {
            _logger.LogInformation(
                "[UserTagAlert] poll: logs={Logs}, matchedDefinedAddr={Matched}, fired={Fired}, defs={Defs}",
                newLogs.Count, matchedCount, firedCount, defsSnap.Count);
        }
    }

    /// <summary>
    /// UserTag 알림 변경을 SignalR("UserTagAlertsChanged")로 전체 클라이언트에 통지.
    /// /uptime 페이지가 이 신호를 받아 스냅샷(/api/user-tags/snapshot)을 즉시 재조회 →
    /// 총알림·시계열 추이가 상단바 배지와 동일하게 실시간 갱신됨.
    /// 통지 실패는 폴링이 보완하므로 fail-safe 로 로그만 남긴다.
    /// </summary>
    private async Task BroadcastAlertsChangedAsync(int count, CancellationToken ct)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("UserTagAlertsChanged", new { count }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UserTagAlert] SignalR broadcast failed (count={Count})", count);
        }
    }
}

/// <summary>UserTag 정의 한 행 (UI/Service 공용).</summary>
public sealed record UserTagDefinition(
    Guid SystemId,
    string SystemName,
    string Name,
    string LogLevel,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string MatchValue);

/// <summary>
/// 라이브 활성 UserTag 알람 한 건 (대시보드/전체화면 배너용).
/// 조건이 걸려 있는 동안만 보유 — 현재 값이 매칭 조건을 더 이상 만족하지 않으면 제거된다.
/// 해소 재평가를 위해 ValueType/MatchOp/MatchValue 를 함께 보관(폴링마다 isConditionActive 로 판정).
/// </summary>
public sealed record ActiveUserAlarm(
    DateTime OccurredAt,
    string SystemName,
    string Name,
    string LogLevel,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string MatchValue,
    string Value);

/// <summary>UserTag 매칭 결과 알림 한 건 (UI 표시용).</summary>
public sealed record UserTagAlert(
    long Id,
    DateTime Timestamp,
    string SystemName,
    string Name,
    string LogLevel,
    string TagAddress,
    string ValueType,
    string Value,
    string MatchOp,
    string MatchValue);
