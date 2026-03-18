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
    private readonly IDspRepository _dspRepository;
    private readonly ILogger<FlowMetricsService> _logger;

    // Flow별 분석 결과 캐시
    private readonly ConcurrentDictionary<string, FlowAnalysis.FlowAnalysisResult> _flowAnalysisCache = new();

    // Flow별 사이클 상태 추적 (Phase 2)
    private readonly ConcurrentDictionary<string, FlowCycleState> _flowCycleStates = new();

    public FlowMetricsService(
        DsProjectService projectService,
        IDspRepository dspRepository,
        ILogger<FlowMetricsService> logger)
    {
        _projectService = projectService;
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

                    // DB에 MovingStartName/MovingEndName 설정
                    if (analysisResult.MovingStartName != null || analysisResult.MovingEndName != null)
                    {
#pragma warning disable CS8602 // F# Option interop - get_IsSome guarantees Value is not null
                        var movingStartCallName = Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(analysisResult.MovingStartName)
                            ? analysisResult.MovingStartName.Value
                            : null;
                        var movingEndCallName = Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(analysisResult.MovingEndName)
                            ? analysisResult.MovingEndName.Value
                            : null;
#pragma warning restore CS8602

                        // 단일 Call Flow 여부 확인 (MovingStartName == MovingEndName)
                        bool isSingleCallFlow = movingStartCallName == movingEndCallName;

                        if (isSingleCallFlow && movingStartCallName != null)
                        {
                            _logger.LogInformation("Flow '{FlowName}' is a single-Call Flow with Call '{CallName}'",
                                flow.Name, movingStartCallName);
                        }

                        // FlowName.CallName 형식으로 고유하게 저장
                        var movingStartName = movingStartCallName != null
                            ? $"{flow.Name}.{movingStartCallName}"
                            : null;
                        var movingEndName = movingEndCallName != null
                            ? $"{flow.Name}.{movingEndCallName}"
                            : null;

                        await _dspRepository.UpdateFlowMetricsAsync(
                            flow.Name,
                            mt: null,
                            wt: null,
                            ct: null,
                            movingStartName: movingStartName!,
                            movingEndName: movingEndName!);

                        _logger.LogInformation(
                            "Flow '{FlowName}': MovingStart={Start}, MovingEnd={End}",
                            flow.Name,
                            analysisResult.MovingStartName,
                            analysisResult.MovingEndName);

                        // 사이클 상태 초기화
#pragma warning disable CS8602 // F# Option interop - get_IsSome guarantees Value is not null
                        var headCallName = Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(analysisResult.MovingStartName)
                            ? analysisResult.MovingStartName.Value
                            : null;
                        var tailCallName = Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(analysisResult.MovingEndName)
                            ? analysisResult.MovingEndName.Value
                            : null;
#pragma warning restore CS8602

                        _flowCycleStates[flow.Name] = new FlowCycleState
                        {
                            FlowName = flow.Name,
                            HeadCallName = headCallName,
                            TailCallName = tailCallName,
                            IsSingleCallFlow = isSingleCallFlow,
                            CurrentCycleStart = null,
                            PreviousCycleFinish = null,
                            CurrentMT = null,
                            CurrentWT = null,
                            CurrentCT = null
                        };
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

                        // DB 갱신
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var movingStartName = state.HeadCallName != null
                                    ? $"{flowName}.{state.HeadCallName}"
                                    : null;
                                var movingEndName = state.TailCallName != null
                                    ? $"{flowName}.{state.TailCallName}"
                                    : null;

                                await _dspRepository.UpdateFlowMetricsAsync(
                                    flowName,
                                    mt: prevMT,
                                    wt: wt,
                                    ct: ct,
                                    movingStartName: movingStartName,
                                    movingEndName: movingEndName);

                                _logger.LogInformation(
                                    "Single-Call Flow '{FlowName}' metrics updated: MT={MT}ms, WT={WT}ms, CT={CT}ms",
                                    flowName, prevMT, wt, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to update metrics for single-Call Flow '{FlowName}'", flowName);
                            }
                        });
                    }

                    // 새 사이클 시작
                    state.CurrentCycleStart = timestamp;
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

                        // DB 갱신 (비동기 작업을 Fire-and-Forget 방식으로 실행)
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // FlowName.CallName 형식으로 고유하게 저장
                                var movingStartName = state.HeadCallName != null
                                    ? $"{flowName}.{state.HeadCallName}"
                                    : null;
                                var movingEndName = state.TailCallName != null
                                    ? $"{flowName}.{state.TailCallName}"
                                    : null;

                                await _dspRepository.UpdateFlowMetricsAsync(
                                    flowName,
                                    mt: prevMT,
                                    wt: wt,
                                    ct: ct,
                                    movingStartName: movingStartName,
                                    movingEndName: movingEndName);

                                _logger.LogInformation(
                                    "Flow '{FlowName}' metrics updated: MT={MT}ms, WT={WT}ms, CT={CT}ms",
                                    flowName, prevMT, wt, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to update metrics for Flow '{FlowName}'", flowName);
                            }
                        });
                    }

                    // 새 사이클 시작 (MT는 tail 완료 시 계산됨)
                    state.CurrentCycleStart = timestamp;
                    // CurrentMT는 유지 (다음 Head에서 사용)
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
    public DateTime? CurrentCycleStart { get; set; }
    public DateTime? PreviousCycleFinish { get; set; }
    public int? CurrentMT { get; set; }
    public int? CurrentWT { get; set; }
    public int? CurrentCT { get; set; }
}
