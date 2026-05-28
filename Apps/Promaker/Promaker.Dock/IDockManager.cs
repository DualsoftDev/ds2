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

    /// <summary>사용자가 X 버튼 등으로 anchor 를 hide 하면 발화. VM 의 SSOT 와 단방향 sync 용.</summary>
    event EventHandler<DockAnchorVisibilityChangedEventArgs>? AnchorVisibilityChanged;
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
