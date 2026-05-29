namespace DSPilot.Models.Dashboard;

// 격리형 호스팅 Dashboard API 전송 DTO.
// 도메인 타입을 그대로 직렬화하지 않는 이유:
//  - DspDbSnapshot.CallsByFlow 는 순환 가능 dict (불필요)
//  - BlueprintLayout.CellWidth/CellHeight 는 [JsonIgnore] 계산 속성인데 클라이언트가 필요로 함 → DTO 로 노출
// 전역 camelCase 정책(Program.cs) 이 속성명을 변환한다. (MT→mt, AvgMT→avgMT, ColSpan→colSpan ...)

public record DashboardSnapshotDto(
    List<FlowStateDto> Flows,
    LayoutDto Layout,
    bool HasData,
    DateTimeOffset Timestamp);

public record FlowStateDto(
    string FlowName,
    string State,
    int? MT,
    int? WT,
    int? CT,
    double? AvgMT,
    double? AvgWT,
    double? AvgCT,
    string? MovingStartName,
    string? MovingEndName);

public record LayoutDto(
    int CanvasWidth,
    int CanvasHeight,
    int GridColumns,
    int GridRows,
    int OffsetX,
    int OffsetY,
    int OffsetRight,
    int OffsetBottom,
    int CellWidth,
    int CellHeight,
    string? BlueprintImagePath,
    long ImageVersion,
    List<FlowPlacementDto> FlowPlacements,
    List<FlowOrderDto> FlowProcessOrder);

public record FlowPlacementDto(
    string FlowName,
    System.Guid SystemId,
    int Col,
    int Row,
    int ColSpan,
    int RowSpan);

public record FlowOrderDto(string FlowName);

public record FlowHistoryDto(
    int? CycleNo,
    int? MT,
    int? WT,
    int? CT,
    System.DateTime RecordedAt,
    bool IsIdle);
