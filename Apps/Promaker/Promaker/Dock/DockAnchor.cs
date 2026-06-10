using System.Windows;

namespace Promaker.Dock;

/// <summary>
/// dock layout 의 한 자리에 들어갈 content + meta data.
/// PR-A4 — caption Help 뱃지 삭제. HasHelp 매개는 record 시그니처 호환을 위해 보존하되 DockHost 가 무시.
/// PR-A6 — Bottom 위치 단일화 (BottomLeft/BottomRight → Bottom). Log/Gantt/StatusMonitor 가 같은 bottom pane 에 tabbed.
/// </summary>
public sealed record DockAnchor(
    string ContentId,
    string Title,
    FrameworkElement Content,
    DockAnchorPosition DefaultPosition,
    bool HasHelp = false);

/// <summary>
/// Promaker 의 dock anchor 기본 위치.
/// PR-A6 — Simulation dock 삭제 + Gantt / StatusMonitor 독립 dock 화. bottom 은 단일 pane 으로 통합.
/// <list type="bullet">
/// <item><see cref="Left"/>       — Explorer</item>
/// <item><see cref="Bottom"/>     — Log / Gantt Chart / Status Monitor (tabbed, 기본 첫 탭=Log)</item>
/// <item><see cref="RightTop"/>   — Properties</item>
/// <item><see cref="RightMiddle"/>— History</item>
/// <item><see cref="RightBottom"/>— LlmChat (lazy)</item>
/// <item><see cref="Document"/>   — Welcome / Canvas (tabbed)</item>
/// </list>
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
