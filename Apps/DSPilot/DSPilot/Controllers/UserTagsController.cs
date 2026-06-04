using DSPilot.Models.UserTagAlerts;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 UserTag(이상발생 관리) API.
/// Blazor /user-tags 가 쓰던 UserTagAlertService(정의) + IUserTagAlertRepository(쿼리) 를 얇게 래핑.
/// 8개 granular 쿼리를 하나의 /snapshot 으로 통합(라운드트립·레이스 축소). CSV 용 flat /alerts 별도.
/// 기간 프리셋→날짜·버킷 변환은 Blazor SetPresetState 와 동일하게 서버에서 처리(로컬 tz).
/// </summary>
[ApiController]
[Route("api/user-tags")]
public class UserTagsController : ControllerBase
{
    private const int PageSize = 10;

    private readonly UserTagAlertService _alertService;
    private readonly IUserTagAlertRepository _repo;

    public UserTagsController(UserTagAlertService alertService, IUserTagAlertRepository repo)
    {
        _alertService = alertService;
        _repo = repo;
    }

    [HttpGet("snapshot")]
    public async Task<ActionResult<UserTagSnapshotDto>> GetSnapshot(
        [FromQuery] string period = "today",
        [FromQuery] int page = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? level = null,
        [FromQuery] string? system = null,
        CancellationToken ct = default)
    {
        var (startLocal, endLocal, gran) = ResolvePeriod(period);
        var startUtc = startLocal.ToUniversalTime();
        var endUtc = endLocal.ToUniversalTime();
        var name = Blank(search);
        var lvl = Blank(level);
        var sys = Blank(system);

        var total = await _repo.CountAlertsAsync(startUtc, endUtc, name, lvl, sys, ct);
        var maxPage = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        if (page * PageSize >= total) page = 0;

        var dataPage = await _repo.QueryAlertsAsync(startUtc, endUtc, name, lvl, sys, PageSize, page * PageSize, ct);
        var buckets = await _repo.GetBucketCountsAsync(startUtc, endUtc, gran, name, lvl, sys, ct);
        var top = await _repo.GetTopByNameAsync(startUtc, endUtc, 10, lvl, sys, ct);
        var levelCounts = await _repo.GetLevelCountsAsync(startUtc, endUtc, name, sys, ct);

        // 히어로 운영지표 — 사용자 필터와 무관하게 "지금 관제 상황".
        var nowUtc = DateTime.UtcNow;
        var todayStartUtc = DateTime.Now.Date.ToUniversalTime();
        var activeError = await _repo.CountAlertsAsync(nowUtc - TimeSpan.FromMinutes(10), nowUtc, null, "Error", null, ct);
        var todayError = await _repo.CountAlertsAsync(todayStartUtc, nowUtc, null, "Error", null, ct);
        var latest = await _repo.GetLatestAlertsAsync(1, ct);
        var lastAlertAtLocal = latest.Count > 0 ? latest[0].OccurredAt.ToLocalTime().ToString("MM-dd HH:mm:ss") : null;

        var definitions = _alertService.GetDefinitions();
        var defDtos = definitions
            .Select(d => new UtDefinitionDto(d.SystemName, d.Name, d.LogLevel, d.TagAddress, d.ValueType, d.MatchOp, d.MatchValue))
            .ToList();
        var systemOptions = definitions.Select(d => d.SystemName).Distinct().OrderBy(s => s).ToList();

        return new UserTagSnapshotDto(
            period, startLocal.ToString("yyyy-MM-dd HH:mm"), endLocal.ToString("yyyy-MM-dd HH:mm"),
            gran, BucketLabel(gran),
            total, page, maxPage, PageSize,
            dataPage.Select(ToAlertDto).ToList(),
            buckets.Select(b => new UtBucketDto(b.BucketStart.ToLocalTime().ToString("o"), b.LogLevel, b.Count)).ToList(),
            top.Select(t => new UtTopDto(t.Name, t.LogLevel, t.Count)).ToList(),
            new Dictionary<string, int>(levelCounts),
            activeError, todayError, lastAlertAtLocal,
            defDtos, systemOptions);
    }

    /// <summary>
    /// 대시보드 이상(Error) 배너용 경량 상태 — 최근 10분 활성 Error 수 + 오늘 누적 + 최신 Error 1건.
    /// 대시보드가 스냅샷 주기(5초/SignalR)에 맞춰 폴링해 배너를 띄운다. snapshot 의 무거운 8쿼리를 피한다.
    /// </summary>
    [HttpGet("error-status")]
    public async Task<ActionResult<UserTagErrorStatusDto>> GetErrorStatus(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var activeWindowStart = nowUtc - TimeSpan.FromMinutes(10);
        var todayStartUtc = DateTime.Now.Date.ToUniversalTime();

        var activeError = await _repo.CountAlertsAsync(activeWindowStart, nowUtc, null, "Error", null, ct);
        var todayError = await _repo.CountAlertsAsync(todayStartUtc, nowUtc, null, "Error", null, ct);

        // 활성 창의 최신 Error 1건(배너 부제: 시각·시스템·태그명). 활성 0 이면 조회 생략.
        UserTagAlertRecord? latest = null;
        if (activeError > 0)
        {
            var page = await _repo.QueryAlertsAsync(activeWindowStart, nowUtc, null, "Error", null, 1, 0, ct);
            latest = page.Count > 0 ? page[0] : null;
        }

        return new UserTagErrorStatusDto(
            activeError,
            todayError,
            latest?.Id,
            latest is null ? null : latest.OccurredAt.ToLocalTime().ToString("MM-dd HH:mm:ss"),
            latest?.SystemName,
            latest?.Name);
    }

    /// <summary>CSV 내보내기용 — 현재 필터의 전체 알림(최신순, 상한 limit).</summary>
    [HttpGet("alerts")]
    public async Task<ActionResult<List<UtAlertDto>>> GetAlerts(
        [FromQuery] string period = "today",
        [FromQuery] string? search = null,
        [FromQuery] string? level = null,
        [FromQuery] string? system = null,
        [FromQuery] int limit = 100000,
        CancellationToken ct = default)
    {
        var (startLocal, endLocal, _) = ResolvePeriod(period);
        var all = await _repo.QueryAlertsAsync(
            startLocal.ToUniversalTime(), endLocal.ToUniversalTime(),
            Blank(search), Blank(level), Blank(system), limit, 0, ct);
        return all.Select(ToAlertDto).ToList();
    }

    private static UtAlertDto ToAlertDto(UserTagAlertRecord a) => new(
        a.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
        a.LogLevel, a.SystemName, a.Name, a.TagAddress, a.ValueType, a.MatchOp, a.MatchValue, a.ActualValue);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Blazor UserTags.SetPresetState 와 동일.
    private static (DateTime startLocal, DateTime endLocal, string gran) ResolvePeriod(string preset)
    {
        var now = DateTime.Now;
        return preset switch
        {
            "7d" => (now.Date.AddDays(-6), now, "day"),
            "30d" => (now.Date.AddDays(-29), now, "day"),
            "60d" => (now.Date.AddDays(-59), now, "week"),
            _ => (now.Date, now, "hour"),
        };
    }

    private static string BucketLabel(string g) => g switch
    {
        "hour" => "1시간",
        "day" => "1일",
        "week" => "1주",
        "month" => "1개월",
        _ => g,
    };
}
