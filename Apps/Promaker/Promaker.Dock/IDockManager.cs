using System;

namespace Promaker.Dock;

/// <summary>
/// Promaker 본체가 사용할 dock 관리 abstract API.
/// DevExpress type 은 외부에 노출되지 않는다 — 모든 DX 의존성은 Promaker.Dock 격리 csproj 안에서 처리.
/// </summary>
public interface IDockManager
{
    /// <summary>anchor (좌/우/하단 panel) 을 layout 에 등록. PR-D3 의 default position 에 따라 배치.</summary>
    void RegisterAnchor(DockAnchor anchor);

    /// <summary>document area (Canvas / Welcome) 에 들어가는 content 등록.</summary>
    void RegisterDocument(DockAnchor document);

    /// <summary>anchor 의 visible 토글. Promaker SSOT (예: IsLlmChatVisible PropertyChanged) 가 호출.</summary>
    void SetAnchorVisible(string contentId, bool visible);

    /// <summary>현재 anchor 의 visible 상태 조회. 보기 메뉴 OneWay binding 등에 활용.</summary>
    bool IsAnchorVisible(string contentId);

    /// <summary>
    /// 현재 dock layout 을 xml 파일로 저장 (DX <c>DockLayoutManager.SaveLayoutToXml</c> wrapping).
    /// 경로의 상위 디렉토리가 없으면 생성. 호출 측은 <c>%LOCALAPPDATA%\Promaker\dock-layout.xml</c>
    /// 같은 user-scope 경로 사용. DX type 외부 노출 차단 — 매개변수는 <c>string filepath</c> 만.
    /// </summary>
    void SaveLayout(string filepath);

    /// <summary>
    /// 저장된 dock layout xml 을 복원 (DX <c>DockLayoutManager.RestoreLayoutFromXml</c> wrapping).
    /// 파일 미존재 / parse 실패 시 default layout 유지 (XAML 박제 그대로). DX type 외부 노출 차단.
    /// </summary>
    void RestoreLayout(string filepath);

    /// <summary>사용자가 X 버튼 등으로 anchor 를 hide 하면 발화. VM 의 SSOT 와 단방향 sync 용.</summary>
    event EventHandler<DockAnchorVisibilityChangedEventArgs>? AnchorVisibilityChanged;

    /// <summary>
    /// PR-D9 (MJ2 복구) — anchor caption 의 Help 버튼 (<c>?</c>) click 시 발화.
    /// 매개는 anchor 의 ContentId 문자열 (DX type 외부 노출 0 — 격리 §7 #4 박제).
    /// Promaker 본체가 <c>Promaker.Help.HelpNavigator.NavigateCommand.Execute(contentId)</c> hook 하여
    /// baseline AvalonDock 의 Help 동작 (3 anchor: explorer / properties / history) 을 복구.
    /// </summary>
    event EventHandler<string>? AnchorHelpRequested;
}

public sealed class DockAnchorVisibilityChangedEventArgs : EventArgs
{
    public string ContentId { get; }
    public bool IsVisible { get; }

    public DockAnchorVisibilityChangedEventArgs(string contentId, bool isVisible)
    {
        ContentId = contentId;
        IsVisible = isVisible;
    }
}
