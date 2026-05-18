using System.Collections.Generic;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private IReadOnlyList<IRelayCommand>? _editorCommandsNeedingRefresh;

    internal void RefreshEditorCommandStates()
    {
        NormalizeConnectArrowTypeForActiveTab();

        foreach (var command in GetEditorCommandsNeedingRefresh())
            command.NotifyCanExecuteChanged();
    }

    private void NormalizeConnectArrowTypeForActiveTab()
    {
        if (Canvas.ActiveTab is not { } tab) return;

        if (!EntityKindRules.isWorkArrowModeForTab(tab.Kind)
            && SelectedConnectArrowType is ArrowType.Reset or ArrowType.StartReset or ArrowType.ResetReset)
        {
            SelectedConnectArrowType = ArrowType.Start;
        }
    }

    private IReadOnlyList<IRelayCommand> GetEditorCommandsNeedingRefresh()
    {
        return _editorCommandsNeedingRefresh ??= BuildCommandRefreshList();
    }

    private IReadOnlyList<IRelayCommand> BuildCommandRefreshList()
    {
        var commands = new HashSet<IRelayCommand>();
        CollectRelayCommands(commands, this);
        CollectRelayCommands(commands, Canvas);
        CollectRelayCommands(commands, PropertyPanel);
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
