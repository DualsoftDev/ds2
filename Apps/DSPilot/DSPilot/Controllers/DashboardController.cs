using System.Globalization;
using DSPilot.Adapters;
using DSPilot.Hubs;
using DSPilot.Models.Dashboard;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Dashboard(대시보드) 데이터 API.
///
/// Blazor /dashboard 가 @inject 로 쓰던 DspDbService(실시간 스냅샷) + BlueprintService(도면 레이아웃) +
/// DspRepositoryAdapter(Flow 히스토리) 를 정적 페이지(/app/dashboard.html)가 fetch 로 쓸 수 있게 얇게 래핑한다.
/// 신규 데이터 로직 없음(직렬화 경계). 세 서비스 모두 싱글톤이라 Blazor 와 동일 인스턴스를 공유한다.
/// 실시간은 /hubs/monitoring SignalR 이벤트를 트리거로 이 스냅샷을 디바운스 refetch 한다.
/// </summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly DspDbService _db;
    private readonly BlueprintService _blueprint;
    private readonly DspRepositoryAdapter _dspRepository;
    private readonly AppSettingsService _settings;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly CycleAnalysisService _cycleAnalysis;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly AbnormalEventService _abnormal;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        DspDbService db,
        BlueprintService blueprint,
        DspRepositoryAdapter dspRepository,
        AppSettingsService settings,
        IFlowMetricsService flowMetrics,
        CycleAnalysisService cycleAnalysis,
        IHubContext<MonitoringHub> hub,
        AbnormalEventService abnormal,
        ILogger<DashboardController> logger)
    {
        _db = db;
        _blueprint = blueprint;
        _dspRepository = dspRepository;
        _settings = settings;
        _flowMetrics = flowMetrics;
        _cycleAnalysis = cycleAnalysis;
        _hub = hub;
        _abnormal = abnormal;
        _logger = logger;
    }

    /// <summary>현재 Flow 상태 스냅샷 + 도면 레이아웃(셀 크기·배치·이미지 버전 포함).</summary>
    [HttpGet("snapshot")]
    public ActionResult<DashboardSnapshotDto> GetSnapshot()
    {
        var snap = _db.Snapshot;
        var layout = _blueprint.Layout;

        var flows = snap.Flows
            .Select(f => new FlowStateDto(
                f.FlowName, f.State, f.MT, f.WT, f.CT,
                f.AvgMT, f.AvgWT, f.AvgCT, f.MovingStartName, f.MovingEndName))
            .ToList();

        var layoutDto = new LayoutDto(
            layout.CanvasWidth, layout.CanvasHeight, layout.CardScale,
            layout.BlueprintImagePath, _blueprint.ImageVersion,
            layout.FlowPlacements
                .Select(p => new FlowPlacementDto(p.FlowName, p.SystemId, p.X ?? 0.5, p.Y ?? 0.5))
                .ToList(),
            layout.FlowProcessOrder
                .Select(o => new FlowOrderDto(o.FlowName))
                .ToList());

        return new DashboardSnapshotDto(flows, layoutDto, _db.HasData, snap.Timestamp,
            _settings.LoadSettings().Ui.AlarmTickerIntervalSec);
    }

    /// <summary>
    /// 최근 경로이탈 이상감지(DS 4종) N건 — 대시보드 라이브 피드 초기 적재용.
    /// 실시간 갱신은 SignalR "AbnormalDetected" 트리거를 받아 이 엔드포인트를 재조회한다.
    /// 인메모리 링버퍼(AbnormalEventService) 소스 — 프로세스 재시작 시 비고 새 신호부터 채워짐.
    /// </summary>
    [HttpGet("abnormals")]
    public ActionResult<IReadOnlyList<AbnormalEventDto>> GetAbnormals([FromQuery] int limit = 20)
        => Ok(_abnormal.GetRecent(limit));

    /// <summary>
    /// 데모용 이상감지 이벤트 주입 — 브라우저 콘솔에서 demoAlarm() 으로 호출.
    /// kind: 0=센서단선(Error), 1=센서오감지(Warning), 2=동작지연(Error), 3=동작과속(Warning)
    /// </summary>
    [HttpPost("demo-alarm")]
    public async Task<IActionResult> InjectDemoAlarm(
        [FromQuery] int kind = 0,
        [FromQuery] string flowName = "데모-Flow",
        [FromQuery] string workName = "데모-Work")
    {
        await _abnormal.InjectDemoAsync(kind, flowName, workName);
        return Ok(new { ok = true, kind, flowName, workName });
    }

    /// <summary>이상감지 피드 전체 초기화 — 데모 리셋용 (demoAlarm.clear()).</summary>
    [HttpDelete("demo-alarm")]
    public async Task<IActionResult> ClearDemoAlarms()
    {
        await _abnormal.ClearAsync();
        return Ok(new { ok = true });
    }

    /// <summary>
    /// 특정 Flow 의 사이클 히스토리(비가동 제외, 최신순). Blazor Dashboard.LoadFlowHistoryAsync 와 동일:
    /// !IsIdle 필터 후 CycleNo 를 (개수-인덱스)로 재할당해 최신이 가장 큰 번호.
    /// </summary>
    [HttpGet("flows/{flowName}/history")]
    public async Task<ActionResult<List<FlowHistoryDto>>> GetHistory(string flowName, [FromQuery] int limit = 200)
    {
        var hist = await _dspRepository.GetFlowHistoryAsync(flowName, limit);
        hist = hist.Where(h => !h.IsIdle).ToList();
        for (int i = 0; i < hist.Count; i++)
            hist[i].CycleNo = hist.Count - i;

        return hist
            // RecordedAt 은 DateTime.UtcNow 로 저장되지만 SQLite 왕복 후 Kind=Unspecified 라
            // System.Text.Json 이 'Z' 없이 직렬화 → 브라우저 new Date() 가 로컬 시각으로 오인(KST 면 9h 밀림).
            // UTC 로 마킹해 'Z' 표기로 emit → 클라가 절대시각으로 정확히 파싱(기간별 추이 '오늘' 필터 누락 수정).
            .Select(h => new FlowHistoryDto(h.CycleNo, h.MT, h.WT, h.CT,
                DateTime.SpecifyKind(h.RecordedAt, DateTimeKind.Utc), h.IsIdle))
            .ToList();
    }

    /// <summary>
    /// 시프트 생산목표 Work 드롭다운(Flow→Work) 용 — 한 Flow 의 Work 이름 목록(정의 순서).
    /// </summary>
    [HttpGet("flows/{flowName}/works")]
    public ActionResult<List<string>> GetFlowWorks(string flowName)
    {
        if (string.IsNullOrWhiteSpace(flowName))
            return new List<string>();
        return _cycleAnalysis.GetWorkNamesForFlow(flowName);
    }

    /// <summary>
    /// 시프트 운영 설정 + 실시간 진행. 여러 작업자 화면이 공유하도록 서버(appsettings) 에 보관.
    /// MadeCount = 현재 시프트 시작 이후 만든 수 — TargetWork 설정 시 그 Work 의 완료(InTag↑) 횟수,
    /// 미설정 시 TargetFlow 의 완료(비가동 제외) 사이클 수(구버전 폴백).
    /// </summary>
    [HttpGet("shift")]
    public async Task<ActionResult<ShiftDto>> GetShift()
    {
        var s = _settings.LoadSettings().Shift;
        var made = 0;
        if (!string.IsNullOrWhiteSpace(s.TargetFlow))
        {
            var startUtc = ResolveShiftStartUtc(s.Start, s.End);
            if (!string.IsNullOrWhiteSpace(s.TargetWork))
            {
                // Work 단위 — 완료(InTag↑) rising edge 수. (윈도 끝 = 지금까지)
                made = await _cycleAnalysis.CountWorkCompletionsAsync(
                    s.TargetFlow, s.TargetWork, startUtc, DateTime.UtcNow);
            }
            else
            {
                // 폴백: Flow 사이클 수(비가동 제외).
                var hist = await _dspRepository.GetFlowHistoryByStartTimeAsync(s.TargetFlow, startUtc);
                made = hist.Count(h => !h.IsIdle);
            }
        }
        return new ShiftDto(s.Start, s.End, s.ShiftType, s.TargetFlow, s.TargetWork, s.TargetCount, made);
    }

    /// <summary>시프트 설정 저장 후 ShiftChanged 브로드캐스트(다른 작업자 화면 동기화). 저장 직후의 진행값을 반환.</summary>
    [HttpPost("shift")]
    public async Task<ActionResult<ShiftDto>> SaveShift([FromBody] ShiftSaveDto req)
    {
        var model = _settings.LoadSettings();
        var sh = model.Shift;
        sh.Start = NormalizeTime(req.Start, sh.Start);
        sh.End = NormalizeTime(req.End, sh.End);
        sh.ShiftType = string.IsNullOrWhiteSpace(req.ShiftType) ? sh.ShiftType : req.ShiftType.Trim();
        sh.TargetFlow = string.IsNullOrWhiteSpace(req.TargetFlow) ? null : req.TargetFlow.Trim();
        // Flow 가 비면 Work 도 무의미하므로 함께 비운다.
        sh.TargetWork = string.IsNullOrWhiteSpace(req.TargetWork) || sh.TargetFlow is null
            ? null
            : req.TargetWork.Trim();
        sh.TargetCount = req.TargetCount < 0 ? 0 : req.TargetCount;
        _settings.SaveSettings(model);

        try { await _hub.Clients.All.SendAsync("ShiftChanged"); }
        catch { /* best effort — 브로드캐스트 실패해도 저장은 유효 */ }

        return await GetShift();
    }

    /// <summary>
    /// 히스토리 이상치 제외 필터(Flow별 최소·최대 CT 범위). 여러 작업자 화면이 같은 기준을 보도록 서버 공유.
    /// </summary>
    [HttpGet("exclusions")]
    public ActionResult<List<CycleExclusionDto>> GetExclusions()
    {
        return _settings.LoadSettings().CycleExclusion.Ranges
            .Select(r => new CycleExclusionDto(r.FlowName, r.MinSec, r.MaxSec))
            .ToList();
    }

    /// <summary>
    /// Flow 의 이상치 제외 범위 저장(upsert) 또는 해제(min/max 둘 다 null). 저장 후 유효범위를 IsIdle 에 소급 박제
    /// (ReapplyIdleThresholdsAsync)해 대시보드 평균·시프트·OEE 가 즉시 일관 반영되게 하고, ExclusionsChanged(화면
    /// 필터 동기화) + DatabaseRebuilt(평균/미러 새로고침) 를 브로드캐스트. 정규화된 전체 목록을 반환.
    /// </summary>
    [HttpPost("exclusions")]
    public async Task<ActionResult<List<CycleExclusionDto>>> SaveExclusion([FromBody] CycleExclusionSaveDto req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.FlowName))
            return BadRequest("flowName is required.");

        _settings.SaveCycleExclusion(req.FlowName.Trim(), req.MinSec, req.MaxSec);

        // per-flow 제외 변경 = 유효 비가동 범위 변경 → 글로벌 설정 저장과 동일하게 소급 재집계(평균·IsIdle 단일 소스).
        try { await _flowMetrics.ReapplyIdleThresholdsAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Dashboard] 이상치 제외 변경 후 소급 재집계 실패 (non-critical)"); }

        try { await _hub.Clients.All.SendAsync("ExclusionsChanged"); }
        catch { /* best effort — 브로드캐스트 실패해도 저장은 유효 */ }
        try { await _hub.Clients.All.SendAsync("DatabaseRebuilt"); }
        catch { /* best effort */ }

        return GetExclusions();
    }

    // 현재(또는 가장 최근) 시프트 시작을 UTC 로 해석. 클라이언트 _shiftWindow() 와 동일 규칙:
    //  - 주간(End>Start): 윈도우는 오늘 [Start, End]. 시작은 오늘 Start.
    //  - 야간(End≤Start, 자정 넘김): now≥Start 면 오늘 Start, 아니면 어제 Start.
    // RecordedAt 은 UtcNow 로 저장되므로 로컬 시작을 UTC 로 변환해 비교.
    private static DateTime ResolveShiftStartUtc(string start, string end)
    {
        var now = DateTime.Now; // 현장 PC 로컬(벽시계) — 시프트 시각도 로컬 기준
        var startT = ParseTime(start, new TimeSpan(8, 0, 0));
        var endT = ParseTime(end, new TimeSpan(17, 0, 0));
        var crosses = endT <= startT;
        var startToday = now.Date + startT;

        DateTime resolved;
        if (!crosses) resolved = startToday;
        else resolved = now.TimeOfDay >= startT ? startToday : startToday.AddDays(-1);

        return DateTime.SpecifyKind(resolved, DateTimeKind.Local).ToUniversalTime();
    }

    private static TimeSpan ParseTime(string? value, TimeSpan fallback)
        => TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var t) ? t : fallback;

    // "HH:mm" 형식만 허용, 아니면 기존값 유지.
    private static string NormalizeTime(string? value, string fallback)
        => TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var t)
            ? t.ToString("hh\\:mm", CultureInfo.InvariantCulture)
            : fallback;
}
