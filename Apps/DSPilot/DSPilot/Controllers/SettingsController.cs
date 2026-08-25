// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Adapters;
using DSPilot.Models;
using DSPilot.Hubs;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Settings API (FIRST heavy-mutation 레퍼런스).
/// Blazor /settings 가 쓰던 AppSettingsService + DatabaseLifecycleService + IFlowMetricsService +
/// DsProjectService + CctvMediaMtxService + IDatabasePathResolver 를 얇게 래핑.
/// GET 은 현재 설정/AASX 상태 스냅샷, POST 들은 저장/라이프사이클 mutation.
/// 모든 POST 는 antiforgery 미적용 — 평범한 tokenless JSON fetch (CctvController 와 동일).
/// 라이프사이클 mutation 성공 후 IHubContext 로 DatabaseRebuilt/FlowHistoryCleared 를 브로드캐스트하여
/// dashboard/heatmap/usertags 정적 미러가 자동 새로고침되게 한다.
/// ConnectionString ↔ DB 폴더 분리 / H·M·S↔ms 변환은 Blazor Settings.razor 와 동일하게 서버에서 처리.
/// </summary>
[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private const string DbFileName = "plc.db";

    private readonly AppSettingsService _settings;
    private readonly OeeCtStatsService _ctStats;   // Max 권장값 산출(실측 CT 중앙값·p99)
    private readonly DatabaseLifecycleService _lifecycle;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly DsProjectService _project;
    private readonly CctvMediaMtxService _cctvSync;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly AutoCalibrationService _autoCal;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly HeatmapService _heatmap;
    private readonly SimulationEngineService _engine;
    private readonly UserTagAlertService _userTags;
    private readonly ExternalAccessService _externalAccess;
    // 앱 정보 카드(app-info) 전용 — 실제 바인딩 주소/콘텐츠 루트/호스팅 설정 조회.
    private readonly IServer _server;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<SettingsController> _logger;

    private readonly HubSubscriberService _hubSubscriber;

    public SettingsController(
        AppSettingsService settings,
        DatabaseLifecycleService lifecycle,
        IFlowMetricsService flowMetrics,
        DsProjectService project,
        CctvMediaMtxService cctvSync,
        IDatabasePathResolver pathResolver,
        AutoCalibrationService autoCal,
        IHubContext<MonitoringHub> hub,
        HeatmapService heatmap,
        SimulationEngineService engine,
        UserTagAlertService userTags,
        OeeCtStatsService ctStats,
        ExternalAccessService externalAccess,
        HubSubscriberService hubSubscriber,
        IServer server,
        IHostEnvironment env,
        IConfiguration config,
        ILogger<SettingsController> logger)
    {
        _settings = settings;
        _ctStats = ctStats;
        _lifecycle = lifecycle;
        _flowMetrics = flowMetrics;
        _project = project;
        _cctvSync = cctvSync;
        _pathResolver = pathResolver;
        _autoCal = autoCal;
        _hub = hub;
        _heatmap = heatmap;
        _engine = engine;
        _userTags = userTags;
        _externalAccess = externalAccess;
        _hubSubscriber = hubSubscriber;
        _server = server;
        _env = env;
        _config = config;
        _logger = logger;
    }

    // ── PLC 스캔 주기 (Agent 게이트웨이 설정 — 라이브 적용/전 클라이언트 동기화) ──
    [HttpGet("plc-scan-interval")]
    public async Task<IActionResult> GetPlcScanInterval()
    {
        var ms = await _hubSubscriber.GetScanIntervalMsAsync();
        return Ok(new { ms, connected = ms.HasValue });
    }

    public sealed class ScanIntervalRequest { public int Ms { get; set; } }

    [HttpPost("plc-scan-interval")]
    public async Task<IActionResult> SetPlcScanInterval([FromBody] ScanIntervalRequest req)
    {
        var clamped = Math.Clamp(req.Ms, 10, 500);
        var ok = await _hubSubscriber.SetScanIntervalMsAsync(clamped);
        if (!ok)
            return StatusCode(503, "Agent hub 미연결 — 모니터링이 활성 상태인지 확인하세요.");
        _logger.LogInformation("PLC scan interval set via settings page: {Ms}ms", clamped);
        return Ok(new { ms = clamped });
    }

    // ── POST: 건강 기준선 수동 동결 — hub 브로드캐스트로 전 인스턴스(Promaker 등) 동시 동결 ──
    [HttpPost("health-baseline-freeze")]
    public async Task<IActionResult> FreezeHealthBaseline()
    {
        var ok = await _hubSubscriber.FreezeHealthBaselineAsync();
        if (!ok)
            return StatusCode(503, "Agent hub 미연결 — 모니터링이 활성 상태인지 확인하세요.");
        _logger.LogInformation("Health baseline freeze requested via settings page");
        return Ok();
    }

    // ── 자동 duration 정합 ON/OFF — Agent abnormal 판정 기준 전환(실측 학습 ↔ 모델 확정값) ──
    [HttpGet("auto-calibrate")]
    public IActionResult GetAutoCalibrate()
        => Ok(new { on = _hubSubscriber.CurrentAutoCalibrate, connected = _hubSubscriber.CurrentStatus == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected });

    public sealed class AutoCalibrateRequest { public bool On { get; set; } }

    [HttpPost("auto-calibrate")]
    public async Task<IActionResult> SetAutoCalibrate([FromBody] AutoCalibrateRequest req)
    {
        var ok = await _hubSubscriber.SetAutoCalibrateAsync(req.On);
        if (!ok)
            return StatusCode(503, "Agent hub 미연결 — 모니터링이 활성 상태인지 확인하세요.");
        _logger.LogInformation("Auto-calibrate set to {On} via settings page", req.On);
        return Ok(new { on = req.On });
    }

    // ── GET: 전체 설정 + 파생 표시값 ──
    [HttpGet]
    public ActionResult<SettingsDto> Get()
    {
        var m = _settings.LoadSettings();
        return ToDto(m);
    }

    /// <summary>
    /// 이상치 제외 Max 권장값 — flow별 실측 CT 분포(중앙값·p99)에서 산출한다.
    /// <para>설정 화면이 "무제한(0)" 경고와 함께 한 번 클릭으로 넣을 값을 제시하는 데 쓴다. 고정 초를 제시하면
    /// 사이클이 수 초인 설비와 수 분인 설비 중 한쪽에서 반드시 틀리므로, 그 현장 데이터에서 만든다.
    /// 전역 Max 는 <b>가장 느린 flow</b>를 기준으로 해야 다른 flow 의 정상 사이클이 잘리지 않으므로 최댓값을 쓴다.
    /// 워치독 자동 폴백(<see cref="OeeMath.ResolveAutoAbandonBoundaryMs"/>)과 같은 공식이라 제시값과
    /// 실제 동작이 어긋나지 않는다.</para>
    /// </summary>
    [HttpGet("recommended-cycle-max")]
    public async Task<ActionResult<RecommendedCycleMaxDto>> RecommendedCycleMax()
    {
        var tickSec = _settings.LoadSettings().HistoryView.StateReconcileIntervalSeconds;
        var floorMs = (tickSec > 0 ? tickSec : 30) * 3 * 1000.0;
        var rows = new List<RecommendedCycleMaxFlowDto>();
        try
        {
            var stats = await _ctStats.ComputeCtRobustAsync();
            foreach (var (flow, st) in stats.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var b = OeeMath.ResolveAutoAbandonBoundaryMs(st.MedianMs, st.P99Ms, st.Sample, floorMs);
                rows.Add(new RecommendedCycleMaxFlowDto(flow, (int)st.MedianMs, (int)st.P99Ms, st.Sample, (int)b));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Settings] Max 권장값 산출 실패");
        }
        // 초 단위로 올림 — 사용자가 시/분/초 입력칸에 그대로 넣는 값이라 ms 잔여를 남기지 않는다.
        var maxMs = rows.Count > 0 ? rows.Max(r => r.BoundaryMs) : 0;
        var recSec = maxMs > 0 ? (int)Math.Ceiling(maxMs / 1000.0) : 0;
        return new RecommendedCycleMaxDto(recSec * 1000, recSec, (int)floorMs, rows);
    }

    // ── GET: AASX 파일 상태 + 동기화 배지 (Settings.razor RefreshAasxStatus + SyncBadge*) ──
    [HttpGet("aasx-status")]
    public ActionResult<AasxStatusDto> GetAasxStatus() => BuildAasxStatus();

    // ── GET: 공유 폴더의 project.aasx 다운로드 ──
    // Promaker 와 공유하는 파일이므로 FileShare.ReadWrite 로 열어 잠금 충돌을 피한다.
    [HttpGet("download-aasx")]
    public IActionResult DownloadAasx()
    {
        var path = _project.AasxFilePath;
        if (!System.IO.File.Exists(path))
            return NotFound("AASX 파일이 존재하지 않습니다. Promaker 에서 먼저 저장하세요.");

        var stream = System.IO.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // fileName 지정 시 Content-Disposition: attachment 가 설정되어 브라우저가 다운로드한다.
        return File(stream, "application/octet-stream", "project.aasx");
    }

    // ── POST: 저장 (SaveSettings) ──
    // 클라이언트는 split 된 dbDir + 전체 모델을 보낸다. ConnectionString 은 서버에서 재조립.
    [HttpPost("save")]
    public async Task<ActionResult<SaveResultDto>> Save([FromBody] SaveRequestDto req, CancellationToken ct)
    {
        try
        {
            // 동작편차 통계 캡 변경 여부 — 변경 시 저장 후 매트릭스 통계를 새 캡으로 즉시 재청소(재시작 불요).
            var prevHv = _settings.LoadSettings().HistoryView;
            bool goingCapsChanged =
                prevHv.MaxCallGoingTimeMs != req.MaxCallGoingTimeMs ||
                prevHv.MinCallGoingTimeMs != req.MinCallGoingTimeMs;

            // 외부 접속 주소 검증(스킴 생략 시 http:// 보정, http(s) 절대 URL 만). null=구 클라이언트 → 기존 값 보존.
            var normalizedExternalUrl = req.ExternalUrl is null ? null : ExternalAccessService.Normalize(req.ExternalUrl);
            if (req.ExternalUrl is not null && normalizedExternalUrl is null)
                return new SaveResultDto(false, "외부 접속 주소가 올바르지 않습니다. 예: http://192.168.0.10 또는 https://dspilot.company.com");

            // 현재 디스크 설정을 baseline 으로 로드 후 클라이언트 편집값을 덮어쓴다(load-modify-save 를 단일 잠금으로 원자화 —
            // 수동 보정의 CompletedAt 박제와 경합해도 유실되지 않도록 AppSettingsService.Update 사용).
            // (UI 미노출 섹션 DspTables/Hub/Ui.ShowPlcDebug 등은 baseline 유지 — appsettings.json 으로만 관리)
            _settings.Update(m =>
            {
                m.Database.ConnectionString = BuildConnectionString(req.DbDir);
                m.Logging.LogLevel.Default = string.IsNullOrWhiteSpace(req.LogLevelDefault) ? m.Logging.LogLevel.Default : req.LogLevelDefault;

                m.HistoryView.MaxCycleTimeMs = req.MaxCycleTimeMs;
                m.HistoryView.MinCycleTimeMs = req.MinCycleTimeMs;
                m.HistoryView.MaxCallGoingTimeMs = req.MaxCallGoingTimeMs;
                m.HistoryView.MinCallGoingTimeMs = req.MinCallGoingTimeMs;
                m.HistoryView.CycleAverageWindow = req.CycleAverageWindow;

                // 동작편차 색상 임계(편차 %) — 주의 < 위험 보장(역전·동일 시 위험=주의+1 로 보정), 0 이상.
                var caution = Math.Max(0, req.HeatmapCautionPct);
                var danger = Math.Max(caution + 1, req.HeatmapDangerPct);
                m.HistoryView.HeatmapCautionPct = caution;
                m.HistoryView.HeatmapDangerPct = danger;
                m.Ui.AlarmTickerIntervalSec = Math.Clamp(req.AlarmTickerIntervalSec, 1, 30);
                m.AbnormalAlarm.ResetIntervalHours = Math.Max(0, req.AbnormalAlarmResetIntervalHours);
                // 배너 표시 레벨 — null(구 클라이언트)이면 기존 값 보존, 보내면 정규화 후 교체(빈 배열=전체 표시).
                if (req.AbnormalAlarmDisplayLevels is not null)
                    m.AbnormalAlarm.DisplayLevels = NormalizeDisplayLevels(req.AbnormalAlarmDisplayLevels);

                // 디바이스별 이상감지 차단 규칙(AbnormalAlarm.DeviceFilters)은 uptime 페이지의
                // 차단 관리(POST abnormal-device-filters)가 소유 — 여기서는 건드리지 않는다(CCTV 카메라와 동일 원칙).

                // 실측 보정 파라미터. 실행은 "지금 실측값 채우기" 버튼(/auto-calibrate/run)으로만 한다.
                if (req.AutoCalibration is { } acReq)
                {
                    m.AutoCalibration.Enabled = false; // 구버전 입력 호환: 자동 백그라운드 실행은 폐기.
                    m.AutoCalibration.MinCleanCycles = Math.Max(1, acReq.MinCleanCycles);
                    // null = 구(캐시) 클라이언트가 필드 없이 보낸 경우 — 기존값 보존(0 으로 조여지는 사고 방지).
                    m.AutoCalibration.MedianMarginMaxPct = Math.Clamp(acReq.MedianMarginMaxPct ?? m.AutoCalibration.MedianMarginMaxPct, 0, 5);
                    m.AutoCalibration.MarginMaxAbsMs = Math.Clamp(acReq.MarginMaxAbsMs, 0, 600000);
                    m.AutoCalibration.FillMin = acReq.FillMin;
                    m.AutoCalibration.PercentileMin = Math.Clamp(acReq.PercentileMin, 0, 50);
                    m.AutoCalibration.MarginMinPct = Math.Clamp(acReq.MarginMinPct, 0, 1);
                    // 판정 주체 — null(구 클라이언트)이면 기존값 보존, 미지 값은 dspilot 정규화(fail-safe).
                    if (acReq.ActionOverJudge is { } judge)
                        m.AutoCalibration.ActionOverJudge =
                            string.Equals(judge.Trim(), "agent", StringComparison.OrdinalIgnoreCase) ? "agent" : "dspilot";
                }

                // 외부 접속 주소 — null(구 클라이언트)이면 기존 값 보존. 형식 오류는 Update 진입 전에 걸렀다.
                if (normalizedExternalUrl is not null)
                    m.ExternalAccess.Url = normalizedExternalUrl;

                // CCTV(RTSP) 카메라 설정은 CCTV 페이지(CctvController.SaveSettings)가 소유 — 여기서는 건드리지 않는다.
                // (Settings 저장이 카메라 목록을 덮어써 오버레이/카메라가 유실되는 것을 방지.)
            });

            // ActionOver 여유값(MarginMaxAbsMs) 변경을 라이브 판정에 즉시 반영 — 재시작·재측정 불필요.
            // 임계는 AASX 에 굽지 않고 엔진 인덱스에서 산출하므로, 모델 원본 스냅샷에서 다시 계산하면 끝난다.
            _engine.RefreshActionOverThresholds();

            // 비가동 임계값 변경 소급 적용 (대시보드·히스토리 즉시 반영) — Blazor SaveSettings 와 동일.
            var (restamped, flows) = await _flowMetrics.ReapplyIdleThresholdsAsync();

            // 동작편차 캡이 바뀌었으면 매트릭스 저장통계를 새 캡으로 원시 엣지에서 재도출 + 누산기 재시드
            // → 재시작 없이 즉시 반영(상세 패널·새 캡과 동일 필터). 캡 미변경 시 무거운 전체 재스캔 생략.
            int healedCalls = 0;
            if (goingCapsChanged)
            {
                try
                {
                    healedCalls = await _heatmap.RecomputeAllCallGoingStatisticsAsync(ct);
                    if (healedCalls > 0) _engine.ReseedCallStatsFromDb();
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[Settings] 동작편차 통계 캡 재청소 실패(비치명적)"); }
            }

            // 임계값 소급 적용 → 대시보드/히트맵 미러 새로고침.
            try { await _hub.Clients.All.SendAsync("DatabaseRebuilt", ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Settings] SignalR broadcast failed (non-critical)"); }

            var capMsg = goingCapsChanged ? $" 동작편차 캡 재적용: {healedCalls}개 Call 재계산." : "";
            return new SaveResultDto(
                true,
                $"설정이 저장되었습니다. 비가동 판정 소급 적용: 히스토리 {restamped}건 재평가, Flow {flows}개 평균 재집계.{capMsg}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Settings] Save failed");
            return new SaveResultDto(false, $"저장 실패: {ex.Message}");
        }
    }

    // ── POST: AASX 모델 다시 불러오기 (ReloadAasxAsync) ──
    [HttpPost("reload")]
    public async Task<ActionResult<RebuildResultDto>> ReloadAasx()
        => Result(await _lifecycle.ReloadAasxAsync());

    // ── POST: 모델 정의 동기화 (ReloadAndResyncAsync) — Blazor 는 자동 watcher 경로지만 정적 미러용 수동 트리거 제공 ──
    [HttpPost("rebuild-aasx")]
    public async Task<ActionResult<RebuildResultDto>> RebuildAasx()
        => Result(await _lifecycle.ReloadAndResyncAsync());

    // ── POST: 캐시 재계산 (InvalidateCachesAsync, raw 보존) ──
    [HttpPost("invalidate-caches")]
    public async Task<ActionResult<RebuildResultDto>> InvalidateCaches()
        => Result(await _lifecycle.InvalidateCachesAsync());

    // ── POST: Flow 히스토리 삭제 (ClearFlowHistoryAsync) — 파괴적 ──
    [HttpPost("clear-flow-history")]
    public async Task<ActionResult<RebuildResultDto>> ClearFlowHistory()
        => Result(await _lifecycle.ClearFlowHistoryAsync());

    // ── GET: AASX 변경 이력 목록 (연표 다이얼로그용) ──
    [HttpGet("aasx-changelog")]
    public async Task<ActionResult<IReadOnlyList<AasxChangeLogDto>>> GetAasxChangeLog()
    {
        var entries = await _lifecycle.GetAasxChangeLogAsync(100);
        var dtos = entries.Select(e => new AasxChangeLogDto(
            e.Id,
            e.ChangedAtLocal.ToString("yyyy-MM-dd HH:mm:ss"),
            e.ChangedAtLocal.ToString("o"),
            e.Source,
            e.Notes)).ToList();
        return Ok(dtos);
    }

    // ── GET: 현재 AASX 에 없는 flow('유령 설비') 잔존 현황 — 정리 미리보기 ──
    // 화면은 읽기 필터로 이미 유령을 숨기지만 행 자체는 남아 있다. 이 엔드포인트가 "무엇이 얼마나
    // 지워지는지"를 먼저 보여주고, 사용자가 확인한 뒤에만 prune-stale-flows 로 실제 삭제한다.
    [HttpGet("stale-flows")]
    public async Task<ActionResult<StaleFlowReportDto>> GetStaleFlows()
    {
        var r = await _lifecycle.GetStaleFlowReportAsync();
        return Ok(new StaleFlowReportDto(
            r.FlowNames, r.DspFlowRows, r.DspCallRows, r.HistoryRows,
            r.DowntimeEvents, r.CycleOverrides, r.Total, r.ModelLoaded));
    }

    // ── POST: 유령 설비 데이터 정리 (비가역) ──
    // AASX 변경 이력(aasxChangeLog)이 없어도 실행 가능해야 한다 — 서비스 정지 중 AASX 를 교체하면
    // 이력이 남지 않고(워처가 '콘텐츠 동일'로 판정), 그때가 바로 유령이 생기는 경우다.
    [HttpPost("prune-stale-flows")]
    public async Task<ActionResult<RebuildResultDto>> PruneStaleFlows()
        => Result(await _lifecycle.PruneStaleFlowsAsync());

    // ── POST: 기준 시각 이전 데이터 선택 삭제 ──
    [HttpPost("delete-data-before")]
    public async Task<ActionResult<RebuildResultDto>> DeleteDataBefore([FromBody] DeleteBeforeRequestDto req)
    {
        if (!DateTimeOffset.TryParse(req.CutoffIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
            return BadRequest(new RebuildResultDto(false, "잘못된 시각 형식입니다."));
        return Result(await _lifecycle.DeleteDataBeforeAsync(dto.UtcDateTime));
    }

    // ── POST: 전체 초기화 (RebuildDatabaseAsync) — 가장 파괴적 (plcTagLog 포함 삭제) ──
    [HttpPost("rebuild-database")]
    public async Task<ActionResult<RebuildResultDto>> RebuildDatabase()
        => Result(await _lifecycle.RebuildDatabaseAsync());

    // ── POST: Flow CT 기준 복원 (per-flow override 제거 + FlowMetrics 재초기화) ──
    // Blazor ReloadFlowCycleDefaultsFromAasxAsync 와 동일: ClearFlowCycleOverrides → InitializeAsync.
    [HttpPost("restore-flow-defaults")]
    public async Task<ActionResult<RebuildResultDto>> RestoreFlowDefaults()
    {
        try
        {
            _settings.ClearFlowCycleOverrides();
            await _flowMetrics.InitializeAsync();
            try { await _hub.Clients.All.SendAsync("DatabaseRebuilt"); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Settings] SignalR broadcast failed (non-critical)"); }
            return new RebuildResultDto(true, "Flow CT 기준을 AASX 기본값으로 다시 적용했습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Settings] RestoreFlowDefaults failed");
            return new RebuildResultDto(false, $"복원 실패: {ex.Message}");
        }
    }

    // ── POST: 앱 설정 전체를 코드 기본값으로 초기화 (구버전 stale 설정 복구용 escape hatch) ──
    // 업그레이드 시 appsettings.Production.json(사용자 설정)을 보존하므로, 구버전 설정이 문제를 일으키면
    // 사용자가 이 버튼으로 깨끗한 기본값으로 되돌린다. CCTV 카메라/이상치/시프트/OEE 등 모든 설정이 초기화된다.
    // PLC 원시 데이터(plc.db)는 건드리지 않는다. DB 경로/포트 등 호스트 바인딩은 서비스 재시작 후 적용.
    [HttpPost("reset-defaults")]
    public async Task<ActionResult<RebuildResultDto>> ResetDefaults(CancellationToken ct)
    {
        try
        {
            _settings.ResetToDefaults();

            // 새 임계값(기본값) 소급 적용 — 대시보드/히스토리 즉시 반영 (Save 와 동일 경로).
            var (restamped, flows) = await _flowMetrics.ReapplyIdleThresholdsAsync();

            // 카메라 목록이 비워졌으므로 MediaMTX 경로도 즉시 해제(실패해도 초기화 자체는 성공).
            try { await _cctvSync.SyncAsync(ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Settings] CCTV resync after reset failed (non-critical)"); }

            try { await _hub.Clients.All.SendAsync("DatabaseRebuilt", ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Settings] SignalR broadcast failed (non-critical)"); }

            return new RebuildResultDto(true,
                $"모든 설정을 기본값으로 초기화했습니다 (히스토리 {restamped}건 재평가, Flow {flows}개). " +
                "DB 경로·포트 등 호스트 설정은 서비스 재시작 후 적용됩니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Settings] ResetDefaults failed");
            return new RebuildResultDto(false, $"초기화 실패: {ex.Message}");
        }
    }

    // ── 서비스명(SSOT = Installer/DSPilot.iss) ──
    private const string SvcDspilot = "DSPilotService";
    private const string SvcAgent = "PromakerAgentService";
    private const string SvcMtx = "DSPilotMediaMtx";

    // Windows 서비스로 기동된 설치본에서만 서비스 재시작을 제공한다. (SSOT — app-info 의
    // ServiceControlSupported 로 노출되어 설정 화면이 카드 자체를 숨긴다.)
    // net stop/start 는 Windows 서비스 제어 명령이라, systemd 서비스·콘솔 실행에서는 대응 수단이
    // 아니다(Linux 는 systemctl 로 dspilot / promaker-agent / dspilot-mediamtx 를 재시작해야 하고,
    // 서비스 계정 dspilot 에 polkit 예외가 필요하다 — 미구현).
    private static bool ServiceControlSupported => WindowsServiceHelpers.IsWindowsService();

    // ── POST: 서비스 재시작 (target = dspilot | agent | mtx | all) ──
    // 고급 설정의 접힘 카드에서 확인 후 호출. 대상 Windows 서비스를 net stop → net start 한다.
    //  • DSPilot 자신을 포함하지 않는 대상(agent/mtx)은 인라인 동기 실행 → 실제 성공/실패를 즉시 회신.
    //  • DSPilot 을 포함(dspilot/all)하면 net stop 이 이 프로세스를 종료시키므로, DSPilot 프로세스 트리와
    //    분리된(detached) 임시 배치로 실행하고 HTTP 응답이 먼저 반환되도록 DSPilot 은 짧은 지연 뒤 마지막에 재시작.
    //    브라우저는 그 사이 끊기지만 SignalR 자동 재연결로 곧 복구된다.
    //  • net stop/start 는 서비스가 완전히 멈출 때까지 동기 대기. 미설치/미기동 서비스는 무해하게 실패한다.
    [HttpPost("restart-services")]
    public async Task<IActionResult> RestartServices([FromBody] RestartServicesRequest? req, CancellationToken ct)
    {
        var target = (req?.Target ?? "all").Trim().ToLowerInvariant();

        // UI 는 카드를 숨기지만, 직접 호출·오래된 화면 캐시에도 실패 이유가 분명하도록 서버에서 한 번 더 막는다.
        if (!ServiceControlSupported)
        {
            _logger.LogWarning("[Settings] RestartServices target={Target} → 무시(Windows 서비스 아님, hostMode={Host})",
                target, HostModeText());
            return Ok(new RebuildResultDto(false,
                $"서비스 재시작은 Windows 서비스로 설치된 경우에만 사용할 수 있습니다 (현재: {HostModeText()})."));
        }

        // 대상 → (서비스명, 표시명) 목록. all 은 DSPilot 을 마지막에 둔다(자기 재시작이 응답 flush 뒤 오도록).
        var svc = new Dictionary<string, (string name, string label)>
        {
            ["dspilot"] = (SvcDspilot, "DSPilot"),
            ["agent"] = (SvcAgent, "Promaker.Agent"),
            ["mtx"] = (SvcMtx, "DSPilot MediaMTX"),
        };

        List<(string name, string label)> targets;
        if (target == "all")
            targets = new() { svc["mtx"], svc["agent"], svc["dspilot"] };
        else if (svc.TryGetValue(target, out var one))
            targets = new() { one };
        else
            return Ok(new RebuildResultDto(false, $"알 수 없는 재시작 대상입니다: {target}"));

        var includesSelf = targets.Any(t => t.name == SvcDspilot);

        try
        {
            if (!includesSelf)
            {
                // DSPilot 자신을 건드리지 않음 → 인라인 동기 실행 후 실제 결과 회신.
                var msgs = new List<string>();
                var okAll = true;
                foreach (var (name, label) in targets)
                {
                    var code = await RunCmdAsync($"/c net stop {name} & net start {name}", ct);
                    var ok = code == 0; // 마지막 net start 의 종료코드 (미설치/실패 시 비0)
                    okAll &= ok;
                    msgs.Add($"{label}: {(ok ? "재시작됨" : "재시작 실패(서비스 미설치이거나 권한 부족)")}");
                }
                _logger.LogWarning("[Settings] RestartServices target={Target} → {Msg}", target, string.Join("; ", msgs));
                return Ok(new RebuildResultDto(okAll, string.Join(" · ", msgs)));
            }

            // DSPilot 포함 → detached 배치. DSPilot 은 응답 flush 를 위해 2초 지연 뒤 마지막.
            var script = new System.Text.StringBuilder("@echo off\r\n");
            foreach (var (name, _) in targets.Where(t => t.name != SvcDspilot))
                script.Append($"net stop {name}\r\nnet start {name}\r\n");
            script.Append("timeout /t 2 /nobreak\r\n");
            script.Append($"net stop {SvcDspilot}\r\nnet start {SvcDspilot}\r\n");
            script.Append("del \"%~f0\"\r\n"); // 임시 배치 자기삭제

            var batPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"dspilot-restart-{Guid.NewGuid():N}.bat");
            await System.IO.File.WriteAllTextAsync(batPath, script.ToString(), ct);

            // start 로 배치를 DSPilot 프로세스와 분리 → net stop DSPilotService 에도 배치는 살아남아 재시작을 마친다.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"dspilot-restart\" /min \"{batPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            _logger.LogWarning("[Settings] RestartServices target={Target} → detached self-restart scheduled", target);
            var others = targets.Where(t => t.name != SvcDspilot).Select(t => t.label).ToList();
            var prefix = others.Count > 0 ? string.Join("·", others) + " 를 재시작한 뒤 약 2초 후 " : "약 2초 후 ";
            return Ok(new RebuildResultDto(true,
                prefix + "DSPilot 서비스가 재시작됩니다. 재시작 동안 이 화면 연결이 잠시 끊겼다가 자동으로 다시 연결됩니다."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Settings] RestartServices failed (target={Target})", target);
            return Ok(new RebuildResultDto(false, $"서비스 재시작 요청 실패: {ex.Message}"));
        }
    }

    // cmd.exe 를 실행하고 종료코드를 반환. net stop/start 완료까지 동기 대기. (인라인 재시작용)
    private static async Task<int> RunCmdAsync(string arguments, CancellationToken ct)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    // ── POST: 실측 duration 수동 보정 즉시 실행 ("지금 실측값 채우기(재시도)") ──
    // CompletedAt(1회성 플래그)을 무시하고 manual 로 즉시 보정한다. 적합 Flow 의 디바이스 duration/min/max 를
    // 현재 실측값으로 재기록(공식 적용) 후, 성공하면 AutoCalibrationService 가 DatabaseRebuilt 를 브로드캐스트한다.
    [HttpPost("auto-calibrate/run")]
    public async Task<ActionResult<RebuildResultDto>> RunAutoCalibration(CancellationToken ct)
    {
        try
        {
            var r = await _autoCal.RunAsync(manual: true, ct);
            return new RebuildResultDto(r.Success, r.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Settings] RunAutoCalibration failed");
            return new RebuildResultDto(false, $"실측값 채우기 실패: {ex.Message}");
        }
    }

    // ── POST: 모든 디바이스 Min/Max 초기화 (null) — 자동 보정의 역연산 ──
    // Duration(거동 구동 평균)은 보존하고 MinDuration/MaxDuration(이상감지 임계)만 전부 비운 뒤 project.aasx 재export.
    // 성공하면 AutoCalibrationService 가 DatabaseRebuilt 를 브로드캐스트한다.
    [HttpPost("auto-calibrate/clear-ranges")]
    public async Task<ActionResult<RebuildResultDto>> ClearCalibrationRanges(CancellationToken ct)
    {
        try
        {
            var r = await _autoCal.ClearRangesAsync(ct);
            return new RebuildResultDto(r.Success, r.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Settings] ClearCalibrationRanges failed");
            return new RebuildResultDto(false, $"Min/Max 초기화 실패: {ex.Message}");
        }
    }

    // ── GET: 이상감지 게이트(실측 확정) 상태 — 모델 duration 과 어긋나 닫힌(stale) Work 배지용 ──
    // stale = 확정값 ≠ 현재 모델 duration → ActionOver/Under 가 조용히 안 뜸(모델 재발행 후 재측정 안 한 경우).
    // 자동 stale-repair는 폐기됨. stale Work는 사용자가 "지금 실측값 채우기"를 실행해야 한다.
    [HttpGet("calibration-status")]
    public IActionResult GetCalibrationStatus()
    {
        var all = _project.GetCalibrationStatus();
        var stale = all.Where(s => s.StaleMax || s.StaleMin).ToList();
        return Ok(new
        {
            autoEnabled = false,
            loaded = _project.IsLoaded,
            total = all.Count,
            staleCount = stale.Count,
            stale = stale.Select(s => new
            {
                workId = s.WorkId,
                name = s.WorkName,
                staleMax = s.StaleMax,
                staleMin = s.StaleMin,
                calibMaxMs = s.CalibMaxMs,
                modelMaxMs = s.ModelMaxMs,
                calibMinMs = s.CalibMinMs,
                modelMinMs = s.ModelMinMs,
            }),
        });
    }

    // ── GET: 디바이스별 이상감지 차단 상태 (uptime 페이지 차단 관리 모달용) ──
    // 디바이스 = AASX 모델 모든 Call 의 DevicesAlias. 경로(FLOW / WORK / CALL)별로 그룹해 내려주고,
    // 현재 차단 규칙(AbnormalAlarm.DeviceFilters)을 병합. 규칙에만 남고 모델에서 사라진 디바이스도
    // InModel=false 로 포함(해제 가능해야 하므로).
    [HttpGet("abnormal-device-filters")]
    public ActionResult<AbnormalDeviceFilterStateDto> GetAbnormalDeviceFilters()
    {
        var pathsByDevice = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_project.IsLoaded)
                foreach (var flow in _project.GetAllFlows())
                    foreach (var work in _project.GetWorks(flow.Id))
                        foreach (var call in _project.GetCalls(work.Id))
                        {
                            if (string.IsNullOrWhiteSpace(call.DevicesAlias)) continue;
                            var device = call.DevicesAlias.Trim();
                            if (!pathsByDevice.TryGetValue(device, out var paths))
                                pathsByDevice[device] = paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                            paths.Add(AbnormalDeviceFilterHelpers.BuildPath(flow.Name, work.Name, call.Name));
                        }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Settings] 디바이스 경로 수집 실패 (non-critical)");
        }

        var rules = AbnormalDeviceFilterHelpers.Normalize(_settings.LoadSettings().AbnormalAlarm.DeviceFilters)
            .ToDictionary(r => r.Device, r => r.Kinds, StringComparer.OrdinalIgnoreCase);

        var devices = pathsByDevice.Keys.Union(rules.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => new AbnormalDeviceInfoDto(
                d,
                pathsByDevice.TryGetValue(d, out var paths) ? [.. paths] : [],
                rules.TryGetValue(d, out var kinds) ? [.. kinds] : [],
                pathsByDevice.ContainsKey(d)))
            .ToList();

        var kindOptions = AbnormalDeviceFilterHelpers.KindOptions
            .Select(o => new AbnormalKindOptionDto(o.Kind, o.Name, o.Label))
            .ToList();

        return new AbnormalDeviceFilterStateDto(devices, kindOptions);
    }

    // ── POST: 디바이스별 이상감지 차단 규칙 저장 (전체 교체) ──
    // 적용 즉시: 신규 발생분은 소스에서 완전 차단(미기록), 기존 기록은 알람/통계/기록 조회에서 숨김(가역).
    [HttpPost("abnormal-device-filters")]
    public async Task<ActionResult<SaveResultDto>> SaveAbnormalDeviceFilters(
        [FromBody] AbnormalDeviceFiltersSaveDto req, CancellationToken ct)
    {
        try
        {
            var normalized = AbnormalDeviceFilterHelpers.Normalize(
                (req.Filters ?? []).Select(f => new AbnormalDeviceFilter { Device = f.Device, Kinds = f.Kinds ?? [] }));

            _settings.Update(m => m.AbnormalAlarm.DeviceFilters = normalized);

            // 알람 배너/사이드바가 REST 재조회하도록 트리거 — 숨김/해제가 모든 화면에 즉시 반영.
            try { await _hub.Clients.All.SendAsync("AbnormalDetected", ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Settings] SignalR broadcast failed (non-critical)"); }

            return new SaveResultDto(true,
                normalized.Count == 0 ? "디바이스 차단이 모두 해제되었습니다." : $"디바이스 차단 규칙 {normalized.Count}건이 적용되었습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Settings] SaveAbnormalDeviceFilters failed");
            return new SaveResultDto(false, $"차단 규칙 저장 실패: {ex.Message}");
        }
    }

    // ── GET: 사용자정의(UserTag) 알람 차단 상태 (알람 차단 모달 "사용자지정 알람" 탭용) ──
    // 원천 = AASX 프로젝트에 정의된 UserTag(UserTagAlertService.GetDefinitions), 식별키 = TagAddress.
    // 현재 차단 목록(AbnormalAlarm.UserTagFilters)을 병합. 규칙에만 남고 모델에서 사라진 주소도
    // InModel=false 로 포함(해제 가능해야 하므로).
    [HttpGet("usertag-filters")]
    public ActionResult<UserTagFilterStateDto> GetUserTagFilters()
    {
        var defs = _userTags.GetDefinitions()
            .Where(d => !string.IsNullOrWhiteSpace(d.TagAddress))
            .GroupBy(d => d.TagAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var blocked = AbnormalDeviceFilterHelpers.NormalizeUserTagFilters(_settings.LoadSettings().AbnormalAlarm.UserTagFilters);
        var blockedSet = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);

        var tags = defs.Keys.Union(blocked, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => defs.TryGetValue(a, out var d0) ? d0.Name : a, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
            .Select(a =>
            {
                var inModel = defs.TryGetValue(a, out var d);
                return new UserTagFilterInfoDto(
                    a,
                    inModel ? d!.Name : a,
                    inModel ? d!.SystemName : "",
                    inModel ? d!.ValueType : "",
                    inModel ? d!.MatchOp : "",
                    inModel ? d!.MatchValue : "",
                    blockedSet.Contains(a),
                    inModel);
            })
            .ToList();

        return new UserTagFilterStateDto(tags);
    }

    // ── POST: 사용자정의(UserTag) 알람 차단 목록 저장 (전체 교체) ──
    // 적용 즉시: 신규 발생분은 소스에서 차단(미기록), 기존 기록은 알람/통계/기록 조회에서 숨김(가역).
    [HttpPost("usertag-filters")]
    public async Task<ActionResult<SaveResultDto>> SaveUserTagFilters(
        [FromBody] UserTagFiltersSaveDto req, CancellationToken ct)
    {
        try
        {
            var normalized = AbnormalDeviceFilterHelpers.NormalizeUserTagFilters(req.TagAddresses);

            _settings.Update(m => m.AbnormalAlarm.UserTagFilters = normalized);

            // 알람 배너/사이드바/이상알람 페이지가 REST 재조회하도록 트리거 — 숨김/해제가 모든 화면에 즉시 반영.
            try { await _hub.Clients.All.SendAsync("UserTagAlertsChanged", new { count = 0 }, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Settings] SignalR broadcast failed (non-critical)"); }

            return new SaveResultDto(true,
                normalized.Count == 0 ? "수동등록TAG 알람 차단이 모두 해제되었습니다." : $"수동등록TAG 알람 차단 {normalized.Count}건이 적용되었습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Settings] SaveUserTagFilters failed");
            return new SaveResultDto(false, $"차단 목록 저장 실패: {ex.Message}");
        }
    }

    // ── GET: 이 설치본의 제품/실행환경 정보 (고급 탭 하단 "이 DSPilot 정보" 카드) ──
    // 버전·빌드시각·런타임·경로·바인딩 주소를 한 번에 모아, 지원 문의 시 그대로 복사해 보낼 수 있게 한다.
    // 전부 읽기 전용 조회이며, 개별 항목이 실패해도(파일 접근 거부·단일파일 배포 등) 그 필드만 "—" 로
    // 떨어지고 응답 자체는 성공한다 — 정보 카드 때문에 설정 페이지가 깨지지 않도록.
    [HttpGet("app-info")]
    public ActionResult<AppInfoDto> GetAppInfo()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(SettingsController).Assembly;

        // 단일파일 배포에서는 Location 이 빈 문자열 → BaseDirectory 기준으로 재구성.
        var asmPath = asm.Location;
        if (string.IsNullOrEmpty(asmPath))
            asmPath = Path.Combine(AppContext.BaseDirectory, (asm.GetName().Name ?? "DSPilot") + ".dll");

        var version = FileVersionOf(asmPath) ?? asm.GetName().Version?.ToString() ?? "—";

        // InformationalVersion 은 SourceRevisionId 가 붙어 "1.0.1.38+<sha>" 형태가 될 수 있다 → 커밋 해시만 분리.
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = informational.IndexOf('+');
        var commit = plus >= 0 && plus + 1 < informational.Length ? informational[(plus + 1)..] : "";
        if (commit.Length > 12) commit = commit[..12];

        string buildLocal;
        try { buildLocal = System.IO.File.Exists(asmPath) ? System.IO.File.GetLastWriteTime(asmPath).ToString("yyyy-MM-dd HH:mm:ss") : "—"; }
        catch { buildLocal = "—"; }

        var startedLocal = "—";
        var uptimeSeconds = 0L;
        var pid = 0;
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            pid = p.Id;
            startedLocal = p.StartTime.ToString("yyyy-MM-dd HH:mm:ss");
            uptimeSeconds = (long)Math.Max(0, (DateTime.Now - p.StartTime).TotalSeconds);
        }
        catch { /* 권한/플랫폼 제약 — 표시만 생략 */ }

        // 실제 바인딩된 주소(Kestrel). 미노출 환경이면 설치 스크립트가 기록한 "Urls" 설정으로 폴백.
        var urls = _server.Features.Get<IServerAddressesFeature>()?.Addresses?.ToArray() ?? Array.Empty<string>();
        if (urls.Length == 0)
            urls = (_config["Urls"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var dbPath = GetDatabasePath();
        string dbSize;
        try { dbSize = System.IO.File.Exists(dbPath) ? FormatBytes(new FileInfo(dbPath).Length) : "—"; }
        catch { dbSize = "—"; }

        return new AppInfoDto(
            "DSPilot",
            version,
            commit,
            buildLocal,
            FileVersionOf(Path.Combine(AppContext.BaseDirectory, "Ds2.Core.dll")) ?? "—",
            RuntimeInformation.FrameworkDescription,
            $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            Environment.MachineName,
            HostModeText(),
            ServiceControlSupported,
            pid,
            startedLocal,
            uptimeSeconds,
            AppContext.BaseDirectory,
            _env.ContentRootPath,
            urls,
            _externalAccess.ResolveUrl(),
            dbPath,
            dbSize,
            _project.AasxFilePath,
            "© 2026 Dualsoft Inc.");
    }

    // 실행 방식 표시 문구. 정보 카드와 재시작 거부 메시지가 같은 문구를 쓰도록 한 곳에 둔다.
    private static string HostModeText()
        => WindowsServiceHelpers.IsWindowsService() ? "Windows 서비스"
            : Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService() ? "systemd 서비스"
            : "콘솔 실행";

    // 파일의 FileVersion(없으면 ProductVersion). 파일이 없거나 읽기 실패면 null.
    private static string? FileVersionOf(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var v = fvi.FileVersion ?? fvi.ProductVersion;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch { return null; }
    }

    // ── helpers ──

    private ActionResult<RebuildResultDto> Result(RebuildResult r)
        => new RebuildResultDto(r.Success, r.Message);

    private SettingsDto ToDto(AppSettingsModel m)
    {
        var hv = m.HistoryView;
        return new SettingsDto(
            ParseDbDir(m.Database.ConnectionString),
            GetDatabasePath(),
            m.Logging.LogLevel.Default,
            new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" },
            m.Ui.ShowPlcDebug,
            new HistoryViewDto(hv.MaxCycleTimeMs, hv.MinCycleTimeMs, hv.MaxCallGoingTimeMs, hv.MinCallGoingTimeMs,
                hv.CycleAverageWindow, hv.HeatmapCautionPct, hv.HeatmapDangerPct),
            new CctvDto(
                m.Cctv.MediaMtxApiUrl,
                m.Cctv.WebRtcPort,
                m.Cctv.Cameras.Select(c => new CameraDto(c.Name, c.RtspUrl, c.Enabled)).ToList(),
                _cctvSync.LastSyncOk,
                _cctvSync.LastSyncMessage),
            _project.AasxFilePath,
            BuildAasxStatus(),
            m.Ui.AlarmTickerIntervalSec,
            m.AbnormalAlarm.ResetIntervalHours,
            new AutoCalibrationDto(
                m.AutoCalibration.Enabled,
                m.AutoCalibration.MinCleanCycles,
                m.AutoCalibration.MedianMarginMaxPct,
                m.AutoCalibration.MarginMaxAbsMs,
                m.AutoCalibration.FillMin,
                m.AutoCalibration.PercentileMin,
                m.AutoCalibration.MarginMinPct,
                LocalStamp(m.AutoCalibration.CompletedAt),
                LocalStamp(m.AutoCalibration.LastAppliedAt),
                m.AutoCalibration.LastAppliedSummary,
                m.AutoCalibration.IsActionOverJudgeDsPilot() ? "dspilot" : "agent"),
            m.AbnormalAlarm.DisplayLevels.ToArray(),
            m.ExternalAccess.Url ?? "",
            _externalAccess.SeedUrlRaw);
    }

    // 배너 표시 레벨 정규화 — 유효값(Info/Warning/Error)만, 표준 표기·순서로, 중복 제거. 빈 결과 허용(=전체 표시).
    private static readonly string[] ValidAlarmLevels = { "Info", "Warning", "Error" };
    private static List<string> NormalizeDisplayLevels(string[]? input)
        => input is null
            ? new List<string>()
            : ValidAlarmLevels.Where(v => input.Any(i => string.Equals(i, v, StringComparison.OrdinalIgnoreCase))).ToList();

    // UTC(또는 Unspecified=UTC 저장) DateTime? → 로컬 표시 문자열. null 이면 null.
    private static string? LocalStamp(DateTime? utc)
        => utc is DateTime d
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : null;

    // Settings.razor RefreshAasxStatus + SyncBadgeText/SyncBadgeColor.
    private AasxStatusDto BuildAasxStatus()
    {
        bool exists = false;
        long size = 0;
        DateTime? writeUtc = null;
        string? currentSha = null;

        try
        {
            var path = _project.AasxFilePath;
            exists = System.IO.File.Exists(path);
            if (exists)
            {
                var info = new FileInfo(path);
                size = info.Length;
                writeUtc = info.LastWriteTimeUtc;
                currentSha = _project.GetAasxFileSha256();
            }
        }
        catch
        {
            exists = false; size = 0; writeUtc = null; currentSha = null;
        }

        var loadedUtc = _project.LastLoadedUtc;
        var loadedSha = _project.LastLoadedSha256;

        string syncText;
        if (!exists) syncText = "—";
        else if (loadedUtc is null) syncText = "미로드";
        else if (loadedSha is not null && currentSha is not null)
            syncText = string.Equals(loadedSha, currentSha, StringComparison.OrdinalIgnoreCase) ? "최신" : "외부 변경 감지됨";
        else if (writeUtc is null) syncText = "최신";
        else syncText = writeUtc > loadedUtc ? "외부 변경 감지됨" : "최신";

        var syncColor = syncText == "외부 변경 감지됨" ? "#e67e22"
            : syncText == "최신" ? "#27ae60"
            : "var(--color-text-secondary)";

        return new AasxStatusDto(
            exists, size, FormatBytes(size),
            FormatTime(writeUtc), FormatTime(loadedUtc),
            syncText, syncColor);
    }

    private string GetDatabasePath()
    {
        try { return _pathResolver.GetSharedDbPath(); }
        catch { return "알 수 없음"; }
    }

    // Settings.razor ParseConnectionString — "Data Source" 의 폴더 부분만 추출.
    private static string ParseDbDir(string connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return "";
        foreach (var part in connStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim();
            var val = kv[1].Trim();
            if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase))
            {
                var normalized = val.Replace('\\', '/');
                if (normalized.EndsWith($"/{DbFileName}", StringComparison.OrdinalIgnoreCase))
                    return val[..^(DbFileName.Length + 1)];
                return val;
            }
        }
        return "";
    }

    // Settings.razor BuildConnectionString — 동일 포맷 (verbatim).
    private static string BuildConnectionString(string? dbDir)
    {
        var dir = (dbDir ?? "").TrimEnd('/', '\\');
        return $"Data Source={dir}/{DbFileName};Version=3;BusyTimeout=20000";
    }

    private static string FormatTime(DateTime? utc)
        => utc is null ? "—" : utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

// ── DTOs (camelCase 자동: dbDir, dbPath, logLevel, ... maxCycleTimeMs, mediaMtxApiUrl, webRtcPort, rtspUrl) ──

public record SettingsDto(
    string DbDir,
    string DbPath,
    string LogLevelDefault,
    string[] LogLevels,
    bool ShowPlcDebug,
    HistoryViewDto HistoryView,
    CctvDto Cctv,
    string AasxFilePath,
    AasxStatusDto AasxStatus,
    int AlarmTickerIntervalSec = 3,
    int AbnormalAlarmResetIntervalHours = 24,
    // 실측 duration 수동 보정 설정 + 최초 완료 시각(표시용 로컬 문자열, 미실행이면 null). 기본값으로 기존 호출부 무손상.
    AutoCalibrationDto? AutoCalibration = null,
    // 배너 표시 레벨(Info/Warning/Error). 기본값으로 기존 호출부 무손상.
    string[]? AbnormalAlarmDisplayLevels = null,
    // 외부 접속 주소 — ExternalUrl=사용자 설정값(편집 대상), ExternalUrlSeed=설치 시 주입값(비어 있을 때 폴백 안내용).
    string ExternalUrl = "",
    string ExternalUrlSeed = "");

// ── 디바이스별 이상감지 차단 (uptime 페이지 차단 관리 모달용) ──

// 디바이스 1개의 차단 상태/입력. Kinds = 차단할 AbnormalKind int 값(0..3).
public record AbnormalDeviceFilterDto(string Device, List<int>? Kinds);

// 이상감지 유형 옵션 (Kind=int 값, Name=enum 이름, Label=한글 라벨) — 서버 enum 과 UI 체크박스 정합 보장.
public record AbnormalKindOptionDto(int Kind, string Name, string Label);

// 디바이스 1개의 통합 뷰 — 모델상 등장 경로(FLOW / WORK / CALL) + 현재 차단 유형.
// InModel=false 는 규칙에만 남고 현재 AASX 모델에는 없는 디바이스(해제할 수 있도록 계속 노출).
public record AbnormalDeviceInfoDto(string Device, List<string> Paths, List<int> BlockedKinds, bool InModel);

public record AbnormalDeviceFilterStateDto(
    List<AbnormalDeviceInfoDto> Devices,
    List<AbnormalKindOptionDto> KindOptions);

// POST 본문 — 전체 규칙 교체(PUT 의미). 클라이언트가 일괄 추가/해제를 계산해 최종 상태를 보낸다.
public record AbnormalDeviceFiltersSaveDto(List<AbnormalDeviceFilterDto>? Filters);

// ── 사용자정의(UserTag) 알람 차단 (알람 차단 모달 "사용자지정 알람" 탭용) ──

// UserTag 1개의 차단 상태. TagAddress = 정의 고유키. InModel=false 는 규칙에만 남고 현재 모델엔 없는 주소.
public record UserTagFilterInfoDto(
    string TagAddress, string Name, string SystemName,
    string ValueType, string MatchOp, string MatchValue,
    bool Blocked, bool InModel);

public record UserTagFilterStateDto(List<UserTagFilterInfoDto> Tags);

// POST 본문 — 차단할 TagAddress 전체 목록 교체(PUT 의미).
public record UserTagFiltersSaveDto(List<string>? TagAddresses);

// CompletedAt = 최초 수동 적용 시각(고정). LastAppliedAt = 마지막으로 AASX 에 기록한 시각(매 적용 갱신).
// 둘 다 로컬 표시 문자열, null = 미실행. MedianMarginMaxPct = Max 여유율(중앙값 대비 분수, 기본 0.60). PercentileMin = Min 백분위수(기본 5).
public record AutoCalibrationDto(
    bool Enabled,
    int MinCleanCycles,
    double MedianMarginMaxPct,
    int MarginMaxAbsMs,
    bool FillMin,
    double PercentileMin,
    double MarginMinPct,
    string? CompletedAt,
    string? LastAppliedAt,
    string? LastAppliedSummary,
    // ActionOver 판정 주체 — "dspilot" | "agent". 끝에 추가(positional 호환).
    // 응답은 항상 명시 값으로 채워진다(이 기본값은 미사용) — 모델 기본은 AppSettingsModel.ActionOverJudge 참조.
    string ActionOverJudge = "agent");

// 수동 보정 저장 입력 — 편집 가능한 필드만(CompletedAt 은 서버 관리, 저장으로 변경 불가).
// MedianMarginMaxPct 는 nullable — 구(캐시) 클라이언트가 이 필드 없이 보내면 서버가 기존값을 보존한다.
public record AutoCalibrationSaveDto(
    bool Enabled,
    int MinCleanCycles,
    double? MedianMarginMaxPct,
    int MarginMaxAbsMs,
    bool FillMin,
    double PercentileMin,
    double MarginMinPct,
    // null = 구(캐시) 클라이언트 — 기존값 보존. "agent" 외 값은 전부 dspilot 정규화.
    string? ActionOverJudge = null);

/// <summary>이상치 제외 Max 권장값(전역) + 산출 근거(flow별). BoundaryMs=0 은 표본 부족(권장값 없음).</summary>
public record RecommendedCycleMaxDto(
    int RecommendedMs,
    int RecommendedSec,
    int FloorMs,
    List<RecommendedCycleMaxFlowDto> Flows);

public record RecommendedCycleMaxFlowDto(
    string FlowName, int MedianMs, int P99Ms, int Sample, int BoundaryMs);

public record HistoryViewDto(
    int MaxCycleTimeMs,
    int MinCycleTimeMs,
    int MaxCallGoingTimeMs,
    int MinCallGoingTimeMs,
    int CycleAverageWindow = 20,
    // 동작편차 색상 범례 임계(편차 %). 기본값으로 기존 호출부 무손상.
    double HeatmapCautionPct = 10.0,
    double HeatmapDangerPct = 30.0);

public record CctvDto(
    string MediaMtxApiUrl,
    int WebRtcPort,
    List<CameraDto> Cameras,
    bool SyncOk,
    string SyncMessage,
    // 외부(원격·클라우드) 접속용 공인 IP/도메인. 기본값으로 기존 호출부(SettingsController) 무손상.
    string WebRtcAdditionalHosts = "",
    // 무조작 일시정지(절전 가드, LTE 종량 회선 보호). 기본값으로 기존 호출부 무손상.
    bool IdlePauseEnabled = true,
    int IdlePauseMinutes = 60,
    // 유효 전역 외부 접속 주소(표시 전용 — CCTV 모달이 "현재 적용 중" 안내에 사용, 편집은 설정 페이지).
    string ExternalUrl = "");

// Slug = MediaMTX 경로명(ASCII). GET 응답에만 포함(디버깅·참고용). 클라이언트는 slug 를 보내지 않으며,
// 서버(CctvController.SaveSettings)가 포지션 기반 이어받기 + AssignSlugs(cam1/cam2/…) 로 관리.
// FallbackImage = 대체(폴백) 이미지 URL. GET 에 포함되고, POST(CctvController.SaveSettings) 에서 라운드트립으로
// 영속된다(없으면 null → 포지션 기준 기존값 유지, 구 클라이언트 호환).
public record CameraDto(string Name, string RtspUrl, bool Enabled, string Slug = "", string? FallbackImage = null);

// 고급 탭 하단 "이 DSPilot 정보" 카드 — 제품/빌드/실행환경/경로 스냅샷(읽기 전용).
// Commit = InformationalVersion 의 "+" 뒤 커밋 해시(없으면 ""), UptimeSeconds = 조회 시점 가동초(클라이언트가 1초씩 증가시켜 표시).
public record AppInfoDto(
    string Product,
    string Version,
    string Commit,
    string BuildTimeLocal,
    string EngineVersion,
    string Runtime,
    string Os,
    string MachineName,
    string HostMode,
    bool ServiceControlSupported, // Windows 서비스 기동 여부 — 설정 화면의 "서비스 재시작" 카드 표시 조건
    int ProcessId,
    string StartedAtLocal,
    long UptimeSeconds,
    string InstallPath,
    string ContentRoot,
    string[] ListenUrls,
    string ExternalUrl,
    string DbPath,
    string DbSizeDisplay,
    string AasxPath,
    string Copyright);

public record AasxStatusDto(
    bool Exists,
    long Size,
    string SizeDisplay,
    string WriteTimeLocal,
    string LastLoadedLocal,
    string SyncText,
    string SyncColor);

public record SaveRequestDto(
    string DbDir,
    string? LogLevelDefault,
    int MaxCycleTimeMs,
    int MinCycleTimeMs,
    int MaxCallGoingTimeMs,
    int MinCallGoingTimeMs,
    int CycleAverageWindow = 20,
    int AlarmTickerIntervalSec = 3,
    int AbnormalAlarmResetIntervalHours = 24,
    // 동작편차 색상 범례 임계(편차 %). 기본값으로 기존 호출부 무손상.
    double HeatmapCautionPct = 10.0,
    double HeatmapDangerPct = 30.0,
    // 수동 보정 파라미터(편집 5필드). null 이면 기존 값 보존 — 기존 호출부 무손상.
    AutoCalibrationSaveDto? AutoCalibration = null,
    // 배너 표시 레벨(Info/Warning/Error). null 이면 기존 값 보존 — 기존 호출부 무손상.
    string[]? AbnormalAlarmDisplayLevels = null,
    // 외부 접속 주소(ExternalAccess.Url). null 이면 기존 값 보존 — 기존(캐시) 클라이언트 무손상.
    string? ExternalUrl = null);

public record SaveResultDto(bool Ok, string Message);

public record RebuildResultDto(bool Success, string Message);

public record AasxChangeLogDto(long Id, string ChangedAtLocal, string CutoffIso, string Source, string? Notes);

public record DeleteBeforeRequestDto(string CutoffIso);

// 현재 AASX 에 없는 flow 잔존 현황. ModelLoaded=false 면 AASX 미로드로 판정 불가(0 과 구별).
public record StaleFlowReportDto(
    IReadOnlyList<string> FlowNames, int DspFlowRows, int DspCallRows, int HistoryRows,
    int DowntimeEvents, int CycleOverrides, int Total, bool ModelLoaded);

// 서비스 재시작 대상: dspilot | agent | mtx | all (기본 all)
public record RestartServicesRequest(string? Target);
