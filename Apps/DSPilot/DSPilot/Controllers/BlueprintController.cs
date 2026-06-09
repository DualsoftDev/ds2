// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Blueprint(도면 레이아웃 편집기) API.
///
/// Blazor /editor(Editor.razor) 가 @inject 로 쓰던 BlueprintService(도면 레이아웃) +
/// DsProjectService(활성 시스템/Flow 목록) 를 정적 페이지(/app/editor.html)가 fetch 로 쓸 수 있게 얇게 래핑한다.
/// 신규 데이터 로직 없음(직렬화 경계). 두 서비스 모두 싱글톤이라 Blazor 와 동일 인스턴스를 공유한다.
///
/// Editor.razor 와의 차이: Blazor 는 UpdatePlacementLocal/RemovePlacementLocal 로 메모리에만 누적했다가
/// "적용" 시 SaveLayout 으로 한 번에 flush 했다. 정적 페이지는 서버에 회로가 없으므로 각 mutation 을
/// 즉시 영속화(UpdatePlacement / SaveLayout)한다. 결과 레이아웃 자체는 동일하다.
/// FlowName/SystemName/SystemId 등 placement 메타는 클라이언트를 신뢰하지 않고 항상 DsProjectService 로 재해석한다.
/// </summary>
[ApiController]
[Route("api/blueprint")]
public class BlueprintController : ControllerBase
{
    private readonly BlueprintService _blueprint;
    private readonly DsProjectService _project;
    private readonly ILogger<BlueprintController> _logger;

    public BlueprintController(
        BlueprintService blueprint,
        DsProjectService project,
        ILogger<BlueprintController> logger)
    {
        _blueprint = blueprint;
        _project = project;
        _logger = logger;
    }

    // ── GET: 레이아웃 전체 + 자동배치용 Flow 목록 + stale 판정 ──
    [HttpGet]
    public ActionResult<BlueprintDto> Get()
    {
        var ordered = BuildOrderedFlows();
        var layout = BuildLayoutDto();

        var available = ordered
            .Select(f => new AvailableFlowDto(f.FlowId, f.FlowName, f.SystemName, f.SystemId))
            .ToList();

        bool isStale = _blueprint.IsFlowSetStale(ordered.Select(f => f.FlowId));

        return new BlueprintDto(_project.IsLoaded, layout, available, isStale);
    }

    // ── POST: 단일 블럭 자유 배치/이동 → UpdatePlacement + 즉시 저장 ──
    // 자유 배치 모델: 클라이언트는 flowId + 정규화 중심좌표 X/Y(0..1) 만 보낸다(겹침 허용 → occupancy 검사 없음).
    // FlowName/SystemName/SystemId 는 서버에서 재해석. 격자(Col/Row/Span)는 폐지(레거시 0 유지).
    [HttpPost("placement")]
    public ActionResult<BlueprintDto> Placement([FromBody] PlacementRequestDto req)
    {
        if (!Guid.TryParse(req.FlowId, out var flowId))
            return BadRequest(new { error = "invalid flowId" });

        var info = FlowInfo(flowId);
        if (info is null)
            return BadRequest(new { error = "unknown flowId (project 미로드 또는 삭제된 Flow)" });

        _blueprint.UpdatePlacement(new FlowPlacement
        {
            FlowId = flowId,
            SystemId = info.Value.SystemId,
            FlowName = info.Value.FlowName,
            SystemName = info.Value.SystemName,
            X = Math.Clamp(req.X, 0, 1),
            Y = Math.Clamp(req.Y, 0, 1),
        });

        return new BlueprintDto(_project.IsLoaded, BuildLayoutDto(), BuildAvailable(), Stale());
    }

    // ── POST: 단일 블럭 제거 ──
    [HttpPost("placement/delete")]
    public ActionResult<BlueprintDto> DeletePlacement([FromBody] FlowIdRequestDto req)
    {
        if (!Guid.TryParse(req.FlowId, out var flowId))
            return BadRequest(new { error = "invalid flowId" });
        _blueprint.RemovePlacement(flowId);
        return new BlueprintDto(_project.IsLoaded, BuildLayoutDto(), BuildAvailable(), Stale());
    }

