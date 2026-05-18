using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Promaker.Presentation;

namespace Promaker;

public partial class MainWindow
{
    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowTheme();
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
    }

    private void ThemeManager_ThemeChanged(AppTheme theme)
    {
        Dispatcher.Invoke(ApplyWindowTheme);
    }

    private void ApplyWindowTheme()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        TrySetDwmAttribute(hwnd, DwmwaUseImmersiveDarkMode, ThemeManager.CurrentTheme == AppTheme.Dark ? 1 : 0);
        TrySetDwmAttribute(hwnd, DwmwaCaptionColor, GetColorRef("ToolbarShellBrush", ThemeManager.CurrentTheme == AppTheme.Dark ? Color.FromRgb(0x12, 0x21, 0x36) : Color.FromRgb(0xED, 0xF3, 0xFA)));
        TrySetDwmAttribute(hwnd, DwmwaTextColor, GetColorRef("PrimaryTextBrush", ThemeManager.CurrentTheme == AppTheme.Dark ? Colors.White : Color.FromRgb(0x1F, 0x29, 0x37)));
        TrySetDwmAttribute(hwnd, DwmwaBorderColor, GetColorRef("BorderBrush", ThemeManager.CurrentTheme == AppTheme.Dark ? Color.FromRgb(0x41, 0x54, 0x6C) : Color.FromRgb(0xC4, 0xCF, 0xDB)));
    }

    private int GetColorRef(string resourceKey, Color fallback)
    {
        var brush = TryFindResource(resourceKey) as SolidColorBrush;
        var color = brush?.Color ?? fallback;
        return color.R | (color.G << 8) | (color.B << 16);
    }

    private static void TrySetDwmAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch
        {
            // Ignore unsupported DWM attributes on older Windows builds.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
