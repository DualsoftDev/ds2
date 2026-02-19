using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ds2.UI.Frontend.ViewModels;

public partial class EntityNode : ObservableObject
{
    public EntityNode(Guid id, string entityType, string name, Guid? parentId = null)
    {
        Id = id;
        EntityType = entityType;
        _name = name;
        ParentId = parentId;
    }

    public Guid Id { get; }
    public string EntityType { get; }
    public Guid? ParentId { get; }

    [ObservableProperty] private string _name;

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width = 120;
    [ObservableProperty] private double _height = 40;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isTreeSelected;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private int _selectionOrder;

    public ObservableCollection<EntityNode> Children { get; } = [];

    public override string ToString() => $"[{EntityType}] {Name}";
}