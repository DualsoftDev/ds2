using System;
using System.ComponentModel;
using System.IO;
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
    // 은 PR-D5 에서 SyncWelcomeCanvasVisibility 로 처리.
    private SplitCanvasContainer? _workspacePane;

    // PR-D5 — DockHost ↔ VM 양방향 sync 의 재진입 가드.
    // done-dock-avalon.md §2 F3 박제 — visibility 변경이 4회+ 중복 raise 되는 DX 동작에 대한 절대 필수 가드.
    // 한쪽 방향 처리 중에 다른 방향 raise 가 와도 무시 (loop 차단).
    private bool _suppressAnchorSync;

    // PR-D6 — dock layout 영속화 경로. `%LOCALAPPDATA%\Promaker\dock-layout.xml`.
    // 사용자 의도 verbatim 박제 (todo-dock-devexpress.md §3 PR-D6): "%LOCALAPPDATA%\Promaker\dock-layout.xml".
    private static readonly string LayoutXmlPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Promaker", "dock-layout.xml");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // PR-D4 — 5 anchor + 2 document 등록. PR-D3 의 IDockManager (DockHost) API 사용.
        // ContentId / Title / Content / DefaultPosition 매핑은 done-dock-avalon.md §3.1 안 A + todo §9 의 PR-D3 spike 박제.
        var explorerPane = new ExplorerPane();
        var simulationPanel = new SimulationPanel { DataContext = _vm.Simulation };
        var propertyPanel = new PropertyPanel { DataContext = _vm.PropertyPanel };
        var historyPanel = new HistoryPanel();
        var llmChatPanel = new System.Windows.Controls.ContentControl();
        llmChatPanel.SetBinding(System.Windows.Controls.ContentControl.ContentProperty,
            new System.Windows.Data.Binding(nameof(MainViewModel.LlmChatVm)));
        var welcomeView = new WelcomeView();
        _workspacePane = new SplitCanvasContainer { MinHeight = 120 };

        dockHost.RegisterAnchor(new DockAnchor("explorer",   "Explorer",   explorerPane,        DockAnchorPosition.Left));
        dockHost.RegisterAnchor(new DockAnchor("simulation", "Simulation", simulationPanel,     DockAnchorPosition.BottomLeft));
        dockHost.RegisterAnchor(new DockAnchor("log",        "Log",        new AppLogView(),    DockAnchorPosition.BottomRight));
        dockHost.RegisterAnchor(new DockAnchor("properties", "Properties", propertyPanel,       DockAnchorPosition.RightTop));
        dockHost.RegisterAnchor(new DockAnchor("history",    "History",    historyPanel,        DockAnchorPosition.RightMiddle));
        dockHost.RegisterAnchor(new DockAnchor("llmchat",    "LLM Chat",   llmChatPanel,        DockAnchorPosition.RightBottom));

        dockHost.RegisterDocument(new DockAnchor("welcome", "Welcome",   welcomeView,      DockAnchorPosition.Document));
        dockHost.RegisterDocument(new DockAnchor("canvas",  "Workspace", _workspacePane,   DockAnchorPosition.Document));

        // PR-D5 — VM SSOT ↔ DockHost 양방향 wiring.
        //   VM → DockHost : VM.PropertyChanged 의 IsXxxVisible / IsLlmChatVisible / HasProject 에 반응.
        //   DockHost → VM : X 버튼 등 DX 자체 visibility 변경 → AnchorVisibilityChanged → VM property set.
        // 양방향 _suppressAnchorSync 가드로 loop 차단 (F3 박제).
        _vm.PropertyChanged += Vm_PropertyChanged;
        dockHost.AnchorVisibilityChanged += DockHost_AnchorVisibilityChanged;

        // 초기 동기화 — HasProject 의 현재 값에 따라 Welcome ↔ Canvas 즉시 설정.
        // done-dock-avalon.md §3.1 안 A: HasProject=false → Welcome 보임 / Canvas 숨김, true → 역전.
        SyncWelcomeCanvasVisibility();

        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;
    }

    /// <summary>
    /// VM property → DockHost 호출 (SSOT → View).
    /// IsLlmChatVisible: baseline 박제 보존 (ToggleLlmChat 의 consent 거부 / lazy 생성 그대로) — 본 핸들러는
    /// 단순히 변경된 값을 DockHost 에 통보. 4 anchor visibility 도 동일 패턴.
    /// HasProject: Welcome ↔ Canvas swap.
    /// </summary>
    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressAnchorSync) return;

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsLlmChatVisible):
                ApplyAnchorVisible("llmchat", _vm.IsLlmChatVisible);
                break;
            case nameof(MainViewModel.IsExplorerVisible):
                ApplyAnchorVisible("explorer", _vm.IsExplorerVisible);
                break;
            case nameof(MainViewModel.IsSimulationVisible):
                ApplyAnchorVisible("simulation", _vm.IsSimulationVisible);
                break;
            case nameof(MainViewModel.IsPropertiesVisible):
                ApplyAnchorVisible("properties", _vm.IsPropertiesVisible);
                break;
            case nameof(MainViewModel.IsHistoryVisible):
                ApplyAnchorVisible("history", _vm.IsHistoryVisible);
                break;
            case nameof(MainViewModel.IsLogVisible):
                ApplyAnchorVisible("log", _vm.IsLogVisible);
                break;
            case nameof(MainViewModel.HasProject):
                SyncWelcomeCanvasVisibility();
                break;
        }
    }

    private void ApplyAnchorVisible(string contentId, bool visible)
    {
        _suppressAnchorSync = true;
        try { dockHost.SetAnchorVisible(contentId, visible); }
        finally { _suppressAnchorSync = false; }
    }

    /// <summary>
    /// DockHost → VM 단방향 sync (X 버튼 등 DX 자체 visibility 변경 → SSOT 갱신).
    /// _suppressAnchorSync 로 VM → DockHost 진행 중인 raise 는 무시.
    /// </summary>
    private void DockHost_AnchorVisibilityChanged(object? sender, Promaker.Dock.DockAnchorVisibilityChangedEventArgs e)
    {
        if (_suppressAnchorSync) return;
        _suppressAnchorSync = true;
        try
        {
            switch (e.ContentId)
            {
                case "llmchat":    _vm.IsLlmChatVisible    = e.IsVisible; break;
                case "explorer":   _vm.IsExplorerVisible   = e.IsVisible; break;
                case "simulation": _vm.IsSimulationVisible = e.IsVisible; break;
                case "properties": _vm.IsPropertiesVisible = e.IsVisible; break;
                case "history":    _vm.IsHistoryVisible    = e.IsVisible; break;
                case "log":        _vm.IsLogVisible        = e.IsVisible; break;
                // welcome / canvas 는 HasProject SSOT 가 SyncWelcomeCanvasVisibility 로 일방 관리 — 무시.
            }
        }
        finally { _suppressAnchorSync = false; }
    }

    /// <summary>
    /// HasProject SSOT → welcome / canvas document 가시성.
    /// done-dock-avalon.md §3.1 안 A: false → Welcome 보임 / Canvas 숨김, true → 역전.
    /// PR-D6 — 호출자가 이미 _suppressAnchorSync 안일 수도 있어 본문은 guard 없는 단순 적용,
    /// guard 책임은 호출자 (외부 호출 path 는 SyncWelcomeCanvasVisibility 가 wrapping).
    /// </summary>
    private void SyncWelcomeCanvasVisibility()
    {
        _suppressAnchorSync = true;
        try { SyncWelcomeCanvasVisibilityNoGuard(); }
        finally { _suppressAnchorSync = false; }
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
        // PR-D6 — dock layout 복원. WPF event 순서상 자식 (DockHost) Loaded 가 먼저 발화 →
        // DockHost 의 ItemIsVisibleChanged hook 이 이미 등록된 상태이므로 Restore 시 4 anchor visibility
        // 변경에 따른 raise 가 외부 (Vm_PropertyChanged) 로 누설될 수 있음.
        // → `_suppressAnchorSync=true` guard 안에서 Restore 후 5 property 강제 sync 로 raise loop 차단 + 정합 보장.
        // (PR-D5 검열 Minor 1 박제 해소 — `IsAnchorVisible` API 본 PR 에서 처음 활용.)
        RestoreDockLayoutAndSyncVm();

        if (App.StartupFilePath is { } path)
        {
            App.StartupFilePath = null;
            _vm.OpenFilePath(path);
        }
    }

    /// <summary>
    /// PR-D6 — dock layout xml 복원 후 VM 의 anchor visibility property 강제 sync.
    /// 처리 순서 (todo-dock-devexpress.md §9 PR-D6 step 3 안전 패턴 박제):
    ///   1. `_suppressAnchorSync=true` set — Restore 가 발화하는 ItemIsVisibleChanged raise loop 차단.
    ///   2. `dockHost.RestoreLayout(...)` — 파일 없음 / parse 실패 시 default 유지.
    ///   3. 4 anchor property 강제 sync (`IsAnchorVisible` API 활용 — PR-D5 검열 Minor 1 해소).
    ///   4. LlmChat 만 별도 처리 — baseline 박제 §5 의 consent 흐름 보존을 위해 Restore 결과 무시 + false 강제.
    ///      (사용자가 LLM Chat 버튼 click 시 ToggleLlmChat 의 consent 검사를 거쳐 정상 흐름 진입.)
    ///   5. HasProject SSOT 정합 — Welcome / Canvas 는 Restore 결과 무시 + SyncWelcomeCanvasVisibility 재적용.
    ///   6. `_suppressAnchorSync=false`.
    /// </summary>
    private void RestoreDockLayoutAndSyncVm()
    {
        _suppressAnchorSync = true;
        try
        {
            dockHost.RestoreLayout(LayoutXmlPath);

            // 5 anchor — Restore 결과를 VM property 로 강제 sync.
            _vm.IsExplorerVisible   = dockHost.IsAnchorVisible("explorer");
            _vm.IsSimulationVisible = dockHost.IsAnchorVisible("simulation");
            _vm.IsPropertiesVisible = dockHost.IsAnchorVisible("properties");
            _vm.IsHistoryVisible    = dockHost.IsAnchorVisible("history");
            _vm.IsLogVisible        = dockHost.IsAnchorVisible("log");

            // LlmChat — baseline 박제 §5 (consent 흐름 + LlmChatVm lazy 생성) 보존 의무 → Restore 결과 무시.
            // Restore 가 llmchat=Closed=false (visible) 로 복원했더라도 LlmChatVm 은 아직 null 일 수 있고,
            // consent 검사도 통과 안 됨. 사용자 click 시 ToggleLlmChat 가 정상 흐름 (consent + lazy 생성) 진입.
            dockHost.SetAnchorVisible("llmchat", false);
            _vm.IsLlmChatVisible = false;

            // Welcome / Canvas — HasProject SSOT 가 일방 관리. Restore 결과를 무시하고 현재 HasProject 로 재적용.
            SyncWelcomeCanvasVisibilityNoGuard();
        }
        finally { _suppressAnchorSync = false; }
    }

    /// <summary>
    /// <see cref="SyncWelcomeCanvasVisibility"/> 의 guard 미적용 버전.
    /// 호출자가 이미 `_suppressAnchorSync=true` 안에 있을 때 사용 (이중 set 회피).
    /// 사용자 의도 박제: HasProject=false 시 Log 제외 4 anchor (Explorer/Properties/History/Simulation) 자동 hide,
    /// HasProject=true 시 자동 show. LlmChat 은 baseline §5 보존 (consent 흐름), Log 는 시스템 로그라 무관.
    /// VM property 도 함께 sync 하여 보기 메뉴 체크박스 UI 와 일관.
    /// </summary>
    private void SyncWelcomeCanvasVisibilityNoGuard()
    {
        var hasProject = _vm.HasProject;
        dockHost.SetAnchorVisible("welcome", !hasProject);
        dockHost.SetAnchorVisible("canvas", hasProject);

        // 4 anchor 도 HasProject 따라 자동 show/hide.
        dockHost.SetAnchorVisible("explorer", hasProject);
        dockHost.SetAnchorVisible("properties", hasProject);
        dockHost.SetAnchorVisible("history", hasProject);
        dockHost.SetAnchorVisible("simulation", hasProject);
        _vm.IsExplorerVisible = hasProject;
        _vm.IsPropertiesVisible = hasProject;
        _vm.IsHistoryVisible = hasProject;
        _vm.IsSimulationVisible = hasProject;

        // 상단 ribbon 4 section (프로젝트/편집/연결/시뮬레이션) 도 HasProject 따라 자동 show/hide.
        // 파일/기타 section 은 HasProject 무관 (NewProject/Open/Save/보기/설정 등 항상 필요).
        _vm.IsToolbarProjectVisible = hasProject;
        _vm.IsToolbarEditVisible = hasProject;
        _vm.IsToolbarConnectVisible = hasProject;
        _vm.IsToolbarSimulationVisible = hasProject;
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

        // PR-D6 — 사용자 의도 verbatim: "`Window_Closing` 의 `_llmChatDisposed=true` 직후 Save".
        // `%LOCALAPPDATA%\Promaker\dock-layout.xml` 박제. 상위 디렉토리는 DockHost.SaveLayout 안에서 자동 생성.
        dockHost.SaveLayout(LayoutXmlPath);

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
