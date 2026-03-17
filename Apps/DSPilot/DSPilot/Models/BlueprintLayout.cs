namespace DSPilot.Models;

public class BlueprintLayout
{
    public string? BlueprintImagePath { get; set; }
    public int CanvasWidth { get; set; } = 1200;
    public int CanvasHeight { get; set; } = 800;
    public int GridColumns { get; set; } = 6;
    public int GridRows { get; set; } = 4;
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int OffsetRight { get; set; }
    public int OffsetBottom { get; set; }
    public List<FlowPlacement> FlowPlacements { get; set; } = [];

    public int CellWidth => GridColumns > 0 ? (CanvasWidth - OffsetX - OffsetRight) / GridColumns : 200;
    public int CellHeight => GridRows > 0 ? (CanvasHeight - OffsetY - OffsetBottom) / GridRows : 200;
}

public class FlowPlacement
{
    public Guid FlowId { get; set; }
    public Guid SystemId { get; set; }
    public string FlowName { get; set; } = "";
    public string SystemName { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }
    public int ColSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
}
