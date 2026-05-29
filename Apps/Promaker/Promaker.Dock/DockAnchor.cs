using System.Windows;

namespace Promaker.Dock;

/// <summary>
/// dock layout 의 한 자리에 들어갈 content + meta data. DevExpress LayoutPanel 의 추상.
/// <para>
/// <c>HasHelp</c> (PR-D9 — MJ2 Help 버튼 복구): baseline AvalonDock 의
/// <c>AnchorableHeaderTemplate</c>/<c>AnchorableTitleTemplate</c> 에 박혀 있던
/// explorer/properties/history 3 anchor 의 caption 영역 <c>?</c> Help 버튼을 DX
/// <c>BaseLayoutItem.CaptionTemplate</c> 으로 복구. <c>true</c> 시 DockHost 가
/// CaptionTemplate (AnchorCaptionWithHelp) 을 panel 에 적용 + Help 버튼 click 시
/// <see cref="IDockManager.AnchorHelpRequested"/> event 발화 (contentId 매개).
/// </para>
/// <para>
/// 격리 (§7 #4): DX type 외부 노출 0 — <c>bool</c> default <c>false</c> 인 positional record
/// 마지막 매개로 추가. 기존 RegisterAnchor/RegisterDocument 호출 측은 backward compat (기본 false).
/// </para>
/// </summary>
public sealed record DockAnchor(
    string ContentId,
    string Title,
    FrameworkElement Content,
    DockAnchorPosition DefaultPosition,
    bool HasHelp = false);

/// <summary>
/// done-dock-devexpress.md §3 PR-D3 의 layout 트리 위치. PR-D3 의 DockLayoutManager 초기 트리 구성 시 사용.
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