    // ── POST: 그리드/오프셋(+선택적 공정순서) 저장 또는 전체 레이아웃 JSON import ──
    // layoutJson 이 있으면 LoadLayoutJson(=import), 없으면 grid/offset 필드만 갱신 후 SaveLayout.
    [HttpPost("save")]
    public ActionResult<BlueprintDto> Save([FromBody] SaveLayoutRequestDto req)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(req.LayoutJson))
            {
                _blueprint.LoadLayoutJson(req.LayoutJson!); // grid/offset/placements/order 통째 교체 + 즉시 저장
                return new BlueprintDto(_project.IsLoaded, BuildLayoutDto(), BuildAvailable(), Stale());
            }

            var L = _blueprint.Layout;
            // 자유 배치 모델: 공통 카드 크기. (격자/오프셋 필드는 레거시 — 넘어오면 받되 자유 배치 렌더엔 미사용)
            if (req.CardScale is double cs) L.CardScale = Math.Clamp(cs, 0.02, 0.6);
            if (req.GridColumns is int gc) L.GridColumns = Math.Clamp(gc, 1, 100);
            if (req.GridRows is int gr) L.GridRows = Math.Clamp(gr, 1, 100);
            if (req.OffsetX is int ox) L.OffsetX = Math.Max(0, ox);
            if (req.OffsetY is int oy) L.OffsetY = Math.Max(0, oy);
            if (req.OffsetRight is int orr) L.OffsetRight = Math.Max(0, orr);
            if (req.OffsetBottom is int ob) L.OffsetBottom = Math.Max(0, ob);

            // 공정 순서가 넘어오면 갱신(Editor 의 FlowProcessOrder 저장과 동일).
            if (req.FlowProcessOrder is { Count: > 0 } order)
            {
                var infoMap = AllFlowInfo();
                L.FlowProcessOrder = order
                    .Where(id => Guid.TryParse(id, out _))
                    .Select(id => Guid.Parse(id))
                    .Select(id => new FlowOrderEntry
                    {
                        FlowId = id,
                        FlowName = infoMap.TryGetValue(id, out var fi) ? fi.FlowName : "",
                    })
                    .ToList();
            }

            _blueprint.SaveLayout();
            return new BlueprintDto(_project.IsLoaded, BuildLayoutDto(), BuildAvailable(), Stale());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Blueprint] Save failed");
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── POST: 도면 이미지 업로드 (multipart, field 'file') → 저장 + 캔버스 크기 자동 감지 ──
    [HttpPost("image")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<ActionResult<ImageResultDto>> UploadImage(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "no file" });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg"))
            return BadRequest(new { error = "png/jpg 만 허용됩니다." });

        await using var stream = file.OpenReadStream();
        var (w, h) = await _blueprint.SaveBlueprintImageAsync(stream, file.FileName);

        return new ImageResultDto(
            _blueprint.Layout.BlueprintImagePath,
            _blueprint.ImageVersion,
            w, h,
            _blueprint.Layout.CanvasWidth,
            _blueprint.Layout.CanvasHeight);
    }

    // ── POST: 자동 배치 (Editor 의 "블럭 모두 채우기" = AutoFillPlacements) ──
    [HttpPost("autofill")]
    public ActionResult<BlueprintDto> AutoFill()
    {
        var ordered = BuildOrderedFlows();
        if (ordered.Count == 0)
            return BadRequest(new { error = "배치할 Flow 가 없습니다(project 미로드)." });

        _blueprint.AutoFillPlacements(ordered);
        _blueprint.SaveLayout();
        return new BlueprintDto(_project.IsLoaded, BuildLayoutDto(), BuildAvailable(), Stale());
    }

    // ── POST: 초기화 후 자동 배치 (ResetFlowPlacementsAndAutoFill = 즉시 flush) ──
    [HttpPost("reset")]
    public ActionResult<BlueprintDto> Reset()
    {
        var ordered = BuildOrderedFlows();
        if (ordered.Count == 0)
            return BadRequest(new { error = "배치할 Flow 가 없습니다(project 미로드)." });

        _blueprint.ResetFlowPlacementsAndAutoFill(ordered);
        return new BlueprintDto(_project.IsLoaded, BuildLayoutDto(), BuildAvailable(), Stale());
    }

    // ── POST: 인플레이스 편집 일괄 적용 ("저장" 버튼) ──
    // 정적 페이지가 편집 중 메모리에만 누적한 배치/크기를 한 번에 서버로 flush 한다(드래그/배치마다의 라운드트립 제거).
    // 배치 전체를 교체하고 공통 카드 크기를 갱신한 뒤 1회 저장. 다른 mutation 과 동일하게 FlowName/SystemName/SystemId
    // 는 클라이언트를 신뢰하지 않고 DsProjectService 로 재해석하며, 알 수 없는(삭제·미로드) flowId 는 조용히 무시한다.
    [HttpPost("replace")]
    public ActionResult<BlueprintDto> Replace([FromBody] ReplaceLayoutRequestDto req)
    {
        var infoMap = AllFlowInfo();
        var L = _blueprint.Layout;

        if (req.CardScale is double cs) L.CardScale = Math.Clamp(cs, 0.02, 0.6);

        var placements = new List<FlowPlacement>();
        foreach (var p in req.Placements ?? [])
        {
            if (!Guid.TryParse(p.FlowId, out var fid)) continue;
            if (!infoMap.TryGetValue(fid, out var info)) continue; // 삭제된/미로드 Flow 스킵
            placements.Add(new FlowPlacement
            {
                FlowId = fid,
                SystemId = info.SystemId,
                FlowName = info.FlowName,
                SystemName = info.SystemName,
                X = Math.Clamp(p.X, 0, 1),
                Y = Math.Clamp(p.Y, 0, 1),
            });
        }
        L.FlowPlacements = placements;

        // 공정순서(옵션) — 넘어오면 갱신, 없으면 기존 보존.
        if (req.FlowProcessOrder is { Count: > 0 } order)
        {
            L.FlowProcessOrder = order
                .Where(id => Guid.TryParse(id, out _))
                .Select(id => Guid.Parse(id))
                .Select(id => new FlowOrderEntry
                {
                    FlowId = id,
                    FlowName = infoMap.TryGetValue(id, out var fi) ? fi.FlowName : "",
                })
                .ToList();
        }

        _blueprint.SaveLayout();
        return new BlueprintDto(_project.IsLoaded, BuildLayoutDto(), BuildAvailable(), Stale());
    }

    // ── helpers ──

    private bool Stale() => _blueprint.IsFlowSetStale(BuildOrderedFlows().Select(f => f.FlowId));

    private List<AvailableFlowDto> BuildAvailable()
        => BuildOrderedFlows()
            .Select(f => new AvailableFlowDto(f.FlowId, f.FlowName, f.SystemName, f.SystemId))
            .ToList();

    private BpLayoutDto BuildLayoutDto()
    {
        var L = _blueprint.Layout;
        return new BpLayoutDto(
            L.CanvasWidth, L.CanvasHeight, L.CardScale,
            L.BlueprintImagePath, _blueprint.ImageVersion,
            L.FlowPlacements
                .Select(p => new BpPlacementDto(p.FlowId, p.FlowName, p.SystemId, p.SystemName, p.X ?? 0.5, p.Y ?? 0.5))
                .ToList(),
            L.FlowProcessOrder
                .Select(o => new BpOrderDto(o.FlowId, o.FlowName))
                .ToList());
    }

    // Editor.razor.OnInitialized 의 _orderedFlows 빌드와 동일:
    // 활성 시스템들의 Flow 평탄화 → 저장된 FlowProcessOrder 기준 정렬(없으면 자연 순서).
    private List<(Guid FlowId, string FlowName, string SystemName, Guid SystemId)> BuildOrderedFlows()
    {
        if (!_project.IsLoaded) return [];

        var allFlows = _project.GetActiveSystems()
            .SelectMany(sys => _project.GetFlows(sys.Id)
                .Select(f => (FlowId: f.Id, FlowName: f.Name, SystemName: sys.Name, SystemId: sys.Id)))
            .ToList();

        var savedOrder = _blueprint.Layout.FlowProcessOrder;
        if (savedOrder.Count > 0)
        {
            var orderMap = savedOrder
                .Select((e, i) => (e.FlowId, Index: i))
                .ToDictionary(x => x.FlowId, x => x.Index);
            return allFlows
                .OrderBy(f => orderMap.GetValueOrDefault(f.FlowId, int.MaxValue))
                .ToList();
        }
        return allFlows;
    }

    private Dictionary<Guid, (string FlowName, string SystemName, Guid SystemId)> AllFlowInfo()
    {
        var map = new Dictionary<Guid, (string FlowName, string SystemName, Guid SystemId)>();
        if (!_project.IsLoaded) return map;
        foreach (var sys in _project.GetActiveSystems())
            foreach (var f in _project.GetFlows(sys.Id))
                map[f.Id] = (f.Name, sys.Name, sys.Id);
        return map;
    }

    private (string FlowName, string SystemName, Guid SystemId)? FlowInfo(Guid flowId)
        => AllFlowInfo().TryGetValue(flowId, out var info) ? info : null;
}

