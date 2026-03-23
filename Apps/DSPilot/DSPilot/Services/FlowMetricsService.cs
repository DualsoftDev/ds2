using Ds2.UI.Core;
using DSPilot.Engine;
using DSPilot.Repositories;
using Microsoft.FSharp.Collections;
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
    private readonly ConcurrentDictionary<string, FlowAnalysis.FlowAnalysisResult> _flowAnalysisCache = new();

    // Flow별 사이클 상태 추적 (Phase 2)
    private readonly ConcurrentDictionary<string, FlowCycleState> _flowCycleStates = new();

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
                    // F# FlowAnalysis 모듈 사용
                    var store = GetDsStore();
                    var analysisResult = FlowAnalysis.analyzeFlow(flow, store);

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
            return FlowAnalysis.analyzeFlow(flow, store);
        });

        return (
            FromFSharpStringOption(analysisResult.MovingStartName),
            FromFSharpStringOption(analysisResult.MovingEndName));
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

        await ApplyResolvedCycleBoundaryAsync(flowName, effectiveStartCallName, effectiveEndCallName);

        _logger.LogInformation(
            "Flow '{FlowName}' cycle boundary override applied. Effective Start={Start}, Effective End={End}",
            flowName,
            effectiveStartCallName,
            effectiveEndCallName);
    }

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
            // 평균값 계산 (누적 평균)
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

            // 2. History 테이블 삽입
            var history = new Models.Dsp.DspFlowHistoryEntity
            {
                FlowName = flowName,
                MT = mt,
                WT = wt,
                CT = ct,
                CycleNo = state.CycleCount,
                RecordedAt = DateTime.UtcNow
            };

            await _dspRepository.InsertFlowHistoryAsync(history);

            _logger.LogInformation(
                "Flow '{FlowName}' Cycle #{CycleNo}: MT={MT}ms, WT={WT}ms, CT={CT}ms | Avg: MT={AvgMT:F0}ms, WT={AvgWT:F0}ms, CT={AvgCT:F0}ms",
                flowName, state.CycleCount, mt, wt, ct, avgMT, avgWT, avgCT);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update metrics with averages for Flow '{FlowName}'", flowName);
        }
    }

    private async Task ApplyResolvedCycleBoundaryAsync(string flowName, string? startCallName, string? endCallName)
    {
        var movingStartName = startCallName != null ? $"{flowName}.{startCallName}" : null;
        var movingEndName = endCallName != null ? $"{flowName}.{endCallName}" : null;
        var isSingleCallFlow = startCallName != null && startCallName == endCallName;

        await _dspRepository.UpdateFlowCycleBoundariesAsync(flowName, movingStartName, movingEndName);

        _flowCycleStates[flowName] = new FlowCycleState
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
    }

    private static string? NormalizeCallName(string? callName)
    {
        return string.IsNullOrWhiteSpace(callName) ? null : callName.Trim();
    }

    private static string? FromFSharpStringOption(Microsoft.FSharp.Core.FSharpOption<string> option)
    {
        return Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(option)
            ? option.Value
            : null;
    }

    /// <summary>
    /// DsStore 접근 (리플렉션 사용)
    /// </summary>
    private DsStore GetDsStore()
    {
        var storeField = typeof(DsProjectService).GetField("_store",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (storeField == null)
        {
            throw new InvalidOperationException("Failed to access DsStore from DsProjectService");
        }

        var store = storeField.GetValue(_projectService) as DsStore;
        if (store == null)
        {
            throw new InvalidOperationException("DsStore is null");
        }

        return store;
    }
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
