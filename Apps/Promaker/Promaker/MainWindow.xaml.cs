using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Promaker.Presentation;
using Promaker.ViewModels;

namespace Promaker;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.FocusNameEditorRequested = PropertyPane.FocusNameEditorControl;
        // viewport 콜백은 SplitCanvasContainer.OnDataContextChanged에서 각 pane에 연결됩니다.

        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (App.StartupFilePath is { } path)
        {
            App.StartupFilePath = null;
            _vm.OpenFilePath(path);
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscardChangesPublic())
            e.Cancel = true;
    }

    private static readonly string[] SupportedExtensions = [".sdf", ".json", ".aasx", ".md", ".mmd"];

    private bool IsSupportedFileDrop(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop)
        && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files
        && SupportedExtensions.Contains(
            System.IO.Path.GetExtension(files[0]).ToLowerInvariant());

    private string? GetDragFileType(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return null;

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0)
            return null;

        var filePath = files[0];
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".sdf") return "sdf";
        if (ext == ".json") return "json";
        if (ext == ".aasx") return "aasx";
        if (ext == ".md" || ext == ".mmd") return "mermaid";
        return null;
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        var fileType = GetDragFileType(e);
        if (fileType != null)
        {
            UpdateDragDropOverlay(fileType);
            FileDragOverlay.Visibility = Visibility.Visible;
            e.Effects = DragDropEffects.Copy;
        }
    }

    private void UpdateDragDropOverlay(string fileType)
    {
        // Hide all icons first
        DragDropSdfIcon.Visibility = Visibility.Collapsed;
        DragDropJsonIcon.Visibility = Visibility.Collapsed;
        DragDropAasxIcon.Visibility = Visibility.Collapsed;
        DragDropMermaidIcon.Visibility = Visibility.Collapsed;

        // Show appropriate icon and message based on file type
        switch (fileType)
        {
            case "sdf":
                DragDropSdfIcon.Visibility = Visibility.Visible;
                DragDropMessage.Text = "SDF 파일을 여기에 놓으세요";
                DragDropSubMessage.Text = "Software Defined Factory 프로젝트 파일";
                break;
            case "json":
                DragDropJsonIcon.Visibility = Visibility.Visible;
                DragDropMessage.Text = "JSON 파일을 여기에 놓으세요";
                DragDropSubMessage.Text = "레거시 프로젝트 파일 형식";
                break;
            case "aasx":
                DragDropAasxIcon.Visibility = Visibility.Visible;
                DragDropMessage.Text = "AASX 파일을 여기에 놓으세요";
                DragDropSubMessage.Text = "Asset Administration Shell 패키지";
                break;
            case "mermaid":
                DragDropMermaidIcon.Visibility = Visibility.Visible;
                DragDropMessage.Text = "Mermaid 파일을 여기에 놓으세요";
                DragDropSubMessage.Text = "Mermaid 다이어그램 형식";
                break;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var fileType = GetDragFileType(e);
        if (fileType != null)
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        FileDragOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        FileDragOverlay.Visibility = Visibility.Collapsed;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
            return;

        if (!_vm.ConfirmDiscardChangesPublic())
            return;

        _vm.OpenFilePath(files[0]);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowTheme();
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
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
