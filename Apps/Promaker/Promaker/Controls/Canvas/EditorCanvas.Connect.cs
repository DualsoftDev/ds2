using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Dialogs;
using Promaker.Presentation;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class EditorCanvas
{
    private ArrowType _connectArrowType = ArrowType.Start;

    private void StartConnect_Click(object sender, RoutedEventArgs e)
    {
        StartConnectFromCurrentSelection();
    }

    private void StartConnectFromCurrentSelection()
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

    internal static bool TryResolveConnectSourceEntityType(MainViewModel? viewModel, out EntityKind entityType)
    {
        entityType = default;

        if (viewModel is null)
            return false;

        if (viewModel.Selection.TryGetOrderedSelectionConnectEntityType(out var orderedSelectionType))
        {
            entityType = orderedSelectionType;
            return true;
        }

        if (viewModel.SelectedNode is { EntityType: EntityKind.Work or EntityKind.Call } node)
        {
            entityType = node.EntityType;
            return true;
        }

        return false;
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
        if (_connectSource is not { } sourceId || VM is null || ActiveCanvasState!.ActiveTab is null) return;

        var srcNode = ActiveCanvasState!.CanvasNodes.FirstOrDefault(n => n.Id == sourceId);
        var tgtNode = ActiveCanvasState!.CanvasNodes.FirstOrDefault(n => n.Id == targetId);
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

    /// <summary>
    /// 2개 이상 노드 선택 시 우클릭 컨텍스트 메뉴에 화살표 타입 목록을 표시합니다.
    /// Work: 핀된 타입 + "연결 옵션 선택..." / Call: 2개 전부
    /// </summary>
    internal void ShowArrowTypeContextMenu(EntityKind entityType)
    {
        var isWorkMode = EntityKindRules.isWorkArrowMode(entityType);
        var available = EntityKindRules.availableArrowTypes(isWorkMode);

        var menu = new ContextMenu();

        IEnumerable<ArrowType> menuTypes = isWorkMode
            ? ArrowTypeFrequencyTracker.GetPinnedTypes(available)
            : available;

        foreach (var at in menuTypes)
        {
            var type = at;
            var item = new MenuItem
            {
                Header = ArrowTypeFrequencyTracker.DisplayName(type),
                Icon = CreateArrowIcon(type),
            };
            item.Click += (_, _) =>
            {
                ArrowTypeFrequencyTracker.RecordUsage(type);
                VM?.Selection.ConnectSelectedNodesInOrder(type);
            };
            menu.Items.Add(item);
        }

        if (isWorkMode)
        {
            menu.Items.Add(new Separator());
            var settingsItem = new MenuItem { Header = "연결 옵션 선택..." };
            settingsItem.Click += StartConnect_Click;
            menu.Items.Add(settingsItem);
        }

        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "삭제" };
        delete.Click += (_, _) => VM?.DeleteSelectedCommand.Execute(null);
        menu.Items.Add(delete);

        menu.IsOpen = true;
    }

    private static Canvas CreateArrowIcon(ArrowType type)
    {
        var canvas = new Canvas { Width = 32, Height = 14 };
        Brush brush;
        bool dashed = false;
        bool bidirectional = false;
        bool hasStartRect = false;
        bool noArrowHead = false;

        switch (type)
        {
            case ArrowType.Start:
                brush = new SolidColorBrush(Color.FromRgb(76, 175, 80));  // green
                break;
            case ArrowType.Reset:
                brush = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // orange
                dashed = true;
                break;
            case ArrowType.StartReset:
                brush = new SolidColorBrush(Color.FromRgb(244, 67, 54));  // red
                hasStartRect = true;
                break;
            case ArrowType.ResetReset:
                brush = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // orange
                dashed = true;
                bidirectional = true;
                break;
            case ArrowType.Group:
                brush = new SolidColorBrush(Color.FromRgb(158, 158, 158)); // gray
                noArrowHead = true;
                break;
            default:
                brush = Brushes.Gray;
                break;
        }

        // 시작점 사각형 (StartReset)
        double lineStart = 0;
        if (hasStartRect)
        {
            var rect = new Rectangle { Width = 6, Height = 6, Fill = brush };
            Canvas.SetLeft(rect, 0);
            Canvas.SetTop(rect, 4);
            canvas.Children.Add(rect);
            lineStart = 6;
        }

        // 선
        var line = new Line
        {
            X1 = lineStart, Y1 = 7,
            X2 = noArrowHead ? 32 : 24, Y2 = 7,
            Stroke = brush,
            StrokeThickness = 2,
        };
        if (dashed)
            line.StrokeDashArray = new DoubleCollection { 2, 1 };
        canvas.Children.Add(line);

        // 역방향 화살촉 (ResetReset)
        if (bidirectional)
        {
            var backArrow = new Path
            {
                Data = Geometry.Parse("M7,1 L0,7 L7,13"),
                Stroke = brush,
                StrokeThickness = 1.5,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Miter,
            };
            canvas.Children.Add(backArrow);
        }

        // 정방향 화살촉
        if (!noArrowHead)
        {
            var arrow = new Path
            {
                Data = Geometry.Parse("M24,1 L32,7 L24,13"),
                Stroke = brush,
                StrokeThickness = 1.5,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Miter,
            };
            canvas.Children.Add(arrow);
        }

        return canvas;
    }
}
