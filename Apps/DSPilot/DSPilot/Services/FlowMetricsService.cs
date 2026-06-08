using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using DSPilot.Repositories;
using DSPilot.Services.FlowAnalysis;
using System.Collections.Concurrent;

namespace DSPilot.Services;

/// <summary>
/// Flow 메트릭 추적 서비스
/// - Flow별 대표 Work 분석 및 MovingStartName/MovingEndName 설정
/// - MT/WT/CT 런타임 추적 및 갱신
/// </summary>
public class FlowMetricsService : IFlowMetricsService
{
    private readonly DsProjectService _projectService;
    private readonly AppSettingsService _appSettingsService;
    private readonly Adapters.DspRepositoryAdapter _dspRepository;
    private readonly ILogger<FlowMetricsService> _logger;

    // Flow별 분석 결과 캐시
    private readonly ConcurrentDictionary<string, FlowAnalysisResult> _flowAnalysisCache = new();

    // Flow별 사이클 상태 추적 (Phase 2)
    private readonly ConcurrentDictionary<string, FlowCycleState> _flowCycleStates = new();

    private volatile bool _isInitialized = false;
    public bool IsInitialized => _isInitialized;

    public FlowMetricsService(
        DsProjectService projectService,
        AppSettingsService appSettingsService,
        Adapters.DspRepositoryAdapter dspRepository,
        ILogger<FlowMetricsService> logger)
    {
        _projectService = projectService;
        _appSettingsService = appSettingsService;
        _dspRepository = dspRepository;
        _logger = logger;
    }

    /// <summary>
    /// Phase 1: 모든 Flow 분석 및 초기화
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing FlowMetricsService...");

        try
        {
            if (!_projectService.IsLoaded)
            {
                _logger.LogWarning("Project not loaded. Skipping Flow metrics initialization.");
                return;
            }

            var allFlows = _projectService.GetAllFlows();
            _logger.LogInformation("Total flows in AASX: {Count}", allFlows.Count);

            // "_Flow" 접미사를 가진 Flow 제외 (실제 제조 Flow만 분석)
            var flows = allFlows
                .Where(f => !f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase))
                .ToList();

            _logger.LogInformation("Analyzing {Count} flows (excluding '*_Flow')...", flows.Count);

            int successCount = 0;
            int failCount = 0;

            foreach (var flow in flows)
            {
                try
                {
                    var store = GetDsStore();
                    var analysisResult = FlowAnalyzer.AnalyzeFlow(flow, store);

                    // 캐시 저장
                    _flowAnalysisCache[flow.Name] = analysisResult;

                    // 복수 Head/Tail 경고
                    if (analysisResult.HeadCount > 1)
                    {
                        _logger.LogWarning(
                            "Flow '{FlowName}' has {HeadCount} head calls. Using first head only for cycle tracking.",
                            flow.Name, analysisResult.HeadCount);
                    }
                    if (analysisResult.TailCount > 1)
                    {
                        _logger.LogWarning(
                            "Flow '{FlowName}' has {TailCount} tail calls. Using first tail only for cycle tracking.",
                            flow.Name, analysisResult.TailCount);
                    }

                    var (defaultStartCallName, defaultEndCallName) = GetAasxCycleBoundaries(flow.Name);
                    var overrideConfig = _appSettingsService.GetFlowCycleOverride(flow.Name);
                    var effectiveStartCallName = NormalizeCallName(overrideConfig?.StartCallName) ?? defaultStartCallName;
                    var effectiveEndCallName = NormalizeCallName(overrideConfig?.EndCallName) ?? defaultEndCallName;

                    if (effectiveStartCallName != null || effectiveEndCallName != null)
                    {
                        // 단일 Call Flow 여부 확인 (MovingStartName == MovingEndName)
                        bool isSingleCallFlow = effectiveStartCallName == effectiveEndCallName;

                        if (isSingleCallFlow && effectiveStartCallName != null)
                        {
                            _logger.LogInformation("Flow '{FlowName}' is a single-Call Flow with Call '{CallName}'",
                                flow.Name, effectiveStartCallName);
                        }

                        await ApplyResolvedCycleBoundaryAsync(flow.Name, effectiveStartCallName, effectiveEndCallName);

                        _logger.LogInformation(
                            "Flow '{FlowName}': AASX Start={AasxStart}, AASX End={AasxEnd}, Effective Start={Start}, Effective End={End}",
                            flow.Name,
                            defaultStartCallName,
                            defaultEndCallName,
                            effectiveStartCallName,
                            effectiveEndCallName);
                    }

                    successCount++;
                }
                catch (InvalidOperationException ex)
                {
                    // DAG 순환 오류
                    _logger.LogError(ex, "Cycle detected in Flow '{FlowName}'. Skipping metrics.", flow.Name);
                    failCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to analyze Flow '{FlowName}'", flow.Name);
                    failCount++;
                }
            }

