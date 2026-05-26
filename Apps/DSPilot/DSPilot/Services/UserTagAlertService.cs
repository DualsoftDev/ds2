using System.Collections.Concurrent;
using Ds2.Core;
using Ds2.Editor;
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
    private readonly ILogger<UserTagAlertService> _logger;

    private readonly object _stateLock = new();
    private Dictionary<string, UserTagDefinition> _definitionsByAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private List<UserTagDefinition> _definitions = new();
    private DateTime? _projectLoadedAt;

    // 주소별 직전 값 — edge / 임계치 전이 평가에 필요.
    private readonly Dictionary<string, string> _lastValueByAddress =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly LinkedList<UserTagAlert> _alerts = new();
    private long _lastCheckedLogId;
    private bool _initialized;

    public event Action? AlertsChanged;

    public UserTagAlertService(
        DsProjectService projectService,
        IServiceScopeFactory scopeFactory,
        SimulationEngineService engineService,
        ILogger<UserTagAlertService> logger)
    {
        _projectService = projectService;
        _scopeFactory = scopeFactory;
        _engineService = engineService;
        _logger = logger;
    }

    public IReadOnlyList<UserTagDefinition> GetDefinitions()
    {
        lock (_stateLock) return _definitions.ToList();
    }

    /// <summary>최신순 메모리 큐 — 빠른 UI 푸시용. 장기 조회는 Repository 사용.</summary>
    public IReadOnlyList<UserTagAlert> GetAlerts(int? maxCount = null)
    {
        lock (_stateLock)
        {
            var list = _alerts.ToList();
            if (maxCount.HasValue && list.Count > maxCount.Value)
                return list.Take(maxCount.Value).ToList();
            return list;
        }
    }

    public void ClearAlerts()
    {
        lock (_stateLock) _alerts.Clear();
        AlertsChanged?.Invoke();
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

        var byAddr = new Dictionary<string, UserTagDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs)
        {
            byAddr.TryAdd(d.TagAddress, d);
        }

        lock (_stateLock)
        {
            _definitions = defs;
            _definitionsByAddress = byAddr;
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

        Dictionary<string, UserTagDefinition> defsSnap;
        lock (_stateLock) defsSnap = _definitionsByAddress;

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
            if (!defsSnap.TryGetValue(log.Address, out var def)) continue;

            matchedCount++;

            var newValue = log.Value ?? string.Empty;

            // 직전 값 lookup — 첫 샘플이면 prev = None.
            string? prevValue;
            lock (_stateLock)
            {
                _lastValueByAddress.TryGetValue(def.TagAddress, out var p);
                prevValue = p; // null 이면 첫 샘플 (== F# 의 None 와 동치)
                _lastValueByAddress[def.TagAddress] = newValue;
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

        // 진단 — newLogs 중 정의된 주소 행이 0 이면 plcTagLog INSERT 가 안 일어남 (A 케이스).
        if (newLogs.Count > 0)
        {
            _logger.LogInformation(
                "[UserTagAlert] poll: logs={Logs}, matchedDefinedAddr={Matched}, fired={Fired}, defs={Defs}",
                newLogs.Count, matchedCount, firedCount, defsSnap.Count);
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
