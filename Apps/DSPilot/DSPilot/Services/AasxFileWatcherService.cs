using DSPilot.Hubs;
using DSPilot.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace DSPilot.Services;

/// <summary>
/// 공유 AASX 파일(<see cref="SharedPaths.AasxFilePath"/>) 의 변경을 감시.
///
/// 정책:
/// - mtime 만 보면 Promaker 가 모니터링 Start 마다 동일 모델을 재 export(zip 타임스탬프 차이) 할 때 오탐 발생.
///   → 콘텐츠 SHA256 으로만 변경 여부 판정.
/// - DSPilot 이 아직 AASX 를 한 번도 로드한 적이 없는 경우(초기 설치 직후) 자동으로 DB 재구축.
/// - 콘텐츠가 변경된 경우 (Promaker "agent 보내기" 등) 자동으로 <see cref="DatabaseLifecycleService.ReloadAndResyncAsync"/>
///   → in-memory 재로딩 + dspFlow/dspCall UPSERT + 사라진 Flow 정리 + Layout 자동 재배치.
///   살아남은 Flow 의 통계 / 히스토리는 보존되며, 전체 초기화는 사용자가 Settings 에서 수동.
/// </summary>
public sealed class AasxFileWatcherService : BackgroundService
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(1);

    private readonly DsProjectService _projectService;
    private readonly IServiceProvider _services;
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly ILogger<AasxFileWatcherService> _logger;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private readonly object _debounceLock = new();

    public AasxFileWatcherService(
        DsProjectService projectService,
        IServiceProvider services,
        IHubContext<MonitoringHub> hubContext,
        ILogger<AasxFileWatcherService> logger)
    {
        _projectService = projectService;
        _services = services;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dir = SharedPaths.SharedDirectory;
        var fileName = Path.GetFileName(SharedPaths.AasxFilePath);

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AasxWatcher] 공유 디렉토리 생성 실패: {Dir}", dir);
        }

        try
        {
            _watcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnFileEvent;
            _watcher.Changed += OnFileEvent;
            _watcher.Renamed += OnRenamed;

            _logger.LogInformation("[AasxWatcher] 감시 시작: {Path}", SharedPaths.AasxFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AasxWatcher] FileSystemWatcher 시작 실패: {Dir}", dir);
            return Task.CompletedTask;
        }

        // 서비스 시작 시점에 1회 체크 — 시작 전 외부에서 파일이 떨어졌거나
        // DSPilot 이 아직 로드하지 못한 상태(초기 설치)면 자동 처리.
        ScheduleCheck(stoppingToken);

        return Task.CompletedTask;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleCheck(CancellationToken.None);
    private void OnRenamed(object sender, RenamedEventArgs e) => ScheduleCheck(CancellationToken.None);

    private void ScheduleCheck(CancellationToken externalToken)
    {
        CancellationTokenSource cts;
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            cts = _debounceCts;
        }

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceWindow, token);
                if (token.IsCancellationRequested) return;
                await CheckAsync();
            }
            catch (TaskCanceledException) { /* expected on debounce */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AasxWatcher] 체크 중 오류");
            }
        }, externalToken);
    }

    private async Task CheckAsync()
    {
        var path = _projectService.AasxFilePath;
        if (!File.Exists(path))
        {
            _logger.LogDebug("[AasxWatcher] 파일이 없음 — skip ({Path})", path);
            return;
        }

        var currentHash = _projectService.GetAasxFileSha256();
        if (currentHash is null)
        {
            _logger.LogDebug("[AasxWatcher] 해시 계산 실패 (잠금 가능성) — skip");
            return;
        }

        // 케이스 1: DSPilot 이 AASX 를 한 번도 로드한 적 없음
        //   → 초기 설치/배포 직후 처음 AASX 가 떨어진 상황. 통계/히스토리가 아직 없으므로 자동 로드 + DB 재구축이 안전.
        if (!_projectService.IsLoaded)
        {
            _logger.LogInformation("[AasxWatcher] 미로드 상태에서 AASX 감지 → 자동 로드 + DB 재구축 시작 ({Path})", path);
            try
            {
                var lifecycle = _services.GetRequiredService<DatabaseLifecycleService>();
                var reload = await lifecycle.ReloadAasxAsync();
                if (!reload.Success)
                {
                    _logger.LogWarning("[AasxWatcher] 자동 AASX 로드 실패: {Message}", reload.Message);
                    return;
                }
                var result = await lifecycle.RebuildDatabaseAsync();
                _logger.LogInformation("[AasxWatcher] 자동 재구축 결과: {Success} / {Message}", result.Success, result.Message);

                if (result.Success)
                {
                    TryAutoFillBlueprint();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AasxWatcher] 자동 재구축 실패");
            }
            return;
        }

        // 케이스 2: 콘텐츠 동일 — Promaker 가 동일 모델 재 export 한 경우 (zip 타임스탬프만 다름). 무시.
        var lastHash = _projectService.LastLoadedSha256;
        if (lastHash is not null && string.Equals(lastHash, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("[AasxWatcher] 콘텐츠 동일 (sha256 일치) — 알림 skip");
            return;
        }

        // 케이스 3: 콘텐츠 변경 — 자동 ReloadAndResync.
        //   in-memory 재로딩 + UPSERT + 사라진 Flow 정리 + Layout 자동 재배치 (이전 layout 은 백업).
        //   살아남은 Flow 의 통계 / 히스토리는 보존. 전체 초기화는 사용자가 Settings 의 "DB 재구축" 으로 수동.
        _logger.LogInformation("[AasxWatcher] 외부 변경 감지 (sha256 변경): {Old} → {New}", lastHash ?? "<none>", currentHash);

        try
        {
            var lifecycle = _services.GetRequiredService<DatabaseLifecycleService>();
            var result = await lifecycle.ReloadAndResyncAsync();
            if (result.Success)
                _logger.LogInformation("[AasxWatcher] 자동 ReloadAndResync 완료: {Message}", result.Message);
            else
                _logger.LogWarning("[AasxWatcher] 자동 ReloadAndResync 실패: {Message}", result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AasxWatcher] 자동 ReloadAndResync 예외");
        }

        try
        {
            _projectService.RaiseAasxExternallyChanged();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AasxWatcher] 로컬 이벤트 발행 실패 (비중요)");
        }

        try
        {
            await _hubContext.Clients.All.SendAsync("AasxFileChanged", new
            {
                Path = path,
                Sha256 = currentHash,
                ChangedAtUtc = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AasxWatcher] SignalR 브로드캐스트 실패 (비중요)");
        }
    }

    /// <summary>
    /// 초기 설치 후 처음 AASX 가 로드된 직후, Blueprint 에 아직 배치가 없으면
    /// Flow 들을 격자에 자동으로 채워 대시보드 첫 화면을 비어 있지 않게 만든다.
    /// 이미 사용자가 배치를 가지고 있다면(레이아웃 JSON 보존) 손대지 않는다.
    /// </summary>
    private void TryAutoFillBlueprint()
    {
        try
        {
            var blueprint = _services.GetRequiredService<BlueprintService>();
            if (blueprint.Layout.FlowPlacements.Count > 0)
            {
                _logger.LogDebug("[AasxWatcher] Blueprint 자동 채움 skip — 이미 배치 {N}개 존재", blueprint.Layout.FlowPlacements.Count);
                return;
            }

            var systems = _projectService.GetActiveSystems();
            var flows = systems.SelectMany(sys =>
                _projectService.GetFlows(sys.Id)
                    .Select(f => (FlowId: f.Id, FlowName: f.Name, SystemName: sys.Name, SystemId: sys.Id)))
                .ToList();

            if (flows.Count == 0)
            {
                _logger.LogDebug("[AasxWatcher] Blueprint 자동 채움 skip — Flow 없음");
                return;
            }

            blueprint.AutoFillPlacements(flows);
            blueprint.SaveLayout();
            _logger.LogInformation("[AasxWatcher] Blueprint 자동 채움 완료 — Flow {N}개 → {C}×{R} 격자",
                flows.Count, blueprint.Layout.GridColumns, blueprint.Layout.GridRows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AasxWatcher] Blueprint 자동 채움 실패 (비중요)");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileEvent;
                _watcher.Changed -= OnFileEvent;
                _watcher.Renamed -= OnRenamed;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch { /* best effort */ }

        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        await base.StopAsync(cancellationToken);
    }
}
