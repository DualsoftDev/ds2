using System.Windows;

namespace Promaker.Dock;

/// <summary>dock layout 의 한 자리에 들어갈 content + meta data. DevExpress LayoutPanel 의 추상.</summary>
public sealed record DockAnchor(
    string ContentId,
    string Title,
    FrameworkElement Content,
    DockAnchorPosition DefaultPosition);

/// <summary>
/// done-dock-layout.md §3.1 의 layout 트리 위치. PR-D3 의 DockLayoutManager 초기 트리 구성 시 사용.
/// Promaker 의 5종 anchor + 1 fill (LlmChat) + Document area 매핑:
/// - <see cref="Left"/>:        Explorer (좌측 column 상단).
/// - <see cref="Bottom"/>:      Simulation / Log (가운데 column 하단, tab).
/// - <see cref="RightTop"/>:    Properties (우측 column 상단).
/// - <see cref="RightMiddle"/>: History (우측 column 중단).
/// - <see cref="RightBottom"/>: LlmChat (우측 column 하단, 기본 닫힘).
/// - <see cref="Document"/>:    Canvas / Welcome (가운데 상단 document area, tabbing 가능).
/// </summary>
public enum DockAnchorPosition
{
    Left,
    Bottom,
    RightTop,
    RightMiddle,
    RightBottom,
    Document,
}
