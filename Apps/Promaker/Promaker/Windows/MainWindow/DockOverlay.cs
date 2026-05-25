using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Promaker;

public partial class MainWindow
{
    private const string CenterDocumentDropTargetGridName = "_gridDocumentPaneFullDropTargets";
    private static readonly string[] InnerDocumentDropTargetFieldNames =
    [
        "_documentPaneFullDropTargetTop",
        "_documentPaneFullDropTargetLeft",
        "_documentPaneFullDropTargetInto",
        "_documentPaneFullDropTargetRight",
        "_documentPaneFullDropTargetBottom"
    ];
    private static readonly HashSet<Window> FilteredOverlayWindows = [];

    private void AttachFloatingOverlayFilter(Window floatingWindow)
    {
        floatingWindow.LocationChanged += FloatingWindow_FilterOverlayDropTargets;
        floatingWindow.Activated += FloatingWindow_FilterOverlayDropTargets;
        floatingWindow.Closed += FloatingWindow_DetachOverlayFilter;
        ScheduleHideDocumentPaneDropTargets();
    }

    private void FloatingWindow_FilterOverlayDropTargets(object? sender, EventArgs e)
    {
        HideDocumentPaneDropTargets();
        ScheduleHideDocumentPaneDropTargets();
    }

    private void FloatingWindow_DetachOverlayFilter(object? sender, EventArgs e)
    {
        if (sender is not Window floatingWindow) return;
        floatingWindow.LocationChanged -= FloatingWindow_FilterOverlayDropTargets;
        floatingWindow.Activated -= FloatingWindow_FilterOverlayDropTargets;
        floatingWindow.Closed -= FloatingWindow_DetachOverlayFilter;
    }

    private static void HideDocumentPaneDropTargets()
    {
        foreach (var overlayWindow in Application.Current.Windows
                     .OfType<Window>()
                     .Where(w => w.GetType().FullName == "AvalonDock.Controls.OverlayWindow"))
        {
            AttachOverlayWindowFilter(overlayWindow);
            HideDocumentPaneDropTargets(overlayWindow);
        }
    }

    private static void ScheduleHideDocumentPaneDropTargets()
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(HideDocumentPaneDropTargets),
            System.Windows.Threading.DispatcherPriority.Render);
        Application.Current.Dispatcher.BeginInvoke(new Action(HideDocumentPaneDropTargets),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static void HideDocumentPaneDropTargets(Window overlayWindow)
    {
        // Hidden — 공간은 차지하되 invisible. Collapsed 면 wrapper Grid 의 size 가 0 으로 줄어
        // 외곽 wrapper 자체가 사라지지만, Hidden 으로 두면 *5 X 자리는 비어 있고 wrapper 는 유지* 된다.
        var overlayType = overlayWindow.GetType();
        foreach (var fieldName in InnerDocumentDropTargetFieldNames)
        {
            var field = overlayType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(overlayWindow) is UIElement element)
                element.Visibility = Visibility.Hidden;
        }
    }

    private static void AttachOverlayWindowFilter(Window overlayWindow)
    {
        if (!FilteredOverlayWindows.Add(overlayWindow))
            return;

        overlayWindow.LayoutUpdated += OverlayWindow_LayoutUpdated;
        overlayWindow.Closed += OverlayWindow_Closed;
    }

    private static void OverlayWindow_LayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is Window overlayWindow)
            HideDocumentPaneDropTargets(overlayWindow);
    }

    private static void OverlayWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not Window overlayWindow) return;
        overlayWindow.LayoutUpdated -= OverlayWindow_LayoutUpdated;
        overlayWindow.Closed -= OverlayWindow_Closed;
        FilteredOverlayWindows.Remove(overlayWindow);
    }
}
