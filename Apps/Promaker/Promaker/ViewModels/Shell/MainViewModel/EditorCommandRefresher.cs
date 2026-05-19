using System.Collections.Generic;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Ds2.Editor;

namespace Promaker.ViewModels;

/// <summary>
/// MainViewModel / CanvasWorkspaceState / PropertyPanelState 에 흩어진 RelayCommand 의 CanExecute 를
/// 일괄 재평가하는 collaborator. reflection 기반 수집은 한 번 캐시 후 재사용.
/// Slice 3 (Refresh/Command availability collapse) 진입의 첫 단위 — 향후 result applier 가 이 객체를
/// 통해 NotifyCanExecuteChanged 트리거를 일원화한다.
/// </summary>
public sealed class EditorCommandRefresher
{
    private IReadOnlyList<IRelayCommand>? _cache;

    private readonly object _owner;
    private readonly object _canvasManager;
    private readonly object _propertyPanel;

    public EditorCommandRefresher(object owner, object canvasManager, object propertyPanel)
    {
        _owner          = owner;
        _canvasManager  = canvasManager;
        _propertyPanel  = propertyPanel;
    }

    /// <summary>모든 RelayCommand 의 NotifyCanExecuteChanged 일괄 호출.</summary>
    public void Refresh()
    {
        foreach (var command in GetCommandsCached())
            command.NotifyCanExecuteChanged();
    }

    /// <summary>F# RefreshScopeDecision 결과를 받아 CommandAvailability flag 가 있을 때만 Refresh.
    /// 다른 scope 만 있는 trigger (예: VisualOnly) 에서는 reflection 비용 회피.</summary>
    public void RefreshFor(RefreshScope scope)
    {
        if ((scope & RefreshScope.CommandAvailability) == RefreshScope.CommandAvailability)
            Refresh();
    }

    /// <summary>ActivePane 변경 등으로 owner 가 보는 RelayCommand 집합이 바뀌었을 때 호출 — 캐시 무효화.</summary>
    public void InvalidateCache() => _cache = null;

    private IReadOnlyList<IRelayCommand> GetCommandsCached() => _cache ??= BuildList();

    private IReadOnlyList<IRelayCommand> BuildList()
    {
        var commands = new HashSet<IRelayCommand>();
        CollectRelayCommands(commands, _owner);
        CollectRelayCommands(commands, _canvasManager);
        CollectRelayCommands(commands, _propertyPanel);
        return [.. commands];
    }

    private static void CollectRelayCommands(HashSet<IRelayCommand> commands, object? owner)
    {
        if (owner is null)
            return;

        foreach (var property in owner.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!typeof(IRelayCommand).IsAssignableFrom(property.PropertyType) || property.GetIndexParameters().Length > 0)
                continue;

            if (property.GetValue(owner) is IRelayCommand command)
                commands.Add(command);
        }
    }
}
