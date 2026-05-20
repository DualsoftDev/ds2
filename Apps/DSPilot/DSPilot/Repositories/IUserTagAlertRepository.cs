using DSPilot.Models.UserTagAlerts;

namespace DSPilot.Repositories;

/// <summary>
/// UserTag 매칭 알림 저장소 — userTagAlertLog (raw) + userTagAlertDaily (집계).
/// </summary>
public interface IUserTagAlertRepository
{
    /// <summary>한 건 INSERT (서비스가 매칭 시점에 호출).</summary>
    Task<long> InsertAlertAsync(UserTagAlertRecord record, CancellationToken ct = default);

    /// <summary>주어진 기간의 알림을 최신순으로 반환 (페이지네이션 + 필터).</summary>
    Task<IReadOnlyList<UserTagAlertRecord>> QueryAlertsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? levelFilter, string? systemFilter,
        int limit, int offset,
        CancellationToken ct = default);

    /// <summary>주어진 기간의 알림 총 개수 (필터 동일 적용).</summary>
    Task<int> CountAlertsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? levelFilter, string? systemFilter,
        CancellationToken ct = default);

    /// <summary>시간 버킷별 레벨 카운트 — 차트용. bucketSeconds: 3600=시간, 86400=일, 604800=주, 2592000=월(30일 근사).</summary>
    Task<IReadOnlyList<UserTagAlertBucket>> GetBucketCountsAsync(
        DateTime startUtc, DateTime endUtc,
        string bucketGranularity,    // "hour" | "day" | "week" | "month"
        string? nameFilter, string? levelFilter, string? systemFilter,
        CancellationToken ct = default);

    /// <summary>태그별 Top N (이름 기준 카운트 내림차순).</summary>
    Task<IReadOnlyList<UserTagAlertTopRow>> GetTopByNameAsync(
        DateTime startUtc, DateTime endUtc,
        int topN,
        string? levelFilter, string? systemFilter,
        CancellationToken ct = default);

    /// <summary>레벨별 카운트 (Info/Warning/Error 도넛용).</summary>
    Task<IReadOnlyDictionary<string, int>> GetLevelCountsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? systemFilter,
        CancellationToken ct = default);

    /// <summary>가장 최근 알림 한 건 (각 주소별 "최근 알림" 컬럼용 — 최신 N건 한 번에 조회).</summary>
    Task<IReadOnlyList<UserTagAlertRecord>> GetLatestAlertsAsync(int maxCount, CancellationToken ct = default);

    /// <summary>가장 최근 알림 한 건의 ID — UI 폴링 비교용.</summary>
    Task<long> GetMaxAlertIdAsync(CancellationToken ct = default);

    /// <summary>cutoff 보다 오래된 row 삭제 — 보관 기간 정리.</summary>
    Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);

    /// <summary>day 단위로 raw 행을 집계해 userTagAlertDaily 에 upsert. 마지막 집계된 다음 날부터 어제까지.</summary>
    Task<int> RebuildDailyAggregatesAsync(DateTime fromDateUtc, DateTime toDateUtc, CancellationToken ct = default);

    /// <summary>가장 최근에 집계된 bucketDate (없으면 null).</summary>
    Task<DateTime?> GetLastAggregatedDateAsync(CancellationToken ct = default);
}
