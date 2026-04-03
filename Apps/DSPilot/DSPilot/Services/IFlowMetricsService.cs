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
    /// Call Going 시작 이벤트 처리
    /// </summary>
    void OnCallGoingStarted(string flowName, string callName, DateTime timestamp);

    /// <summary>
    /// Call 완료 이벤트 처리
    /// </summary>
    void OnCallFinished(string flowName, string callName, DateTime timestamp);
}
