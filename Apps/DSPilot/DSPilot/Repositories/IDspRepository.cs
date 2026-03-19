using DSPilot.Models;
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
    /// Call 상태 조회 (CallKey 기반)
    /// </summary>
    Task<string> GetCallStateAsync(CallKey key);

    /// <summary>
    /// Call 정보 조회 (WorkName, FlowName 포함)
    /// </summary>
    Task<(string WorkName, string FlowName)?> GetCallInfoAsync(string callName);

    /// <summary>
    /// Call 전체 데이터 조회 (GoingCount 등 포함, CallKey 기반)
    /// </summary>
    Task<DspCallEntity?> GetCallByKeyAsync(CallKey key);

    /// <summary>
    /// Call 상태 업데이트 (CallKey 기반)
    /// </summary>
    Task<bool> UpdateCallStateAsync(CallKey key, string state);

    /// <summary>
    /// Call 상태 및 통계 업데이트 (Going → Finish 시, CallKey 기반)
    /// </summary>
    Task<bool> UpdateCallWithStatisticsAsync(
        CallKey key,
        string state,
        int previousGoingTime,
        double averageGoingTime,
        double stdDevGoingTime);

    /// <summary>
    /// Flow 상태 업데이트
    /// </summary>
    Task<bool> UpdateFlowStateAsync(string flowName, string state);

    /// <summary>
    /// Flow 내 Going 상태 Call 존재 여부 확인
    /// </summary>
    Task<bool> HasGoingCallsInFlowAsync(string flowName);

    /// <summary>
    /// Flow 메트릭 업데이트 (MT, WT, CT, MovingStartName, MovingEndName)
    /// </summary>
    Task<bool> UpdateFlowMetricsAsync(
        string flowName,
        int? mt,
        int? wt,
        int? ct,
        string? movingStartName,
        string? movingEndName);

    /// <summary>
    /// 전체 데이터 삭제 (재초기화용)
    /// </summary>
    Task<bool> ClearAllDataAsync();

    /// <summary>
    /// 데이터베이스 정리 (중복 제거, 무결성 검사)
    /// </summary>
    Task CleanupDatabaseAsync();

    /// <summary>
    /// Heatmap용 Call 통계 데이터 조회 (GoingCount > 0인 항목만)
    /// </summary>
    Task<List<CallStatisticsDto>> GetCallStatisticsAsync();

    /// <summary>
    /// Call IO 이벤트 삽입 (In/Out Tag Rising Edge 기록)
    /// </summary>
    Task InsertCallIOEventAsync(Models.Analysis.CallIOEvent ioEvent);
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
