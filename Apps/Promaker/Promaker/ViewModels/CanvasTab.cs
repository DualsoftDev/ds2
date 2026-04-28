using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public enum TreePaneKind
{
    Control,
    Device
}

public partial class CanvasTab : ObservableObject
{
    public CanvasTab(Guid rootId, TabKind kind, string title)
    {
        RootId = rootId;
        Kind = kind;
        _title = title;
    }

    public Guid RootId { get; }
    public TabKind Kind { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isActive;

    /// <summary>탭 전환 시 줌/팬 상태를 보존하기 위한 캐시. 한 번이라도 활성화된 적 있으면 true.</summary>
    public bool HasSavedView { get; set; }
    public double SavedZoom { get; set; } = 1.0;
    public double SavedPanX { get; set; }
    public double SavedPanY { get; set; }
}
