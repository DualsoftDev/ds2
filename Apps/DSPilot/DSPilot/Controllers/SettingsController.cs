using DSPilot.Adapters;
using DSPilot.Models;
using DSPilot.Hubs;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

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
    private readonly DatabaseLifecycleService _lifecycle;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly DsProjectService _project;
    private readonly CctvMediaMtxService _cctvSync;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        AppSettingsService settings,
        DatabaseLifecycleService lifecycle,
        IFlowMetricsService flowMetrics,
        DsProjectService project,
        CctvMediaMtxService cctvSync,
        IDatabasePathResolver pathResolver,
        IHubContext<MonitoringHub> hub,
        ILogger<SettingsController> logger)
    {
        _settings = settings;
        _lifecycle = lifecycle;
        _flowMetrics = flowMetrics;
        _project = project;
        _cctvSync = cctvSync;
        _pathResolver = pathResolver;
        _hub = hub;
        _logger = logger;
    }

    // ── GET: 전체 설정 + 파생 표시값 ──
    [HttpGet]
    public ActionResult<SettingsDto> Get()
    {
        var m = _settings.LoadSettings();
        return ToDto(m);
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
            // 현재 디스크 설정을 baseline 으로 로드 후 클라이언트 편집값을 덮어쓴다.
            // (UI 미노출 섹션 DspTables/Hub/Ui.ShowPlcDebug 등은 baseline 유지 — appsettings.json 으로만 관리)
            var m = _settings.LoadSettings();

            m.Database.ConnectionString = BuildConnectionString(req.DbDir);
            m.Logging.LogLevel.Default = string.IsNullOrWhiteSpace(req.LogLevelDefault) ? m.Logging.LogLevel.Default : req.LogLevelDefault;

            m.HistoryView.MaxCycleTimeMs = req.MaxCycleTimeMs;
            m.HistoryView.MinCycleTimeMs = req.MinCycleTimeMs;
            m.HistoryView.MaxCallGoingTimeMs = req.MaxCallGoingTimeMs;
            m.HistoryView.MinCallGoingTimeMs = req.MinCallGoingTimeMs;
            m.HistoryView.CycleAverageWindow = req.CycleAverageWindow;

            // CCTV(RTSP) 카메라 설정은 CCTV 페이지(CctvController.SaveSettings)가 소유 — 여기서는 건드리지 않는다.
            // (Settings 저장이 카메라 목록을 덮어써 오버레이/카메라가 유실되는 것을 방지.)

            _settings.SaveSettings(m);

            // 비가동 임계값 변경 소급 적용 (대시보드·히스토리 즉시 반영) — Blazor SaveSettings 와 동일.
            var (restamped, flows) = await _flowMetrics.ReapplyIdleThresholdsAsync();

            // 임계값 소급 적용 → 대시보드/히트맵 미러 새로고침.
            try { await _hub.Clients.All.SendAsync("DatabaseRebuilt", ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Settings] SignalR broadcast failed (non-critical)"); }

            return new SaveResultDto(
                true,
                $"설정이 저장되었습니다. 비가동 판정 소급 적용: 히스토리 {restamped}건 재평가, Flow {flows}개 평균 재집계.");
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
            new HistoryViewDto(hv.MaxCycleTimeMs, hv.MinCycleTimeMs, hv.MaxCallGoingTimeMs, hv.MinCallGoingTimeMs, hv.CycleAverageWindow),
            new CctvDto(
                m.Cctv.MediaMtxApiUrl,
                m.Cctv.WebRtcPort,
                m.Cctv.Cameras.Select(c => new CameraDto(c.Name, c.RtspUrl, c.Enabled)).ToList(),
                _cctvSync.LastSyncOk,
                _cctvSync.LastSyncMessage),
            _project.AasxFilePath,
            BuildAasxStatus());
    }

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
    AasxStatusDto AasxStatus);

public record HistoryViewDto(
    int MaxCycleTimeMs,
    int MinCycleTimeMs,
    int MaxCallGoingTimeMs,
    int MinCallGoingTimeMs,
    int CycleAverageWindow = 20);

public record CctvDto(
    string MediaMtxApiUrl,
    int WebRtcPort,
    List<CameraDto> Cameras,
    bool SyncOk,
    string SyncMessage,
    // 외부(원격·클라우드) 접속용 공인 IP/도메인. 기본값으로 기존 호출부(SettingsController) 무손상.
    string WebRtcAdditionalHosts = "");

// Slug = MediaMTX 경로명(ASCII). GET 응답에만 포함(디버깅·참고용). 클라이언트는 slug 를 보내지 않으며,
// 서버(CctvController.SaveSettings)가 포지션 기반 이어받기 + AssignSlugs(cam1/cam2/…) 로 관리.
public record CameraDto(string Name, string RtspUrl, bool Enabled, string Slug = "");

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
    int CycleAverageWindow = 20);

public record SaveResultDto(bool Ok, string Message);

public record RebuildResultDto(bool Success, string Message);

public record AasxChangeLogDto(long Id, string ChangedAtLocal, string CutoffIso, string Source, string? Notes);

public record DeleteBeforeRequestDto(string CutoffIso);
