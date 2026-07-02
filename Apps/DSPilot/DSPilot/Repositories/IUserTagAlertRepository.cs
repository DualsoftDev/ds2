// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models.UserTagAlerts;

namespace DSPilot.Repositories;

/// <summary>
/// UserTag 매칭 알림 저장소 — userTagAlertLog (raw) + userTagAlertDaily (집계).
/// </summary>
public interface IUserTagAlertRepository
{
    /// <summary>한 건 INSERT (서비스가 매칭 시점에 호출).</summary>
    Task<long> InsertAlertAsync(UserTagAlertRecord record, CancellationToken ct = default);

    /// <summary>주어진 기간의 알림을 최신순으로 반환 (페이지네이션 + 필터).
    /// categoryFilter: "abnormal"(경로이탈 이상감지) | "usertag"(사용자정의) | null(전체).
    /// flowFilter: 설비(Flow)명 — tagAddress 맨 앞 세그먼트로 자동감지(Abnormal)만 그 Flow 로 거른다(UserTag 자동 제외).</summary>
    Task<IReadOnlyList<UserTagAlertRecord>> QueryAlertsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? levelFilter, string? systemFilter, string? categoryFilter,
        int limit, int offset,
        CancellationToken ct = default, string? flowFilter = null,
        string? sortColumn = null, bool sortDesc = true);

    /// <summary>주어진 기간의 알림 총 개수 (필터 동일 적용).</summary>
    Task<int> CountAlertsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? levelFilter, string? systemFilter, string? categoryFilter = null,
        CancellationToken ct = default, string? flowFilter = null);

    /// <summary>시간 버킷별 <b>구분(ABNORMAL/USERTAG)</b> 카운트 — 차트용(스택 막대). 레벨이 Error 단일로 통일돼
    /// 버킷은 레벨 대신 구분으로 스택한다(반환 DTO 의 LogLevel 슬롯에 구분 문자열을 담는다).</summary>
    Task<IReadOnlyList<UserTagAlertBucket>> GetBucketCountsAsync(
        DateTime startUtc, DateTime endUtc,
        string bucketGranularity,    // "hour" | "day" | "week" | "month"
        string? nameFilter, string? levelFilter, string? systemFilter, string? categoryFilter,
        CancellationToken ct = default, string? flowFilter = null);

    /// <summary>태그별 Top N (카운트 내림차순). groupBy="name"(기본, 이름 기준 — abnormal 은 4개 유형으로 묶임)
    /// | "path"(경로 tagAddress 기준 — abnormal 을 경로별로 펼침). 반환 TopRow 의 Name 슬롯에 그룹키를 담는다.</summary>
    Task<IReadOnlyList<UserTagAlertTopRow>> GetTopByNameAsync(
        DateTime startUtc, DateTime endUtc,
        int topN,
        string? levelFilter, string? systemFilter, string? categoryFilter,
        string groupBy = "name",
        CancellationToken ct = default, string? flowFilter = null);

    /// <summary>구분별 카운트 (ABNORMAL/USERTAG 도넛용). 키 = "ABNORMAL" | "USERTAG".</summary>
    Task<IReadOnlyDictionary<string, int>> GetCategoryCountsAsync(
        DateTime startUtc, DateTime endUtc,
        string? nameFilter, string? levelFilter, string? systemFilter,
        CancellationToken ct = default, string? flowFilter = null);

    /// <summary>가장 최근 알림 한 건 (각 주소별 "최근 알림" 컬럼용 — 최신 N건 한 번에 조회).</summary>
    Task<IReadOnlyList<UserTagAlertRecord>> GetLatestAlertsAsync(int maxCount, CancellationToken ct = default);

    /// <summary>가장 최근 알림 한 건의 ID — UI 폴링 비교용.</summary>
    Task<long> GetMaxAlertIdAsync(CancellationToken ct = default);

    /// <summary>day 단위로 raw 행을 집계해 userTagAlertDaily 에 upsert. 마지막 집계된 다음 날부터 어제까지.</summary>
    Task<int> RebuildDailyAggregatesAsync(DateTime fromDateUtc, DateTime toDateUtc, CancellationToken ct = default);

    /// <summary>가장 최근에 집계된 bucketDate (없으면 null).</summary>
    Task<DateTime?> GetLastAggregatedDateAsync(CancellationToken ct = default);
}
