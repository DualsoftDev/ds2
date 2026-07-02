// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
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
    private readonly AppSettingsService _settings;

    public UserTagsController(UserTagAlertService alertService, IUserTagAlertRepository repo, AppSettingsService settings)
    {
        _alertService = alertService;
        _repo = repo;
        _settings = settings;
    }

    // DSPilot 은 Error 레벨만 표시한다(운영 정책 — usertag/abnormal 모두 Error 취급, Warning/Info 는 미사용).
    // 서버에서 강제하므로 클라이언트가 다른 레벨을 요청해도 무시된다.
    private const string DisplayLevel = "Error";

    [HttpGet("snapshot")]
    public async Task<ActionResult<UserTagSnapshotDto>> GetSnapshot(
        [FromQuery] string period = "today",
        [FromQuery] int page = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,   // "abnormal" | "usertag" | null(전체 구분)
        [FromQuery] string? system = null,
        [FromQuery] string? flow = null,        // 설비(Flow)명 — 자동감지(Abnormal)만 그 Flow 로 필터(UserTag 자동 제외)
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var (startLocal, endLocal, gran) = ResolvePeriod(period, from, to);
        var startUtc = startLocal.ToUniversalTime();
        var endUtc = endLocal.ToUniversalTime();
        var name = Blank(search);
        var lvl = DisplayLevel;               // Error 고정
        var sys = Blank(system);
        var flw = Blank(flow);
        // flow 필터가 걸리면 자동감지(Abnormal)만 남으므로 구분 필터는 무의미 → 무시(모순 방지: flow+usertag=0건).
        var cat = flw is null ? Blank(category) : null;

        var total = await _repo.CountAlertsAsync(startUtc, endUtc, name, lvl, sys, cat, ct, flowFilter: flw);
        var maxPage = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        if (page * PageSize >= total) page = 0;

        var dataPage = await _repo.QueryAlertsAsync(startUtc, endUtc, name, lvl, sys, cat, PageSize, page * PageSize, ct, flowFilter: flw);
        var buckets = FillBucketGaps(
            await _repo.GetBucketCountsAsync(startUtc, endUtc, gran, name, lvl, sys, cat, ct, flowFilter: flw),
            startUtc, endUtc, gran);
        var top = await _repo.GetTopByNameAsync(startUtc, endUtc, 10, lvl, sys, cat, "name", ct, flowFilter: flw);
        var topByPath = await _repo.GetTopByNameAsync(startUtc, endUtc, 10, lvl, sys, cat, "path", ct, flowFilter: flw);
        // 구분(ABNORMAL/USERTAG) 도넛 — 구분 필터와 무관하게 항상 두 구분을 함께 집계(Error 레벨 한정).
        // flow 선택 시엔 자동감지만 남아 도넛도 ABNORMAL 단일이 된다(프런트에서 도넛 UI 숨김).
        var categoryCounts = await _repo.GetCategoryCountsAsync(startUtc, endUtc, name, lvl, sys, ct, flowFilter: flw);

        // 히어로 운영지표 — 사용자 필터와 무관하게 "지금 관제 상황".
        var nowUtc = DateTime.UtcNow;
        var todayStartUtc = DateTime.Now.Date.ToUniversalTime();
        var activeError = await _repo.CountAlertsAsync(nowUtc - TimeSpan.FromMinutes(10), nowUtc, null, "Error", null, null, ct);
        var todayError = await _repo.CountAlertsAsync(todayStartUtc, nowUtc, null, "Error", null, null, ct);
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
            topByPath.Select(t => new UtTopDto(t.Name, t.LogLevel, t.Count)).ToList(),
            new Dictionary<string, int>(categoryCounts),
            activeError, todayError, lastAlertAtLocal,
            defDtos, systemOptions,
            _settings.LoadSettings().Ui.AlarmTickerIntervalSec);
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

        var activeError = await _repo.CountAlertsAsync(activeWindowStart, nowUtc, null, "Error", null, null, ct);
        var todayError = await _repo.CountAlertsAsync(todayStartUtc, nowUtc, null, "Error", null, null, ct);

        // 활성 창의 최신 Error 1건(배너 부제: 시각·시스템·태그명). 활성 0 이면 조회 생략.
        UserTagAlertRecord? latest = null;
        if (activeError > 0)
        {
            var page = await _repo.QueryAlertsAsync(activeWindowStart, nowUtc, null, "Error", null, null, 1, 0, ct);
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

    /// <summary>
    /// 정의된 UserTag(사용자 정의 에러) 목록만 반환하는 경량 엔드포인트 — 설정 &gt; 일반 탭의
    /// 읽기전용 조회용. snapshot 의 무거운 집계 쿼리 없이 AASX System 정의(GetDefinitions)만 얇게 래핑.
    /// </summary>
    [HttpGet("definitions")]
    public ActionResult<List<UtDefinitionDto>> GetDefinitions()
    {
        var defs = _alertService.GetDefinitions()
            .Select(d => new UtDefinitionDto(d.SystemName, d.Name, d.LogLevel, d.TagAddress, d.ValueType, d.MatchOp, d.MatchValue))
            .OrderBy(d => d.SystemName).ThenBy(d => d.Name)
            .ToList();
        return defs;
    }

    /// <summary>CSV 내보내기용 — 현재 필터의 전체 알림(최신순, 상한 limit).</summary>
    [HttpGet("alerts")]
    public async Task<ActionResult<List<UtAlertDto>>> GetAlerts(
        [FromQuery] string period = "today",
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,   // "abnormal" | "usertag" | null(전체 구분)
        [FromQuery] string? system = null,
        [FromQuery] string? flow = null,        // 설비(Flow)명 — 자동감지(Abnormal)만 그 Flow 로 필터
        [FromQuery] int limit = 100000,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var (startLocal, endLocal, _) = ResolvePeriod(period, from, to);
        var flw = Blank(flow);
        var cat = flw is null ? Blank(category) : null; // flow 필터 시 구분 필터 무시(snapshot 과 동일)
        var all = await _repo.QueryAlertsAsync(
            startLocal.ToUniversalTime(), endLocal.ToUniversalTime(),
            Blank(search), DisplayLevel, Blank(system), cat, limit, 0, ct, flowFilter: flw);
        return all.Select(ToAlertDto).ToList();
    }

    /// <summary>Excel(.xlsx) 내보내기 — 현재 필터의 전체 알림을 단일 시트 테이블로. CSV(/alerts)와 동일 데이터원.</summary>
    [HttpGet("excel")]
    public async Task<IActionResult> GetExcel(
        [FromQuery] string period = "today",
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        [FromQuery] string? system = null,
        [FromQuery] string? flow = null,        // 설비(Flow)명 — 자동감지(Abnormal)만 그 Flow 로 필터
        [FromQuery] int limit = 100000,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var (startLocal, endLocal, _) = ResolvePeriod(period, from, to);
        var flw = Blank(flow);
        var cat = flw is null ? Blank(category) : null; // flow 필터 시 구분 필터 무시(snapshot 과 동일)
        var all = await _repo.QueryAlertsAsync(
            startLocal.ToUniversalTime(), endLocal.ToUniversalTime(),
            Blank(search), DisplayLevel, Blank(system), cat, limit, 0, ct, flowFilter: flw);
        var bytes = UserTagAlertExcelExporter.Build(all, startLocal, endLocal, flw);
        var fn = $"UserTagAlerts_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return File(bytes, UserTagAlertExcelExporter.XlsxMimeType, fn);
    }

    private static UtAlertDto ToAlertDto(UserTagAlertRecord a) => new(
        a.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
        a.LogLevel, a.SystemName, a.Name, a.TagAddress, a.ValueType, a.MatchOp, a.MatchValue, a.ActualValue);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Blazor UserTags.SetPresetState 와 동일.
    // period="custom" 이면 from/to(로컬 벽시계)를 그대로 사용 — 기간 직접선택 + 피드 알람 진입(그 날 하루)이 이 경로.
    // (없으면 today 로 폴백. 호출자는 startLocal.ToUniversalTime() 로 UTC 변환하므로 Kind=Unspecified 도 로컬로 해석됨.)
    private static (DateTime startLocal, DateTime endLocal, string gran) ResolvePeriod(
        string preset, DateTime? from = null, DateTime? to = null)
    {
        var now = DateTime.Now;
        if (preset == "custom" && from.HasValue && to.HasValue && to.Value > from.Value)
        {
            var days = (to.Value - from.Value).TotalDays;
            var gran = days > 45 ? "week" : days > 2 ? "day" : "hour";
            return (from.Value, to.Value, gran);
        }
        return preset switch
        {
            "7d" => (now.Date.AddDays(-6), now, "day"),
            "30d" => (now.Date.AddDays(-29), now, "day"),
            "60d" => (now.Date.AddDays(-59), now, "week"),
            _ => (now.Date, now, "hour"),
        };
    }

    // 시계열 연속성 규약: 조회 기간 내 모든 단위시간 버킷을 빠짐없이 채운다(데이터 없는 슬롯은 count=0).
    // GetBucketCountsAsync 는 GROUP BY 결과라 알람이 없는 구간을 통째로 건너뛰어, 시간축 차트가 데이터
    // 첫/끝 지점 사이만 그려지고 빈 구간이 잘렸다. 슬롯 정렬은 SQL(strftime, UTC 기준)과 동일하게 맞춘다.
    private static IReadOnlyList<UserTagAlertBucket> FillBucketGaps(
        IReadOnlyList<UserTagAlertBucket> buckets, DateTime startUtc, DateTime endUtc, string gran)
    {
        var present = new HashSet<DateTime>(buckets.Select(b => b.BucketStart));
        var merged = new List<UserTagAlertBucket>(buckets);
        var slot = TruncBucketUtc(startUtc, gran);
        // 빈 슬롯은 x축 연속성 확보용(count=0) — 구분 스택 키는 임의(USERTAG), 0건이라 어떤 시리즈에도 기여하지 않음.
        for (var guard = 0; slot <= endUtc && guard < 100000; guard++, slot = NextBucketUtc(slot, gran))
            if (present.Add(slot)) merged.Add(new UserTagAlertBucket(slot, "USERTAG", 0));
        return merged.OrderBy(b => b.BucketStart).ToList();
    }

    private static DateTime TruncBucketUtc(DateTime utc, string gran) => gran switch
    {
        "hour"  => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc),
        // ISO 주(월요일 시작) — SQL 의 (%w+6)%7 보정과 동일.
        "week"  => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-(((int)utc.DayOfWeek + 6) % 7)),
        "month" => new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        _        => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc), // day
    };

    private static DateTime NextBucketUtc(DateTime utc, string gran) => gran switch
    {
        "hour"  => utc.AddHours(1),
        "week"  => utc.AddDays(7),
        "month" => utc.AddMonths(1),
        _        => utc.AddDays(1), // day
    };

    private static string BucketLabel(string g) => g switch
    {
        "hour" => "1시간",
        "day" => "1일",
        "week" => "1주",
        "month" => "1개월",
        _ => g,
    };
}
