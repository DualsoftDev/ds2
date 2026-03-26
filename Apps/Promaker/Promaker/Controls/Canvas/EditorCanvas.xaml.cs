using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Dialogs;
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
        Focusable = true;
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private CanvasWorkspaceState? _pane;

    /// <summary>이 캔버스가 표시하는 pane입니다. SplitCanvasContainer에서 설정됩니다.</summary>
    public CanvasWorkspaceState? Pane
    {
        get => _pane;
        set
        {
            _pane = value;
            BindPaneCollections();
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

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _lastContextMenuCanvasPos = Mouse.GetPosition(MainCanvas);

        // 열린 탭이 없으면 컨텍스트 메뉴 표시 안 함
        var tabKind = ActiveCanvasState?.ActiveTab?.Kind;
        if (tabKind is null)
        {
            e.Handled = true;
            return;
        }

        // 2개 이상 노드 선택 시 화살표 연결 메뉴 표시
        if (VM is not null
            && VM.Selection.TryGetOrderedSelectionConnectEntityType(out var entityType))
        {
            e.Handled = true; // 기본 ContextMenu 억제
            ShowArrowTypeContextMenu(entityType);
            return;
        }

        // 탭 종류별 메뉴 항목 가시성
        AddWorkMenuItem.Visibility = tabKind == Ds2.Editor.TabKind.System
            ? Visibility.Visible : Visibility.Collapsed;
        AddCallMenuItem.Visibility = tabKind == Ds2.Editor.TabKind.Work
            ? Visibility.Visible : Visibility.Collapsed;
        // 레퍼런스 노드: Work가 선택된 상태 + Flow/System 탭에서만
        AddRefWorkMenuItem.Visibility =
            VM?.SelectedNode?.EntityType == EntityKind.Work
            && tabKind is Ds2.Editor.TabKind.System or Ds2.Editor.TabKind.Flow
                ? Visibility.Visible : Visibility.Collapsed;

        // 노드 선택 없으면 화살표 연결/복사/삭제 숨김
        bool hasSelection = VM?.SelectedNode is not null;
        ConnectMenuItem.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CopyMenuItem.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        DeleteMenuItem.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        // 클립보드 비어있으면 붙여넣기 숨김
        PasteMenuItem.Visibility = VM?.HasClipboardData == true
            ? Visibility.Visible : Visibility.Collapsed;

        // 연속/선행/후행 구분선 정리
        CollapseBoundarySeparators(CanvasContextMenu);
    }

    /// <summary>맨 위/아래 및 연속 Separator를 숨깁니다.</summary>
    private static void CollapseBoundarySeparators(ContextMenu menu)
    {
        var items = menu.Items.OfType<FrameworkElement>()
            .Where(fe => fe.Visibility == Visibility.Visible)
            .ToList();

        // 모든 Separator를 일단 보이게 복원
        foreach (var item in menu.Items.OfType<Separator>())
            item.Visibility = Visibility.Visible;

        // visible 항목만 다시 수집
        items = menu.Items.OfType<FrameworkElement>()
            .Where(fe => fe.Visibility == Visibility.Visible)
            .ToList();

        // 맨 위 Separator 숨김
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is Separator sep) sep.Visibility = Visibility.Collapsed;
            else break;
        }
        // 맨 아래 Separator 숨김
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is Separator sep) sep.Visibility = Visibility.Collapsed;
            else break;
        }
        // 연속 Separator 숨김
        bool prevWasSep = false;
        foreach (var item in menu.Items.OfType<FrameworkElement>())
        {
            if (item.Visibility != Visibility.Visible) continue;
            if (item is Separator sep)
            {
                if (prevWasSep) sep.Visibility = Visibility.Collapsed;
                prevWasSep = true;
            }
            else prevWasSep = false;
        }
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

    // ── Condition drag & drop from tree onto canvas Call node ──

    private EntityNode? _currentDropTarget;

    private void ClearDropTarget()
    {
        if (_currentDropTarget is not null)
        {
            _currentDropTarget.IsDropTarget = false;
            _currentDropTarget = null;
        }
    }

    private void UpdateDropTarget(Point canvasPos)
    {
        var hit = FindNodeAt(canvasPos);
        var callHit = hit is { EntityType: EntityKind.Call } ? hit : null;

        if (callHit == _currentDropTarget) return;

        ClearDropTarget();
        if (callHit is not null)
        {
            callHit.IsDropTarget = true;
            _currentDropTarget = callHit;
        }
    }

    private void OnConditionDragEnter(object sender, DragEventArgs e)
    {
        // 파일 드롭인 경우 상위(MainWindow)로 전파
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return; // e.Handled = false로 두어 상위에서 처리
        }

        if (!ConditionDropHelper.IsConditionCallDrag(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        UpdateDropTarget(e.GetPosition(MainCanvas));
        e.Handled = true;
    }

    private void OnConditionDragOver(object sender, DragEventArgs e)
    {
        // 파일 드롭인 경우 상위(MainWindow)로 전파
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return; // e.Handled = false로 두어 상위에서 처리
        }

        if (!ConditionDropHelper.IsConditionCallDrag(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        UpdateDropTarget(e.GetPosition(MainCanvas));
        e.Handled = true;
    }

    private void OnConditionDragLeave(object sender, DragEventArgs e)
    {
        ClearDropTarget();
        e.Handled = true;
    }

    private void OnConditionDrop(object sender, DragEventArgs e)
    {
        // 파일 드롭인 경우 상위(MainWindow)로 전파
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return; // e.Handled = false로 두어 상위에서 처리
        }

        ClearDropTarget();

        if (ConditionDropHelper.GetDroppedCallNode(e) is not { } droppedCallNode) return;
        if (VM is null) return;

        var dropPos = e.GetPosition(MainCanvas);
        var targetNode = FindNodeAt(dropPos);
        if (targetNode is null || targetNode.EntityType != EntityKind.Call)
            return;

        // 자기 자신에게 드롭 방지
        if (targetNode.Id == droppedCallNode.Id)
            return;

        var picker = new ConditionTypePickerDialog();
        if (Application.Current.MainWindow is { } owner) picker.Owner = owner;
        if (picker.ShowDialog() != true)
            return;

        var host = VM.PropertyPanel.Host;
        ConditionDropHelper.ExecuteConditionDrop(
            host.Store, host, targetNode.Id, picker.SelectedConditionType, droppedCallNode.Id);

        e.Handled = true;
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

}
