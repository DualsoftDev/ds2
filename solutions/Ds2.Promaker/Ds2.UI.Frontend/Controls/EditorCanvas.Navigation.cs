using System.Windows;
using System.Windows.Input;

namespace Ds2.UI.Frontend.Controls;

public partial class EditorCanvas
{
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(ViewportCanvas);
        var delta = e.Delta > 0 ? ZoomStep : -ZoomStep;
        var newZoom = Math.Clamp(_zoom + delta, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.001) return;

        var scale = newZoom / _zoom;
        PanTransform.X = pos.X - scale * (pos.X - PanTransform.X);
        PanTransform.Y = pos.Y - scale * (pos.Y - PanTransform.Y);

        _zoom = newZoom;
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        ZoomText.Text = $"{(int)(_zoom * 100)}%";
    }

    private void OnFitToView(object sender, RoutedEventArgs e)
    {
        if (VM is null || VM.CanvasNodes.Count == 0) return;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var n in VM.CanvasNodes)
        {
            minX = Math.Min(minX, n.X);
            minY = Math.Min(minY, n.Y);
            maxX = Math.Max(maxX, n.X + n.Width);
            maxY = Math.Max(maxY, n.Y + n.Height);
        }

        var contentW = maxX - minX + 100;
        var contentH = maxY - minY + 100;
        var viewW = RootGrid.ActualWidth;
        var viewH = RootGrid.ActualHeight;
        if (viewW <= 0 || viewH <= 0) return;

        _zoom = Math.Clamp(Math.Min(viewW / contentW, viewH / contentH), MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        PanTransform.X = (viewW - contentW * _zoom) / 2 - minX * _zoom + 50 * _zoom;
        PanTransform.Y = (viewH - contentH * _zoom) / 2 - minY * _zoom + 50 * _zoom;
        ZoomText.Text = $"{(int)(_zoom * 100)}%";
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ApplyZoom(_zoom + ZoomStep);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ApplyZoom(_zoom - ZoomStep);
    private void OnResetZoom(object sender, RoutedEventArgs e) => ApplyZoom(1.0);

    private void ApplyZoom(double newZoom)
    {
        _zoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        ZoomText.Text = $"{(int)(_zoom * 100)}%";
    }
}
