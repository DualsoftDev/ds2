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
    private readonly IHubContext<MonitoringHub> _hub;

    public DashboardController(
        DspDbService db,
        BlueprintService blueprint,
        DspRepositoryAdapter dspRepository,
        AppSettingsService settings,
        IHubContext<MonitoringHub> hub)
    {
        _db = db;
        _blueprint = blueprint;
        _dspRepository = dspRepository;
        _settings = settings;
        _hub = hub;
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

        return new DashboardSnapshotDto(flows, layoutDto, _db.HasData, snap.Timestamp);
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
            .Select(h => new FlowHistoryDto(h.CycleNo, h.MT, h.WT, h.CT, h.RecordedAt, h.IsIdle))
            .ToList();
    }

    /// <summary>
    /// 시프트 운영 설정 + 실시간 진행. 여러 작업자 화면이 공유하도록 서버(appsettings) 에 보관.
    /// MadeCount = TargetFlow 의 현재 시프트 시작 이후 완료(비가동 제외) 사이클 수.
    /// </summary>
    [HttpGet("shift")]
    public async Task<ActionResult<ShiftDto>> GetShift()
    {
        var s = _settings.LoadSettings().Shift;
        var made = 0;
        if (!string.IsNullOrWhiteSpace(s.TargetFlow))
        {
            var startUtc = ResolveShiftStartUtc(s.Start, s.End);
            var hist = await _dspRepository.GetFlowHistoryByStartTimeAsync(s.TargetFlow, startUtc);
            made = hist.Count(h => !h.IsIdle);
        }
        return new ShiftDto(s.Start, s.End, s.ShiftType, s.TargetFlow, s.TargetCount, made);
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
        sh.TargetCount = req.TargetCount < 0 ? 0 : req.TargetCount;
        _settings.SaveSettings(model);

        try { await _hub.Clients.All.SendAsync("ShiftChanged"); }
        catch { /* best effort — 브로드캐스트 실패해도 저장은 유효 */ }

        return await GetShift();
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
