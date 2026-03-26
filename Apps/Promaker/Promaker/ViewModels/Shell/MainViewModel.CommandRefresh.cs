using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private IReadOnlyList<IRelayCommand>? _editorCommandsNeedingRefresh;

    internal void RefreshEditorCommandStates()
    {
        foreach (var command in GetEditorCommandsNeedingRefresh())
            command.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<IRelayCommand> GetEditorCommandsNeedingRefresh()
    {
        return _editorCommandsNeedingRefresh ??=
        [
            AddSystemCommand,
            AddWorkCommand,
            AddCallCommand,
            DeleteSelectedCommand,
            CopySelectedCommand,
            PasteCopiedCommand,
            ConnectSelectedNodesCommand,
            AutoLayoutCommand,
            FocusNameEditorCommand,
            ImportMermaidCommand,
            Canvas.FocusSelectedInCanvasCommand
        ];
    }
}
