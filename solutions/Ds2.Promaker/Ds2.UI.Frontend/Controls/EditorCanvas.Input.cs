using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Ds2.UI.Frontend.ViewModels;
using Microsoft.FSharp.Core;

namespace Ds2.UI.Frontend.Controls;

public partial class EditorCanvas
{
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.MiddleButton == MouseButtonState.Pressed)
        {
            _isPanning = true;
            _panStart = e.GetPosition(RootGrid);
            RootGrid.CaptureMouse();
            RootGrid.Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (TryGetNodeFromElement(e.OriginalSource as DependencyObject, out var nodeId, out var border) && VM is not null)
        {
            if (_connectSource is { } sourceId && sourceId != nodeId)
            {
                CompleteConnect(nodeId);
                e.Handled = true;
                return;
            }

            var node = VM.CanvasNodes.FirstOrDefault(n => n.Id == nodeId);
            if (node is null) return;

            if (e.ClickCount == 2 && node.EntityType == "Work")
            {
                VM.OpenCanvasTab(nodeId, "Work");
                e.Handled = true;
                return;
            }

            var dragNodes = VM.PrepareCanvasDragSelection(node, ctrlPressed, shiftPressed);

            VM.ClearArrowSelection();

            if (!ctrlPressed && !shiftPressed)
            {
                var canvasPos = e.GetPosition(MainCanvas);
                _drag = new DragState(
                    canvasPos,
                    dragNodes
                        .Select(n => new DragItem(n, n.X, n.Y))
                        .ToList());
                _dragElement = border;
                border.CaptureMouse();
            }

            e.Handled = true;
            return;
        }

        if (_connectSource is not null)
        {
            CancelConnect();
            e.Handled = true;
            return;
        }

        _isBoxSelecting = true;
        _boxSelectAdditive = ctrlPressed;
        _boxStart = e.GetPosition(MainCanvas);
        UpdateSelectionRectangle(_boxStart, _boxStart);
        SelectionRect.Visibility = Visibility.Visible;
        RootGrid.CaptureMouse();
        VM?.ClearArrowSelection();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            var pos = e.GetPosition(RootGrid);
            PanTransform.X += pos.X - _panStart.X;
            PanTransform.Y += pos.Y - _panStart.Y;
            _panStart = pos;
            return;
        }

        if (_connectSource is not null)
        {
            var mp = e.GetPosition(MainCanvas);
            ConnectPreview.X2 = mp.X;
            ConnectPreview.Y2 = mp.Y;
            return;
        }

        if (_arrowReconnect is not null)
        {
            var mp = e.GetPosition(MainCanvas);
            ConnectPreview.X1 = _arrowReconnect.AnchorPoint.X;
            ConnectPreview.Y1 = _arrowReconnect.AnchorPoint.Y;
            ConnectPreview.X2 = mp.X;
            ConnectPreview.Y2 = mp.Y;
            return;
        }

        if (_isBoxSelecting)
        {
            var current = e.GetPosition(MainCanvas);
            UpdateSelectionRectangle(_boxStart, current);
            return;
        }

        if (_drag is null || _dragElement is null) return;

        var canvasPos = e.GetPosition(MainCanvas);
        var dx = canvasPos.X - _drag.StartPoint.X;
        var dy = canvasPos.Y - _drag.StartPoint.Y;

        foreach (var item in _drag.Items)
        {
            item.Node.X = Math.Max(0, item.OriginX + dx);
            item.Node.Y = Math.Max(0, item.OriginY + dy);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning && e.MiddleButton == MouseButtonState.Released)
        {
            _isPanning = false;
            RootGrid.ReleaseMouseCapture();
            RootGrid.Cursor = Cursors.Arrow;
            return;
        }

        if (_isBoxSelecting && e.ChangedButton == MouseButton.Left)
        {
            FinishBoxSelection(e.GetPosition(MainCanvas));
            e.Handled = true;
            return;
        }

        if (_arrowReconnect is not null && e.ChangedButton == MouseButton.Left)
        {
            FinishArrowReconnect(e.GetPosition(MainCanvas));
            e.Handled = true;
            return;
        }

        if (_drag is null) return;

        _dragElement?.ReleaseMouseCapture();

        if (VM is not null)
        {
            var requests = new List<MoveEntityRequest>();

            foreach (var item in _drag.Items)
            {
                var moved =
                    Math.Abs(item.Node.X - item.OriginX) > 0.1 ||
                    Math.Abs(item.Node.Y - item.OriginY) > 0.1;

                if (!moved)
                    continue;

                var newPos = new Xywh((int)item.Node.X, (int)item.Node.Y, (int)item.Node.Width, (int)item.Node.Height);
                requests.Add(new MoveEntityRequest(item.Node.EntityType, item.Node.Id, FSharpOption<Xywh>.Some(newPos)));
            }

            if (requests.Count > 0)
                VM.Editor.MoveEntities(requests);
        }

        _drag = null;
        _dragElement = null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _arrowReconnect is not null)
        {
            CancelArrowReconnect();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _connectSource is not null)
        {
            CancelConnect();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            VM?.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Arrow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid arrowId } || VM is null) return;

        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        VM.ClearNodeSelection();

        var arrow = VM.CanvasArrows.FirstOrDefault(a => a.Id == arrowId);
        if (arrow is not null)
            VM.SelectArrowFromCanvas(arrow, ctrlPressed);

        e.Handled = true;
    }

    private void ArrowStartHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BeginArrowReconnect(sender, replaceSource: true, e);

    private void ArrowEndHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BeginArrowReconnect(sender, replaceSource: false, e);

    private void BeginArrowReconnect(object sender, bool replaceSource, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid arrowId } || VM is null)
            return;

        var arrow = VM.CanvasArrows.FirstOrDefault(a => a.Id == arrowId);
        if (arrow is null)
            return;

        if (_connectSource is not null)
            CancelConnect();

        var anchorPoint = replaceSource
            ? new Point(arrow.EndX, arrow.EndY)
            : new Point(arrow.StartX, arrow.StartY);

        _arrowReconnect = new ArrowReconnectState(arrowId, replaceSource, anchorPoint);
        ConnectPreview.X1 = anchorPoint.X;
        ConnectPreview.Y1 = anchorPoint.Y;
        ConnectPreview.X2 = anchorPoint.X;
        ConnectPreview.Y2 = anchorPoint.Y;
        ConnectPreview.Visibility = Visibility.Visible;
        RootGrid.Cursor = Cursors.Cross;
        RootGrid.CaptureMouse();
        Focus();
        e.Handled = true;
    }

    private void FinishArrowReconnect(Point dropPoint)
    {
        if (_arrowReconnect is not { } reconnect)
            return;

        var vm = VM;
        CancelArrowReconnect();
        if (vm is null)
            return;

        var target = FindNodeAt(dropPoint);
        if (target is null)
            return;

        vm.Editor.ReconnectArrow(reconnect.ArrowId, reconnect.ReplaceSource, target.Id);
    }

    private void CancelArrowReconnect()
    {
        _arrowReconnect = null;
        ConnectPreview.Visibility = Visibility.Collapsed;
        RootGrid.Cursor = Cursors.Arrow;
        RootGrid.ReleaseMouseCapture();
    }

    private EntityNode? FindNodeAt(Point point)
    {
        if (VM is null)
            return null;

        return VM.CanvasNodes.LastOrDefault(node =>
            point.X >= node.X && point.X <= node.X + node.Width
            && point.Y >= node.Y && point.Y <= node.Y + node.Height);
    }
}
