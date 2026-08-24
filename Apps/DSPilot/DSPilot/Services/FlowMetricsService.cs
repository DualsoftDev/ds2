// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
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
    /// <summary>
    /// flow → (callName → 누적 Going 횟수). 사이클 경계 자동선정의 <b>동작 증거</b> 캐시.
    /// init/reload 때 채우고 <see cref="GetAasxCycleBoundaries"/>(동기)가 읽는다 — 비어 있으면 종전 동작(순수 토폴로지).
    /// </summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, int>> _goingEvidence = new();

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

                    // 동작 증거 적재 — 경계 결정 전에 채워야 head 후보 tie-break 에 반영된다.
                    try { _goingEvidence[flow.Name] = await _dspRepository.GetCallGoingCountsAsync(flow.Name); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Flow '{FlowName}' goingCount 적재 실패 — 토폴로지 기본값 사용", flow.Name); }

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
        // 라벨은 명시적 결정이라 그대로 존중하고, 토폴로지 폴백(알파벳 tie-break)만 동작 증거로 보정한다.
        var startName = labelStart ?? PreferOperatingCandidate(
            flow.Name, analysisResult.MovingStartName, analysisResult.HeadCandidates, "head");
        var endName = labelEnd ?? PreferOperatingCandidate(
            flow.Name, analysisResult.MovingEndName, analysisResult.TailCandidates, "tail");

        return (startName, endName);
    }

    /// <summary>
    /// 토폴로지 tie-break 보정 — 후보가 여럿인데 선택된 Call 이 <b>한 번도 Going 하지 않았고</b> 실제로 도는
    /// 후보가 있으면 그쪽으로 바꾼다.
    /// <para>필요한 이유: <see cref="FlowAnalyzer"/> 의 head/tail 선정은 InDegree/OutDegree 0 후보 중 <b>이름
    /// 오름차순 첫 번째</b>다. 실측(2026-08-20)에서 F6 의 후보가 {Conveyor5.STOP(Going 0), Conveyor6.MOVE(33,145)}
    /// 였고 알파벳순으로 STOP 이 이겨 <b>사이클이 영구 미기록</b>(dspFlowHistory 0행)이었다 — 그 결과 CT 임계·
    /// 가용성·정지 판정·대기 분류가 전부 죽고 달력근사 폴백으로 떨어졌다.</para>
    /// <para>보수 규칙 — 아래 중 하나라도 걸리면 원래 선택을 유지한다(오작동 방지):
    /// ① 후보가 1개 이하(자의성 없음) ② 증거 미적재(부팅 직후·신규 설치) ③ 선택된 Call 에 이미 Going 증거 있음
    /// ④ 어떤 후보에도 증거 없음(전부 0 — 신규 라인이라 판단 근거 없음).</para>
    /// </summary>
    private string? PreferOperatingCandidate(
        string flowName, string? chosen, IReadOnlyList<string> candidates, string role)
    {
        if (chosen is null || candidates.Count <= 1) return chosen;
        if (!_goingEvidence.TryGetValue(flowName, out var evidence) || evidence.Count == 0) return chosen;
        if (evidence.TryGetValue(chosen, out var chosenGoing) && chosenGoing > 0) return chosen;

        // 증거 있는 후보 중 최다 — 동수면 이름 오름차순(종전 tie-break 와 같은 결정론).
        var better = candidates
            .Where(c => evidence.TryGetValue(c, out var g) && g > 0)
            .OrderByDescending(c => evidence[c])
            .ThenBy(c => c, StringComparer.Ordinal)
            .FirstOrDefault();
        if (better is null) return chosen;

        _logger.LogWarning(
            "Flow '{FlowName}' {Role} 자동선정 보정: '{Chosen}'(Going {ChosenGoing}건) → '{Better}'(Going {BetterGoing}건) — "
            + "후보 {CandidateCount}개 중 알파벳순 선택이 동작하지 않는 Call 이었습니다. 의도한 경계가 다르면 설비 화면에서 직접 지정하세요.",
            flowName, role, chosen, chosenGoing, better, evidence[better], candidates.Count);
        return better;
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
                // 래치 필드(PreviousCycleFinish)는 워치독/교차검증과 공유 — 락으로 캡처.
                // 완료 1회 = 기록 1회(consume-once, 2026-08-19): CurrentMT 는 캡처 즉시 비운다. 종전엔 tail 을
                // 놓친 채 다음 head 가 오면 직전 완료의 stale MT + 정지 전체를 머금은 WT 로 오염 행이 나갔고
                // (다중 Call flow '오염 2행'의 1행째), abandon 해제 후 재시작 사이클도 같은 경로로 오염됐다.
                // 소비 후엔 다음 tail 완료가 CurrentMT 를 다시 채울 때까지 head start 가 아무 행도 쓰지 않는다.
                DateTime? prevFinish;
                int? prevCompletedMT;
                lock (state.LatchLock)
                {
                    prevFinish = state.PreviousCycleFinish;
                    prevCompletedMT = state.CurrentMT;
                    state.CurrentMT = null;
                }

                // 이전 사이클이 완료되었고 MT가 계산된 경우 WT/CT 계산 및 DB 업데이트.
                // (단일/다중 Call Flow 공통 — 기존 로직과 동일. 락 밖에서 수행: 설정 디스크 읽기/누산기 갱신 포함.)
                if (prevFinish.HasValue && prevCompletedMT.HasValue)
                {
                    var prevMT = prevCompletedMT.Value;
                    var wt = (int)(timestamp - prevFinish.Value).TotalMilliseconds;
                    var ct = prevMT + wt;

                    state.CurrentWT = wt;
                    state.CurrentCT = ct;

                    // 평균 계산 및 DB 갱신
                    _ = UpdateFlowMetricsWithAveragesAsync(state, flowName, prevMT, wt, ct);
                }

                // 사이클 시작: 단일 Call Flow 는 항상, 다중 Call Flow 는 진행 중 사이클이 없을 때만(파이프라인 방어).
                lock (state.LatchLock)
                {
                    if (state.IsSingleCallFlow || !state.IsCycleActive)
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

            // Tail Call이 완료된 경우 — 사이클 기록 조건은 기존과 동일(CurrentCycleStart.HasValue).
            // 래치 3-필드를 락으로 감싸 워치독/교차검증과의 교차 스레드 접근을 보호한다.
            if (state.TailCallName == callName)
            {
                int mt = 0;
                bool recorded = false;
                lock (state.LatchLock)
                {
                    if (state.CurrentCycleStart.HasValue)
                    {
                        // MT 계산 (Going 시작 → Finish 완료까지의 시간)
                        mt = (int)(timestamp - state.CurrentCycleStart.Value).TotalMilliseconds;
                        state.CurrentMT = mt;
                        state.PreviousCycleFinish = timestamp;
                        state.IsCycleActive = false;
                        recorded = true;
                    }
                }

                if (recorded)
                {
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
                // 평균값 계산 (롤링 윈도우 평균) — 비가동 사이클은 평균에서 제외.
                // 전(全)기간 누적 대신 "최근 N 사이클"(N = CycleAverageWindow) 만 평균내 현재 거동을 반영한다.
                // N<=0 이면 trim 하지 않음(세션 누적 = 윈도우 비활성).
                state.CycleCount++;
                int window = _appSettingsService.GetCycleAverageWindow();
                state.Recent.Enqueue(new Adapters.CycleSample(mt, wt, ct));
                if (window > 0)
                    while (state.Recent.Count > window) state.Recent.Dequeue();

                var (avgMT, avgWT, avgCT) = AverageOf(state.Recent);

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

    /// <summary>롤링 윈도우(최근 사이클 큐)의 MT/WT/CT 평균. 빈 큐면 (0,0,0).</summary>
    private static (double AvgMT, double AvgWT, double AvgCT) AverageOf(IReadOnlyCollection<Adapters.CycleSample> recent)
    {
        if (recent.Count == 0) return (0, 0, 0);
        double sumMT = 0, sumWT = 0, sumCT = 0;
        foreach (var s in recent) { sumMT += s.MT; sumWT += s.WT; sumCT += s.CT; }
        int n = recent.Count;
        return (sumMT / n, sumWT / n, sumCT / n);
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

        var window = settings.HistoryView.CycleAverageWindow;
        var result = await _dspRepository.ReapplyIdleThresholdsAsync(maxCT, minCT, perFlow, window);

        // in-memory 롤링 평균 윈도우 재구성(DB 최근 N 비가동 행) → 다음 사이클이 DB 를 stale 값으로 덮어쓰지 않게 함
        try
        {
            var recent = await _dspRepository.GetRecentNonIdleCyclesAsync(window, byCurrentBoundary: false);
            SeedRecentWindowsInto(recent);
            _logger.LogInformation(
                "Rebuilt rolling-average windows (last {Window}) from non-idle history for {Count} flow states",
                window, _flowCycleStates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rebuild rolling-average windows after reapplying idle thresholds");
        }

        return result;
    }

    public async Task ReseedCycleStatesFromCurrentBoundaryAsync()
    {
        try
        {
            var window = _appSettingsService.GetCycleAverageWindow();
            var recent = await _dspRepository.GetRecentNonIdleCyclesAsync(window, byCurrentBoundary: true);
            SeedRecentWindowsInto(recent);
            _logger.LogInformation(
                "Reseeded rolling-average windows (last {Window}) from current-boundary history for {Count} flow states",
                window, _flowCycleStates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReseedCycleStatesFromCurrentBoundaryAsync 실패");
        }
    }

    /// <summary>DB 에서 읽은 flow 별 최근 사이클(오래된→최신)로 각 상태의 롤링 평균 큐를 교체. CycleCount=윈도우 크기.</summary>
    private void SeedRecentWindowsInto(IReadOnlyDictionary<string, List<Adapters.CycleSample>> recent)
    {
        foreach (var kv in _flowCycleStates)
        {
            var state = kv.Value;
            state.Recent.Clear();
            if (recent.TryGetValue(kv.Key, out var list) && list.Count > 0)
            {
                foreach (var s in list) state.Recent.Enqueue(s);
                state.CycleCount = list.Count;
            }
            else
            {
                state.CycleCount = 0;
            }
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
            LatchEligible = ComputeLatchEligibility(flowName, startCallName, endCallName),
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
                // 누적 평균 상태(CycleCount/SumMT/SumWT/SumCT)는 여기서 건드리지 않는다 — 부트스트랩의 목적은
                // 다음 사이클 시작 때 WT/CT 를 이어 계산할 CurrentMT/PreviousCycleFinish 복원뿐이다.
                // (과거엔 CycleCount=1 만 세팅 → SumCT=0 과 불일치해 첫 평균이 ct/2 로 반토막났다. 누적 합/카운트는
                //  reseed 경로(ReapplyIdleThresholds / ReseedCycleStatesFromCurrentBoundary)가 비가동-제외 history
                //  전체로 시드하거나, 라이브 사이클이 0 에서 정확히 쌓는다.)
                _logger.LogInformation("Flow '{FlowName}' bootstrapped from history: MT={MT}ms", flowName, lastHistory[0].MT);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flow '{FlowName}' history bootstrap failed, starting fresh", flowName);
        }

        _flowCycleStates[flowName] = state;
    }

    /// <summary>
    /// Flow 가 head-start→tail-complete 엣지 래치로 배지를 도출할 자격이 있는지(순수 판정은 <see cref="FlowLatchBadge.IsEligible"/>).
    /// effective head/tail 이 비면(경계 미정) 부적격. head/tail 과 동명인 Call 이 여러 Work 에 있으면 경계가 모호해 강등.
    /// </summary>
    /// <inheritdoc />
    public async Task<TailSuggestion?> SuggestTailAsync(string flowName, string headCallName)
    {
        if (string.IsNullOrWhiteSpace(flowName) || string.IsNullOrWhiteSpace(headCallName)) return null;
        var flow = _projectService.GetFlowByName(flowName);
        if (flow is null) return null;

        List<string> options;
        try
        {
            options = _projectService.GetWorks(flow.Id)
                .SelectMany(w => _projectService.GetCalls(w.Id))
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n) && n != headCallName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SuggestTail: Call 목록 조회 실패 {Flow}", flowName); return null; }
        if (options.Count == 0) return null;

        var going = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try { going = await _dspRepository.GetCallGoingCountsAsync(flowName); } catch { /* 증거 없이도 제안은 한다 */ }
        going.TryGetValue(headCallName, out var headGoing);

        // 발화 횟수가 head 와 비슷한가(±20%) — 증거가 없으면(둘 다 0) 이 조건은 통과로 본다(신규 라인).
        bool Similar(string call)
        {
            if (headGoing <= 0) return true;
            going.TryGetValue(call, out var g);
            if (g <= 0) return false;
            return Math.Abs(g - headGoing) <= headGoing * 0.2;
        }

        // ① 같은 장비의 대응 동작 — 실측 6개 flow 중 4개가 이 규칙으로 정확(*_stp.ADV → *_stp.RET).
        var dot = headCallName.LastIndexOf('.');
        var headDevice = dot > 0 ? headCallName[..dot] : null;
        if (headDevice is not null)
        {
            var same = options.FirstOrDefault(o => o.StartsWith(headDevice + ".", StringComparison.Ordinal) && Similar(o));
            if (same is not null)
                return new TailSuggestion(same, "same-device",
                    $"같은 장비({headDevice}) 대응 동작 · 발화 {Going(same)}회");
        }

        // ② 토폴로지 종단(out-degree 0) 중 발화 근접 — 실측 '투입'(head=Conveyor1.MOVE)이 이 경로로 정확.
        //    ①은 Conveyor1.STOP(0회)을 고르려 해 배제되고, 종단 후보에서 1IN_CYL.RET 를 찾는다.
        try
        {
            var analysis = _flowAnalysisCache.GetOrAdd(flow.Name, _ => FlowAnalyzer.AnalyzeFlow(flow, GetDsStore()));
            var term = analysis.TailCandidates.FirstOrDefault(t => t != headCallName && options.Contains(t) && Similar(t));
            if (term is not null)
                return new TailSuggestion(term, "topology", $"공정 마지막 동작 · 발화 {Going(term)}회");
        }
        catch (Exception ex) { _logger.LogDebug(ex, "SuggestTail: 토폴로지 조회 실패 {Flow}", flowName); }

        // ③ 남은 후보 중 발화 횟수가 head 에 가장 가까운 것.
        var best = options.Where(Similar)
            .OrderBy(o => Math.Abs(Going(o) - headGoing))
            .ThenBy(o => o, StringComparer.Ordinal)
            .FirstOrDefault();
        return best is null ? null : new TailSuggestion(best, "frequency", $"발화 {Going(best)}회 — head 와 가장 근접");

        int Going(string c) => going.TryGetValue(c, out var g) ? g : 0;
    }

    private bool ComputeLatchEligibility(string flowName, string? effectiveStart, string? effectiveEnd)
    {
        if (string.IsNullOrEmpty(effectiveStart) || string.IsNullOrEmpty(effectiveEnd))
            return false;

        var flow = _projectService.GetFlowByName(flowName);
        if (flow is null) return false;

        try
        {
            var calls = _projectService.GetWorks(flow.Id)
                .SelectMany(w => _projectService.GetCalls(w.Id))
                .ToList();

            // 동명 Call 다중 Work → 어느 Call 의 이벤트로 래치를 여닫을지 모호 → 강등.
            bool headAmbiguous = calls.Count(c => c.Name == effectiveStart) > 1;
            bool tailAmbiguous = calls.Count(c => c.Name == effectiveEnd) > 1;

            var overrideConfig = _appSettingsService.GetFlowCycleOverride(flowName);
            bool hasOverride = overrideConfig is not null
                && !string.IsNullOrWhiteSpace(overrideConfig.StartCallName)
                && !string.IsNullOrWhiteSpace(overrideConfig.EndCallName);

            var (labelStart, labelEnd) = ResolveSequenceLabelBoundaries(flow);

            var analysis = _flowAnalysisCache.GetOrAdd(flow.Name,
                _ => FlowAnalyzer.AnalyzeFlow(flow, GetDsStore()));

            return FlowLatchBadge.IsEligible(
                hasExplicitOverride: hasOverride,
                hasHeadLabel: labelStart is not null,
                hasTailLabel: labelEnd is not null,
                topologyHeadCount: analysis.HeadCount,
                topologyTailCount: analysis.TailCount,
                headAmbiguous: headAmbiguous,
                tailAmbiguous: tailAmbiguous);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flow '{FlowName}' 래치 적격 판정 실패 — going-any 폴백", flowName);
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsLatchEligible(string flowName)
        => _flowCycleStates.TryGetValue(flowName, out var state) && state.LatchEligible;

    /// <inheritdoc />
    public bool IsLatchCycleActive(string flowName)
    {
        if (!_flowCycleStates.TryGetValue(flowName, out var state) || !state.LatchEligible) return false;
        lock (state.LatchLock) return state.IsCycleActive;
    }

    /// <inheritdoc />
    public string? GetLatchBadgeState(string flowName)
    {
        if (!_flowCycleStates.TryGetValue(flowName, out var state) || !state.LatchEligible)
            return null;

        bool active;
        DateTime? prevFinish;
        lock (state.LatchLock)
        {
            active = state.IsCycleActive;
            prevFinish = state.PreviousCycleFinish;
        }
        return FlowLatchBadge.Compute(active, prevFinish, DateTime.Now);
    }

    /// <inheritdoc />
    public IReadOnlyList<(string FlowName, DateTime CycleStart)> GetActiveLatchedCycles()
    {
        var result = new List<(string, DateTime)>();
        foreach (var kv in _flowCycleStates)
        {
            var state = kv.Value;
            if (!state.LatchEligible) continue;
            lock (state.LatchLock)
            {
                if (state.IsCycleActive && state.CurrentCycleStart.HasValue)
                    result.Add((kv.Key, state.CurrentCycleStart.Value));
            }
        }
        return result;
    }

    /// <inheritdoc />
    public bool AbandonLatchedCycle(string flowName)
    {
        if (!_flowCycleStates.TryGetValue(flowName, out var state) || !state.LatchEligible)
            return false;
        lock (state.LatchLock)
        {
            if (!state.IsCycleActive) return false;
            // 사이클/통계 미기록 — 기존 _timeoutAbandoned 의미와 동일. CurrentCycleStart 를 비워
            // 이후 tail 완료가 폐기된 시작으로 사이클을 기록하지 못하게 한다(다음 head-start 가 새로 세팅).
            state.IsCycleActive = false;
            state.CurrentCycleStart = null;
        }
        return true;
    }

    /// <inheritdoc />
    public bool TryForceOpenLatch(string flowName, DateTime cycleStart)
    {
        if (!_flowCycleStates.TryGetValue(flowName, out var state) || !state.LatchEligible)
            return false;
        lock (state.LatchLock)
        {
            if (state.IsCycleActive) return false;
            // head-start 엣지 유실 복구 — 래치만 연다(WT/CT 계산 없음). 후속 tail 완료가 추정 시작으로 MT 를
            // 기록하되, MaxMs 초과 시 워치독이 abandon 한다(배지는 래치를 읽기만, 메트릭 식 불변).
            state.IsCycleActive = true;
            state.CurrentCycleStart = cycleStart;
        }
        return true;
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

    /// <summary>
    /// 배지를 head-start→tail-complete 엣지 래치로 도출할지 여부. true 면 dspFlow.State 를 래치에서 직접 쓰고
    /// (Body 구간 "가동중" 유지), false(미정의·복수 head/tail·동명 모호)면 기존 going-any 폴백을 쓴다.
    /// <see cref="FlowAnalysis.FlowAnalyzer"/> 토폴로지/SequenceLabel/override 로 init·override 시 1회 산정.
    /// </summary>
    public bool LatchEligible { get; set; }

    /// <summary>
    /// 래치 3-필드(<see cref="IsCycleActive"/>/<see cref="CurrentCycleStart"/>/<see cref="PreviousCycleFinish"/>)의
    /// 교차 스레드 접근(이벤트 컨슈머 ↔ StateReconcile 워치독/교차검증) 보호용. 사소한 필드 대입만 감싸므로 경합 무시 가능.
    /// </summary>
    internal readonly object LatchLock = new();

    public bool IsCycleActive { get; set; }
    public DateTime? CurrentCycleStart { get; set; }
    public DateTime? PreviousCycleFinish { get; set; }
    public int? CurrentMT { get; set; }
    public int? CurrentWT { get; set; }
    public int? CurrentCT { get; set; }

    // 평균 계산용 필드
    // CycleCount = 비가동-제외 사이클의 누적 카운트(주로 history CycleNo 표시·로그용).
    public int CycleCount { get; set; } = 0;

    // 롤링 평균 윈도우 — 최근 비가동-제외 사이클(오래된→최신). 라이브 완료 때 enqueue 후 윈도우(N)로 trim 하고
    // 큐 평균을 dspFlow.Avg* 에 쓴다. 전(全)기간 누적합 대신 "최근 N 사이클"만 보여 요약 대시보드가 현재 거동 반영.
    // (윈도우 N = HistoryView.CycleAverageWindow, 0/음수면 trim 안 함 = 세션 누적). reseed 가 DB 최근행으로 채운다.
    public Queue<Adapters.CycleSample> Recent { get; } = new();
}

/// <summary>tail 1차 제안 결과. <paramref name="Source"/> = same-device | topology | frequency.</summary>
public sealed record TailSuggestion(string TailCallName, string Source, string Reason);
