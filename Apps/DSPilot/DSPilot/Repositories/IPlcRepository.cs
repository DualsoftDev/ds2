using DSPilot.Models.Plc;

namespace DSPilot.Repositories;

/// <summary>
/// PLC 데이터 저장소 인터페이스
/// </summary>
public interface IPlcRepository
{
    /// <summary>
    /// 모든 PLC 정보 조회
    /// </summary>
    Task<List<PlcEntity>> GetAllPlcsAsync();

    /// <summary>
    /// 모든 PLC 태그 정보 조회
    /// </summary>
    Task<List<PlcTagEntity>> GetAllTagsAsync();

    /// <summary>
    /// 특정 시간 이후의 새로운 로그 조회 (증분 읽기)
    /// </summary>
    /// <param name="sinceDateTime">마지막 읽은 시간</param>
    /// <returns>새로운 로그 목록</returns>
    Task<List<PlcTagLogEntity>> GetNewLogsAsync(DateTime sinceDateTime);

    /// <summary>
    /// 지정한 시간 범위의 로그 조회
    /// </summary>
    /// <param name="startExclusive">시작 시각(초과)</param>
    /// <param name="endInclusive">종료 시각(이하)</param>
    /// <returns>범위 내 로그 목록</returns>
    Task<List<PlcTagLogEntity>> GetLogsInRangeAsync(DateTime startExclusive, DateTime endInclusive);

    /// <summary>
    /// 가장 오래된 로그 시각 조회
    /// </summary>
    Task<DateTime?> GetOldestLogDateTimeAsync();

    /// <summary>
    /// 가장 최신 로그 시각 조회
    /// </summary>
    Task<DateTime?> GetLatestLogDateTimeAsync();

    /// <summary>
    /// ID로 태그 정보 조회
    /// </summary>
    /// <param name="tagId">태그 ID</param>
    /// <returns>태그 정보</returns>
    Task<PlcTagEntity?> GetTagByIdAsync(int tagId);

    /// <summary>
    /// 특정 태그의 최신 로그 조회
    /// </summary>
    /// <param name="tagId">태그 ID</param>
    /// <returns>최신 로그</returns>
    Task<PlcTagLogEntity?> GetLatestLogByTagIdAsync(int tagId);

    /// <summary>
    /// 전체 로그 개수 조회
    /// </summary>
    Task<int> GetTotalLogCountAsync();

    /// <summary>
    /// 연결 테스트
    /// </summary>
    Task<bool> TestConnectionAsync();
}
