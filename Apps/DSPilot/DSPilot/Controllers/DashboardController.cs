using DSPilot.Adapters;
using DSPilot.Models.Dashboard;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

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

    public DashboardController(DspDbService db, BlueprintService blueprint, DspRepositoryAdapter dspRepository)
    {
        _db = db;
        _blueprint = blueprint;
        _dspRepository = dspRepository;
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
            layout.CanvasWidth, layout.CanvasHeight,
            layout.GridColumns, layout.GridRows,
            layout.OffsetX, layout.OffsetY, layout.OffsetRight, layout.OffsetBottom,
            layout.CellWidth, layout.CellHeight,
            layout.BlueprintImagePath, _blueprint.ImageVersion,
            layout.FlowPlacements
                .Select(p => new FlowPlacementDto(p.FlowName, p.SystemId, p.Col, p.Row, p.ColSpan, p.RowSpan))
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
}
