using DSPilot.Models.Plc;

namespace DSPilot.Abstractions;

/// <summary>
/// PLC 이력 데이터 소스 인터페이스
/// CycleAnalysis 등 과거 데이터 조회용
/// </summary>
public interface IPlcHistorySource
{
    /// <summary>
    /// 지정한 시간 범위의 로그 조회
    /// </summary>
    Task<List<PlcTagLogEntity>> GetLogsInRangeAsync(DateTime startExclusive, DateTime endInclusive);

    /// <summary>
    /// 특정 주소의 Rising Edge 시간 목록 조회
    /// </summary>
    Task<List<DateTime>> FindRisingEdgesAsync(string address, DateTime startTime, DateTime endTime);

    /// <summary>
    /// 여러 태그의 Rising Edge 조회 (배치)
    /// </summary>
    Task<List<PlcTagLogEntity>> GetMultipleTagRisingEdgesInRangeAsync(
        List<string> addresses,
        DateTime startTime,
        DateTime endTime);

    /// <summary>
    /// 특정 주소의 로그 조회
    /// </summary>
    Task<List<PlcTagLogEntity>> GetTagLogsByAddressInRangeAsync(
        string address,
        DateTime startTime,
        DateTime endTime);

    /// <summary>
    /// 가장 오래된 로그 시간 조회
    /// </summary>
    Task<DateTime?> GetOldestLogDateTimeAsync();

    /// <summary>
    /// 가장 최근 로그 시간 조회
    /// </summary>
    Task<DateTime?> GetLatestLogDateTimeAsync();
}
