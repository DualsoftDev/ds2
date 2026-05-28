using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using log4net;
using Promaker.Controls;
using Promaker.Controls.Logging;
using Promaker.Dock;
using Promaker.Presentation;
using Promaker.ViewModels;

namespace Promaker;

public partial class MainWindow : Window
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MainWindow));
    private readonly MainViewModel _vm = new();
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    // PR-D4 — Welcome / Canvas SplitCanvasContainer 인스턴스 핸들. HasProject 토글 기반 visibility 정책 (Welcome ↔ Canvas)
    // 은 PR-D5 영역. PR-D4 에서는 두 document 모두 등록 + 기본 visibility 는 DX native 처리에 위임.
    private SplitCanvasContainer? _workspacePane;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // PR-D4 — 5 anchor + 2 document 등록. PR-D3 의 IDockManager (DockHost) API 사용.
        // ContentId / Title / Content / DefaultPosition 매핑은 done-dock-layout.md §3.1 안 A + todo §9 의 PR-D3 spike 박제.
        var explorerPane = new ExplorerPane();
        var simulationPanel = new SimulationPanel { DataContext = _vm.Simulation };
        var propertyPanel = new PropertyPanel { DataContext = _vm.PropertyPanel };
        var historyPanel = new HistoryPanel();
        var llmChatPanel = new System.Windows.Controls.ContentControl();
        llmChatPanel.SetBinding(System.Windows.Controls.ContentControl.ContentProperty,
            new System.Windows.Data.Binding(nameof(MainViewModel.LlmChatVm)));
        var welcomeView = new WelcomeView();
        _workspacePane = new SplitCanvasContainer { MinHeight = 120 };

        // PR-D5 위임: 보기 메뉴 (MainToolbarEtcContent.xaml 의 5 체크박스) 의 binding 은 현재 dead path —
        // PR-D5 가 IDockManager.IsAnchorVisible / SetAnchorVisible 로 재wiring 예정.
        dockHost.RegisterAnchor(new DockAnchor("explorer",   "Explorer",   explorerPane,    DockAnchorPosition.Left));
        dockHost.RegisterAnchor(new DockAnchor("simulation", "Simulation", simulationPanel, DockAnchorPosition.Bottom));
        dockHost.RegisterAnchor(new DockAnchor("properties", "Properties", propertyPanel,   DockAnchorPosition.RightTop));
        dockHost.RegisterAnchor(new DockAnchor("history",    "History",    historyPanel,    DockAnchorPosition.RightMiddle));
        dockHost.RegisterAnchor(new DockAnchor("llmchat",    "LLM Chat",   llmChatPanel,    DockAnchorPosition.RightBottom));

        dockHost.RegisterDocument(new DockAnchor("welcome", "Welcome",   welcomeView,      DockAnchorPosition.Document));
        dockHost.RegisterDocument(new DockAnchor("canvas",  "Workspace", _workspacePane,   DockAnchorPosition.Document));

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

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
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

        // Monitoring + 실 PLC 상태로 동작 중이어도 Promaker WPF 는 그대로 닫는다 — 모니터링은
        // Promaker.Agent (Windows Service) 가 별도 컨텍스트에서 계속 진행하고, 사용자에게는
        // Promaker.AgentTray 가 상태/제어를 제공한다. WPF 창 = 편집 UI, 닫혀도 모니터링은 유지.

        // --autostart-llm 측정 모드 = mutation 변경 자동 폐기 (Closing dialog skip).
        // 측정 끝난 후 fsx 가 CloseMainWindow 보내면 dialog 없이 진행 → log4net flush + DisposeLlmChatAsync 정상.
        if (!App.StartupAutoOpenLlm && !_vm.ConfirmDiscardChangesPublic())
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _llmChatDisposed = true;

        await _vm.DisposeLlmChatAsync();
        // 다음 message pump cycle 에서 close. 같은 cycle 안 Close() 는 IsClosing race 로 throw 가능.
        // fire-and-forget 의도 — DispatcherOperation 결과 무시.
        _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // LlmChat dispose 는 Window_Closing 에서 await 완료됨 (1d-4 D 정석 패턴).
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
    }

}
