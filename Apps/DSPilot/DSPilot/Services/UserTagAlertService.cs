using System.Collections.Concurrent;
using Ds2.Core;
using Ds2.Editor;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// 프로젝트에 정의된 UserTag(LoggingSystemProperties.UserTags)들을 모니터링.
/// plcTagLog 신규 행을 폴링하여 UserTag 주소와 일치하는 신호를 잡아 알림 큐에 적재.
/// UI(/user-tags 페이지) 가 이벤트 + 스냅샷으로 표시.
///
/// 처리 흐름: HubSubscriber → SimulationEngine → PlcTagLogWriter (plcTagLog INSERT)
///           → UserTagAlertService 가 GetLogsAfterIdAsync 로 신규 행 조회 → 매칭 → 알림.
/// PlcDatabaseMonitorService 와 같은 폴링 패턴이지만, Call 매핑이 아닌 UserTag 정의에 매칭.
/// </summary>
public sealed class UserTagAlertService : BackgroundService
{
    private const int PollIntervalMs = 750;
    private const int MaxAlerts = 500;

    private readonly DsProjectService _projectService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserTagAlertService> _logger;

    private readonly object _stateLock = new();
    private Dictionary<string, UserTagDefinition> _definitionsByAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private List<UserTagDefinition> _definitions = new();
    private DateTime? _projectLoadedAt;
    private readonly LinkedList<UserTagAlert> _alerts = new();
    private long _lastCheckedLogId;
    private bool _initialized;

    public event Action? AlertsChanged;

    public UserTagAlertService(
        DsProjectService projectService,
        IServiceScopeFactory scopeFactory,
        ILogger<UserTagAlertService> logger)
    {
        _projectService = projectService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public IReadOnlyList<UserTagDefinition> GetDefinitions()
    {
        lock (_stateLock) return _definitions.ToList();
    }

    /// <summary>최신순으로 알림 목록 반환. 필요 시 호출자가 추가 필터링.</summary>
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

    /// <summary>
    /// 프로젝트 로딩 시각이 바뀌었을 때만 UserTag 정의를 재구성.
    /// AASX 가 재로딩되면 LastLoadedUtc 가 갱신되므로 그것으로 캐시 무효화.
    /// </summary>
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
                r.LogLevel ?? "Info", r.TagAddress, r.ValueType ?? "Bit"))
            .ToList();

        var byAddr = new Dictionary<string, UserTagDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs)
        {
            // 동일 주소에 여러 UserTag 가 매핑되면 첫 항목 우선 — Promaker UI 도 보통 1:1.
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
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var plcRepo = scope.ServiceProvider.GetRequiredService<IPlcRepository>();

        // 최초 1회: 현재 최대 log ID 부터 시작 — 과거 로그까지 한꺼번에 알림 폭주 방지.
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

        // 스냅샷 캡처 — 룩업은 lock 밖에서.
        Dictionary<string, UserTagDefinition> defsSnap;
        lock (_stateLock) defsSnap = _definitionsByAddress;

        bool any = false;
        foreach (var log in newLogs)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(log.Address)) continue;
            if (!defsSnap.TryGetValue(log.Address, out var def)) continue;

            // Bit 타입은 0→1 rising edge 만 알림으로 — 0 으로 떨어지는 신호는 무시.
            // 비-Bit 타입은 값이 바뀔 때마다 기록.
            var value = log.Value ?? string.Empty;
            if (string.Equals(def.ValueType, "Bit", StringComparison.OrdinalIgnoreCase))
            {
                var normalized = NormalizeBool(value);
                if (normalized != "1") continue;
            }

            var alert = new UserTagAlert(
                Id: log.Id,
                Timestamp: log.DateTime,
                SystemName: def.SystemName,
                Name: def.Name,
                LogLevel: def.LogLevel,
                TagAddress: def.TagAddress,
                ValueType: def.ValueType,
                Value: value);

            lock (_stateLock)
            {
                _alerts.AddFirst(alert);
                while (_alerts.Count > MaxAlerts) _alerts.RemoveLast();
            }
            any = true;
        }

        if (any) AlertsChanged?.Invoke();
    }

    private static string NormalizeBool(string v)
    {
        if (string.IsNullOrEmpty(v)) return "0";
        var l = v.Trim().ToLowerInvariant();
        return (l == "1" || l == "true") ? "1" : "0";
    }
}

/// <summary>UI 가 사용하는 UserTag 정의 한 행.</summary>
public sealed record UserTagDefinition(
    Guid SystemId,
    string SystemName,
    string Name,
    string LogLevel,
    string TagAddress,
    string ValueType);

/// <summary>UserTag 모니터링 결과 알림 한 건.</summary>
public sealed record UserTagAlert(
    long Id,
    DateTime Timestamp,
    string SystemName,
    string Name,
    string LogLevel,
    string TagAddress,
    string ValueType,
    string Value);
