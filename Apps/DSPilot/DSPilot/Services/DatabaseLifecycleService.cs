using DSPilot.Adapters;
using DSPilot.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DSPilot.Services;

/// <summary>
/// plc.db 의 라이프사이클(삭제 + 재로딩 + 재초기화) 을 한 메서드로 묶는다.
/// Settings 페이지에서 호출하면 서버 재시작 없이 in-place 로 모든 상태가 fresh 가 된다.
/// </summary>
public sealed class DatabaseLifecycleService
{
    private readonly SimulationEngineService _engineService;
    private readonly DspDbService _dspDbService;
    private readonly DspDatabaseServiceAdapter _bootstrap;
    private readonly DspRepositoryAdapter _dspRepository;
    private readonly DsProjectService _projectService;
    private readonly AppSettingsService _settingsService;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly ILogger<DatabaseLifecycleService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DatabaseLifecycleService(
        SimulationEngineService engineService,
        DspDbService dspDbService,
        DspDatabaseServiceAdapter bootstrap,
        DspRepositoryAdapter dspRepository,
        DsProjectService projectService,
        AppSettingsService settingsService,
        IDatabasePathResolver pathResolver,
        IHubContext<MonitoringHub> hubContext,
        ILogger<DatabaseLifecycleService> logger)
    {
        _engineService = engineService;
        _dspDbService = dspDbService;
        _bootstrap = bootstrap;
        _dspRepository = dspRepository;
        _projectService = projectService;
        _settingsService = settingsService;
        _pathResolver = pathResolver;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// plc.db 전체 삭제 + 스키마 재생성 + AASX 재적재 + 엔진 재시작.
    /// 서버 재시작 불필요.
    /// </summary>
    public async Task<RebuildResult> RebuildDatabaseAsync()
    {
        if (!await _gate.WaitAsync(0))
            return new RebuildResult(false, "다른 재초기화 작업이 진행 중입니다.");

        try
        {
            _logger.LogInformation("[DBLifecycle] Rebuild starting...");

            // 1. 엔진 teardown — DB 핸들 / 컨슈머 / 캐시 모두 해제
            await _engineService.ResetAsync();

            // 2. UI 스냅샷 클리어 — DspDbService 가 stale 값(GoingCount 등) 보호 로직으로 새 fresh 데이터 무시 못하게
            _dspDbService.Reset();

            // 3. plc.db 파일 삭제 (connection pool clear 포함)
            var dbPath = _pathResolver.GetSharedDbPath();
            _settingsService.DeleteDatabase(dbPath);

            // 4. AASX 디스크 재로딩 — Promaker 가 갱신한 모델을 in-memory DsStore 에 반영
            if (File.Exists(_projectService.AasxFilePath))
            {
                _projectService.LoadProject(_projectService.AasxFilePath);
            }
            else
            {
                _logger.LogWarning("[DBLifecycle] AASX 파일이 없습니다: {Path}", _projectService.AasxFilePath);
            }

            // 5. 스키마 + AASX → dspFlow/dspCall 재적재
            var ok = await _bootstrap.BootstrapAsync();
            if (!ok)
            {
                _logger.LogWarning("[DBLifecycle] Bootstrap failed after delete");
                return new RebuildResult(false, "AASX 재로딩 실패 — 로그 확인");
            }

            // 6. 엔진 재시작 — 첫 Hub 신호 도착 시 자동 init 됨 (lazy) 또는 즉시 init
            _engineService.TryEnsureInitialized();

            // 7. 새 DB 로딩 완료 시점에 OnDataChanged 한 번 더 발화 — step 2 의 Reset 은 DB
            // 삭제 직전이라 그 시점에 페이지가 reload 해도 빈 결과. BootstrapAsync 가 끝나고
            // 새 dspFlow / dspCall 이 채워진 지금 발화해야 Heatmap / Dashboard 등이 fresh 데이터로
            // 자동 재구성된다. (CycleTimeAnalysis 도 OnDataChanged 구독 — 동일 경로)
            _dspDbService.Reset();

            // 8. 모든 클라이언트에 알림 (UI 페이지가 새로고침할 수 있도록)
            try
            {
                await _hubContext.Clients.All.SendAsync("DatabaseRebuilt");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DBLifecycle] SignalR broadcast failed (non-critical)");
            }

            _logger.LogInformation("[DBLifecycle] Rebuild complete");
            return new RebuildResult(true, "데이터베이스가 초기화되고 재로딩되었습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DBLifecycle] Rebuild failed");
            return new RebuildResult(false, $"재초기화 실패: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// dspFlowHistory + dspCall 통계 컬럼만 reset.
    /// 사용자 의도: "Flow 히스토리 + 사이클 통계 처음부터 다시 측정".
    /// AASX 재로딩 / 엔진 재시작 없이 즉시 적용.
    /// </summary>
    public async Task<RebuildResult> ClearFlowHistoryAsync()
    {
        if (!await _gate.WaitAsync(0))
            return new RebuildResult(false, "다른 재초기화 작업이 진행 중입니다.");

        try
        {
            _logger.LogInformation("[DBLifecycle] ClearFlowHistory starting...");

            // 1. dspFlowHistory 행 삭제
            var deleted = await _dspRepository.ClearFlowHistoryAsync();

            // 2. dspCall 통계 컬럼 reset (PrevGoingTime/AvgGoingTime/StdDev/GoingCount → NULL/0)
            await _dspRepository.ResetCallStatisticsAsync();

            // 3. 엔진 in-memory 통계도 reset (Welford accumulator)
            _engineService.ResetCallStats();

            // 4. UI 스냅샷 클리어
            _dspDbService.Reset();

            try
            {
                await _hubContext.Clients.All.SendAsync("FlowHistoryCleared");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DBLifecycle] SignalR broadcast failed (non-critical)");
            }

            _logger.LogInformation("[DBLifecycle] ClearFlowHistory complete (deleted {Count} rows)", deleted);
            return new RebuildResult(true, $"Flow 히스토리 {deleted}건 + Call 통계가 초기화되었습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DBLifecycle] ClearFlowHistory failed");
            return new RebuildResult(false, $"히스토리 초기화 실패: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed record RebuildResult(bool Success, string Message);
