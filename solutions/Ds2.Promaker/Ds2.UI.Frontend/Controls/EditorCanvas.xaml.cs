using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.UI.Frontend.ViewModels;

namespace Ds2.UI.Frontend.Controls;

public partial class EditorCanvas : UserControl
{
    private double _zoom = 1.0;
    private const double MinZoom = 0.2;
    private const double MaxZoom = 5.0;
    private const double ZoomStep = 0.1;
    private const double ClickThreshold = 3.0;

    private bool _isPanning;
    private Point _panStart;

    private DragState? _drag;
    private FrameworkElement? _dragElement;

    private bool _isBoxSelecting;
    private bool _boxSelectAdditive;
    private Point _boxStart;

    private Guid? _connectSource;
    private Point _connectSourcePos;
    private ArrowReconnectState? _arrowReconnect;

    private sealed class DragItem(EntityNode node, double originX, double originY)
    {
        public EntityNode Node { get; } = node;
        public double OriginX { get; } = originX;
        public double OriginY { get; } = originY;
    }

    private sealed class DragState(Point startPoint, IReadOnlyList<DragItem> items)
    {
        public Point StartPoint { get; } = startPoint;
        public IReadOnlyList<DragItem> Items { get; } = items;
    }

    private sealed class ArrowReconnectState(Guid arrowId, bool replaceSource, Point anchorPoint)
    {
        public Guid ArrowId { get; } = arrowId;
        public bool ReplaceSource { get; } = replaceSource;
        public Point AnchorPoint { get; } = anchorPoint;
    }

    public EditorCanvas()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Focusable = true;
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private void AddWork_Click(object sender, RoutedEventArgs e) => VM?.AddWorkCommand.Execute(null);
    private void AddCall_Click(object sender, RoutedEventArgs e) => VM?.AddCallCommand.Execute(null);

    private static Rect BuildRect(Point p1, Point p2)
    {
        var x = Math.Min(p1.X, p2.X);
        var y = Math.Min(p1.Y, p2.Y);
        var width = Math.Abs(p2.X - p1.X);
        var height = Math.Abs(p2.Y - p1.Y);
        return new Rect(x, y, width, height);
    }

    private static bool TryGetNodeFromElement(DependencyObject? source, out Guid nodeId, out FrameworkElement border)
    {
        while (source is not null)
        {
            if (source is Border { Tag: Guid id } b)
            {
                nodeId = id;
                border = b;
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        nodeId = Guid.Empty;
        border = null!;
        return false;
    }

}
