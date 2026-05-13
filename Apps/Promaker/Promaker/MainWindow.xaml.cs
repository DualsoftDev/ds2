using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Layout;
using Ds2.Core;
using Promaker.Presentation;
using Promaker.Services;
using Promaker.ViewModels;

namespace Promaker;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly TrayService _trayService = new();
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    // v7 PR-2a — VM ↔ View 동기화 재진입 가드. spike 결과 IsVisibleChanged 가 Hide()/Show() 1회당 3~4회 raise.
    private bool _suppressLlmChatSync;

    // v7 PR-2a Q1 — 빈 column 자동 collapse 시 복원할 default 폭/높이.
    private static readonly GridLength ExplorerDefaultW = new(320);
    private static readonly GridLength SimulationDefaultH = new(200);
    private static readonly GridLength HistoryDefaultH = new(220);
    private static readonly GridLength RightDefaultW = new(280);
    private static readonly GridLength ZeroLength = new(0);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        // v7 PR-2a — _vm.FocusNameEditorRequested 슬롯 set 은 PropertyPane.xaml.cs 의 Loaded/Unloaded 자가 등록으로 이동.
        // viewport 콜백은 SplitCanvasContainer.OnDataContextChanged에서 각 pane에 연결됩니다.

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        llmChatAnchor.Hiding += OnLlmChatHiding;

        // Tray 전환 콜백 wiring — Monitoring + RealPLC 시 PLAY 가 RequestTrayHide 호출.
        // STOP / 모드 전환 시 RequestTrayRestore 호출.
        _vm.Simulation.RequestTrayHide = () =>
        {
            var tooltip = $"Promaker — Monitoring 동작중 (port {_vm.Simulation.MonitoringHubAddress})";
            _trayService.HideToTray(this, tooltip);
        };
        _vm.Simulation.RequestTrayRestore = () => _trayService.RestoreWindow();

        _trayService.StopRequested += () =>
        {
            // 트레이 컨텍스트 메뉴 "STOP" — 시뮬 정지 + 윈도우 복원 (StopSimulation 가 FireTrayRestore 호출).
            Dispatcher.BeginInvoke(() =>
            {
                if (_vm.Simulation.StopSimulationCommand.CanExecute(null))
                    _vm.Simulation.StopSimulationCommand.Execute(null);
            });
        };
        _trayService.ExitRequested += () =>
        {
            // 트레이 컨텍스트 메뉴 "종료" — 정상 close 흐름 진입.
            Dispatcher.BeginInvoke(() =>
            {
                _trayService.RestoreWindow(); // close 흐름에서 confirm 다이얼로그 보이게
                Close();
            });
        };

        // v7 PR-2a Q1 — 빈 column 자동 collapse listener. 5개 anchor 모두 동일 핸들러.
        explorerAnchor.IsVisibleChanged += OnAnchorIsVisibleChanged;
        simulationAnchor.IsVisibleChanged += OnAnchorIsVisibleChanged;
        propertyAnchor.IsVisibleChanged += OnAnchorIsVisibleChanged;
        historyAnchor.IsVisibleChanged += OnAnchorIsVisibleChanged;
        llmChatAnchor.IsVisibleChanged += OnAnchorIsVisibleChanged;

        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;
    }

    // 외부 에디터 등으로 파일이 변경된 경우 포커스 복귀 시 사용자 confirm → reload.
    // Window_Closing / OpenFilePath 와 동일하게 Dispatcher.BeginInvoke(Background) 로 분리 —
    // activate cycle 안에서 modal Confirm 을 직접 호출하면 nested message pump 위험 + 다중 발화 비용.
    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(
            new Action(_vm.CheckExternalFileChange),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsLlmChatVisible):
                SyncLlmChatAnchorFromVm();
                break;
            case nameof(MainViewModel.HasProject):
                SyncWelcomeCanvasVisibility();
                break;
        }
    }

    // VM → View 단방향 (3속성 동시 set). 재진입 가드로 IsVisibleChanged 중복 raise → VM 역류 차단.
    // Edge case (todo §3.3): LlmChatVm==null (consent 거부) 또는 IsLlmEnabled=false → 안전 가드로 hide 유지.
    private void SyncLlmChatAnchorFromVm()
    {
        if (_suppressLlmChatSync) return;
        _suppressLlmChatSync = true;
        try
        {
            bool show = _vm.IsLlmChatVisible && _vm.LlmChatVm != null && _vm.IsLlmEnabled;
            llmChatAnchor.IsVisible = show;
            llmChatAnchor.IsActive = show;
            llmChatAnchor.IsSelected = show;
        }
        finally { _suppressLlmChatSync = false; }
    }

    // v7 PR-2a Q1/F2 — anchor 가 hidden 시 그 anchor 의 LayoutAnchorablePane DockWidth/DockHeight 잔존 문제.
    // 각 anchor 의 IsVisible 에 따라 pane 의 default 폭/높이를 toggle. 매 raise 마다 호출되어도 멱등.
    private void OnAnchorIsVisibleChanged(object? sender, EventArgs e)
    {
        explorerPane.DockWidth    = explorerAnchor.IsVisible    ? ExplorerDefaultW   : ZeroLength;
        simulationPane.DockHeight = simulationAnchor.IsVisible  ? SimulationDefaultH : ZeroLength;
        historyPane.DockHeight    = historyAnchor.IsVisible     ? HistoryDefaultH    : ZeroLength;

        // rightPanel 의 children (property/history/llm) 모두 hidden 시 column 자체 collapse.
        bool anyRightVisible = propertyAnchor.IsVisible || historyAnchor.IsVisible || llmChatAnchor.IsVisible;
        rightPanel.DockWidth = anyRightVisible ? RightDefaultW : ZeroLength;
    }

    // View → VM (X 버튼 = 사용자 명시 close 한 곳만). auto-hide / float 상태 변화는 무관.
    // e.Cancel 은 기본값 false 유지 — hide 자체는 그대로 진행 + VM 만 동기화.
    private void OnLlmChatHiding(object? sender, CancelEventArgs e)
    {
        if (_suppressLlmChatSync) return;
        _suppressLlmChatSync = true;
        try
        {
            _vm.IsLlmChatVisible = false;
        }
        finally { _suppressLlmChatSync = false; }
    }

    // HasProject 토글 시 welcomeDoc / canvasDoc 중 하나만 LayoutDocumentPane 에 attach.
    // LayoutDocument.IsVisible 은 XAML/C# 양쪽 모두 read-only (CS0200) 라 anchor 와 달리 set 불가능 →
    // workspaceDocs.Children Add/Remove 동적 관리로 처리.
    //
    // M1 (PR-1b 검열 발견) 잔여 우려: PR-3 의 XmlLayoutSerializer 도입 시 detach 된 LayoutDocument 인스턴스 가
    // serialize 직전에 attach 되어 있어야 round-trip 정합. PR-3 진입 시 spike 1차 검증 필요.
    private void SyncWelcomeCanvasVisibility()
    {
        var children = workspaceDocs.Children;
        var (target, other) = _vm.HasProject ? (canvasDoc, welcomeDoc) : (welcomeDoc, canvasDoc);
        if (children.Contains(other)) children.Remove(other);
        if (!children.Contains(target)) children.Add(target);
        target.IsActive = true;
        target.IsSelected = true;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // v7 PR-2a (검열 Major 1) — XAML 트리 / LayoutDocumentPane SelectedContentIndex 완전 unwound 이후 시점.
        // 생성자 InitializeComponent 직후 호출은 nondeterministic 위험.
        SyncWelcomeCanvasVisibility();
        // 초기 collapse 적용 (XAML 기본값 — llmChatAnchor IsVisible=False).
        OnAnchorIsVisibleChanged(null, EventArgs.Empty);

        if (App.StartupFilePath is { } path)
        {
            App.StartupFilePath = null;
            _vm.OpenFilePath(path);
        }
    }

    private bool _llmChatDisposed;

    /// <summary>
    /// 1d-5/1d-4 D — 명시적 cleanup 패턴: 첫 진입 시 close cancel + Dispose 후 Close() 재호출,
    /// 두 번째 진입 시 (`_llmChatDisposed=true`) 통과. async void Closed fire-and-forget 회피.
    ///
    /// Hot-fix-9 v2: 한 번 X 클릭만으로 발생하는 IsClosing race —
    /// `e.Cancel = true` 후 await 이 끝난 시점에 같은 close 사이클의 `IsClosing` 가 아직 남아있어
    /// `Close()` 가 `VerifyNotClosing` throw. v1 의 try/catch 는 throw 를 흡수만 해서 첫 X 무반응 → 두 번째 X
    /// 시 _llmChatDisposed=true 분기로 close. 정확한 fix = `Dispatcher.BeginInvoke(Close, Background)` 로
    /// 다음 message pump cycle 에 close 큐 → WPF 가 첫 close 사이클 정리 끝낸 후 background priority 로 실행.
    /// </summary>
    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        // 두 번째 진입(BeginInvoke 로 재큐된 Close)은 이미 confirm/dispose 완료 — 그대로 통과.
        // 가드를 confirm 보다 앞에 두지 않으면 IsDirty 상태에 따라 저장 확인 다이얼로그가 2번 표시될 수 있음.
        if (_llmChatDisposed) return;

        // 트레이에서 복원한 창을 다시 X 로 닫는 경우 — Monitoring + 실 PLC + 동작 중이면 종료가 아닌 백그라운드 복귀.
        // 진짜 종료는 트레이 컨텍스트 메뉴 "종료" 사용. PLAY 시점과 동일한 TrayConsentDialog 재사용 — "다시 묻지 않기" 도 공유.
        if (_vm.Simulation.SelectedRuntimeMode == RuntimeMode.Monitoring
            && _vm.Simulation.IsRealPlcConnected
            && _vm.Simulation.IsSimulating)
        {
            if (!Dialogs.TrayConsentDialog.ShowAndAskConsent())
            {
                e.Cancel = true;
                return;
            }
            e.Cancel = true;
            var tooltip = $"Promaker — Monitoring 동작중 (port {_vm.Simulation.MonitoringHubAddress})";
            _trayService.HideToTray(this, tooltip);
            return;
        }

        // --autostart-llm 측정 모드 = mutation 변경 자동 폐기 (Closing dialog skip).
        // 측정 끝난 후 fsx 가 CloseMainWindow 보내면 dialog 없이 진행 → log4net flush + DisposeLlmChatAsync 정상.
        if (!App.StartupAutoOpenLlm && !_vm.ConfirmDiscardChangesPublic())
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _llmChatDisposed = true;

        // v7 PR-2b — DisposeLlmChatAsync 전에 floating window 일괄 close.
        // 순서 (todo R2.M4): (1) save [PR-3 도입] → (2) floating close → (3) Dispose → (4) Close.
        CloseAllFloatingWindows();

        await _vm.DisposeLlmChatAsync();
        // 다음 message pump cycle 에서 close. 같은 cycle 안 Close() 는 IsClosing race 로 throw 가능.
        // fire-and-forget 의도 — DispatcherOperation 결과 무시.
        _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
    }

    // v7 PR-2b — Window 종료 직전 dockManager 의 floating anchor 들 일괄 close.
    // anchor.IsFloating=true 인 항목 Hide → AvalonDock 가 빈 floating window 자동 정리.
    // LlmChat 의 경우 SyncLlmChatAnchorFromVm 의 가드를 거치는 대신 직접 Hide (Window 종료 path).
    private void CloseAllFloatingWindows()
    {
        if (dockManager.Layout == null) return;
        var floatingAnchors = dockManager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .Where(a => a.IsFloating)
            .ToList();
        foreach (var anchor in floatingAnchors)
            anchor.Hide();
    }

    private static readonly string[] SupportedExtensions =
        [FileExtensions.Sdf, FileExtensions.Json, FileExtensions.Aasx, FileExtensions.Mermaid, FileExtensions.MermaidAlt];

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
        return null;
    }

    // ── Drag overlay watchdog ────────────────────────────────────────
    // WPF drop target 측엔 drag cancel (ESC) / 외부 release 이벤트가 없어
    // DragEnter 후 DragLeave/Drop 가 둘 다 발화 안 되는 케이스에서 overlay stuck.
    // 안전망: 짧은 폴링 타이머가 (1) 마우스 버튼 release (2) 일정 시간 동안 DragOver 미수신
    // 둘 중 하나라도 잡으면 overlay 자동 collapse.
    private DispatcherTimer? _overlayWatchdog;
    private DateTime _lastDragOverAt;
    // ESC 즉시 감지를 위해 짧게. 정상 drag 중엔 tick 당 일 거의 없음.
    private static readonly TimeSpan OverlayWatchdogInterval = TimeSpan.FromMilliseconds(60);
    private const int VkEscape = 0x1B;
    private const int VkLButton = 0x01;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Drag operation 중엔 mouse capture 가 OLE 측에 있어 <see cref="Mouse.GetPosition"/> 이
    /// stale/부정확한 좌표 반환 → false-positive "outside window" 로 overlay 가 토글되며 깜빡임.
    /// 대신 OS level 의 <c>GetCursorPos</c> + <c>PointFromScreen</c> 으로 정확한 hit-test.
    /// </summary>
    private bool IsCursorOutsideWindow()
    {
        if (!GetCursorPos(out var cursorPos)) return false; // 실패 시 보수적으로 inside 간주
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

    /// <summary>
    /// Drag 종료 조건들을 폴링으로만 판단 — WPF 의 DragLeave 는 자식 컨트롤 transition 시
    /// 부모로 bubble 되어 false-fire 가 잦아 신뢰할 수 없음 (가만히 있어도 layout 변화로
    /// hit-test 가 바뀌면 leave 가 토글되며 깜빡임). 폴링이 단일 진실원.
    /// </summary>
    private void OverlayWatchdog_Tick(object? sender, EventArgs e)
    {
        // ESC — drag source 가 cancel. drop target 측엔 별도 이벤트 안 옴.
        if ((GetAsyncKeyState(VkEscape) & 0x8000) != 0)
        {
            HideDragOverlay();
            return;
        }

        // Mouse left button release — WPF Mouse.LeftButton 은 OLE drag 중 stale (false-released)
        // 가 잦아 깜빡임 원흉이었음. Win32 GetAsyncKeyState 가 OS level 실시간 상태라 정확.
        if ((GetAsyncKeyState(VkLButton) & 0x8000) == 0)
        {
            HideDragOverlay();
            return;
        }

        // Mouse 가 window 밖으로 나감 — OS level cursor 위치로 hit-test.
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
            // 매 frame sync — Enter 누락 / 자식 transition 후에도 시각 복귀 보장.
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
        // DragLeave 는 신뢰하지 않음 — 자식 transition / layout 변화로 false-fire 가 잦아 깜빡임 원인.
        // 진짜 leave (mouse 가 window 밖) 는 watchdog 의 mouse position 폴링이 잡음.
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

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowTheme();
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // LlmChat dispose 는 Window_Closing 에서 await 완료됨 (1d-4 D 정석 패턴).
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        llmChatAnchor.Hiding -= OnLlmChatHiding;

        // v7 PR-2a Q1 — anchor IsVisibleChanged listener 해지.
        explorerAnchor.IsVisibleChanged    -= OnAnchorIsVisibleChanged;
        simulationAnchor.IsVisibleChanged  -= OnAnchorIsVisibleChanged;
        propertyAnchor.IsVisibleChanged    -= OnAnchorIsVisibleChanged;
        historyAnchor.IsVisibleChanged     -= OnAnchorIsVisibleChanged;
        llmChatAnchor.IsVisibleChanged     -= OnAnchorIsVisibleChanged;

        // 트레이 아이콘 정리 — stale 잔존 방지.
        try { _trayService.Dispose(); } catch { /* ignore */ }
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
