using System.Windows;

namespace Promaker.Dock;

/// <summary>dock layout 의 한 자리에 들어갈 content + meta data. DevExpress LayoutPanel 의 추상.</summary>
public sealed record DockAnchor(
    string ContentId,
    string Title,
    FrameworkElement Content,
    DockAnchorPosition DefaultPosition);

/// <summary>
/// done-dock-avalon.md §3.1 의 layout 트리 위치. PR-D3 의 DockLayoutManager 초기 트리 구성 시 사용.
/// Promaker 의 6종 anchor + Document area 매핑 (post-D8 fix — Log 독립 anchor 승격):
/// - <see cref="Left"/>:        Explorer (좌측 column 상단).
/// - <see cref="BottomLeft"/>:  Simulation (가운데 column 하단, horizontal split 좌).
/// - <see cref="BottomRight"/>: Log (가운데 column 하단, horizontal split 우).
/// - <see cref="RightTop"/>:    Properties (우측 column 상단).
/// - <see cref="RightMiddle"/>: History (우측 column 중단).
/// - <see cref="RightBottom"/>: LlmChat (우측 column 하단, 기본 닫힘).
/// - <see cref="Document"/>:    Canvas / Welcome (가운데 상단 document area, tabbing 가능).
/// </summary>
public enum DockAnchorPosition
{
    Left,
    BottomLeft,
    BottomRight,
    RightTop,
    RightMiddle,
    RightBottom,
    Document,
}
