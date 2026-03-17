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

    // Call Name -> Flow Name 매핑 (빠른 조회용)
    private readonly ConcurrentDictionary<string, string> _callToFlowMap = new();

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

            var flows = _projectService.GetAllFlows();
            _logger.LogInformation("Analyzing {Count} flows...", flows.Count);

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

                    // Call -> Flow 매핑 구축
                    var works = _projectService.GetWorks(flow.Id);
                    foreach (var work in works)
                    {
                        var calls = _projectService.GetCalls(work.Id);
                        foreach (var call in calls)
                        {
                            _callToFlowMap[call.Name] = flow.Name;
                        }
                    }

                    // DB에 MovingStartName/MovingEndName 설정
                    if (analysisResult.MovingStartName != null || analysisResult.MovingEndName != null)
                    {
#pragma warning disable CS8602 // F# Option interop - get_IsSome guarantees Value is not null
                        var movingStartName = Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(analysisResult.MovingStartName)
                            ? analysisResult.MovingStartName.Value
                            : null;
                        var movingEndName = Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(analysisResult.MovingEndName)
                            ? analysisResult.MovingEndName.Value
                            : null;
#pragma warning restore CS8602

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
    public void OnCallGoingStarted(string callName, DateTime timestamp)
    {
        try
        {
            // Call이 어느 Flow에 속하는지 확인
            if (!_callToFlowMap.TryGetValue(callName, out var flowName))
            {
                return; // 매핑되지 않은 Call은 무시
            }

            // Flow의 사이클 상태 조회
            if (!_flowCycleStates.TryGetValue(flowName, out var state))
            {
                return; // 초기화되지 않은 Flow는 무시
            }

            // Head Call이 Going 시작한 경우
            if (state.HeadCallName == callName)
            {
                // 이전 사이클 완료 시점이 있으면 WT 계산 가능
                if (state.PreviousCycleFinish.HasValue && state.CurrentMT.HasValue)
                {
                    var wt = (int)(timestamp - state.PreviousCycleFinish.Value).TotalMilliseconds;
                    var ct = state.CurrentMT.Value + wt;

                    state.CurrentWT = wt;
                    state.CurrentCT = ct;

                    // DB 갱신 (비동기 작업을 동기적으로 실행)
                    Task.Run(async () =>
                    {
                        try
                        {
                            await _dspRepository.UpdateFlowMetricsAsync(
                                flowName,
                                mt: state.CurrentMT,
                                wt: wt,
                                ct: ct,
                                movingStartName: state.HeadCallName,
                                movingEndName: state.TailCallName);

                            _logger.LogDebug(
                                "Flow '{FlowName}' metrics updated: MT={MT}ms, WT={WT}ms, CT={CT}ms",
                                flowName, state.CurrentMT, wt, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to update metrics for Flow '{FlowName}'", flowName);
                        }
                    }).Wait();
                }

                // 새 사이클 시작
                state.CurrentCycleStart = timestamp;
                state.CurrentMT = null; // 아직 tail이 완료되지 않음
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
    public void OnCallFinished(string callName, DateTime timestamp)
    {
        try
        {
            // Call이 어느 Flow에 속하는지 확인
            if (!_callToFlowMap.TryGetValue(callName, out var flowName))
            {
                return;
            }

            // Flow의 사이클 상태 조회
            if (!_flowCycleStates.TryGetValue(flowName, out var state))
            {
                return;
            }

            // Tail Call이 완료된 경우
            if (state.TailCallName == callName && state.CurrentCycleStart.HasValue)
            {
                // MT 계산
                var mt = (int)(timestamp - state.CurrentCycleStart.Value).TotalMilliseconds;
                state.CurrentMT = mt;
                state.PreviousCycleFinish = timestamp;

                _logger.LogDebug(
                    "Flow '{FlowName}' cycle finished: MT={MT}ms",
                    flowName, mt);
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
    public DateTime? CurrentCycleStart { get; set; }
    public DateTime? PreviousCycleFinish { get; set; }
    public int? CurrentMT { get; set; }
    public int? CurrentWT { get; set; }
    public int? CurrentCT { get; set; }
}
