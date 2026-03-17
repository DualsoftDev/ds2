using DSPilot.Models.Dsp;

namespace DSPilot.Repositories;

/// <summary>
/// DSP 실시간 데이터베이스 저장소 인터페이스
/// </summary>
public interface IDspRepository
{
    /// <summary>
    /// 스키마 생성 (Call, Flow, CallFlowView)
    /// </summary>
    Task<bool> CreateSchemaAsync();

    /// <summary>
    /// Flow 대량 삽입 (AASX 초기 로드용)
    /// </summary>
    Task<int> BulkInsertFlowsAsync(List<DspFlowEntity> flows);

    /// <summary>
    /// Call 대량 삽입 (AASX 초기 로드용)
    /// </summary>
    Task<int> BulkInsertCallsAsync(List<DspCallEntity> calls);

    /// <summary>
    /// Call 상태 조회
    /// </summary>
    Task<string> GetCallStateAsync(string callName);

    /// <summary>
    /// Call 상태 업데이트
    /// </summary>
    Task<bool> UpdateCallStateAsync(string callName, string state);

    /// <summary>
    /// Call 상태 및 통계 업데이트 (Going → Finish 시)
    /// </summary>
    Task<bool> UpdateCallWithStatisticsAsync(
        string callName,
        string state,
        int previousGoingTime,
        double averageGoingTime,
        double stdDevGoingTime);

    /// <summary>
    /// Flow 상태 업데이트
    /// </summary>
    Task<bool> UpdateFlowStateAsync(string flowName, string state);

    /// <summary>
    /// 전체 데이터 삭제 (재초기화용)
    /// </summary>
    Task<bool> ClearAllDataAsync();

    /// <summary>
    /// Heatmap용 Call 통계 데이터 조회 (GoingCount > 0인 항목만)
    /// </summary>
    Task<List<CallStatisticsDto>> GetCallStatisticsAsync();
}

/// <summary>
/// Call 통계 DTO
/// </summary>
public class CallStatisticsDto
{
    public string CallName { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public string WorkName { get; set; } = string.Empty;
    public double AverageGoingTime { get; set; }
    public double StdDevGoingTime { get; set; }
    public int GoingCount { get; set; }
}
