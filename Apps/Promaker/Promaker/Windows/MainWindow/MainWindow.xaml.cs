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
    // done-dock-devexpress.md §3 PR-D5 (_suppressAnchorSync) — visibility 변경이 4회+ 중복 raise 되는 DX 동작에 대한 절대 필수 가드.
    // 한쪽 방향 처리 중에 다른 방향 raise 가 와도 무시 (loop 차단).
    private bool _suppressAnchorSync;

    // PR-A3 — AvalonDock 재교체에 따른 layout xml schema 변경. 구 `dock-layout.xml` (DX <XtraSerializer>
    // 포맷) 과 충돌 회피 위해 파일명에 -v2 suffix 추가. 구 파일은 그대로 두고(touch 안 함) 새 파일을 별도 사용:
    //   - 처음 실행: -v2 파일 없음 → XAML 박제 default layout 으로 시작 (안전한 fresh start).
    //   - 종료 시: SaveLayout 이 -v2 파일에 AvalonDock 포맷으로 저장.
    //   - 이후 실행: -v2 파일을 RestoreLayout 가 읽어 사용자 dock 변경 보존.
    // 향후 layout schema 변경 시 -v3 등으로 bump.
    private static readonly string LayoutXmlPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Promaker", "dock-layout-v2.xml");

    // --review mn5 — 5 standard anchor (explorer/simulation/properties/history/log) 의 (contentId, vmProp, get, set)
    // 통합 table. Vm_PropertyChanged / DockHost_AnchorVisibilityChanged / RestoreDockLayoutAndSyncVm 의 switch+magic
    // string 중복을 단일 source 로 통합. LlmChat 은 baseline §5 (consent 흐름 + lazy 생성) 보존 별도.
    private record AnchorSync(string ContentId, string VmPropertyName, Func<bool> Get, Action<bool> Set);
    private readonly AnchorSync[] _anchorSyncs;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // PR-A6 — anchor sync table. Simulation dock 삭제 + Gantt / StatusMonitor 독립화.
        //   - Gantt + StatusMonitor 는 HasProject 게이트만 공유하므로 동일 VM 플래그(IsSimulationVisible)에 매핑.
        _anchorSyncs = new[]
        {
            new AnchorSync("explorer",      nameof(MainViewModel.IsExplorerVisible),   () => _vm.IsExplorerVisible,   v => _vm.IsExplorerVisible   = v),
            new AnchorSync("gantt",         nameof(MainViewModel.IsSimulationVisible), () => _vm.IsSimulationVisible, v => _vm.IsSimulationVisible = v),
            new AnchorSync("statusMonitor", nameof(MainViewModel.IsSimulationVisible), () => _vm.IsSimulationVisible, v => _vm.IsSimulationVisible = v),
            new AnchorSync("properties",    nameof(MainViewModel.IsPropertiesVisible), () => _vm.IsPropertiesVisible, v => _vm.IsPropertiesVisible = v),
            new AnchorSync("history",       nameof(MainViewModel.IsHistoryVisible),    () => _vm.IsHistoryVisible,    v => _vm.IsHistoryVisible    = v),
            new AnchorSync("log",           nameof(MainViewModel.IsLogVisible),        () => _vm.IsLogVisible,        v => _vm.IsLogVisible        = v),
        };

        // PR-A6 — Simulation dock 삭제. 하단 단일 pane 에 Log(첫 탭) / Gantt Chart / Status Monitor 3개를 tabbed 로 등록.
        var explorerPane = new ExplorerPane();
        var ganttChart   = new GanttChartControl { DataContext = _vm.Simulation.GanttChart };
        var statusMonitor = new StatusMonitorPanel { DataContext = _vm.Simulation };
        var propertyPanel = new PropertyPanel { DataContext = _vm.PropertyPanel };
        var historyPanel = new HistoryPanel();
        var welcomeView = new WelcomeView();
        _workspacePane = new SplitCanvasContainer { MinHeight = 120 };

        dockHost.RegisterAnchor(new DockAnchor("explorer",      "Explorer",       explorerPane,    DockAnchorPosition.Left));
        // 하단 tabbed 순서: Log → Gantt Chart → Status Monitor. 첫 등록 anchor 가 활성 탭 (Log).
        dockHost.RegisterAnchor(new DockAnchor("log",           "Log",            new AppLogView(),DockAnchorPosition.Bottom));
        dockHost.RegisterAnchor(new DockAnchor("gantt",         "Gantt Chart",    ganttChart,      DockAnchorPosition.Bottom));
        dockHost.RegisterAnchor(new DockAnchor("statusMonitor", "Status Monitor", statusMonitor,   DockAnchorPosition.Bottom));
        dockHost.RegisterAnchor(new DockAnchor("properties",    "Properties",     propertyPanel,   DockAnchorPosition.RightTop));
        dockHost.RegisterAnchor(new DockAnchor("history",       "History",        historyPanel,    DockAnchorPosition.RightMiddle));

        dockHost.RegisterDocument(new DockAnchor("welcome", "Welcome",   welcomeView,      DockAnchorPosition.Document));
        dockHost.RegisterDocument(new DockAnchor("canvas",  "Workspace", _workspacePane,   DockAnchorPosition.Document));

        // PR-A3 — 모든 Register* 직후, 어떤 visibility 조작도 가하기 전에 default layout 스냅샷 캡쳐.
        // 이후 설정 다이얼로그 '레이아웃 초기화' 버튼이 ResetDockLayoutToDefault() → dockHost.ResetToDefaultLayout()
        // 로 본 스냅샷을 즉시 복원 (재시작 불요).
        dockHost.CaptureDefaultLayout();

        // PR-D5 — VM SSOT ↔ DockHost 양방향 wiring.
        //   VM → DockHost : VM.PropertyChanged 의 IsXxxVisible / IsLlmChatVisible / HasProject 에 반응.
        //   DockHost → VM : X 버튼 등 DX 자체 visibility 변경 → AnchorVisibilityChanged → VM property set.
        // 양방향 _suppressAnchorSync 가드로 loop 차단 (F3 박제).
        _vm.PropertyChanged += Vm_PropertyChanged;
        dockHost.AnchorVisibilityChanged += DockHost_AnchorVisibilityChanged;
        // PR-A4 — anchor caption Help 뱃지 삭제. AnchorHelpRequested 구독 제거 (도움말은 리본 버튼 → HelpNavigator 직접).

        // 초기 동기화 — HasProject 의 현재 값에 따라 Welcome ↔ Canvas 즉시 설정.
        // done-dock-devexpress.md §3 PR-D5 (HasProject 토글): HasProject=false → Welcome 보임 / Canvas 숨김, true → 역전.
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

        if (e.PropertyName == nameof(MainViewModel.HasProject)) { SyncWelcomeCanvasVisibility(); return; }

        foreach (var b in _anchorSyncs)
            if (b.VmPropertyName == e.PropertyName) { ApplyAnchorVisible(b.ContentId, b.Get()); return; }
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
            // welcome/canvas 는 HasProject SSOT 일방 관리라 무시, 나머지 5 anchor table lookup.
            foreach (var b in _anchorSyncs)
                if (b.ContentId == e.ContentId) { b.Set(e.IsVisible); return; }
        }
        finally { _suppressAnchorSync = false; }
    }

    /// <summary>
    /// HasProject SSOT → welcome / canvas document 가시성.
    /// done-dock-devexpress.md §3 PR-D5 (HasProject 토글): false → Welcome 보임 / Canvas 숨김, true → 역전.
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
        // PR-A3 — RestoreLayout 복구. 단, layout xml 경로는 -v2 로 bump 되어(LayoutXmlPath 참조) 구 DX 포맷 파일은
        // 자동으로 무시됨. 새 버전 (AvalonDock) 으로 저장한 -v2 파일만 복원 대상이라 schema 충돌이 발생하지 않음.
        // 처음 실행 시는 -v2 파일이 없어 RestoreLayout 가 silent skip → XAML 박제 default layout 으로 시작.
        _suppressAnchorSync = true;
        try
        {
            dockHost.RestoreLayout(LayoutXmlPath);

            // 5 anchor — Restore 결과를 VM property 로 강제 sync.
            foreach (var b in _anchorSyncs)
                b.Set(dockHost.IsAnchorVisible(b.ContentId));

            // Welcome / Canvas — HasProject SSOT 가 일방 관리. Restore 결과 무시 + 현재 HasProject 로 재적용.
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

        // 5 anchor 도 HasProject 따라 자동 show/hide. PR-A6 — simulation 단일 anchor → gantt + statusMonitor 분리.
        dockHost.SetAnchorVisible("explorer", hasProject);
        dockHost.SetAnchorVisible("properties", hasProject);
        dockHost.SetAnchorVisible("history", hasProject);
        dockHost.SetAnchorVisible("gantt", hasProject);
        dockHost.SetAnchorVisible("statusMonitor", hasProject);
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

    /// <summary>
    /// PR-A3 — 설정 다이얼로그 '레이아웃 초기화' 진입점. dockHost 의 default 스냅샷으로 즉시 복원 +
    /// 현재 HasProject 기준 visibility 재동기화. 재시작 불요. Window_Closing 의 SaveLayout 가 본 상태를 그대로 저장.
    /// </summary>
    public void ResetDockLayoutToDefault()
    {
        _suppressAnchorSync = true;
        try
        {
            dockHost.ResetToDefaultLayout();
            SyncWelcomeCanvasVisibilityNoGuard();
            // LlmChat 은 RegisterAnchor 가 lazy/consent 흐름 (baseline §5) 으로 별도 처리되므로 본 reset 시 강제 토글 안 함.
        }
        finally { _suppressAnchorSync = false; }
    }

    private bool _closeConfirmed;

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_closeConfirmed) return;

        if (!_vm.ConfirmDiscardChangesPublic())
        {
            e.Cancel = true;
            return;
        }

        _closeConfirmed = true;
        dockHost.SaveLayout(LayoutXmlPath);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= Vm_PropertyChanged;
        dockHost.AnchorVisibilityChanged -= DockHost_AnchorVisibilityChanged;
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
    }

    // PR-A4 — DockHost_AnchorHelpRequested 핸들러 제거. 도움말은 리본 우상단 단일 버튼이 HelpNavigator.NavigateCommand 를 직접 실행.

}
