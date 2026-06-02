using System.Text.Json.Serialization;

namespace DSPilot.Models;

public class BlueprintLayout
{
    public string? BlueprintImagePath { get; set; }
    public int CanvasWidth { get; set; } = 1200;
    public int CanvasHeight { get; set; } = 800;

    /// <summary>공통 카드 폭 = CanvasWidth * CardScale (높이 = 폭/2). 자유 배치 모델의 공통 크기(편집 UI 슬라이더).</summary>
    public double CardScale { get; set; } = 0.15;

    // ── 레거시(격자 방식) — 자유 배치(X/Y)로 폐지됨. 휴면 Blazor 뷰(FlowLayoutSvg) 호환 + 구 layout-data.json 마이그레이션용으로만 보존. ──
    public int GridColumns { get; set; } = 6;
    public int GridRows { get; set; } = 4;
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int OffsetRight { get; set; }
    public int OffsetBottom { get; set; }

    public List<FlowPlacement> FlowPlacements { get; set; } = [];
    public List<FlowOrderEntry> FlowProcessOrder { get; set; } = [];

    [JsonIgnore]
    public int CellWidth => GridColumns > 0 ? (CanvasWidth - OffsetX - OffsetRight) / GridColumns : 200;
    [JsonIgnore]
    public int CellHeight => GridRows > 0 ? (CanvasHeight - OffsetY - OffsetBottom) / GridRows : 200;
}

public class FlowPlacement
{
    public Guid FlowId { get; set; }
    public Guid SystemId { get; set; }
    public string FlowName { get; set; } = "";
    public string SystemName { get; set; } = "";

    /// <summary>자유 배치: 카드 중심의 캔버스 정규화 좌표(0..1). null 이면 레거시 격자에서 마이그레이션 대상.</summary>
    public double? X { get; set; }
    public double? Y { get; set; }

    // ── 레거시(격자 방식) — 자유 배치(X/Y)로 폐지. 휴면 Blazor 뷰 호환 + 마이그레이션용 보존. ──
    public int Col { get; set; }
    public int Row { get; set; }
    public int ColSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
}

public class FlowOrderEntry
{
    public Guid FlowId { get; set; }
    public string FlowName { get; set; } = "";
}
