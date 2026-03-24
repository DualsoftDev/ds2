using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.ViewModels;

namespace Promaker.Controls;

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
    private Point _lastContextMenuCanvasPos;

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
        Unloaded += OnCanvasUnloaded;
        Focusable = true;
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private CanvasWorkspaceState? _pane;
    private bool _quickAddStateSubscribed;

    /// <summary>이 캔버스가 표시하는 pane입니다. SplitCanvasContainer에서 설정됩니다.</summary>
    public CanvasWorkspaceState? Pane
    {
        get => _pane;
        set
        {
            UnsubscribeQuickAddState(_pane);
            _pane = value;
            BindPaneCollections();
            SubscribeQuickAddState(_pane);
            UpdateQuickAddToolbar();
        }
    }

    /// <summary>현재 pane의 CanvasNodes 또는 ActivePane의 CanvasNodes를 반환합니다.</summary>
    private CanvasWorkspaceState? ActiveCanvasState => Pane ?? VM?.Canvas;

    private void BindPaneCollections()
    {
        var state = ActiveCanvasState;
        NodesHost.ItemsSource = state?.CanvasNodes;
        ArrowsHost.ItemsSource = state?.CanvasArrows;
    }

    private void SubscribeQuickAddState(CanvasWorkspaceState? pane)
    {
        if (pane is null || _quickAddStateSubscribed)
            return;

        pane.PropertyChanged += OnPanePropertyChanged;
        pane.QuickAddFlowCommand.CanExecuteChanged += OnQuickAddCanExecuteChanged;
        pane.QuickAddContextualNodeCommand.CanExecuteChanged += OnQuickAddCanExecuteChanged;
        _quickAddStateSubscribed = true;
    }

    private void UnsubscribeQuickAddState(CanvasWorkspaceState? pane)
    {
        if (pane is null || !_quickAddStateSubscribed)
            return;

        pane.PropertyChanged -= OnPanePropertyChanged;
        pane.QuickAddFlowCommand.CanExecuteChanged -= OnQuickAddCanExecuteChanged;
        pane.QuickAddContextualNodeCommand.CanExecuteChanged -= OnQuickAddCanExecuteChanged;
        _quickAddStateSubscribed = false;
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CanvasWorkspaceState.ActiveTab) or nameof(CanvasWorkspaceState.ContextualQuickCreateLabel))
            UpdateQuickAddToolbar();
    }

    private void OnQuickAddCanExecuteChanged(object? sender, EventArgs e) => UpdateQuickAddToolbar();

    private void UpdateQuickAddToolbar()
    {
        var state = ActiveCanvasState;
        if (state is null)
        {
            FlowQuickAddButton.IsEnabled = false;
            ContextualQuickAddButton.IsEnabled = false;
            ContextualQuickAddText.Text = "W/C";
            return;
        }

        FlowQuickAddButton.IsEnabled = state.QuickAddFlowCommand.CanExecute(null);
        ContextualQuickAddButton.IsEnabled = state.QuickAddContextualNodeCommand.CanExecute(null);
        ContextualQuickAddText.Text = state.ContextualQuickCreateLabel switch
        {
            "Work" => "W",
            "Call" => "C",
            _ => "W/C"
        };
    }

    private void OnQuickAddFlow(object sender, RoutedEventArgs e)
    {
        var state = ActiveCanvasState;
        if (state?.QuickAddFlowCommand.CanExecute(null) != true)
            return;

        state.QuickAddFlowCommand.Execute(null);
    }

    private void OnQuickAddContextual(object sender, RoutedEventArgs e)
    {
        var state = ActiveCanvasState;
        if (state?.QuickAddContextualNodeCommand.CanExecute(null) != true)
            return;

        state.QuickAddContextualNodeCommand.Execute(null);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _lastContextMenuCanvasPos = Mouse.GetPosition(MainCanvas);

        // 2개 이상 노드 선택 시 화살표 연결 메뉴 표시
        if (VM is not null
            && VM.Selection.TryGetOrderedSelectionConnectEntityType(out var entityType))
        {
            e.Handled = true; // 기본 ContextMenu 억제
            ShowArrowTypeContextMenu(entityType);
            return;
        }

        // 탭 종류별 메뉴 항목 가시성
        var tabKind = ActiveCanvasState?.ActiveTab?.Kind;
        AddCallMenuItem.Visibility = tabKind == Ds2.Editor.TabKind.Work
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddWork_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;
        VM.PendingAddPosition = new Xywh(
            (int)_lastContextMenuCanvasPos.X, (int)_lastContextMenuCanvasPos.Y,
            UiDefaults.DefaultNodeWidth, UiDefaults.DefaultNodeHeight);
        VM.AddWorkCommand.Execute(null);
    }

    private void AddCall_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;
        VM.PendingAddPosition = new Xywh(
            (int)_lastContextMenuCanvasPos.X, (int)_lastContextMenuCanvasPos.Y,
            UiDefaults.DefaultNodeWidth, UiDefaults.DefaultNodeHeight);
        VM.AddCallCommand.Execute(null);
    }

    public Point? GetViewportCenter()
    {
        var viewW = RootGrid.ActualWidth;
        var viewH = RootGrid.ActualHeight;
        if (viewW <= 0 || viewH <= 0)
            return null;

        var canvasX = (viewW / 2 - PanTransform.X) / _zoom;
        var canvasY = (viewH / 2 - PanTransform.Y) / _zoom;
        return new Point(canvasX, canvasY);
    }

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

        nodeId = default;
        border = null!;
        return false;
    }

    private void OnCanvasUnloaded(object sender, RoutedEventArgs e) => UnsubscribeQuickAddState(_pane);

}
