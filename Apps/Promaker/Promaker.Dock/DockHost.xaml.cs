using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Docking;
using DevExpress.Xpf.Docking.Base;

namespace Promaker.Dock;

/// <summary>
/// DevExpress DockLayoutManager 을 캡슐화하는 UserControl. 외부 노출 API = <see cref="IDockManager"/>.
/// DX type 은 본 클래스 안에서만 사용 — Promaker.csproj 의 PrivateAssets="all" ProjectReference 와 함께
/// DX 의 System.Windows.Forms / System.Drawing transitive 가 Promaker 본체에 유입되지 않도록 격리.
///
/// PR-D3 단계:
///   - done-dock-layout.md §3.1 의 안 A 그대로 LayoutGroup 트리 구성 (XAML).
///   - IDockManager 의 4 메서드 + 1 event 구현. dispatch 는 <see cref="DockAnchorPosition"/> switch.
///   - size 보존 / drag-drop / floating 은 DX native 처리 — 별도 보정 코드 없음 (작업 의도 verbatim).
/// </summary>
public partial class DockHost : UserControl, IDockManager
{
    /// <summary>
    /// ContentId → 등록된 BaseLayoutItem (LayoutPanel 또는 DocumentPanel) 매핑.
    /// LayoutGroup.Items[string] 은 직접 자식만 lookup — 트리 전역은 자체 dictionary 가 더 단순/안정.
    /// </summary>
    private readonly Dictionary<string, BaseLayoutItem> _itemsByContentId = new(StringComparer.Ordinal);

    public DockHost()
    {
        InitializeComponent();

        // visibility 변경 통보 — DX 의 단일 manager-level event 를 IDockManager event 로 변환.
        // X 버튼 / Closed DP set / Hide()/Restore() 등 모든 경로에서 raise 됨 (DX native).
        _dockLayout.ItemIsVisibleChanged += OnItemIsVisibleChanged;
    }

    public event EventHandler<DockAnchorVisibilityChangedEventArgs>? AnchorVisibilityChanged;

    public void RegisterAnchor(DockAnchor anchor)
    {
        if (anchor is null) throw new ArgumentNullException(nameof(anchor));
        if (anchor.DefaultPosition == DockAnchorPosition.Document)
            throw new ArgumentException(
                $"DockAnchorPosition.Document is for RegisterDocument(), not RegisterAnchor(). ContentId={anchor.ContentId}",
                nameof(anchor));
        if (_itemsByContentId.ContainsKey(anchor.ContentId))
            throw new InvalidOperationException($"ContentId '{anchor.ContentId}' is already registered.");

        var panel = ResolveAnchorPanel(anchor.DefaultPosition);
        ApplyAnchorMetadata(panel, anchor);
        panel.Content = anchor.Content;
        _itemsByContentId[anchor.ContentId] = panel;
    }

    public void RegisterDocument(DockAnchor document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (_itemsByContentId.ContainsKey(document.ContentId))
            throw new InvalidOperationException($"ContentId '{document.ContentId}' is already registered.");

        // DocumentGroup 도 LayoutGroup 상속 — Items 컬렉션에 DocumentPanel 추가.
        var docPanel = new DocumentPanel();
        ApplyAnchorMetadata(docPanel, document);
        docPanel.Content = document.Content;
        _documentGroup.Items.Add(docPanel);
        _itemsByContentId[document.ContentId] = docPanel;
    }

    public void SetAnchorVisible(string contentId, bool visible)
    {
        // BaseLayoutItem.Closed : bool (DependencyProperty). true 면 hidden.
        FindLayoutItem(contentId).Closed = !visible;
    }

    public bool IsAnchorVisible(string contentId) => !FindLayoutItem(contentId).Closed;

    /// <summary>
    /// DockAnchorPosition → 미리 만든 LayoutPanel 매핑. PR-D2 의 enum 5 anchor 위치 + Document (별도 경로).
    /// done-dock-layout.md §3.1 안 A:
    ///   Left=explorer / Bottom=simulation / RightTop=property / RightMiddle=history / RightBottom=llmchat.
    /// </summary>
    private LayoutPanel ResolveAnchorPanel(DockAnchorPosition position) => position switch
    {
        DockAnchorPosition.Left => _explorerPanel,
        DockAnchorPosition.Bottom => _simulationPanel,
        DockAnchorPosition.RightTop => _propertyPanel,
        DockAnchorPosition.RightMiddle => _historyPanel,
        DockAnchorPosition.RightBottom => _llmChatPanel,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unsupported anchor position."),
    };

    /// <summary>
    /// LayoutPanel / DocumentPanel 공통 메타 (Caption / Name / BindableName) 적용.
    /// Name = ContentId — visibility 통보 event 에서 contentId 식별용.
    /// BindableName 도 동일 — DX layout serializer (PR-D6) 용.
    /// </summary>
    private static void ApplyAnchorMetadata(BaseLayoutItem item, DockAnchor anchor)
    {
        item.Caption = anchor.Title;
        item.Name = anchor.ContentId;
        item.BindableName = anchor.ContentId;
    }

    private BaseLayoutItem FindLayoutItem(string contentId)
    {
        if (string.IsNullOrEmpty(contentId)) throw new ArgumentException("contentId is required.", nameof(contentId));
        if (!_itemsByContentId.TryGetValue(contentId, out var item))
            throw new InvalidOperationException($"DockAnchor not registered: ContentId={contentId}");
        return item;
    }

    private void OnItemIsVisibleChanged(object? sender, ItemIsVisibleChangedEventArgs e)
    {
        // e.Item.Name = anchor.ContentId (RegisterAnchor 에서 set).
        var contentId = e.Item?.Name;
        if (string.IsNullOrEmpty(contentId)) return;
        AnchorVisibilityChanged?.Invoke(this, new DockAnchorVisibilityChangedEventArgs(contentId, e.IsVisible));
    }
}
