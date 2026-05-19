using CommunityToolkit.Mvvm.Input;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanConnectSelectedNodes))]
    private void ConnectSelectedNodes()
    {
        Selection.ConnectSelectedNodesInOrder(SelectedConnectArrowType);
    }

    [RelayCommand(CanExecute = nameof(CanAutoLayout))]
    private void AutoLayout()
    {
        if (Canvas.ActiveTab is not { } tab)
        {
            StatusText = "Open a canvas tab to run auto layout.";
            return;
        }

        if (!TryEditorRef(
                () => EditorCanvasLayout.ComputeAutoLayout(_store, tab.Kind, tab.RootId),
                out var requests))
            return;

        if (requests.IsEmpty)
        {
            StatusText = "Nothing to auto-layout.";
            return;
        }

        if (TryEditorAction(() => _store.MoveEntities(requests)))
        {
            StatusText = $"Auto-layout applied to {requests.Length} item(s).";
            RequestRebuildAll(() => Canvas.FitToViewZoomOutRequested?.Invoke());
        }
    }
}
