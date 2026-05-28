using System;
using System.Windows.Controls;

namespace Promaker.Dock;

/// <summary>
/// DevExpress DockLayoutManager 을 캡슐화하는 UserControl. 외부 노출 API = <see cref="IDockManager"/>.
/// DX type 은 본 클래스 안에서만 사용 — Promaker.csproj 의 PrivateAssets="all" ProjectReference 와 함께
/// DX 의 System.Windows.Forms / System.Drawing transitive 가 Promaker 본체에 유입되지 않도록 격리.
///
/// PR-D2 단계: skeleton. 실제 RegisterAnchor / SetAnchorVisible 등은 PR-D3 에서 layout 트리 구성과 함께 구현.
/// </summary>
public partial class DockHost : UserControl, IDockManager
{
    public DockHost()
    {
        InitializeComponent();
    }

    public void RegisterAnchor(DockAnchor anchor) => throw new NotImplementedException("PR-D3: layout 트리 구성 + LayoutPanel 등록");

    public void RegisterDocument(DockAnchor document) => throw new NotImplementedException("PR-D3: DocumentGroup 등록");

    public void SetAnchorVisible(string contentId, bool visible) => throw new NotImplementedException("PR-D3: LayoutPanel.Closed toggle");

    public bool IsAnchorVisible(string contentId) => throw new NotImplementedException("PR-D3: LayoutPanel.Closed 상태 조회");

    public event EventHandler<DockAnchorVisibilityChangedEventArgs>? AnchorVisibilityChanged;
}
