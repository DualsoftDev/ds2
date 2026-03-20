using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Promaker.Controls;

public partial class SimulationControlPanel : UserControl
{
    private Point _dragStartPoint;
    private Point _dragStartOffset;
    private bool _isDragging;

    public SimulationControlPanel()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureDefaultPosition();
    }

    private void DragHandle_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Parent is not Canvas canvas)
            return;

        EnsurePositionInitialized(canvas);

        _isDragging = true;
        _dragStartPoint = e.GetPosition(canvas);
        _dragStartOffset = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
        DragHandle.CaptureMouse();
        e.Handled = true;
    }

    private void DragHandle_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || Parent is not Canvas canvas)
            return;

        var currentPoint = e.GetPosition(canvas);
        var targetLeft = _dragStartOffset.X + (currentPoint.X - _dragStartPoint.X);
        var targetTop = _dragStartOffset.Y + (currentPoint.Y - _dragStartPoint.Y);

        var maxLeft = Math.Max(12, canvas.ActualWidth - ActualWidth - 12);
        var maxTop = Math.Max(12, canvas.ActualHeight - ActualHeight - 12);

        Canvas.SetLeft(this, Clamp(targetLeft, 12, maxLeft));
        Canvas.SetTop(this, Clamp(targetTop, 12, maxTop));
    }

    private void DragHandle_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        DragHandle.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void EnsureDefaultPosition()
    {
        if (Parent is not Canvas canvas)
            return;

        EnsurePositionInitialized(canvas);
    }

    private void EnsurePositionInitialized(Canvas canvas)
    {
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        if (!double.IsNaN(left) && !double.IsNaN(top))
            return;

        UpdateLayout();
        canvas.UpdateLayout();

        var defaultLeft = Math.Max(12, canvas.ActualWidth - ActualWidth - 18);
        var defaultTop = Math.Max(12, canvas.ActualHeight - ActualHeight - 18);

        Canvas.SetLeft(this, defaultLeft);
        Canvas.SetTop(this, defaultTop);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
            return min;

        return Math.Max(min, Math.Min(max, value));
    }
}
