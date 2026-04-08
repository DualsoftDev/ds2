using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Ds2.Core.Store;

namespace CostSim;

public sealed class TreeNodeItem
{
    public TreeNodeItem(
        Guid id,
        Guid? parentId,
        EntityKind entityKind,
        string name,
        string displayName,
        string secondaryText,
        string glyph,
        Brush accentBrush)
    {
        Id = id;
        ParentId = parentId;
        EntityKind = entityKind;
        Name = name;
        DisplayName = displayName;
        SecondaryText = secondaryText;
        SecondaryVisibility = string.IsNullOrWhiteSpace(secondaryText) ? Visibility.Collapsed : Visibility.Visible;
        Glyph = glyph;
        AccentBrush = accentBrush;
    }

    public Guid Id { get; }
    public Guid? ParentId { get; }
    public EntityKind EntityKind { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public string SecondaryText { get; }
    public Visibility SecondaryVisibility { get; }
    public string Glyph { get; }
    public Brush AccentBrush { get; }
    public ObservableCollection<TreeNodeItem> Children { get; } = [];

    public override string ToString() => DisplayName;
}
