using DSPilot.Adapters;
using DSPilot.Hubs;
using DSPilot.Repositories;
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
    private readonly BlueprintService _blueprint;
    private readonly IFlowMetricsService _flowMetricsService;
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
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
        BlueprintService blueprint,
        IFlowMetricsService flowMetricsService,
        IHubContext<MonitoringHub> hubContext,
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseLifecycleService> logger)
    {
        _engineService = engineService;
        _dspDbService = dspDbService;
        _bootstrap = bootstrap;
        _dspRepository = dspRepository;
        _projectService = projectService;
        _settingsService = settingsService;
        _pathResolver = pathResolver;
        _blueprint = blueprint;
        _flowMetricsService = flowMetricsService;
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// raw 데이터(plcTagLog / plcTag / userTagAlertLog / dspFlowHistory) 는 보존하고
    /// derived/캐시(dspFlow.MT/WT/CT/Avg*, dspCall 누적 통계, in-memory Welford 누적기) 만 reset.
    /// head/tail boundary 변경, 모델 큰 변경 후 평균을 새 baseline 으로 다시 누적하고 싶을 때 사용.
    /// 새 사이클 완료 시점부터 자연스럽게 누적 시작.
    /// </summary>
    public async Task<RebuildResult> InvalidateCachesAsync()
    {
        if (!await _gate.WaitAsync(0))
            return new RebuildResult(false, "다른 재초기화 작업이 진행 중입니다.");

        try
        {
            _logger.LogInformation("[DBLifecycle] InvalidateCaches starting (raw 보존)...");

            // 1. dspFlow / dspCall 누적 통계 컬럼 reset (plcTagLog / dspFlowHistory 는 손대지 않음)
            var (flowsReset, callsReset) = await _dspRepository.InvalidateRunningStatsAsync();

            // 2. 엔진 in-memory 통계 (Call Welford 누적기) reset
            _engineService.ResetCallStats();

            // 3. (옵션 A) history 박제된 boundary 컬럼으로 Avg* 즉시 재집계 — 기다리지 않고 평균 복원
            var (flowsRecomputed, historyRowsUsed) = (0, 0);
            try
            {
                (flowsRecomputed, historyRowsUsed) = await _dspRepository.RecomputeAveragesFromCurrentBoundaryAsync(_settingsService.GetCycleAverageWindow());
                // in-memory Welford 누적기도 같은 boundary 의 history 로 재시드 → 다음 사이클이 정합 유지
                await _flowMetricsService.ReseedCycleStatesFromCurrentBoundaryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DBLifecycle] history 기반 평균 재집계 실패 (NULL 유지, 새 사이클부터 누적)");
            }

            // 4. UI 스냅샷 클리어 → 모든 페이지가 새 상태로 재구성
            _dspDbService.Reset();

            try { await _hubContext.Clients.All.SendAsync("DatabaseRebuilt"); }
            catch (Exception ex) { _logger.LogDebug(ex, "[DBLifecycle] SignalR broadcast failed (non-critical)"); }

            _logger.LogInformation(
                "[DBLifecycle] InvalidateCaches complete — reset(Flow {Reset}, Call {CallReset}), recompute(Flow {Recomp}, history {Rows})",
                flowsReset, callsReset, flowsRecomputed, historyRowsUsed);

            var msg = historyRowsUsed > 0
                ? $"캐시 초기화 완료 (Flow {flowsReset}, Call {callsReset}). " +
                  $"현재 boundary 의 history {historyRowsUsed}건으로 평균 즉시 복원 ({flowsRecomputed} Flow). " +
                  "plcTagLog / dspFlowHistory 는 보존."
                : $"캐시 초기화 완료 (Flow {flowsReset}, Call {callsReset}). " +
                  "현재 boundary 의 history 없음 — 다음 사이클부터 새 평균 누적. plcTagLog / dspFlowHistory 는 보존.";
            return new RebuildResult(true, msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DBLifecycle] InvalidateCaches failed");
            return new RebuildResult(false, $"캐시 초기화 실패: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// plc.db 전체 삭제 + 스키마 재생성 + 현재 in-memory AASX 로부터 dspFlow/dspCall 재적재 + 엔진 재시작.
    /// AASX 파일을 디스크에서 다시 읽지 않는다 — 필요하면 <see cref="ReloadAasxAsync"/> 를 먼저 호출할 것.
    /// 서버 재시작 불필요.
    /// 주의: plcTagLog (GB 단위 가능) 와 dspFlowHistory 가 모두 삭제됨. 일반 사용자는
    /// <see cref="InvalidateCachesAsync"/> 를 사용해야 함.
    /// </summary>
    /// <param name="auditSource">audit log 의 source 값. 기본 "Settings.Rebuild" (사용자 액션). 첫 부팅 자동 호출이면 "Initial".</param>
    public async Task<RebuildResult> RebuildDatabaseAsync(string auditSource = "Settings.Rebuild")
    {
        if (!await _gate.WaitAsync(0))
            return new RebuildResult(false, "다른 재초기화 작업이 진행 중입니다.");

        try
        {
            _logger.LogInformation("[DBLifecycle] Rebuild starting (source={Source})...", auditSource);

            if (!_projectService.IsLoaded)
            {
                _logger.LogWarning("[DBLifecycle] AASX 가 로드되지 않은 상태에서 DB 재구축 시도");
                return new RebuildResult(false, "AASX 모델이 로드되지 않았습니다. 먼저 \"AASX 모델 다시 불러오기\" 를 실행하세요.");
            }

            // 1. 엔진 teardown — DB 핸들 / 컨슈머 / 캐시 모두 해제
            await _engineService.ResetAsync();

            // 2. UI 스냅샷 클리어 — DspDbService 가 stale 값(GoingCount 등) 보호 로직으로 새 fresh 데이터 무시 못하게
            _dspDbService.Reset();

            // 3. plc.db 파일 삭제 (connection pool clear 포함)
            var dbPath = _pathResolver.GetSharedDbPath();
            _settingsService.DeleteDatabase(dbPath);

            // 3-b. oee.db 정지 이벤트도 동반 초기화 — plc.db 를 비웠는데 정지 로그(특히 '진행중' 박제)가
            //      남는 문제 해소. 정지 이벤트(oeeDowntimeEvent) 만 비우고, 작업자가 입력한 불량/생산
            //      (oeeProductionCount)·시프트 예외(oeeShiftException)는 보존한다(doc/21 §1 의도 유지).
            //      IOeeRepository 는 scoped 라 scope 를 직접 연다(상태머신과 동일 패턴).
            int downtimeCleared = 0;
            try
            {
                using var oeeScope = _scopeFactory.CreateScope();
                var oeeRepo = oeeScope.ServiceProvider.GetRequiredService<IOeeRepository>();
                downtimeCleared = await oeeRepo.ClearDowntimeEventsAsync();
                _logger.LogInformation("[DBLifecycle] oeeDowntimeEvent {N}건 초기화 (불량/시프트 보존)", downtimeCleared);
            }
            catch (Exception ex)
            {
                // 정지 로그 초기화 실패는 plc.db 재구축을 막지 않는다(비핵심).
                _logger.LogWarning(ex, "[DBLifecycle] oeeDowntimeEvent 초기화 실패 (plc.db 재구축은 계속)");
            }

            // 4. 스키마 + 현재 in-memory AASX → dspFlow/dspCall 재적재
            var ok = await _bootstrap.BootstrapAsync();
            if (!ok)
            {
                _logger.LogWarning("[DBLifecycle] Bootstrap failed after delete");
                return new RebuildResult(false, "DB 재구축 실패 — 로그 확인");
            }

            // 5. 엔진 재시작 — 첫 Hub 신호 도착 시 자동 init 됨 (lazy) 또는 즉시 init
            _engineService.TryEnsureInitialized();

            // 6. 새 DB 로딩 완료 시점에 OnDataChanged 한 번 더 발화 — step 2 의 Reset 은 DB
            // 삭제 직전이라 그 시점에 페이지가 reload 해도 빈 결과. BootstrapAsync 가 끝나고
            // 새 dspFlow / dspCall 이 채워진 지금 발화해야 Heatmap / Dashboard 등이 fresh 데이터로
            // 자동 재구성된다. (CycleTimeAnalysis 도 OnDataChanged 구독 — 동일 경로)
            _dspDbService.Reset();

            // 7. audit log — 통째 재구축은 통계 단절점이라 명시적으로 박제
            try
            {
                await _dspRepository.InsertAasxChangeLogAsync(
                    sha256Before: null,
                    sha256After: _projectService.LastLoadedSha256 ?? "<unknown>",
                    source: auditSource,
                    flowsAdded: null,
                    flowsRemoved: null,
                    pruneFlows: 0, pruneCalls: 0, pruneHistory: 0,
                    notes: "plc.db full rebuild — plcTagLog / dspFlowHistory 등 raw 데이터 모두 삭제됨");
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[DBLifecycle] aasxChangeLog INSERT 실패 (비중요)"); }

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
            return new RebuildResult(true,
                $"데이터베이스가 재구축되었습니다. (정지 이벤트 {downtimeCleared}건 초기화 · 불량/시프트 보존)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DBLifecycle] Rebuild failed");
            return new RebuildResult(false, $"DB 재구축 실패: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 디스크의 AASX 파일을 다시 읽어 in-memory DsStore 를 갱신.
    /// plc.db / dspFlow / dspCall / 통계 / 히스토리 / 엔진 상태에는 손대지 않는다.
    /// 모델 정의가 바뀌었다면 이후 <see cref="RebuildDatabaseAsync"/> 로 DB 를 동기화해야 한다.
    /// </summary>
    public async Task<RebuildResult> ReloadAasxAsync()
    {
        if (!await _gate.WaitAsync(0))
            return new RebuildResult(false, "다른 재초기화 작업이 진행 중입니다.");

        try
        {
            var path = _projectService.AasxFilePath;
            if (!File.Exists(path))
            {
                _logger.LogWarning("[DBLifecycle] AASX 파일이 없습니다: {Path}", path);
                return new RebuildResult(false, $"AASX 파일이 없습니다: {path}");
            }

            _logger.LogInformation("[DBLifecycle] ReloadAasx starting ({Path})", path);
            _projectService.LoadProject(path);

            if (!_projectService.IsLoaded)
            {
                return new RebuildResult(false, "AASX 파싱 실패 — 구 포맷일 수 있습니다. ds2 에디터에서 다시 Export 하세요.");
            }

            _logger.LogInformation("[DBLifecycle] ReloadAasx complete (sha256={Sha})", _projectService.LastLoadedSha256 ?? "<n/a>");
            return new RebuildResult(true, "AASX 모델을 다시 불러왔습니다. (DB 미반영 — 모델 정의가 바뀌었다면 \"DB 재구축\" 실행)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DBLifecycle] ReloadAasx failed");
            return new RebuildResult(false, $"AASX 재로딩 실패: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 디스크 AASX 재로딩 + 모델 정의 동기화(UPSERT) + 사라진 Flow 의 stale 행 정리 + Layout 자동 재배치.
    /// 살아남은 Flow 의 통계 / 히스토리는 보존된다 (전체 초기화는 <see cref="RebuildDatabaseAsync"/> 사용).
    /// 사용처: Promaker "agent 보내기" 등으로 외부에서 project.aasx 가 변경됐을 때 자동 호출.
    /// </summary>
    public async Task<RebuildResult> ReloadAndResyncAsync()
    {
        if (!await _gate.WaitAsync(0))
            return new RebuildResult(false, "다른 재초기화 작업이 진행 중입니다.");

        try
        {
            var path = _projectService.AasxFilePath;
            if (!File.Exists(path))
                return new RebuildResult(false, $"AASX 파일이 없습니다: {path}");

            // 1) AASX 재로딩 전에 prior SHA + 현재 DB 의 Flow 집합 캡처 (audit log 용)
            var shaBefore = _projectService.LastLoadedSha256;
            var priorFlowNames = await _dspRepository.GetAllFlowNamesAsync();

            // 1) AASX 재로딩 — in-memory DsStore 교체 (ReplaceStore)
            _logger.LogInformation("[DBLifecycle] ReloadAndResync starting ({Path})", path);
            _projectService.LoadProject(path);
            if (!_projectService.IsLoaded)
                return new RebuildResult(false, "AASX 파싱 실패 — 구 포맷일 수 있습니다. ds2 에디터에서 다시 Export 하세요.");

            // 2) 새 모델에 살아있는 Flow 수집 — DspDatabaseServiceAdapter 와 동일하게 "*_Flow" 접미사 제외
            var keepFlows = _projectService.GetAllFlows()
                .Where(f => !f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var keepNames = keepFlows.Select(f => f.Name).ToList();
            var keepIds = keepFlows.Select(f => f.Id).ToHashSet();

            // added / removed 산출 — audit log 용
            var priorSet = new HashSet<string>(priorFlowNames, StringComparer.OrdinalIgnoreCase);
            var keepSet = new HashSet<string>(keepNames, StringComparer.OrdinalIgnoreCase);
            var flowsAdded = keepSet.Except(priorSet, StringComparer.OrdinalIgnoreCase).ToList();
            var flowsRemoved = priorSet.Except(keepSet, StringComparer.OrdinalIgnoreCase).ToList();

            // 3) stale 행 prune — 새 모델에 없는 Flow 의 dspFlow / dspCall / dspFlowHistory 삭제
            //    Bootstrap UPSERT 보다 먼저 — UPSERT 가 같은 row 를 다시 살리지는 않으니 순서는 무관하지만,
            //    prune 후 UPSERT 가 깔끔.
            var pruned = (Flows: 0, Calls: 0, History: 0);
            if (keepNames.Count > 0)
            {
                pruned = await _dspRepository.PruneByFlowNamesAsync(keepNames);
            }

            // 4) UPSERT — 새/변경된 정의 반영 + Mapper / FlowMetrics 재초기화
            //    살아남은 행은 ON CONFLICT DO UPDATE 로 정의만 갱신되고 통계 컬럼은 COALESCE 로 보존됨.
            var bootstrapOk = await _bootstrap.BootstrapAsync();
            if (!bootstrapOk)
            {
                _logger.LogWarning("[DBLifecycle] Bootstrap UPSERT after reload failed");
                return new RebuildResult(false, "AASX reload 후 DB 동기화 실패 — 로그 확인");
            }

            // 5) Engine 재초기화 — 모델 정의(SimIndex / IOMap / UserTag 주소)가 바뀌었을 수 있으므로
            //    teardown 후 새 store 로 재빌드해야 한다. TryEnsureInitialized() 만으로는 이미 초기화된
            //    엔진이 startup 시점의 stale index 를 그대로 들고 있어, 새/변경된 Flow·Call·UserTag 가
            //    인식되지 않고 plcTagLog 기록도 누락된다 (서비스 재시작해야 적용되던 증상).
            //    ResetAsync 직후 재초기화에서 BootstrapPlcTags 가 새 UserTag 주소를 캐시에 등록하고,
            //    SeedCallStatsFromDb 가 DB 통계를 다시 시드하므로 누적 통계 연속성도 유지된다.
            await _engineService.ResetAsync();
            _engineService.TryEnsureInitialized();

            // 6) Layout 동기화 — Flow Guid 집합이 placement 와 다르면 백업 후 자동 재배치.
            //    도면(이미지/Canvas/Offset) 메타는 유지, FlowPlacements/FlowProcessOrder/Grid 만 재구성.
            var layoutChanged = false;
            if (_blueprint.IsFlowSetStale(keepIds))
            {
                _blueprint.BackupCurrentLayoutFile(suffix: "auto-resync");

                var orderedFlows = _projectService.GetActiveSystems()
                    .SelectMany(sys => _projectService.GetFlows(sys.Id)
                        .Where(f => !f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase))
                        .Select(f => (FlowId: f.Id, FlowName: f.Name, SystemName: sys.Name, SystemId: sys.Id)))
                    .ToList();

                _blueprint.ResetFlowPlacementsAndAutoFill(orderedFlows);
                layoutChanged = true;
                _logger.LogInformation("[DBLifecycle] Layout auto-resynced — Flow {N}개 재배치", orderedFlows.Count);
            }

            // 7) UI 스냅샷 클리어 → OnDataChanged + OnStructuralChange 발화로 모든 페이지 자동 새로고침
            _dspDbService.Reset();

            // 8) audit log — "이 시점에 모델이 어떻게 바뀌었는지" 영구 박제
            try
            {
                await _dspRepository.InsertAasxChangeLogAsync(
                    sha256Before: shaBefore,
                    sha256After: _projectService.LastLoadedSha256 ?? "<unknown>",
                    source: "AasxWatcher",
                    flowsAdded: flowsAdded.Count > 0 ? flowsAdded : null,
                    flowsRemoved: flowsRemoved.Count > 0 ? flowsRemoved : null,
                    pruneFlows: pruned.Flows,
                    pruneCalls: pruned.Calls,
                    pruneHistory: pruned.History,
                    notes: layoutChanged ? "layout auto-resynced" : null);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[DBLifecycle] aasxChangeLog INSERT 실패 (비중요)"); }

            // 9) 다른 클라이언트 알림 (DatabaseRebuilt 핸들러 재사용 — 페이지가 강제 reload)
            try { await _hubContext.Clients.All.SendAsync("DatabaseRebuilt"); }
            catch (Exception ex) { _logger.LogDebug(ex, "[DBLifecycle] SignalR broadcast failed (non-critical)"); }

            _logger.LogInformation(
                "[DBLifecycle] ReloadAndResync complete (pruned: flow={F} call={C} hist={H}, layout={Layout})",
                pruned.Flows, pruned.Calls, pruned.History, layoutChanged);

            var msgParts = new List<string> { "AASX 모델을 다시 불러왔습니다." };
            var totalPruned = pruned.Flows + pruned.Calls + pruned.History;
            if (totalPruned > 0)
                msgParts.Add($"사라진 Flow 정리: dspFlow={pruned.Flows}, dspCall={pruned.Calls}, history={pruned.History}.");
            if (layoutChanged)
                msgParts.Add("레이아웃 자동 재배치 (이전 layout 은 백업).");
            return new RebuildResult(true, string.Join(" ", msgParts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DBLifecycle] ReloadAndResync failed");
            return new RebuildResult(false, $"AASX 자동 동기화 실패: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>AASX 변경 이력 목록 (연표 다이얼로그용).</summary>
    public Task<IReadOnlyList<AasxChangeLogEntry>> GetAasxChangeLogAsync(int limit = 100)
        => _dspRepository.GetAasxChangeLogAsync(limit);

    /// <summary>
    /// 지정 시각 이전 raw 데이터 선택 삭제 (plcTagLog, userTagAlertLog, dspFlowHistory, oeeDowntimeEvent).
    /// 해당 시각 이후 데이터는 보존.
    /// </summary>
    public async Task<RebuildResult> DeleteDataBeforeAsync(DateTime cutoffUtc)
    {
        if (!await _gate.WaitAsync(0))
            return new RebuildResult(false, "다른 재초기화 작업이 진행 중입니다.");

        try
        {
            _logger.LogInformation("[DBLifecycle] DeleteDataBefore cutoff={Cutoff}...", cutoffUtc.ToString("o"));

            var (plc, alert, hist) = await _dspRepository.DeleteRawDataBeforeAsync(cutoffUtc);

            var oeeDeleted = 0;
            try
            {
                using var oeeScope = _scopeFactory.CreateScope();
                var oeeRepo = oeeScope.ServiceProvider.GetRequiredService<IOeeRepository>();
                oeeDeleted = await oeeRepo.DeleteDowntimeEventsBeforeAsync(cutoffUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DBLifecycle] OEE downtime 삭제 실패 (비중요)");
            }

            // 삭제 후 집계 캐시 무효화 (평균이 달라질 수 있음)
            try
            {
                await _dspRepository.InvalidateRunningStatsAsync();
                _engineService.ResetCallStats();
                _dspDbService.Reset();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DBLifecycle] 캐시 재계산 실패 (비중요)");
            }

            try { await _hubContext.Clients.All.SendAsync("FlowHistoryCleared"); }
            catch (Exception ex) { _logger.LogDebug(ex, "[DBLifecycle] SignalR broadcast 실패 (비중요)"); }

            var msg = $"삭제 완료 — plcTagLog: {plc}건, 알림이력: {alert}건, FlowHistory: {hist}건, OEE정지: {oeeDeleted}건";
            _logger.LogInformation("[DBLifecycle] DeleteDataBefore complete: {Msg}", msg);
            return new RebuildResult(true, msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DBLifecycle] DeleteDataBefore 실패");
            return new RebuildResult(false, $"삭제 실패: {ex.Message}");
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
