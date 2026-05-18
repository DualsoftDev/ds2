using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Promaker.Presentation;

namespace Promaker;

public partial class MainWindow
{
    private static readonly string[] SupportedExtensions =
        [FileExtensions.Sdf, FileExtensions.Json, FileExtensions.Aasx, FileExtensions.Mermaid, FileExtensions.MermaidAlt, FileExtensions.Yaml, FileExtensions.YamlAlt];

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

        if (ext == FileExtensions.Sdf) return "sdf";
        if (ext == FileExtensions.Json) return "json";
        if (ext == FileExtensions.Aasx) return "aasx";
        if (ext == FileExtensions.Mermaid || ext == FileExtensions.MermaidAlt) return "mermaid";
        if (ext == FileExtensions.Yaml || ext == FileExtensions.YamlAlt) return "yaml";
        return null;
    }

    // WPF drop target has no reliable drag cancel/release event, so a short watchdog clears stuck overlays.
    private DispatcherTimer? _overlayWatchdog;
    private DateTime _lastDragOverAt;
    private static readonly TimeSpan OverlayWatchdogInterval = TimeSpan.FromMilliseconds(60);
    private const int VkEscape = 0x1B;
    private const int VkLButton = 0x01;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// Drag operation 중엔 mouse capture 가 OLE 측에 있어 <see cref="System.Windows.Input.Mouse.GetPosition"/> 이
    /// stale/부정확한 좌표 반환 → false-positive "outside window" 로 overlay 가 토글되며 깜빡임.
    /// 대신 OS level 의 <c>GetCursorPos</c> + <c>PointFromScreen</c> 으로 정확한 hit-test.
    /// </summary>
    private bool IsCursorOutsideWindow()
    {
        if (!GetCursorPos(out var cursorPos)) return false;
        try
        {
            var pt = PointFromScreen(new Point(cursorPos.X, cursorPos.Y));
            return pt.X < 0 || pt.X > ActualWidth || pt.Y < 0 || pt.Y > ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureOverlayWatchdog()
    {
        if (_overlayWatchdog is not null) return;
        _overlayWatchdog = new DispatcherTimer { Interval = OverlayWatchdogInterval };
        _overlayWatchdog.Tick += OverlayWatchdog_Tick;
    }

    private void OverlayWatchdog_Tick(object? sender, EventArgs e)
    {
        if ((GetAsyncKeyState(VkEscape) & 0x8000) != 0)
        {
            HideDragOverlay();
            return;
        }

        if ((GetAsyncKeyState(VkLButton) & 0x8000) == 0)
        {
            HideDragOverlay();
            return;
        }

        if (IsCursorOutsideWindow())
            HideDragOverlay();
    }

    private void HideDragOverlay()
    {
        FileDragOverlay.Visibility = Visibility.Collapsed;
        _overlayWatchdog?.Stop();
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        var fileType = GetDragFileType(e);
        if (fileType != null)
        {
            UpdateDragDropOverlay(fileType);
            FileDragOverlay.Visibility = Visibility.Visible;
            _lastDragOverAt = DateTime.Now;
            EnsureOverlayWatchdog();
            _overlayWatchdog!.Start();
            e.Effects = DragDropEffects.Copy;
        }
    }

    private void UpdateDragDropOverlay(string fileType)
    {
        DragDropSdfIcon.Visibility = Visibility.Collapsed;
        DragDropJsonIcon.Visibility = Visibility.Collapsed;
        DragDropAasxIcon.Visibility = Visibility.Collapsed;
        DragDropMermaidIcon.Visibility = Visibility.Collapsed;
        DragDropYamlIcon.Visibility = Visibility.Collapsed;

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
            case "yaml":
                DragDropYamlIcon.Visibility = Visibility.Visible;
                DragDropMessage.Text = "YAML 파일을 여기에 놓으세요";
                DragDropSubMessage.Text = "lossy 공유 포맷 (GUID·위치 비저장)";
                break;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var fileType = GetDragFileType(e);
        if (fileType != null)
        {
            UpdateDragDropOverlay(fileType);
            FileDragOverlay.Visibility = Visibility.Visible;
            _lastDragOverAt = DateTime.Now;
            EnsureOverlayWatchdog();
            _overlayWatchdog!.Start();
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
        // DragLeave is noisy during child transitions; the watchdog owns real exit detection.
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        HideDragOverlay();

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
            return;

        if (!_vm.ConfirmDiscardChangesPublic())
            return;

        _vm.OpenFilePath(files[0]);
    }
}
