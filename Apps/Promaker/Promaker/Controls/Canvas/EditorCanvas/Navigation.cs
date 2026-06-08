using System.Windows;
using System.Windows.Input;

namespace Promaker.Controls;

public partial class EditorCanvas
{
    private const double ContentVisibilityMargin = 80.0;
    private const double InitialViewportMargin = 24.0;

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

    private void FitToViewCore(double maxZoomCap)
    {
        if (VM is null || ActiveCanvasState!.CanvasNodes.Count == 0) return;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var n in ActiveCanvasState!.CanvasNodes)
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

        _zoom = Math.Clamp(Math.Min(viewW / contentW, viewH / contentH), MinZoom, maxZoomCap);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        PanTransform.X = (viewW - contentW * _zoom) / 2 - minX * _zoom + 50 * _zoom;
        PanTransform.Y = (viewH - contentH * _zoom) / 2 - minY * _zoom + 50 * _zoom;
        ZoomText.Text = $"{(int)(_zoom * 100)}%";
    }

    private void OnFitToView(object sender, RoutedEventArgs e) => FitToViewCore(MaxZoom);

    public void FitToViewZoomOut() => FitToViewCore(1.0);

    public void CenterOnNode(Guid nodeId)
    {
        var node = ActiveCanvasState?.CanvasNodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null) return;

        var viewW = RootGrid.ActualWidth;
        var viewH = RootGrid.ActualHeight;
        if (viewW <= 0 || viewH <= 0) return;

        var centerX = node.X + node.Width / 2;
        var centerY = node.Y + node.Height / 2;
        PanTransform.X = viewW / 2 - centerX * _zoom;
        PanTransform.Y = viewH / 2 - centerY * _zoom;
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

    /// <summary>탭 전환 시 보존하기 위해 현재 줌/팬 상태를 반환합니다.</summary>
    public (double Zoom, double PanX, double PanY) GetCurrentView()
        => (_zoom, PanTransform.X, PanTransform.Y);

    /// <summary>저장된 줌/팬 상태를 그대로 적용합니다 (재계산/센터링 없음).</summary>
    public void RestoreView(double zoom, double panX, double panY)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        PanTransform.X = panX;
        PanTransform.Y = panY;
        ZoomText.Text = $"{(int)(_zoom * 100)}%";
    }

    public void ApplyZoomCentered(double zoom)
    {
        if (ActiveCanvasState?.CanvasNodes is not { Count: > 0 } nodes) return;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var n in nodes)
        {
            minX = Math.Min(minX, n.X);
            minY = Math.Min(minY, n.Y);
            maxX = Math.Max(maxX, n.X + n.Width);
            maxY = Math.Max(maxY, n.Y + n.Height);
        }

        var viewW = RootGrid.ActualWidth;
        var viewH = RootGrid.ActualHeight;
        if (viewW <= 0 || viewH <= 0) return;

        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;

        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        PanTransform.X = viewW / 2 - centerX * _zoom;
        PanTransform.Y = viewH / 2 - centerY * _zoom;
        ZoomText.Text = $"{(int)(_zoom * 100)}%";
    }

    public void ApplyZoomTopLeft(double zoom)
    {
        if (ActiveCanvasState?.CanvasNodes is not { Count: > 0 } nodes) return;

        double minX = double.MaxValue, minY = double.MaxValue;
        foreach (var n in nodes)
        {
            minX = Math.Min(minX, n.X);
            minY = Math.Min(minY, n.Y);
        }

        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        PanTransform.X = InitialViewportMargin - minX * _zoom;
        PanTransform.Y = InitialViewportMargin - minY * _zoom;
        ZoomText.Text = $"{(int)(_zoom * 100)}%";
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.PreviousSize.Width <= 0
            || e.PreviousSize.Height <= 0
            || e.NewSize.Width <= 0
            || e.NewSize.Height <= 0)
            return;

        PanTransform.X += (e.NewSize.Width - e.PreviousSize.Width) / 2;
        PanTransform.Y += (e.NewSize.Height - e.PreviousSize.Height) / 2;
        KeepContentReachable(e.NewSize.Width, e.NewSize.Height, centerSmallContent: true);
    }

    public void NormalizeViewportAfterLayoutChange()
    {
        var viewW = RootGrid.ActualWidth;
        var viewH = RootGrid.ActualHeight;
        if (viewW <= 0 || viewH <= 0) return;

        KeepContentReachable(viewW, viewH, centerSmallContent: true);
    }

    private void KeepContentReachable(double viewW, double viewH, bool centerSmallContent)
    {
        if (ActiveCanvasState?.CanvasNodes is not { Count: > 0 } nodes) return;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var n in nodes)
        {
            minX = Math.Min(minX, n.X);
            minY = Math.Min(minY, n.Y);
            maxX = Math.Max(maxX, n.X + n.Width);
            maxY = Math.Max(maxY, n.Y + n.Height);
        }

        var left = minX * _zoom + PanTransform.X;
        var right = maxX * _zoom + PanTransform.X;
        var top = minY * _zoom + PanTransform.Y;
        var bottom = maxY * _zoom + PanTransform.Y;

        if (centerSmallContent)
        {
            var contentW = (maxX - minX) * _zoom;
            var contentH = (maxY - minY) * _zoom;
            var centerX = ((minX + maxX) / 2) * _zoom + PanTransform.X;
            var centerY = ((minY + maxY) / 2) * _zoom + PanTransform.Y;

            if (contentW <= viewW
                && (centerX < viewW * 0.30 || centerX > viewW * 0.70))
                PanTransform.X += viewW / 2 - centerX;

            if (contentH <= viewH
                && (centerY < viewH * 0.30 || centerY > viewH * 0.70))
                PanTransform.Y += viewH / 2 - centerY;

            left = minX * _zoom + PanTransform.X;
            right = maxX * _zoom + PanTransform.X;
            top = minY * _zoom + PanTransform.Y;
            bottom = maxY * _zoom + PanTransform.Y;
        }

        if (right < ContentVisibilityMargin)
            PanTransform.X += ContentVisibilityMargin - right;
        else if (left > viewW - ContentVisibilityMargin)
            PanTransform.X -= left - (viewW - ContentVisibilityMargin);

        if (bottom < ContentVisibilityMargin)
            PanTransform.Y += ContentVisibilityMargin - bottom;
        else if (top > viewH - ContentVisibilityMargin)
            PanTransform.Y -= top - (viewH - ContentVisibilityMargin);
    }
}
