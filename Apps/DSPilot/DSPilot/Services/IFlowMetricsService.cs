namespace DSPilot.Services;

/// <summary>
/// Flow 메트릭 추적 서비스 인터페이스
/// - Flow별 대표 Work 분석
/// - MovingStartName/MovingEndName 설정
/// - MT/WT/CT 런타임 추적 및 갱신
/// </summary>
public interface IFlowMetricsService
{
    /// <summary>
    /// 초기화 완료 여부
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// 모든 Flow 분석 및 MovingStartName/MovingEndName 설정
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// AASX 기준 기본 사이클 시작/종료 Call 조회
    /// </summary>
    (string? StartCallName, string? EndCallName) GetAasxCycleBoundaries(string flowName);

    /// <summary>
    /// Flow별 사이클 시작/종료 Call override 적용
    /// </summary>
    Task ApplyCycleBoundaryOverrideAsync(string flowName, string? startCallName, string? endCallName);

    /// <summary>
    /// Flow의 사이클 시작/종료 Call 이름 조회 (런타임 기준)
    /// </summary>
    (string? HeadCallName, string? TailCallName) GetCycleBoundaryCallNames(string flowName);

    /// <summary>
    /// Flow 가 head-start→tail-complete 엣지 래치로 배지를 도출할 자격이 있는지.
    /// false 면 호출 측은 기존 going-any 폴백을 쓴다(미정의·복수 head/tail·동명 모호 Flow = 회귀 0).
    /// </summary>
    bool IsLatchEligible(string flowName);

    /// <summary>래치 적격 Flow 의 사이클이 현재 열려 있는지(Going). 부적격/미추적이면 false.</summary>
    bool IsLatchCycleActive(string flowName);

    /// <summary>
    /// 래치 적격 Flow 의 배지 상태("Going"/"Finish"/"Ready"). 부적격/미추적이면 null(→ going-any 폴백).
    /// <see cref="FlowLatchBadge.Compute"/> 순수 함수로 도출.
    /// </summary>
    string? GetLatchBadgeState(string flowName);

    /// <summary>
    /// 현재 사이클이 열린(IsCycleActive) 래치 적격 Flow 들의 (FlowName, CurrentCycleStart) 스냅샷.
    /// Phase 2 워치독이 경과 &gt; 유효 이상치 Max 인 박제 사이클을 찾는 데 쓴다.
    /// </summary>
    IReadOnlyList<(string FlowName, DateTime CycleStart)> GetActiveLatchedCycles();

    /// <summary>
    /// 워치독 timeout — 열린 래치를 사이클/통계 기록 없이 닫는다(기존 _timeoutAbandoned 와 동일 의미).
    /// 닫을 활성 사이클이 있었으면 true.
    /// </summary>
    bool AbandonLatchedCycle(string flowName);

    /// <summary>
    /// head-start 엣지 유실 복구(Phase 3 교차검증) — 닫힌 래치를 추정 시작 시각으로 연다. WT/CT 계산은 하지 않는다.
    /// 닫혀 있던 래치를 열었으면 true.
    /// </summary>
    bool TryForceOpenLatch(string flowName, DateTime cycleStart);

    /// <summary>
    /// 사이클 경계가 설정되어 추적 중인 Flow 이름 목록 (주기적 자동 재계산 대상 열거용).
    /// </summary>
    IReadOnlyCollection<string> GetTrackedFlowNames();

    /// <summary>
    /// Call Going 시작 이벤트 처리
    /// </summary>
    void OnCallGoingStarted(string flowName, string callName, DateTime timestamp);

    /// <summary>
    /// Call 완료 이벤트 처리
    /// </summary>
    void OnCallFinished(string flowName, string callName, DateTime timestamp);

    /// <summary>
    /// 현재 유효 비가동 범위(글로벌 HistoryView 기본 + per-flow CycleExclusion override)를 기존 히스토리/평균에
    /// 소급 적용. 글로벌 설정 저장 또는 per-flow 이상치 제외 변경 직후 호출하여, 변경이 대시보드·히스토리·평균·
    /// 시프트·OEE 에 즉시 일관 반영되게 한다(IsIdle 단일 소스 박제).
    /// </summary>
    /// <returns>(재평가된 히스토리 행 수, 재집계된 Flow 수)</returns>
    Task<(int HistoryRestamped, int FlowsRecomputed)> ReapplyIdleThresholdsAsync();

    /// <summary>
    /// 현재 boundary 와 일치하는 dspFlowHistory 행으로 in-memory Welford 누적기를 재시드.
    /// InvalidateCachesAsync 후 호출하여 다음 사이클이 누적 평균을 잘못 덮어쓰지 않게 한다.
    /// </summary>
    Task ReseedCycleStatesFromCurrentBoundaryAsync();
}
