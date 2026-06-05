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
    /// 현재 설정의 비가동 임계값(Max/MinCycleTimeMs)을 기존 히스토리/평균에 소급 적용.
    /// 설정 저장 직후 호출하여, 임계값 변경이 대시보드·히스토리·평균에 즉시 반영되게 한다.
    /// </summary>
    /// <returns>(재평가된 히스토리 행 수, 재집계된 Flow 수)</returns>
    Task<(int HistoryRestamped, int FlowsRecomputed)> ReapplyIdleThresholdsAsync();

    /// <summary>
    /// 현재 boundary 와 일치하는 dspFlowHistory 행으로 in-memory Welford 누적기를 재시드.
    /// InvalidateCachesAsync 후 호출하여 다음 사이클이 누적 평균을 잘못 덮어쓰지 않게 한다.
    /// </summary>
    Task ReseedCycleStatesFromCurrentBoundaryAsync();
}