            _isInitialized = true;

            _logger.LogInformation(
                "Flow metrics initialization completed. Success: {Success}, Failed: {Failed}",
                successCount, failCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize FlowMetricsService");
            throw;
        }
    }

    /// <summary>
    /// Flow 의 사이클 경계(Head/Tail) Call 이름을 돌려준다.
    /// 우선순위: <see cref="Call.SequenceLabel"/>(Head/Tail) → 라벨이 없으면(경계가 전부 Body)
    /// <see cref="FlowAnalyzer"/> 토폴로지 기본값(모델 화살표에서 도출).
    /// 즉 "전부 Body" Flow 는 기존과 동일하게 토폴로지 기본값으로 폴백하므로 사이클 추적 회귀가 없다.
    /// Head/Tail 폴백은 경계별로 독립적이다 — 한쪽만 라벨이 있으면 그쪽만 라벨, 나머지는 토폴로지.
    /// </summary>
    public (string? StartCallName, string? EndCallName) GetAasxCycleBoundaries(string flowName)
    {
        if (string.IsNullOrWhiteSpace(flowName))
        {
            return (null, null);
        }

        if (!_projectService.IsLoaded)
        {
            return (null, null);
        }

        var flow = _projectService.GetFlowByName(flowName);
        if (flow is null)
        {
            return (null, null);
        }

        var analysisResult = _flowAnalysisCache.GetOrAdd(flow.Name, _ =>
        {
            var store = GetDsStore();
            return FlowAnalyzer.AnalyzeFlow(flow, store);
        });

        // SequenceLabel(Head/Tail) 우선 — 라벨이 박혀 있으면 모델 토폴로지 기본값을 덮어쓴다.
        // 라벨이 없는 경계(전부 Body)는 토폴로지 기본값(analysisResult)으로 폴백.
        var (labelStart, labelEnd) = ResolveSequenceLabelBoundaries(flow);
        var startName = labelStart ?? analysisResult.MovingStartName;
        var endName = labelEnd ?? analysisResult.MovingEndName;

        return (startName, endName);
    }

    /// <summary>
    /// Flow 의 Call 들에서 <see cref="Call.SequenceLabel"/>(Head/Tail)을 읽어 경계 Call 이름을 돌려준다.
    /// 모든 Call 이 Body(기본값)이면 해당 경계는 null — 호출 측이 모델 토폴로지 기본값으로 폴백한다.
    /// 같은 라벨이 여러 Call 에 있으면(동명 Call 다중 Work 등) 토폴로지 tie-break 와 동일하게
    /// 이름 오름차순 첫 번째를 사용하고 경고를 남긴다.
    /// </summary>
    private (string? HeadCallName, string? TailCallName) ResolveSequenceLabelBoundaries(Flow flow)
    {
        List<Call> calls;
        try
        {
            calls = _projectService.GetWorks(flow.Id)
                .SelectMany(w => _projectService.GetCalls(w.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flow '{FlowName}' SequenceLabel 조회 실패 — 토폴로지 기본값 사용", flow.Name);
            return (null, null);
        }

        var heads = calls.Where(c => c.SequenceLabel == SequenceLabel.Head).OrderBy(c => c.Name).ToList();
        var tails = calls.Where(c => c.SequenceLabel == SequenceLabel.Tail).OrderBy(c => c.Name).ToList();

        if (heads.Count > 1)
        {
            _logger.LogWarning(
                "Flow '{FlowName}' has {Count} Head-labeled calls. Using first '{Name}'.",
                flow.Name, heads.Count, heads[0].Name);
        }
        if (tails.Count > 1)
        {
            _logger.LogWarning(
                "Flow '{FlowName}' has {Count} Tail-labeled calls. Using first '{Name}'.",
                flow.Name, tails.Count, tails[0].Name);
        }

        return (heads.FirstOrDefault()?.Name, tails.FirstOrDefault()?.Name);
    }

    public async Task ApplyCycleBoundaryOverrideAsync(string flowName, string? startCallName, string? endCallName)
    {
        if (string.IsNullOrWhiteSpace(flowName))
        {
            return;
        }

        var (defaultStartCallName, defaultEndCallName) = GetAasxCycleBoundaries(flowName);
        var effectiveStartCallName = NormalizeCallName(startCallName) ?? defaultStartCallName;
        var effectiveEndCallName = NormalizeCallName(endCallName) ?? defaultEndCallName;

        // 변경 비교용 — 적용 전 boundary 캡처
        string? headBefore = null, tailBefore = null;
        if (_flowCycleStates.TryGetValue(flowName, out var prior))
        {
            headBefore = prior.HeadCallName;
            tailBefore = prior.TailCallName;
        }

        await ApplyResolvedCycleBoundaryAsync(flowName, effectiveStartCallName, effectiveEndCallName);

        // boundary 가 실제로 변경된 경우만 audit log INSERT (UserOverride source)
        try
        {
            await _dspRepository.InsertFlowBoundaryChangeLogAsync(
                flowName: flowName,
                headBefore: headBefore,
                headAfter: effectiveStartCallName,
                tailBefore: tailBefore,
                tailAfter: effectiveEndCallName,
                source: "UserOverride");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "flowBoundaryChangeLog INSERT 실패 (비중요)");
        }

        _logger.LogInformation(
            "Flow '{FlowName}' cycle boundary override applied. Effective Start={Start}, Effective End={End}",
            flowName,
            effectiveStartCallName,
            effectiveEndCallName);
    }

    public (string? HeadCallName, string? TailCallName) GetCycleBoundaryCallNames(string flowName)
    {
        if (_flowCycleStates.TryGetValue(flowName, out var state))
        {
            return (state.HeadCallName, state.TailCallName);
        }
        return (null, null);
    }

    public IReadOnlyCollection<string> GetTrackedFlowNames() => _flowCycleStates.Keys.ToArray();

    /// <summary>
    /// Phase 2: Call Going 시작 이벤트 처리
    /// </summary>
    public void OnCallGoingStarted(string flowName, string callName, DateTime timestamp)
    {
        try
        {
            // Flow의 사이클 상태 조회
            if (!_flowCycleStates.TryGetValue(flowName, out var state))
            {
                return; // 초기화되지 않은 Flow는 무시
            }

            // Head Call이 Going 시작한 경우
            if (state.HeadCallName == callName)
            {
                // 단일 Call Flow의 경우: 바로 이전 Finish 시간과 비교하여 WT 계산
                if (state.IsSingleCallFlow)
                {
                    if (state.PreviousCycleFinish.HasValue && state.CurrentMT.HasValue)
                    {
                        var prevMT = state.CurrentMT.Value;
                        var wt = (int)(timestamp - state.PreviousCycleFinish.Value).TotalMilliseconds;
                        var ct = prevMT + wt;

                        state.CurrentWT = wt;
                        state.CurrentCT = ct;

                        // 평균 계산 및 DB 갱신
                        _ = UpdateFlowMetricsWithAveragesAsync(state, flowName, prevMT, wt, ct);
                    }

                    // 새 사이클 시작
                    state.CurrentCycleStart = timestamp;
                    state.IsCycleActive = true;
                }
                else
                {
                    // 다중 Call Flow: 기존 로직
                    // 이전 사이클이 완료되었고 MT가 계산된 경우 WT/CT 계산 및 DB 업데이트
                    if (state.PreviousCycleFinish.HasValue && state.CurrentMT.HasValue)
                    {
                        var prevMT = state.CurrentMT.Value;
                        var wt = (int)(timestamp - state.PreviousCycleFinish.Value).TotalMilliseconds;
                        var ct = prevMT + wt;

                        state.CurrentWT = wt;
                        state.CurrentCT = ct;

                        // 평균 계산 및 DB 갱신
                        _ = UpdateFlowMetricsWithAveragesAsync(state, flowName, prevMT, wt, ct);
                    }

                    // 사이클 시작: 진행 중인 사이클이 없을 때만 (파이프라인 방어)
                    if (!state.IsCycleActive)
                    {
                        state.CurrentCycleStart = timestamp;
                        state.IsCycleActive = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Going start for Call '{CallName}'", callName);
        }
    }

    /// <summary>
    /// Phase 2: Call 완료 이벤트 처리
    /// </summary>
    public void OnCallFinished(string flowName, string callName, DateTime timestamp)
    {
        try
        {
            // Flow의 사이클 상태 조회
            if (!_flowCycleStates.TryGetValue(flowName, out var state))
            {
                return;
            }

            // Tail Call이 완료된 경우
            if (state.TailCallName == callName && state.CurrentCycleStart.HasValue)
            {
                // MT 계산 (Going 시작 → Finish 완료까지의 시간)
                var mt = (int)(timestamp - state.CurrentCycleStart.Value).TotalMilliseconds;
                state.CurrentMT = mt;
                state.PreviousCycleFinish = timestamp;
                state.IsCycleActive = false;

                if (state.IsSingleCallFlow)
                {
                    _logger.LogDebug(
                        "Single-Call Flow '{FlowName}' cycle finished: Call '{CallName}', MT={MT}ms",
                        flowName, callName, mt);
                }
                else
                {
                    _logger.LogDebug(
                        "Flow '{FlowName}' cycle finished: MT={MT}ms",
                        flowName, mt);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing finish for Call '{CallName}'", callName);
        }
    }

    /// <summary>
    /// Flow 메트릭 및 평균값 업데이트 + 히스토리 삽입
    /// </summary>
    private async Task UpdateFlowMetricsWithAveragesAsync(
        FlowCycleState state,
        string flowName,
        int mt,
        int wt,
        int ct)
    {
        try
        {
            // 비가동 판정: CT가 유효 Max/Min 범위 밖이면 비가동 사이클.
            // 유효범위 = per-flow 이상치 제외(있으면) > 글로벌 HistoryView (단일 소스 — AppSettingsService.GetEffectiveCycleRangeMs).
            var (maxCT, minCT) = _appSettingsService.GetEffectiveCycleRangeMs(flowName);
            bool exceedsMax = maxCT > 0 && ct > maxCT;
            bool belowMin = minCT > 0 && ct < minCT;
            bool isIdle = exceedsMax || belowMin;

            if (!isIdle)
            {
                // 평균값 계산 (누적 평균) — 비가동 사이클은 평균에서 제외
                state.CycleCount++;
                state.SumMT += mt;
                state.SumWT += wt;
                state.SumCT += ct;

                double avgMT = state.SumMT / state.CycleCount;
                double avgWT = state.SumWT / state.CycleCount;
                double avgCT = state.SumCT / state.CycleCount;

                // FlowName.CallName 형식으로 고유하게 저장
                var movingStartName = state.HeadCallName != null
                    ? $"{flowName}.{state.HeadCallName}"
                    : null;
                var movingEndName = state.TailCallName != null
                    ? $"{flowName}.{state.TailCallName}"
                    : null;

                // 1. Flow 테이블 업데이트 (현재값 + 평균값)
                await _dspRepository.UpdateFlowWithAveragesAsync(
                    flowName,
                    mt: mt,
                    wt: wt,
                    ct: ct,
                    avgMT: avgMT,
                    avgWT: avgWT,
                    avgCT: avgCT,
                    movingStartName: movingStartName,
                    movingEndName: movingEndName);

                _logger.LogInformation(
                    "Flow '{FlowName}' Cycle #{CycleNo}: MT={MT}ms, WT={WT}ms, CT={CT}ms | Avg: MT={AvgMT:F0}ms, WT={AvgWT:F0}ms, CT={AvgCT:F0}ms",
                    flowName, state.CycleCount, mt, wt, ct, avgMT, avgWT, avgCT);
            }
            else
            {
                _logger.LogInformation(
                    "Flow '{FlowName}' 비가동 cycle skipped: CT={CT}ms (MaxCycleTimeMs={MaxCT}ms, MinCycleTimeMs={MinCT}ms, exceedsMax={ExceedsMax}, belowMin={BelowMin})",
                    flowName, ct, maxCT, minCT, exceedsMax, belowMin);
            }

            // 2. History 테이블 삽입 (비가동 포함, IsIdle 플래그와 함께)
            //    boundary 박제 — head/tail 이 바뀌어도 row 별로 측정 정의가 보존됨.
            var history = new Models.Dsp.DspFlowHistoryEntity
            {
                FlowName = flowName,
                MT = mt,
                WT = wt,
                CT = ct,
                CycleNo = state.CycleCount,
                RecordedAt = DateTime.UtcNow,
                IsIdle = isIdle,
                HeadCallName = state.HeadCallName,
                TailCallName = state.TailCallName,
            };

            await _dspRepository.InsertFlowHistoryAsync(history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update metrics with averages for Flow '{FlowName}'", flowName);
        }
    }

    /// <summary>
    /// 현재 설정의 비가동 임계값을 기존 히스토리/평균에 소급 적용.
    /// DB 재평가 후 in-memory 누적 평균 상태도 "비가동 제외" 기준으로 재구성하여,
    /// 다음 라이브 사이클이 일관된 baseline 에서 이어지도록 한다.
    /// </summary>
    public async Task<(int HistoryRestamped, int FlowsRecomputed)> ReapplyIdleThresholdsAsync()
    {
        var settings = _appSettingsService.LoadSettings();
        var maxCT = settings.HistoryView.MaxCycleTimeMs;
        var minCT = settings.HistoryView.MinCycleTimeMs;
        // per-flow 이상치 제외 override(있으면)도 함께 박제 — "글로벌=기본, per-flow=override" 단일 유효범위.
        var perFlow = _appSettingsService.GetPerFlowEffectiveRangesMs();

        var result = await _dspRepository.ReapplyIdleThresholdsAsync(maxCT, minCT, perFlow);

        // in-memory 누적 평균 상태 재구성 → 다음 사이클이 DB 를 잘못된 값으로 덮어쓰지 않게 함
        try
        {
            var aggregates = await _dspRepository.GetNonIdleAggregatesAsync();
            foreach (var kv in _flowCycleStates)
            {
                var state = kv.Value;
                if (aggregates.TryGetValue(kv.Key, out var agg) && agg.Count > 0)
                {
                    state.CycleCount = agg.Count;
                    state.SumMT = agg.SumMT;
                    state.SumWT = agg.SumWT;
                    state.SumCT = agg.SumCT;
                }
                else
                {
                    state.CycleCount = 0;
                    state.SumMT = 0;
                    state.SumWT = 0;
                    state.SumCT = 0;
                }
            }
            _logger.LogInformation(
                "Rebuilt in-memory running averages from non-idle history for {Count} active flow states",
                _flowCycleStates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rebuild in-memory running averages after reapplying idle thresholds");
        }

        return result;
    }

    public async Task ReseedCycleStatesFromCurrentBoundaryAsync()
    {
        try
        {
            var aggregates = await _dspRepository.GetNonIdleAggregatesByCurrentBoundaryAsync();
            foreach (var kv in _flowCycleStates)
            {
                var state = kv.Value;
                if (aggregates.TryGetValue(kv.Key, out var agg) && agg.Count > 0)
                {
                    state.CycleCount = agg.Count;
                    state.SumMT = agg.SumMT;
                    state.SumWT = agg.SumWT;
                    state.SumCT = agg.SumCT;
                }
                else
                {
                    state.CycleCount = 0;
                    state.SumMT = 0;
                    state.SumWT = 0;
                    state.SumCT = 0;
                }
            }
            _logger.LogInformation(
                "Reseeded in-memory Welford accumulators from current-boundary history for {Count} flow states",
                _flowCycleStates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReseedCycleStatesFromCurrentBoundaryAsync 실패");
        }
    }

    private async Task ApplyResolvedCycleBoundaryAsync(string flowName, string? startCallName, string? endCallName)
    {
        var movingStartName = startCallName != null ? $"{flowName}.{startCallName}" : null;
        var movingEndName = endCallName != null ? $"{flowName}.{endCallName}" : null;
        var isSingleCallFlow = startCallName != null && startCallName == endCallName;

        await _dspRepository.UpdateFlowCycleBoundariesAsync(flowName, movingStartName, movingEndName);

        var state = new FlowCycleState
        {
            FlowName = flowName,
            HeadCallName = startCallName,
            TailCallName = endCallName,
            IsSingleCallFlow = isSingleCallFlow,
            IsCycleActive = false,
            CurrentCycleStart = null,
            PreviousCycleFinish = null,
            CurrentMT = null,
            CurrentWT = null,
            CurrentCT = null
        };

        // DB 히스토리에서 마지막 사이클 데이터로 부트스트래핑
        // → 재시작 후 첫 번째 사이클 시작 시 바로 WT/CT 계산 가능
        try
        {
            var lastHistory = await _dspRepository.GetFlowHistoryAsync(flowName, 1);
            if (lastHistory.Count > 0 && lastHistory[0].MT.HasValue)
            {
                // RecordedAt 은 UTC 로 저장되지만 SQLite 왕복 후 Kind=Unspecified.
                // OnCallGoingStarted/Finished 의 timestamp 가 DateTime.Now(Local) 이므로
                // 비교 단위를 맞추기 위해 Local 로 변환한다.
                var lastFinishLocal = DateTime.SpecifyKind(lastHistory[0].RecordedAt, DateTimeKind.Utc).ToLocalTime();
                state.CurrentMT = lastHistory[0].MT;
                state.PreviousCycleFinish = lastFinishLocal;
                state.CycleCount = 1;
                _logger.LogInformation("Flow '{FlowName}' bootstrapped from history: MT={MT}ms", flowName, lastHistory[0].MT);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flow '{FlowName}' history bootstrap failed, starting fresh", flowName);
        }

        _flowCycleStates[flowName] = state;
    }

    private static string? NormalizeCallName(string? callName)
    {
        return string.IsNullOrWhiteSpace(callName) ? null : callName.Trim();
    }

    /// <summary>
    /// DsStore 접근 — DsProjectService 의 공개 접근자 사용(리플렉션 제거).
    /// </summary>
    private DsStore GetDsStore() => _projectService.GetStore();
}

/// <summary>
/// Flow 사이클 상태
/// </summary>
public class FlowCycleState
{
    public string FlowName { get; set; } = string.Empty;
    public string? HeadCallName { get; set; }
    public string? TailCallName { get; set; }
    public bool IsSingleCallFlow { get; set; }
    public bool IsCycleActive { get; set; }
    public DateTime? CurrentCycleStart { get; set; }
    public DateTime? PreviousCycleFinish { get; set; }
    public int? CurrentMT { get; set; }
    public int? CurrentWT { get; set; }
    public int? CurrentCT { get; set; }

    // 평균 계산용 필드
    public int CycleCount { get; set; } = 0;
    public double SumMT { get; set; } = 0;
    public double SumWT { get; set; } = 0;
    public double SumCT { get; set; } = 0;
}
