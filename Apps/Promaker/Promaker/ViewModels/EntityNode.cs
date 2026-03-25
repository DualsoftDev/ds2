using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;

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

    [ObservableProperty] private bool _isGhost;
    [ObservableProperty] private bool _isReference;

    /// Reference Work의 원본 Work ID (IsReference=true일 때만 유효)
    public Guid? ReferenceOfId { get; init; }

    [ObservableProperty] private bool _hasAutoAux;
    [ObservableProperty] private bool _hasComAux;
    [ObservableProperty] private bool _hasSkipUnmatch;

    /// 경고 하이라이트 (그래프 검증 경고 등)
    [ObservableProperty] private bool _isWarning;

    /// 조건 드롭 대상 하이라이트
    [ObservableProperty] private bool _isDropTarget;

    /// 시뮬레이션 상태 (null = 비시뮬)
    [ObservableProperty] private Status4? _simState;

    /// 시뮬레이션 토큰 표시 (빈 문자열 = 토큰 없음)
    [ObservableProperty] private string _simTokenDisplay = "";

    public ObservableCollection<EntityNode> Children { get; } = [];

    public void UpdateConditionTypes(IEnumerable<CallConditionType> types)
    {
        HasAutoAux = false;
        HasComAux = false;
        HasSkipUnmatch = false;
        foreach (var t in types)
        {
            switch (t)
            {
                case CallConditionType.AutoAux: HasAutoAux = true; break;
                case CallConditionType.ComAux: HasComAux = true; break;
                case CallConditionType.SkipUnmatch: HasSkipUnmatch = true; break;
            }
        }
    }

    public override string ToString() => $"[{EntityType}] {Name}";
}
