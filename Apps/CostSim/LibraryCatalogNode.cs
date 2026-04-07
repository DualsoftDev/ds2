using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Ds2.Core.Store;

namespace CostSim;

public enum LibraryNodeKind
{
    File,
    Project,
    System,
    Flow,
    Work
}

public sealed class LibraryCatalogNode
{
    public LibraryCatalogNode(
        LibraryNodeKind nodeKind,
        string filePath,
        DsStore sourceStore,
        Guid? sourceEntityId,
        string name,
        string displayName,
        string secondaryText,
        string glyph,
        Brush accentBrush)
    {
        NodeKind = nodeKind;
        FilePath = filePath;
        SourceStore = sourceStore;
        SourceEntityId = sourceEntityId;
        Name = name;
        DisplayName = displayName;
        SecondaryText = secondaryText;
        SecondaryVisibility = string.IsNullOrWhiteSpace(secondaryText) ? Visibility.Collapsed : Visibility.Visible;
        Glyph = glyph;
        AccentBrush = accentBrush;
    }

    public LibraryNodeKind NodeKind { get; }
    public string FilePath { get; }
    public DsStore SourceStore { get; }
    public Guid? SourceEntityId { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public string SecondaryText { get; }
    public Visibility SecondaryVisibility { get; }
    public string Glyph { get; }
    public Brush AccentBrush { get; }
    public ObservableCollection<LibraryCatalogNode> Children { get; } = [];

    public override string ToString() => DisplayName;
}
