using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Store;
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
}
