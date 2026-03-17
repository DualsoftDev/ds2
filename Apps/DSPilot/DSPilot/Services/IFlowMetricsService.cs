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
    /// 모든 Flow 분석 및 MovingStartName/MovingEndName 설정
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Call Going 시작 이벤트 처리
    /// </summary>
    void OnCallGoingStarted(string callName, DateTime timestamp);

    /// <summary>
    /// Call 완료 이벤트 처리
    /// </summary>
    void OnCallFinished(string callName, DateTime timestamp);
}
