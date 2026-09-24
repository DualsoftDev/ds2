// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Ds2.Editor;
using DSPilot.Infrastructure;
using DSPilot.Kpi;
using DSPilot.Models;
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
    private readonly ErrorTagReliabilityService _reliability;
    private readonly ILogger<UserTagsController> _logger;

    public UserTagsController(
        UserTagAlertService alertService, IUserTagAlertRepository repo, AppSettingsService settings,
        DsProjectService project, ErrorTagReliabilityService reliability, ILogger<UserTagsController> logger)
    {
        _alertService = alertService;
        _repo = repo;
        _settings = settings;
        _project = project;
        _reliability = reliability;
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

        // System 드롭다운 옵션만 정의에서 뽑는다. 정의 원본은 스냅샷에 싣지 않는다(GET definitions 사용).
        var systemOptions = _alertService.GetDefinitions()
            .Select(d => d.SystemName).Distinct().OrderBy(s => s).ToList();

        return new UserTagSnapshotDto(
            period, startLocal.ToString("yyyy-MM-dd HH:mm"), endLocal.ToString("yyyy-MM-dd HH:mm"),
            gran, BucketLabel(gran),
            total, page, maxPage, size,
            dataPage.Select(ToAlertDto).ToList(),
            buckets.Select(b => new UtBucketDto(b.BucketStart.ToLocalTime().ToString("o"), b.LogLevel, b.Count)).ToList(),
            top.Select(t => new UtTopDto(t.Name, t.LogLevel, t.Count, t.AltName)).ToList(),
            topByPath.Select(t => new UtTopDto(t.Name, t.LogLevel, t.Count, t.AltName)).ToList(),
            new Dictionary<string, int>(categoryCounts),
            activeError, todayError, lastAlertAtLocal,
            systemOptions,
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

    // ── 설정▸사용자 태그 편집기(이상알람TAG · 모니터링TAG 두 탭) ──────────────
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
            // 표기는 서버가 만든 Endpoint(host:port | USB | USB(selector)) — ip:port 를 직접 조립하면 USB 가 ":0" 이 된다.
            .SelectMany(e => e.SystemName.Split('·').Select(n => (Name: n, Ep: e.Endpoint)))
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
        // 모니터링 메타(단위·데드밴드·간격)는 UserTag 문자열 밖(AID interaction · SignalPolicy)에 살아서 따로 읽는다.
        // System 단위 조회라 한 번씩만 부른다.
        var metaBySystem = new Dictionary<Guid, Dictionary<string, MonitorTagMeta>>();
        foreach (var id in activeIds) metaBySystem[id] = _project.GetMonitorMetaForSystem(id);

        // 이상알람TAG 귀속은 AASX 가 아니라 DSPilot 설정에 산다 — GUID·이름 두 색인으로 한 번에 만든다
        // (리네임 내성: 이름이 바뀌어도 GUID 로 이어진다).
        var abnormal = _settings.LoadSettings().AbnormalAlarm;
        var epById = _project.GetEndpointLabelsBySystemId();
        var deviceIndex = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex(
            abnormal.UserTagDeviceBindings,
            _project.GetActiveSystems().Select(s =>
                (s.Id.ToString(), s.Name ?? string.Empty, epById.TryGetValue(s.Id, out var ep) ? ep : string.Empty)),
            abnormal.SystemAliases);

        var tags = rows
            .Where(r => activeIds.Contains(r.SystemId))
            .Select(r =>
            {
                MonitorTagMeta? meta = null;
                if (metaBySystem.TryGetValue(r.SystemId, out var m) && !string.IsNullOrWhiteSpace(r.TagAddress))
                    m.TryGetValue(r.TagAddress.Trim(), out meta);
                var level = UserTagEditorSupport.NormalizeLevel(r.LogLevel);
                // 미지정(키 부재)은 null, 전역은 "" 로 내려보낸다 — 화면이 둘을 구분해야 커버리지가 뜻을 갖는다.
                string? device = null;
                if (!UserTagEditorSupport.IsMonitorLevel(level)
                    && AbnormalDeviceFilterHelpers.TryGetBoundDevice(
                        deviceIndex, r.SystemId.ToString(), r.SystemName, r.TagAddress, out var bound))
                    device = bound;
                return new UtEditorTagDto(
                    r.SystemId.ToString(), r.SystemName, r.Name, r.TagAddress,
                    UserTagEditorSupport.NormalizeValueType(r.ValueType) ?? "Bit",
                    UserTagEditorSupport.NormalizeMatchOp(r.MatchOp, r.ValueType) ?? "RisingEdge",
                    r.MatchValue,
                    level,
                    meta?.Unit, meta?.DeadbandAbsolute, meta?.MinIntervalMs,
                    device);
            })
            .OrderBy(t => t.SystemName).ThenBy(t => t.Name)
            .ToList();
        var hidden = rows.Count(r => !activeIds.Contains(r.SystemId));

        // 드롭다운 소스 — 모델의 디바이스 + 귀속에만 남은(모델에서 사라진) 이름. 후자를 빼면 유령 귀속을
        // 화면에서 고를 수도 해제할 수도 없게 된다(차단 규칙 UI 의 InModel=false 와 같은 태도).
        var devices = _project.GetDeviceAliases();
        var known = new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);
        foreach (var t in tags)
            if (!string.IsNullOrEmpty(t.Device) && known.Add(t.Device))
                devices.Add(t.Device);

        return new UtEditorDto(systems, tags, UserTagEditorSupport.ValueTypes, matchOps, hidden, true, devices);
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
        var addrWarnings = new List<string>();
        // 이상알람TAG 귀속 — AASX 가 아니라 설정에 쓴다. 요청에 든 System 의 것만 모아 두었다가
        // export 가 성공한 뒤에 반영한다(AASX 가 실패했는데 귀속만 바뀌면 두 저장소가 어긋난다).
        var bindingsBySystemName = new Dictionary<string, List<UserTagDeviceBinding>>(StringComparer.OrdinalIgnoreCase);
        // 엔드포인트가 정본 키의 절반이다 — 저장 시점에 같이 각인한다(doc/31 §6).
        var endpointBySystem = _project.GetEndpointLabelsBySystemId();
        foreach (var sysIn in req.Systems)
        {
            if (!Guid.TryParse(sysIn.SystemId, out var sid) || !nameById.TryGetValue(sid, out var sysName))
            {
                errors.Add($"알 수 없는 System '{sysIn.SystemId}' — 활성 System 만 편집할 수 있습니다.");
                continue;
            }
            if (bySystem.ContainsKey(sid)) { errors.Add($"{sysName}: System 이 요청에 두 번 들어 있습니다."); continue; }

            var entries = new List<UserTagWriteEntry>();
            // 이 System 의 귀속은 통째 교체된다 — 요청이 최종 목록 전체이므로, 지워진 태그의 귀속은
            // 여기 안 담겨 자동으로 사라진다(고아 방지).
            var bindings = bindingsBySystemName[sysName] = [];
            foreach (var t in sysIn.Tags ?? [])
            {
                var (entry, err) = UserTagEditorSupport.Normalize(
                    t.Name, t.TagAddress, t.ValueType, t.MatchOp, t.MatchValue, t.Level);
                if (entry is null) { errors.Add($"{sysName} / '{t.Name}': {err}"); continue; }

                // 모니터링 메타는 모니터링TAG 에만 뜻이 있다 — 이상알람TAG 에 섞여 오면 조용히 버린다.
                // (탭을 옮긴 태그가 옛 값을 끌고 가 SignalPolicy 에 유령 행을 남기는 것을 막는다.)
                if (UserTagEditorSupport.IsMonitorLevel(entry.Level))
                {
                    var metaErr = UserTagEditorSupport.ValidateMonitorMeta(t.Deadband, t.MinIntervalMs);
                    if (metaErr is not null) { errors.Add($"{sysName} / '{t.Name}': {metaErr}"); continue; }
                    entry = entry with
                    {
                        Unit = string.IsNullOrWhiteSpace(t.Unit) ? null : t.Unit.Trim(),
                        DeadbandAbsolute = t.Deadband is > 0 ? t.Deadband : null,
                        MinIntervalMs = t.MinIntervalMs is > 0 ? t.MinIntervalMs : null,
                    };
                }
                else if (t.Device is not null)
                {
                    // 거울상 — 귀속은 이상알람TAG 에만 뜻이 있어 모니터링TAG 에 섞여 오면 위 갈래에서 버려진다.
                    // null(미지정)은 항목을 만들지 않고, ""(전역)은 사용자가 고의로 고른 것이라 항목을 남긴다.
                    // ★SystemId 를 같이 각인한다 — 이름은 AASX 교체로 바뀌지만 GUID 는 남는다.
                    //   2026-09-21 현장에서 이름만 바뀌어 사흘치가 통째로 지표에서 빠진 전례가 있다.
                    bindings.Add(new UserTagDeviceBinding
                    {
                        System = sysName,
                        SystemId = sid.ToString(),
                        Endpoint = endpointBySystem.TryGetValue(sid, out var ep) ? ep : string.Empty,
                        TagAddress = entry.TagAddress,
                        Device = t.Device.Trim(),
                    });
                }
                entries.Add(entry);
            }
            // 이름·주소 중복은 두 탭을 합친 목록으로 본다 — 요청이 그 System 의 최종 목록 전체이므로 여기가 그 범위다.
            // 탭이 다르다고 같은 이름을 허용하면 AASX 한 리스트 안에서 충돌한다.
            foreach (var dup in UserTagEditorSupport.FindDuplicateNames(entries.Select(e => e.Name)))
                errors.Add($"{sysName}: 이름 '{dup}' 가 중복됩니다(대소문자 무시).");
            // 주소 중복은 거부하지 않고 경고만 — 기존 현장 파일에 이미 있을 수 있어 저장 자체를 막으면 손발이 묶인다.
            // 같은 주소를 두 번 등록할 실익은 없다(tag 표가 (systemId,address) UNIQUE 라 신호 이력은 한 벌이고,
            // 이상알람TAG 의 값 추이도 태그 모니터링 화면에서 볼 수 있다).
            foreach (var dup in UserTagEditorSupport.FindDuplicateNames(entries.Select(e => e.TagAddress)))
                addrWarnings.Add($"{sysName}: 주소 '{dup}' 를 여러 태그가 쓰고 있습니다 — 신호 이력은 한 벌이라 값 추이가 같습니다.");
            bySystem[sid] = entries;
        }
        if (errors.Count > 0)
            return new UtEditorSaveResult(false, 0, [], errors, $"검증 실패 {errors.Count}건 — 저장하지 않았습니다.");

        var result = _project.WriteUserTagsAndExport(bySystem);
        var warnings = addrWarnings.Concat(result.Warnings).ToList();
        if (!result.Exported)
        {
            _logger.LogWarning("[UserTags] 편집기 적용 실패 — {Error}", result.Error);
            return new UtEditorSaveResult(false, result.Applied, warnings, errors, result.Error ?? "저장에 실패했습니다.");
        }

        // AASX 가 나간 뒤에 귀속을 반영한다. 요청에 없던 System 의 귀속은 건드리지 않는다
        // (태그 목록과 같은 규약 — "포함되지 않은 System 은 건드리지 않는다").
        try { SaveDeviceBindings(bindingsBySystemName); }
        catch (Exception ex)
        {
            // 태그 자체는 이미 저장됐으므로 실패로 뒤집지 않는다. 다만 조용히 넘기면 사용자가 묶은 것이
            // 사라진 줄 모르므로 경고로 올린다.
            _logger.LogError(ex, "[UserTags] 디바이스 귀속 저장 실패");
            warnings.Add($"태그는 저장됐지만 디바이스 귀속을 저장하지 못했습니다 — {ex.Message}");
        }
        return new UtEditorSaveResult(true, result.Applied, warnings, errors, null);
    }

    // ── GET: 등록 에러 태그 기반 신뢰성 지표 (eMTBF · eMTTR) ──────────────────────
    // doc/31. 리듬축(OEE 의 MTBF/MTTR, 비가동 기준)과 별개 축이라 값이 다른 것이 정상이다 —
    // 느린 사이클 ⊃ 등록된 고장이라 리듬축이 늘 더 많이 잡는다.
    [HttpGet("reliability")]
    public async Task<ActionResult<UtReliabilityDto>> GetReliability(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] string? system = null,
        // 이력 표에 실을 갈래. 기본 = 집계 대상만. 제외 행(무정지 경고·스냅샷·판정 불가)은 칩을 눌렀을 때만
        // 받는다 — 실측에서 2,778건 중 2,325건(84%)이 제외 행이었고 응답 1MB 의 거의 전부였다.
        [FromQuery] string? alerts = null,
        CancellationToken ct = default)
    {
        // 기본 창 = 최근 30일. 표본이 얇은 축이라(실측 11일 164건) 기본을 짧게 잡으면 늘 "표본 부족" 이 뜬다.
        var endUtc = (to ?? DateTime.Now).ToUniversalTime();
        var startUtc = (from ?? (to ?? DateTime.Now).AddDays(-30)).ToUniversalTime();
        var want = NormalizeAlertScope(alerts);

        // 이 탭은 10초마다 폴링한다(uptime-workspace.js). 캐시가 없으면 폴링마다 AASX 전체 순회 +
        // flow 별 사이클 조회 + 정의 수천 건 순회를 다시 돈다 — /snapshot 과 같은 10초 TTL 규약을 쓴다.
        var cacheKey = $"usertags/reliability|{system}|{want}|{startUtc.Ticks}"
                       + $"|{endUtc.Ticks / (TimeSpan.TicksPerSecond * 10)}";
        return await TtlRequestCache.GetOrComputeAsync(cacheKey, TimeSpan.FromSeconds(10),
            () => BuildReliabilityAsync(startUtc, endUtc, system, want, ct));
    }

    /// <summary>이력 표에 실을 갈래 — <c>None</c>(기본) · 제외 사유 이름 · <c>all</c>.</summary>
    private static string NormalizeAlertScope(string? raw)
    {
        var v = (raw ?? string.Empty).Trim();
        if (v.Length == 0) return nameof(ErrorTagReliability.SkipCause.None);
        if (string.Equals(v, "all", StringComparison.OrdinalIgnoreCase)) return "all";
        return Enum.TryParse<ErrorTagReliability.SkipCause>(v, ignoreCase: true, out var c)
            ? c.ToString()
            : nameof(ErrorTagReliability.SkipCause.None);
    }

    private async Task<UtReliabilityDto> BuildReliabilityAsync(
        DateTime startUtc, DateTime endUtc, string? system, string want, CancellationToken ct)
    {
        var r = await _reliability.AnalyzeAsync(startUtc, endUtc, system, ct);
        var s = r.Summary;

        return new UtReliabilityDto(
            EMtbfMs: s.EMtbfMs,
            EMttrMs: s.EMttrMs,
            FaultCount: s.FaultCount,
            RecoveredCount: s.RecoveredCount,
            InProgressCount: s.InProgressCount,
            AwaitingRestartCount: s.AwaitingRestartCount,
            RestartUnconfirmedCount: s.RestartUnconfirmedCount,
            NonStopWarningCount: s.NonStopWarningCount,
            LinkSnapshotCount: s.LinkSnapshotCount,
            UnknownStopCount: s.UnknownStopCount,
            OperatingMs: s.OperatingMs,
            TotalDownMs: s.TotalDownMs,
            MinSample: ErrorTagReliability.MinSample,
            UnboundTagCount: r.UnboundTagCount,
            GlobalTagCount: r.GlobalTagCount,
            SkippedChangedCount: r.SkippedChangedCount,
            MultiFlowDeviceCount: r.MultiFlowDeviceCount,
            StaleSystems: r.StaleSystems,
            ProjectLoaded: r.ProjectLoaded,
            Flows: [.. r.Flows.Select(ToScopeDto)],
            Systems: [.. r.Systems.Select(ToScopeDto)],
            Devices: [.. r.Devices.Select(d => new UtReliabilityDeviceDto(
                SystemName: d.System,
                Device: d.Device,
                FaultCount: d.FaultCount,
                RecoveredCount: d.RecoveredCount,
                InProgressCount: d.InProgressCount,
                AwaitingRestartCount: d.AwaitingRestartCount,
                RestartUnconfirmedCount: d.RestartUnconfirmedCount,
                NonStopWarningCount: d.NonStopWarningCount,
                LinkSnapshotCount: d.LinkSnapshotCount,
                UnknownStopCount: d.UnknownStopCount,
                OperatingMs: d.OperatingMs,
                TotalDownMs: d.TotalDownMs,
                EMtbfMs: d.EMtbfMs,
                EMttrMs: d.EMttrMs))],
            // 요약·스코프·디바이스는 언제나 전체 기준이고, 이력 표만 고른 갈래로 자른다.
            Alerts: [.. r.Alerts
                .Where(a => want == "all" || string.Equals(a.Skip.ToString(), want, StringComparison.Ordinal))
                .Select(a => new UtReliabilityAlertDto(
                OccurredAtLocal: a.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ClearedAtLocal: a.ClearedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                RestartedAtLocal: a.RestartedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                SystemName: a.SystemName,
                Name: a.Name,
                NameAtTime: a.NameAtTime,
                TagAddress: a.TagAddress,
                Device: a.Device,
                State: a.State.ToString(),
                Stop: a.Stop.ToString(),
                Skip: a.Skip.ToString(),
                EventNo: a.EventNo,
                RepairMs: a.RepairMs,
                RestartFlow: a.RestartFlow))]);
    }

    private static UtReliabilityScopeDto ToScopeDto(ErrorTagReliability.ScopeSummary x) => new(
        Kind: x.Kind,
        Name: x.Name,
        FaultCount: x.Totals.FaultCount,
        RecoveredCount: x.Totals.RecoveredCount,
        NonStopWarningCount: x.Totals.NonStopWarningCount,
        OperatingMs: x.Totals.OperatingMs,
        TotalDownMs: x.Totals.TotalDownMs,
        EMtbfMs: x.Totals.EMtbfMs,
        EMttrMs: x.Totals.EMttrMs);

    /// <summary>
    /// 이상알람TAG 디바이스 귀속 반영 — 요청에 든 System 의 것만 통째 교체하고 나머지는 그대로 둔다.
    /// 귀속이 AASX 가 아닌 설정에 사는 이유는 <see cref="UserTagDeviceBinding"/> 주석 참고.
    /// </summary>
    private void SaveDeviceBindings(Dictionary<string, List<UserTagDeviceBinding>> bindingsBySystemName)
    {
        if (bindingsBySystemName.Count == 0) return;

        _settings.Update(m =>
        {
            var kept = (m.AbnormalAlarm.UserTagDeviceBindings ?? [])
                .Where(b => !bindingsBySystemName.ContainsKey(b?.System?.Trim() ?? string.Empty));
            m.AbnormalAlarm.UserTagDeviceBindings = AbnormalDeviceFilterHelpers.NormalizeUserTagDeviceBindings(
                kept.Concat(bindingsBySystemName.Values.SelectMany(v => v)));
        });
    }

    /// <summary>
    /// CSV 내보내기(양식 겸용). systemId 지정 시 그 System 만. 태그가 없으면 헤더 + 예시 2행(template=1 일 때)을 담아
    /// 양식으로 쓸 수 있게 한다. UTF-8 BOM — Excel 한글 호환. 헤더는 Promaker CSV 와 같고 맨 앞에 System 컬럼만 추가.
    /// </summary>
    [HttpGet("editor/csv")]
    public IActionResult GetEditorCsv(
        [FromQuery] string? systemId = null, [FromQuery] bool template = false, [FromQuery] string? level = null)
    {
        var editor = GetEditor().Value!;
        // 탭별 내보내기 — 지정한 종류의 행만 나간다. 미지정(구 클라이언트)은 종전처럼 전체.
        var lv = string.IsNullOrWhiteSpace(level) ? null : UserTagEditorSupport.NormalizeLevel(level);
        IEnumerable<UtEditorTagDto> rows = lv is null
            ? editor.Tags
            : editor.Tags.Where(t => string.Equals(t.Level, lv, StringComparison.Ordinal));
        var fnPart = "All";
        if (!string.IsNullOrWhiteSpace(systemId))
        {
            rows = rows.Where(t => string.Equals(t.SystemId, systemId, StringComparison.OrdinalIgnoreCase));
            fnPart = editor.Systems.FirstOrDefault(s => string.Equals(s.SystemId, systemId, StringComparison.OrdinalIgnoreCase))?.SystemName ?? "System";
        }
        List<UtEditorTagDto> list = template ? [] : rows.ToList();
        var bytes = UserTagEditorSupport.BuildCsv(list, includeExample: template, level: lv);
        var safe = string.Concat(fnPart.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var kind = lv == UserTagEditorSupport.LevelMonitor ? "MonitorTags" : "UserTags";
        var fn = template ? $"{kind}_Template.csv" : $"{safe}_{kind}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        return File(bytes, UserTagEditorSupport.CsvMimeType, fn);
    }

    /// <summary>
    /// CSV 가져오기 1단계 — 파일을 파싱·검증만 하고 행별 결과를 돌려준다(저장 없음). 클라이언트가 미리보기에서
    /// System 배정·추가/교체 모드를 정한 뒤 PUT editor 로 반영한다. 인코딩 = BOM/UTF-8/CP949 자동.
    /// </summary>
    [HttpPost("editor/csv/parse")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<ActionResult<UtCsvParseResult>> ParseEditorCsv(
        [FromForm] IFormFile? file, [FromQuery] string? level, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "파일이 비어 있습니다." });
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        try
        {
            // level = 가져오기를 실행한 탭. 파일에 다른 레벨이 적혀 있어도 그 탭 것으로 귀속시키고
            // 행마다 LevelAdjusted 로 알린다(UserTagEditorSupport.ParseCsv 주석 참조).
            return UserTagEditorSupport.ParseCsv(ms.ToArray(), level);
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

    /// <summary>
    /// 시계열 막대 드릴다운 — 클릭한 버킷 한 칸에 실제로 발생한 알람 목록.
    /// 버킷 끝은 차트 집계와 같은 규칙(NextBucketUtc)으로 서버가 계산한다 — 클라이언트가 시/일/주/월
    /// 경계를 다시 유추하면 주 시작요일·월 길이에서 어긋난다.
    /// snapshot 의 무거운 집계(버킷·TOP·도넛)를 다시 돌리지 않는 경량 경로(막대 클릭마다 호출됨).
    /// 필터 규약은 snapshot 과 동일 — flow 가 걸리면 category 는 무시(자동감지만 남아 모순 방지).
    /// </summary>
    [HttpGet("bucket")]
    public async Task<ActionResult<UtBucketDrillDto>> GetBucketAlerts(
        [FromQuery] DateTime from,              // 버킷 시작(로컬 벽시계 또는 오프셋 포함 ISO)
        [FromQuery] string gran = "hour",       // "hour" | "day" | "week" | "month"
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        [FromQuery] string? system = null,
        [FromQuery] string? flow = null,
        [FromQuery] int limit = 300,
        CancellationToken ct = default)
    {
        var g = gran is "hour" or "day" or "week" or "month" ? gran : "hour";
        var startUtc = TruncBucketUtc(from.ToUniversalTime(), g);
        var endUtc = NextBucketUtc(startUtc, g);
        var flw = Blank(flow);
        var cat = flw is null ? Blank(category) : null;
        var size = Math.Clamp(limit, 1, 1000);
        // BuildFilter 의 끝 경계가 포함(<=)이라 다음 버킷 첫 행을 빨아들이지 않게 1틱 당긴다.
        var endInclusive = endUtc.AddTicks(-1);

        var total = await _repo.CountAlertsAsync(startUtc, endInclusive, Blank(search), DisplayLevel, Blank(system), cat, ct, flowFilter: flw);
        var rows = await _repo.QueryAlertsAsync(
            startUtc, endInclusive, Blank(search), DisplayLevel, Blank(system), cat, size, 0, ct,
            flowFilter: flw, sortColumn: "occurredAt", sortDesc: false);

        return new UtBucketDrillDto(
            startUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            endUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            BucketLabel(g),
            total,
            rows.Select(ToAlertDto).ToList());
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
        a.LogLevel, a.SystemName, a.Name, a.TagAddress, a.ValueType, a.MatchOp, a.MatchValue, a.ActualValue,
        // 해소 시각 — 조건이 풀린(Bit 1→0 등) 시점. 미해소면 null → UI 가 "진행 중" 으로 표시.
        a.ClearedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
        a.ClearedAt is { } clr && clr > a.OccurredAt
            ? (long)(clr - a.OccurredAt).TotalMilliseconds
            : null);

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
