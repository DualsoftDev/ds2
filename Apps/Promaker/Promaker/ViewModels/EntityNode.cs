using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.UI.Core;

namespace Promaker.ViewModels;

public partial class EntityNode : ObservableObject
{
    public EntityNode(Guid id, EntityKind entityType, string name, Guid? parentId = null)
    {
        Id = id;
        EntityType = entityType;
        _name = name;
        ParentId = parentId;
    }

    public Guid Id { get; }
    public EntityKind EntityType { get; }
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

    [ObservableProperty] private bool _hasAutoCondition;
    [ObservableProperty] private bool _hasCommonCondition;
    [ObservableProperty] private bool _hasActiveCondition;

    public ObservableCollection<EntityNode> Children { get; } = [];

    public void UpdateConditionTypes(IEnumerable<CallConditionType> types)
    {
        HasAutoCondition = false;
        HasCommonCondition = false;
        HasActiveCondition = false;
        foreach (var t in types)
        {
            switch (t)
            {
                case CallConditionType.Auto: HasAutoCondition = true; break;
                case CallConditionType.Common: HasCommonCondition = true; break;
                case CallConditionType.Active: HasActiveCondition = true; break;
            }
        }
    }

    public override string ToString() => $"[{EntityType}] {Name}";
}