// ── DTOs (camelCase 자동: canvasWidth, gridColumns, flowPlacements, colSpan, blueprintImagePath ...) ──

public record BlueprintDto(
    bool ProjectLoaded,
    BpLayoutDto Layout,
    List<AvailableFlowDto> AvailableFlows,
    bool IsStale);

public record BpLayoutDto(
    int CanvasWidth,
    int CanvasHeight,
    double CardScale,
    string? BlueprintImagePath,
    long ImageVersion,
    List<BpPlacementDto> FlowPlacements,
    List<BpOrderDto> FlowProcessOrder);

public record BpPlacementDto(
    Guid FlowId,
    string FlowName,
    Guid SystemId,
    string SystemName,
    double X,
    double Y);

public record BpOrderDto(Guid FlowId, string FlowName);

public record AvailableFlowDto(Guid Id, string Name, string System, Guid SystemId);

// 자유 배치: 정규화 중심좌표 X/Y(0..1).
public record PlacementRequestDto(string FlowId, double X, double Y);

public record FlowIdRequestDto(string FlowId);

// 인플레이스 편집 일괄 적용("저장"). 배치는 flowId + 정규화 중심좌표(0..1) 만 — 이름은 서버 재해석.
public record ReplaceLayoutRequestDto(
    double? CardScale,
    List<PlacementItemDto>? Placements,
    List<string>? FlowProcessOrder);

public record PlacementItemDto(string FlowId, double X, double Y);

public record SaveLayoutRequestDto(
    double? CardScale,
    int? GridColumns,
    int? GridRows,
    int? OffsetX,
    int? OffsetY,
    int? OffsetRight,
    int? OffsetBottom,
    List<string>? FlowProcessOrder,
    string? LayoutJson);

public record ImageResultDto(
    string? ImagePath,
    long ImageVersion,
    int Width,
    int Height,
    int CanvasWidth,
    int CanvasHeight);
