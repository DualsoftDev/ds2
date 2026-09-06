// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Ds2.Editor;
using DSPilot.Infrastructure;
using DSPilot.Models.UserTagAlerts;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 UserTag(이상발생 관리) API.
/// Blazor /user-tags 가 쓰던 UserTagAlertService(정의) + IUserTagAlertRepository(쿼리) 를 얇게 래핑.
/// 8개 granular 쿼리를 하나의 /snapshot 으로 통합(라운드트립·레이스 축소).
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
    private readonly DsProjectService _project;
    private readonly ILogger<UserTagsController> _logger;

    public UserTagsController(
        UserTagAlertService alertService, IUserTagAlertRepository repo, AppSettingsService settings,
        DsProjectService project, ILogger<UserTagsController> logger)
    {
        _alertService = alertService;
        _repo = repo;
        _settings = settings;
        _project = project;
        _logger = logger;
    }

    // DSPilot 은 Error 레벨만 표시한다(운영 정책 — usertag/abnormal 모두 Error 취급, Warning/Info 는 미사용).
    // 서버에서 강제하므로 클라이언트가 다른 레벨을 요청해도 무시된다.
    private const string DisplayLevel = "Error";

    [HttpGet("snapshot")]
    public async Task<ActionResult<UserTagSnapshotDto>> GetSnapshot(
        [FromQuery] string period = "today",
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = PageSize, // 페이지 크기 — 기본 10, 클라이언트가 선택(허용 목록으로 클램프)
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,   // "abnormal" | "usertag" | null(전체 구분)
        [FromQuery] string? system = null,
        [FromQuery] string? flow = null,        // 설비(Flow)명 — 자동감지(Abnormal)만 그 Flow 로 필터(UserTag 자동 제외)
        [FromQuery] string? sort = null,        // 정렬 컬럼 키(occurredAt|name|systemName|matchOp|valueType), 기본 occurredAt
        [FromQuery] string? sortDir = null,     // "asc" | "desc"(기본)
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var (startLocal, endLocal, gran) = ResolvePeriod(period, from, to);
        var startUtc = startLocal.ToUniversalTime();
        var endUtc = endLocal.ToUniversalTime();
        // 커스텀 기간 스팬 상한(2개월) — UI 는 자체 클램프하지만 외부 API 소비자 방어(끝 기준으로 시작을 당김).
        // shell.js DSP_MAX_RANGE_DAYS(62)와 동일 값, 인메모리 미러 창(63일)보다 작게 유지.
        if ((endUtc - startUtc).TotalDays > 62)
            startUtc = endUtc.AddDays(-62);
        var name = Blank(search);
        var lvl = DisplayLevel;               // Error 고정
        var sys = Blank(system);
        var flw = Blank(flow);
        // flow 필터가 걸리면 자동감지(Abnormal)만 남으므로 구분 필터는 무의미 → 무시(모순 방지: flow+usertag=0건).
        var cat = flw is null ? Blank(category) : null;
        var size = Math.Clamp(pageSize, 5, 200);
        var sortDesc = !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);

        // 요청당 쿼리 9개(집계 4개 포함) — 탭당 10초 폴링 × 동접 탭 수만큼 곱해지므로 10초 TTL 로
        // 코얼레싱. endUtc 는 보통 '지금'이라 키만 10초 격자로 양자화(staleness = 폴링 주기 이하).
        var cacheKey = $"usertags/snapshot|{page}|{size}|{name}|{cat}|{sys}|{flw}|{Blank(sort)}|{sortDesc}|{gran}"
                       + $"|{startUtc.Ticks}|{endUtc.Ticks / (TimeSpan.TicksPerSecond * 10)}";
        return await TtlRequestCache.GetOrComputeAsync(cacheKey, TimeSpan.FromSeconds(10), async () =>
        {
        var total = await _repo.CountAlertsAsync(startUtc, endUtc, name, lvl, sys, cat, ct, flowFilter: flw);
        var maxPage = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        if (page * size >= total) page = 0;

        var dataPage = await _repo.QueryAlertsAsync(startUtc, endUtc, name, lvl, sys, cat, size, page * size, ct, flowFilter: flw, sortColumn: Blank(sort), sortDesc: sortDesc);
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
            total, page, maxPage, size,
            dataPage.Select(ToAlertDto).ToList(),
            buckets.Select(b => new UtBucketDto(b.BucketStart.ToLocalTime().ToString("o"), b.LogLevel, b.Count)).ToList(),
            top.Select(t => new UtTopDto(t.Name, t.LogLevel, t.Count)).ToList(),
            topByPath.Select(t => new UtTopDto(t.Name, t.LogLevel, t.Count)).ToList(),
            new Dictionary<string, int>(categoryCounts),
            activeError, todayError, lastAlertAtLocal,
            defDtos, systemOptions,
            _settings.LoadSettings().Ui.AlarmTickerIntervalSec);
        });
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

    // ── 설정▸수동등록TAG 편집기 ────────────────────────────────────────────────
    //   정의의 정본은 공유 project.aasx(System.LoggingProperties.UserTags). 여기서 편집한 결과는
    //   DsProjectService.WriteUserTagsAndExport 가 store 교체 → AID 주소 병합 → 재export 하고,
    //   Agent 는 aasx 파일 워처로 재시작해 새 주소를 수집한다(엣지 스캐너는 정적 설정이라 별도 배포).

    /// <summary>편집기 초기 데이터 — 활성 System(endpoint 유무 포함) + 현재 태그 + 허용 값 표. AASX store 직독(집계 없음).</summary>
    [HttpGet("editor")]
    public ActionResult<UtEditorDto> GetEditor()
    {
        var matchOps = new Dictionary<string, string[]>();
        foreach (var vt in UserTagEditorSupport.ValueTypes) matchOps[vt] = UserTagEditorSupport.MatchOpsFor(vt);

        if (!_project.IsLoaded)
            return new UtEditorDto([], [], UserTagEditorSupport.ValueTypes, matchOps, 0, false);

        var endpointBySystem = _project.GetPlcEndpoints()
            .SelectMany(e => e.SystemName.Split('·').Select(n => (Name: n, Ep: $"{e.Ip}:{e.Port}")))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Ep, StringComparer.OrdinalIgnoreCase);
        var active = _project.GetActiveSystems();
        var systems = active
            .Select(s => new UtEditorSystemDto(
                s.Id.ToString(), s.Name,
                endpointBySystem.ContainsKey(s.Name),
                endpointBySystem.TryGetValue(s.Name, out var ep) ? ep : null))
            .ToList();
        var activeIds = active.Select(s => s.Id).ToHashSet();

        var rows = _project.GetStore().GetAllUserTagsForProject();
        var tags = rows
            .Where(r => activeIds.Contains(r.SystemId))
            .Select(r => new UtEditorTagDto(
                r.SystemId.ToString(), r.SystemName, r.Name, r.TagAddress,
                UserTagEditorSupport.NormalizeValueType(r.ValueType) ?? "Bit",
                UserTagEditorSupport.NormalizeMatchOp(r.MatchOp, r.ValueType) ?? "RisingEdge",
                r.MatchValue))
            .OrderBy(t => t.SystemName).ThenBy(t => t.Name)
            .ToList();
        var hidden = rows.Count(r => !activeIds.Contains(r.SystemId));
        return new UtEditorDto(systems, tags, UserTagEditorSupport.ValueTypes, matchOps, hidden, true);
    }

    /// <summary>
    /// 적용 — 요청에 포함된 System 의 태그 목록을 통째로 교체하고 project.aasx 를 저장한다.
    /// 전체 검증을 먼저 끝내고(하나라도 실패면 아무것도 쓰지 않음) 한 번의 export 로 반영해 Agent 재시작을 1회로 제한한다.
    /// </summary>
    [HttpPut("editor")]
    public ActionResult<UtEditorSaveResult> SaveEditor([FromBody] UtEditorSaveRequest req)
    {
        var errors = new List<string>();
        if (req?.Systems is null || req.Systems.Count == 0)
            return new UtEditorSaveResult(false, 0, [], errors, "변경 내용이 없습니다.");
        if (!_project.IsLoaded)
            return new UtEditorSaveResult(false, 0, [], errors, "프로젝트(AASX)가 로드되지 않았습니다.");

        var nameById = _project.GetActiveSystems().ToDictionary(s => s.Id, s => s.Name);
        var bySystem = new Dictionary<Guid, IReadOnlyList<UserTagWriteEntry>>();
        foreach (var sysIn in req.Systems)
        {
            if (!Guid.TryParse(sysIn.SystemId, out var sid) || !nameById.TryGetValue(sid, out var sysName))
            {
                errors.Add($"알 수 없는 System '{sysIn.SystemId}' — 활성 System 만 편집할 수 있습니다.");
                continue;
            }
            if (bySystem.ContainsKey(sid)) { errors.Add($"{sysName}: System 이 요청에 두 번 들어 있습니다."); continue; }

            var entries = new List<UserTagWriteEntry>();
            foreach (var t in sysIn.Tags ?? [])
            {
                var (entry, err) = UserTagEditorSupport.Normalize(t.Name, t.TagAddress, t.ValueType, t.MatchOp, t.MatchValue);
                if (entry is null) errors.Add($"{sysName} / '{t.Name}': {err}");
                else entries.Add(entry);
            }
            foreach (var dup in UserTagEditorSupport.FindDuplicateNames(entries.Select(e => e.Name)))
                errors.Add($"{sysName}: 이름 '{dup}' 가 중복됩니다(대소문자 무시).");
            bySystem[sid] = entries;
        }
        if (errors.Count > 0)
            return new UtEditorSaveResult(false, 0, [], errors, $"검증 실패 {errors.Count}건 — 저장하지 않았습니다.");

        var result = _project.WriteUserTagsAndExport(bySystem);
        if (!result.Exported)
        {
            _logger.LogWarning("[UserTags] 편집기 적용 실패 — {Error}", result.Error);
            return new UtEditorSaveResult(false, result.Applied, result.Warnings, errors, result.Error ?? "저장에 실패했습니다.");
        }
        return new UtEditorSaveResult(true, result.Applied, result.Warnings, errors, null);
    }

    /// <summary>
    /// CSV 내보내기(양식 겸용). systemId 지정 시 그 System 만. 태그가 없으면 헤더 + 예시 2행(template=1 일 때)을 담아
    /// 양식으로 쓸 수 있게 한다. UTF-8 BOM — Excel 한글 호환. 헤더는 Promaker CSV 와 같고 맨 앞에 System 컬럼만 추가.
    /// </summary>
    [HttpGet("editor/csv")]
    public IActionResult GetEditorCsv([FromQuery] string? systemId = null, [FromQuery] bool template = false)
    {
        var editor = GetEditor().Value!;
        IEnumerable<UtEditorTagDto> rows = editor.Tags;
        var fnPart = "All";
        if (!string.IsNullOrWhiteSpace(systemId))
        {
            rows = rows.Where(t => string.Equals(t.SystemId, systemId, StringComparison.OrdinalIgnoreCase));
            fnPart = editor.Systems.FirstOrDefault(s => string.Equals(s.SystemId, systemId, StringComparison.OrdinalIgnoreCase))?.SystemName ?? "System";
        }
        List<UtEditorTagDto> list = template ? [] : rows.ToList();
        var bytes = UserTagEditorSupport.BuildCsv(list, includeExample: template);
        var safe = string.Concat(fnPart.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var fn = template ? "UserTags_Template.csv" : $"{safe}_UserTags_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        return File(bytes, UserTagEditorSupport.CsvMimeType, fn);
    }

    /// <summary>
    /// CSV 가져오기 1단계 — 파일을 파싱·검증만 하고 행별 결과를 돌려준다(저장 없음). 클라이언트가 미리보기에서
    /// System 배정·추가/교체 모드를 정한 뒤 PUT editor 로 반영한다. 인코딩 = BOM/UTF-8/CP949 자동.
    /// </summary>
    [HttpPost("editor/csv/parse")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<ActionResult<UtCsvParseResult>> ParseEditorCsv([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "파일이 비어 있습니다." });
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        try
        {
            return UserTagEditorSupport.ParseCsv(ms.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UserTags] CSV 파싱 실패 ({Name})", file.FileName);
            return BadRequest(new { error = $"CSV 를 읽을 수 없습니다: {ex.Message}" });
        }
    }

    /// <summary>
    /// ChatBot AI 도구용 경량 알람 목록 — period·flow·limit 필터 적용.
    /// snapshot 의 무거운 집계(버킷·TOP·범주 도넛) 없이 목록만 반환.
    /// </summary>
    [HttpGet("alerts")]
    public async Task<ActionResult<List<UtAlertDto>>> GetAlerts(
        [FromQuery] string? period,
        [FromQuery] string? flow,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var (startLocal, endLocal, _) = ResolvePeriod(period ?? "today");
        var flw = Blank(flow);
        var size = Math.Clamp(limit, 1, 200);
        var rows = await _repo.QueryAlertsAsync(
            startLocal.ToUniversalTime(), endLocal.ToUniversalTime(),
            null, DisplayLevel, null, null, size, 0, ct, flowFilter: flw);
        return rows.Select(ToAlertDto).ToList();
    }

    /// <summary>Excel(.xlsx) 내보내기 — 현재 필터의 전체 알림을 단일 시트 테이블로. snapshot 과 동일 데이터원.</summary>
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
