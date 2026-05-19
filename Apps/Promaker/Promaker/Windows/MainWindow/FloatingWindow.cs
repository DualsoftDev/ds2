using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Layout;

namespace Promaker;

public partial class MainWindow
{
    private void OnFloatingWindowCreated(object? sender, LayoutFloatingWindowControlCreatedEventArgs e)
    {
        var w = e.LayoutFloatingWindowControl;
        TraceDock($"FloatingWindowCreated window={w.GetType().Name} model={ElementDesc(w.Model)}", includeTree: true);
        w.OwnedByDockingManagerWindow = false;
        w.Topmost = false;
        if (Application.Current.TryFindResource("PrimaryBackgroundBrush") is Brush bg)
            w.Background = bg;
        DeferAttachFloatingResizeGripHook(w);

        if (w.IsLoaded)
            DeferApplyChromeThickness(w);
        else
            w.Loaded += FloatingWindow_Loaded;
    }

    private static void FloatingWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window w) return;
        w.Loaded -= FloatingWindow_Loaded;
        DeferAttachFloatingResizeGripHook(w);
        DeferApplyChromeThickness(w);
    }

    private static void DeferApplyChromeThickness(Window w)
    {
        w.Dispatcher.BeginInvoke(new Action(() => ApplyChromeThickness(w)),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
        w.Dispatcher.BeginInvoke(new Action(() => ApplyChromeThickness(w)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private const double FloatingResizeBorderSide = 8.0;
    private const double FloatingResizeBorderBottom = 10.0;
    private const double FloatingResizeBorderTop = 4.0;

    private static void ApplyChromeThickness(Window w)
    {
        var chrome = Microsoft.Windows.Shell.WindowChrome.GetWindowChrome(w);
        var origin = "existing";
        if (chrome == null)
        {
            chrome = new Microsoft.Windows.Shell.WindowChrome
            {
                CaptionHeight = 24,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
            };
            origin = "created";
        }
        else if (chrome.IsFrozen)
        {
            chrome = (Microsoft.Windows.Shell.WindowChrome)chrome.Clone();
            origin = "cloned";
        }
        chrome.ResizeBorderThickness = new Thickness(
            FloatingResizeBorderSide,
            FloatingResizeBorderTop,
            FloatingResizeBorderSide,
            FloatingResizeBorderBottom);
        Microsoft.Windows.Shell.WindowChrome.SetWindowChrome(w, chrome);
        Log.Debug($"[Dock] floating chrome ResizeBorderThickness=({FloatingResizeBorderSide},{FloatingResizeBorderTop},{FloatingResizeBorderSide},{FloatingResizeBorderBottom}) 적용 ({origin}, window={w.GetType().Name})");
    }

    private static void DeferAttachFloatingResizeGripHook(Window w)
    {
        w.Dispatcher.BeginInvoke(new Action(() => AttachFloatingResizeGripHook(w)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static void AttachFloatingResizeGripHook(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero || !FloatingGripHookedHandles.Add(hwnd)) return;

        var source = HwndSource.FromHwnd(hwnd);
        if (source == null)
        {
            FloatingGripHookedHandles.Remove(hwnd);
            return;
        }

        source.AddHook(FloatingResizeGripHitTestHook);
        w.Closed += (_, _) =>
        {
            source.RemoveHook(FloatingResizeGripHitTestHook);
            FloatingGripHookedHandles.Remove(hwnd);
        };
    }

    private static IntPtr FloatingResizeGripHitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest || !GetWindowRect(hwnd, out var rect))
            return IntPtr.Zero;

        var x = GetSignedLoWord(lParam);
        var y = GetSignedHiWord(lParam);
        if (x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom)
            return IntPtr.Zero;

        if (y >= rect.Bottom - FloatingBottomCornerGripHeightPx)
        {
            if (x < rect.Left + FloatingBottomCornerGripWidthPx)
            {
                handled = true;
                return new IntPtr(HtBottomLeft);
            }
            if (x >= rect.Right - FloatingBottomCornerGripWidthPx)
            {
                handled = true;
                return new IntPtr(HtBottomRight);
            }
        }

        if (y >= rect.Bottom - FloatingBottomEdgeGripHeightPx)
        {
            handled = true;
            return new IntPtr(HtBottom);
        }

        return IntPtr.Zero;
    }

    private static int GetSignedLoWord(IntPtr value) => unchecked((short)((long)value & 0xffff));
    private static int GetSignedHiWord(IntPtr value) => unchecked((short)(((long)value >> 16) & 0xffff));
}
