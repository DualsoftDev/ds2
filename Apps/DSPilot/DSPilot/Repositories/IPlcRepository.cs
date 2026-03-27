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
    /// 여러 태그 주소에 대해 지정 시각 이전(이하)의 최신 로그를 태그별로 조회
    /// </summary>
    /// <param name="addresses">태그 주소 목록</param>
    /// <param name="atOrBefore">기준 시각</param>
    /// <returns>태그별 최신 로그 목록</returns>
    Task<List<PlcTagLogEntity>> GetLatestLogsByAddressesBeforeAsync(
        List<string> addresses, DateTime atOrBefore);

    /// <summary>
    /// 전체 로그 개수 조회
    /// </summary>
    Task<int> GetTotalLogCountAsync();

    /// <summary>
    /// 연결 테스트
    /// </summary>
    Task<bool> TestConnectionAsync();

    /// <summary>
    /// 특정 태그 주소의 시간 범위 로그 조회
    /// </summary>
    /// <param name="address">태그 주소 (예: X10A0)</param>
    /// <param name="startTime">시작 시각</param>
    /// <param name="endTime">종료 시각</param>
    /// <returns>시간 범위 내 로그 목록</returns>
    Task<List<PlcTagLogEntity>> GetTagLogsByAddressInRangeAsync(
        string address, DateTime startTime, DateTime endTime);

    /// <summary>
    /// 여러 태그의 시간 범위 로그 일괄 조회 (성능 최적화)
    /// </summary>
    /// <param name="addresses">태그 주소 목록</param>
    /// <param name="startTime">시작 시각</param>
    /// <param name="endTime">종료 시각</param>
    /// <returns>시간 범위 내 로그 목록</returns>
    Task<List<PlcTagLogEntity>> GetMultipleTagLogsInRangeAsync(
        List<string> addresses, DateTime startTime, DateTime endTime);

    /// <summary>
    /// 여러 태그의 Rising Edge(0→1) 로그만 시간 범위로 조회
    /// </summary>
    /// <param name="addresses">태그 주소 목록</param>
    /// <param name="startTime">시작 시각</param>
    /// <param name="endTime">종료 시각</param>
    /// <returns>시간 범위 내 Rising Edge 로그 목록</returns>
    Task<List<PlcTagLogEntity>> GetMultipleTagRisingEdgesInRangeAsync(
        List<string> addresses, DateTime startTime, DateTime endTime);

    /// <summary>
    /// 특정 태그의 Rising Edge (0→1) 시점 찾기
    /// </summary>
    /// <param name="address">태그 주소</param>
    /// <param name="startTime">시작 시각</param>
    /// <param name="endTime">종료 시각</param>
    /// <returns>Rising Edge 발생 시각 목록</returns>
    Task<List<DateTime>> FindRisingEdgesAsync(
        string address, DateTime startTime, DateTime endTime);

    /// <summary>
    /// 특정 태그의 최근 N개 Rising Edge (0→1) 시점만 빠르게 조회 (최신순)
    /// </summary>
    Task<List<DateTime>> FindRecentRisingEdgesAsync(string address, int count);

    /// <summary>
    /// 특정 태그의 최근 N개 로그 조회
    /// </summary>
    Task<List<PlcTagLogEntity>> GetTagLogsAsync(string tagAddress, int count);

    /// <summary>
    /// 특정 태그의 시간 범위 로그 조회
    /// </summary>
    Task<List<PlcTagLogEntity>> GetTagLogsByTimeRangeAsync(
        string tagAddress, DateTime startTime, DateTime endTime);

    /// <summary>
    /// 모든 태그의 최신 값을 배치로 조회 (N+1 쿼리 방지)
    /// </summary>
    /// <returns>(address, value, maxLogId) 튜플 목록</returns>
    Task<(Dictionary<string, string> TagValues, long MaxLogId)> GetLatestValuePerTagAsync();

    /// <summary>
    /// 지정 ID 이후의 새 로그를 일괄 조회 (델타 폴링용)
    /// </summary>
    /// <param name="afterId">이 ID보다 큰 로그만 조회</param>
    /// <returns>새 로그 목록 (address 포함)</returns>
    Task<List<PlcTagLogEntity>> GetLogsAfterIdAsync(long afterId);
}
