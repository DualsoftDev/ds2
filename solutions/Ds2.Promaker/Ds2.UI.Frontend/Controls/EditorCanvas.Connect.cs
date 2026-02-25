using System;
using System.Windows;
using Ds2.Core;
using Ds2.UI.Core;
using Ds2.UI.Frontend;
using Ds2.UI.Frontend.Dialogs;
using Microsoft.FSharp.Core;

namespace Ds2.UI.Frontend.Controls;

public partial class EditorCanvas
{
    private ArrowType _connectArrowType = ArrowType.Start;

    private void StartConnect_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        var hasOrderedSelectionType = VM.TryGetOrderedSelectionConnectEntityType(out var orderedSelectionType);
        var hasPromptedArrowType = false;
        var selectedArrowType = ArrowType.Start;

        if (hasOrderedSelectionType)
        {
            if (!TryPromptArrowType(orderedSelectionType, out selectedArrowType))
                return;

            hasPromptedArrowType = true;

            if (VM.ConnectSelectedNodesInOrder(selectedArrowType))
                return;
        }

        if (VM.SelectedNode is not { } node || !EntityTypes.IsWorkOrCall(node.EntityType))
            return;

        if (!hasPromptedArrowType || !EntityTypes.Is(node.EntityType, orderedSelectionType))
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

    private bool TryPromptArrowType(string sourceEntityType, out ArrowType arrowType)
    {
        var dialog = new ArrowTypeDialog(isWorkMode: EntityTypes.Is(sourceEntityType, EntityTypes.Work));

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
        if (_connectSource is not { } sourceId || VM is null || VM.ActiveTab is null) return;

        var srcNode = VM.CanvasNodes.FirstOrDefault(n => n.Id == sourceId);
        var tgtNode = VM.CanvasNodes.FirstOrDefault(n => n.Id == targetId);
        if (srcNode is null || tgtNode is null)
        {
            CancelConnect();
            return;
        }

        var sourceParentId = srcNode.ParentId is { } srcParent ? FSharpOption<Guid>.Some(srcParent) : null;
        var targetParentId = tgtNode.ParentId is { } tgtParent ? FSharpOption<Guid>.Some(tgtParent) : null;
        var flowIdOpt = ConnectionQueries.resolveFlowIdForConnect(
            VM.Store,
            srcNode.EntityType,
            sourceParentId,
            tgtNode.EntityType,
            targetParentId);

        if (!FSharpOption<Guid>.get_IsSome(flowIdOpt))
        {
            CancelConnect();
            return;
        }

        var flowId = flowIdOpt.Value;
        VM.Editor.AddArrow(srcNode.EntityType, flowId, sourceId, targetId, _connectArrowType);

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
