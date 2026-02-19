using System.Windows;
using System.Windows.Controls;

namespace Ds2.UI.Frontend.Controls;

public partial class EditorCanvas
{
    private void FinishBoxSelection(Point end)
    {
        _isBoxSelecting = false;
        SelectionRect.Visibility = Visibility.Collapsed;
        RootGrid.ReleaseMouseCapture();

        if (VM is null) return;

        var rect = BuildRect(_boxStart, end);
        if (rect.Width < ClickThreshold && rect.Height < ClickThreshold)
        {
            if (!_boxSelectAdditive)
                VM.ClearNodeSelection();

            return;
        }

        var selectedNodes = VM.CanvasNodes
            .Where(n => rect.IntersectsWith(new Rect(n.X, n.Y, n.Width, n.Height)))
            .ToList();

        VM.SelectNodesFromCanvasBox(
            selectedNodes,
            _boxSelectAdditive,
            _boxStart.X,
            _boxStart.Y,
            end.X,
            end.Y);
    }

    private void UpdateSelectionRectangle(Point start, Point end)
    {
        var rect = BuildRect(start, end);
        Canvas.SetLeft(SelectionRect, rect.X);
        Canvas.SetTop(SelectionRect, rect.Y);
        SelectionRect.Width = rect.Width;
        SelectionRect.Height = rect.Height;
    }
}
