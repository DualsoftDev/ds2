using System;
using System.Windows;
using Ds2.Core;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.Controls;

public partial class EditorCanvas
{
    private ArrowType _connectArrowType = ArrowType.Start;

    private void StartConnect_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        var hasOrderedSelectionType = VM.Selection.TryGetOrderedSelectionConnectEntityType(out var orderedSelectionType);
        var hasPromptedArrowType = false;
        var selectedArrowType = ArrowType.Start;

        if (hasOrderedSelectionType)
        {
            if (!TryPromptArrowType(orderedSelectionType, out selectedArrowType))
                return;

            hasPromptedArrowType = true;

            if (VM.Selection.ConnectSelectedNodesInOrder(selectedArrowType))
                return;
        }

        if (VM.SelectedNode is not { } node || node.EntityType is not (EntityKind.Work or EntityKind.Call))
            return;

        if (!hasPromptedArrowType || node.EntityType != orderedSelectionType)
        {
            if (!TryPromptArrowType(node.EntityType, out selectedArrowType))
                return;
        }

        _connectArrowType = selectedArrowType;

        _connectSource = node.Id;
        _connectSourcePos = new Point(node.X + node.Width / 2, node.Y + node.Height / 2);

        ConnectPreview.X1 = _connectSourcePos.X;
        ConnectPreview.Y1 = _connectSourcePos.Y;
        ConnectPreview.X2 = _connectSourcePos.X;
        ConnectPreview.Y2 = _connectSourcePos.Y;
        ConnectPreview.Visibility = Visibility.Visible;
        RootGrid.Cursor = System.Windows.Input.Cursors.Cross;
        Focus();
    }

    private bool TryPromptArrowType(EntityKind sourceEntityType, out ArrowType arrowType)
    {
        var dialog = new ArrowTypeDialog(isWorkMode: EntityKindRules.isWorkArrowMode(sourceEntityType));

        if (Window.GetWindow(this) is { } owner)
            dialog.Owner = owner;

        if (dialog.ShowDialog() != true)
        {
            arrowType = ArrowType.Start;
            return false;
        }

        arrowType = dialog.SelectedArrowType;
        return true;
    }

    private void CompleteConnect(Guid targetId)
    {
        if (_connectSource is not { } sourceId || VM is null || VM.Canvas.ActiveTab is null) return;

        var srcNode = VM.Canvas.CanvasNodes.FirstOrDefault(n => n.Id == sourceId);
        var tgtNode = VM.Canvas.CanvasNodes.FirstOrDefault(n => n.Id == targetId);
        if (srcNode is null || tgtNode is null)
        {
            CancelConnect();
            return;
        }

        VM.TryConnectNodesFromCanvas(sourceId, targetId, _connectArrowType);
        CancelConnect();
    }

    private void CancelConnect()
    {
        _connectSource = null;
        _connectArrowType = ArrowType.Start;
        ConnectPreview.Visibility = Visibility.Collapsed;
        RootGrid.Cursor = System.Windows.Input.Cursors.Arrow;
    }
}
