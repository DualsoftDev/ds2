using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using Ds2.Core;
using log4net;
using Promaker.Presentation;
using Promaker.Services;
using Promaker.ViewModels;

namespace Promaker;

public partial class MainWindow : Window
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MainWindow));
    private readonly MainViewModel _vm = new();
    private readonly TrayService _trayService = new();
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int WmNcHitTest = 0x0084;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int FloatingBottomCornerGripHeightPx = 44;
    private const int FloatingBottomCornerGripWidthPx = 56;
    private const int FloatingBottomEdgeGripHeightPx = 12;
    private static readonly HashSet<IntPtr> FloatingGripHookedHandles = [];

    // v7 PR-2a — VM ↔ View 동기화 재진입 가드. spike 결과 IsVisibleChanged 가 Hide()/Show() 1회당 3~4회 raise.
    private bool _suppressLlmChatSync;
    private bool _dockPaneExtentUpdateQueued;
    private bool _inDockPaneUpdate;
    private int _dockTraceSeq;

    // 트레이 컨텍스트 메뉴 "Promaker 종료" 에서 트리거된 Close 인지 표식. true 일 때
    // Window_Closing 의 "Monitoring + 실PLC + 시뮬중 → 트레이 재숨김" 가드를 우회 — 트레이 종료는 진짜 종료.
    private bool _exitingFromTray;

    // v7 PR-2a Q1 — 빈 column 자동 collapse 시 복원할 default 폭/높이.
    private static readonly GridLength ExplorerDefaultW = new(320);
    private static readonly GridLength SimulationDefaultH = new(200);
    private static readonly GridLength HistoryDefaultH = new(220);
    private static readonly GridLength RightDefaultW = new(280);
    // v10 hotfix — llmChatPane / propertyPane 은 fill (마지막 pane) 이라 명시적 default 없음 → Star 로 복원.
    private static readonly GridLength StarLength = new(1, GridUnitType.Star);
    private static readonly GridLength ZeroLength = new(0);

    // floating → docked 복원 wiring — AvalonDock 공개 hook 사용.
    // 공식 동작:
    //   - `Dock()` 는 PreviousContainer 가 없으면 LayoutAnchorable.InternalDock() 으로 fallback.
    //   - fallback 은 활성/우측/첫 pane 순으로 선택하므로 Properties 가 Explorer tab 으로 들어갈 수 있음.
    //   - `InternalDock()` / `Show()` 는 DockingManager.LayoutUpdateStrategy.BeforeInsertAnchorable 을 먼저 호출.
    // 해결: main dock layout 안의 마지막 ILayoutPane/index 를 별도 기록하고, Dock 명령 및 BeforeInsertAnchorable 에서 그 위치로 직접 삽입.
    private readonly Dictionary<LayoutAnchorable, DockAnchorPlacement> _dockPlacements = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        // v7 PR-2a — _vm.FocusNameEditorRequested 슬롯 set 은 PropertyPane.xaml.cs 의 Loaded/Unloaded 자가 등록으로 이동.
        // viewport 콜백은 SplitCanvasContainer.OnDataContextChanged에서 각 pane에 연결됩니다.

        // B-1 — 보기 메뉴 (`Apps/Promaker/Docs/todo-dock-layout.md` §3.1 Q2) anchor IsVisible TwoWay binding 용 VM expose.
        // LlmChat 은 IsLlmChatVisible SSOT 별도라 제외 (toolbar 의 LLM 토글 버튼이 별도 UI).
        _vm.ExplorerAnchor   = explorerAnchor;
        _vm.PropertyAnchor   = propertyAnchor;
        _vm.HistoryAnchor    = historyAnchor;
        _vm.SimulationAnchor = simulationAnchor;

        dockManager.LayoutUpdateStrategy = new DockLayoutUpdateStrategy(this);
        dockManager.ContentFloating += OnDockManagerContentFloating;
        dockManager.ContentFloated += OnDockManagerContentFloated;
        dockManager.ContentDocking += OnDockManagerContentDocking;
        dockManager.ContentDocked += OnDockManagerContentDocked;
        dockManager.Layout.Updated += OnDockLayoutUpdated;
        // v14 — DockingManager.Resources 의 implicit Style 이 별도 Window 인스턴스로 spawn 되는
        // floating window 의 resource resolution chain 에 도달하지 못해 v12/v13 의 Topmost/ResizeBorderThickness/
        // OwnedByDockingManagerWindow/Background setter 가 무력화됨. 공식 hook 으로 직접 set.
        dockManager.LayoutFloatingWindowControlCreated += OnFloatingWindowCreated;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        llmChatAnchor.Hiding += OnLlmChatHiding;

        // Tray 전환 콜백 wiring — Monitoring + RealPLC 시 PLAY 가 RequestTrayHide 호출.
        // STOP / 모드 전환 시 RequestTrayRestore 호출.
        _vm.Simulation.Tray.RequestTrayHide = () =>
        {
            var tooltip = $"Promaker — Monitoring 동작중 (port {_vm.Simulation.MonitoringHubAddress})";
            _trayService.HideToTray(this, tooltip);
        };
        _vm.Simulation.Tray.RequestTrayRestore = () => _trayService.RestoreWindow();

        // Monitoring + 실 PLC PLAY 가 성공하면 DSPilot 웹 대시보드 자동 실행.
        // 트레이 컨텍스트 메뉴 "DSPilot 접속" 도 동일 동작 → 동일 launcher 사용.
        _vm.Simulation.Tray.RequestDspilotOpen = DspilotLauncher.Open;
        _trayService.DspilotOpenRequested += DspilotLauncher.Open;

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
            // 트레이 컨텍스트 메뉴 "Promaker 종료" — 진짜 종료. _exitingFromTray 로 Window_Closing 의
            // 트레이 재숨김 가드를 우회 (가드 없으면 Monitoring+실PLC+시뮬중 상태에서 다시 트레이로 들어가 종료 불가).
            Dispatcher.BeginInvoke(() =>
            {
                _exitingFromTray = true;
                _trayService.RestoreWindow(); // dirty 저장 확인 다이얼로그가 보이도록 윈도우 복원
                Close();
            });
        };

        // v7 PR-2a Q1 — 빈 column 자동 collapse listener. 5개 anchor 모두 동일 핸들러.
        explorerAnchor.IsVisibleChanged += OnAnchorIsVisibleChanged;
        simulationAnchor.IsVisibleChanged += OnAnchorIsVisibleChanged;
        propertyAnchor.IsVisibleChanged += OnAnchorIsVisibleChanged;
        historyAnchor.IsVisibleChanged += OnAnchorIsVisibleChanged;
        llmChatAnchor.IsVisibleChanged += OnAnchorIsVisibleChanged;

        // 매 docked 상태에서 pane/index capture — drag-floating 후 Dock() 시 직전 dock 위치 복원 보장.
        foreach (var a in new[] { explorerAnchor, simulationAnchor, propertyAnchor, historyAnchor, llmChatAnchor })
        {
            a.PropertyChanged += OnAnchorPropertyChanged;
            CaptureDockPlacement(a);  // 초기 1회 (안전망 — Loaded 에도 다시)
        }

        TraceDock("ctor init", includeTree: true);

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

    // HasProject 토글 시 welcomeDoc / canvasDoc 중 하나만 LayoutDocumentPane 에 attach +
    // `Apps/Promaker/Docs/todo-dock-layout.md` §3.1 안 A 명세대로 보조 anchor 4종 (Explorer/Property/History/Simulation) 도 함께 토글.
    // LayoutDocument.IsVisible 은 XAML/C# 양쪽 모두 read-only (CS0200) 라 anchor 와 달리 set 불가능 →
    // workspaceDocs.Children Add/Remove 동적 관리로 처리.
    //
    // 외부 reviewer M1 수용 — Welcome 모드 (HasProject=false) 에 보조 anchor 가 노출되면 동일 문서 §3.1 안 A 명세 drift
    // + 이전 WelcomeLayout (전용 안내 화면) 대비 UX 회귀. LlmChat 은 IsLlmChatVisible SSOT 가 별도라 제외.
    //
    // 재진입 가드 없음 — anchor.IsVisible set 의 IsVisibleChanged 가 OnAnchorIsVisibleChanged 만 부르고
    // VM ↔ View 역방향 없음. LlmChat 의 _suppressLlmChatSync 가드와 비대칭이 의도된 부분.
    //
    // PR-3 잔여 우려: XmlLayoutSerializer 도입 시 detach 된 LayoutDocument 인스턴스가
    // serialize 직전에 attach 되어 있어야 round-trip 정합. PR-3 진입 시 spike 1차 검증 필요.
    private void SyncWelcomeCanvasVisibility()
    {
        var children = workspaceDocs.Children;
        var (target, other) = _vm.HasProject ? (canvasDoc, welcomeDoc) : (welcomeDoc, canvasDoc);
        if (children.Contains(other)) children.Remove(other);
        if (!children.Contains(target)) children.Add(target);
        target.IsActive = true;
        target.IsSelected = true;

        explorerAnchor.IsVisible    = _vm.HasProject;
        propertyAnchor.IsVisible    = _vm.HasProject;
        historyAnchor.IsVisible     = _vm.HasProject;
        simulationAnchor.IsVisible  = _vm.HasProject;
        QueueDockPaneExtentUpdate();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // v7 PR-2a (검열 Major 1) — XAML 트리 / LayoutDocumentPane SelectedContentIndex 완전 unwound 이후 시점.
        // 생성자 InitializeComponent 직후 호출은 nondeterministic 위험.
        SyncWelcomeCanvasVisibility();
        // hotfix — XAML 의 `IsVisible="False"` 가 4.74.1 의 LayoutAnchorable parent attach 시점 race 로
        // 초기 1 frame 동안 visible 노출되는 회귀. IsLlmChatVisible (SSOT) 기준으로 다시 강제 set.
        SyncLlmChatAnchorFromVm();
        // 초기 collapse 적용 (XAML 기본값 — llmChatAnchor IsVisible=False).
        UpdateDockPaneExtents();
        // XAML 트리 완성 후 pane/index 안전망 recapture.
        foreach (var a in new[] { explorerAnchor, simulationAnchor, propertyAnchor, historyAnchor, llmChatAnchor })
            CaptureDockPlacement(a);

        // v15 — Welcome/Canvas 위/아래 흰선 hotfix. Metro theme 의 LayoutDocumentPaneControl Style 이 Background
        // setter 누락 → 시스템 default(밝은 회색) 적용. XAML implicit Style override (DockingManager.Resources /
        // Window.Resources) 가 Metro DefaultStyleKey lookup 보다 약해 미적용 → visual tree walk 로 직접 set.
        // 초기 1회 + LayoutChanged 마다 재적용.
        ApplyDocumentPaneBackgrounds();
        dockManager.LayoutChanged += OnDockManagerLayoutChanged;

        if (App.StartupFilePath is { } path)
        {
            App.StartupFilePath = null;
            _vm.OpenFilePath(path);
        }
    }

    // named handler — anonymous lambda 로 등록하면 MainWindow_Closed 에서 unsubscribe 불가 (이름이 없으므로).
    // 종료 직후 ApplicationIdle 큐에 남은 콜백이 disposed visual 을 순회할 race 도 동일 사유로 차단.
    private void OnDockManagerLayoutChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(ApplyDocumentPaneBackgrounds),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

    private void ApplyDocumentPaneBackgrounds()
    {
        if (TryFindResource("PrimaryBackgroundBrush") is not Brush bg) return;
        foreach (var ctl in EnumerateVisualDescendants(dockManager))
        {
            var typeName = ctl.GetType().Name;
            if (typeName is "LayoutDocumentPaneControl" or "LayoutDocumentPaneGroupControl" or "LayoutDocumentControl"
                && ctl is System.Windows.Controls.Control c)
            {
                c.Background = bg;
                c.BorderBrush = bg;
                c.BorderThickness = new Thickness(0);
            }

            if (ctl is System.Windows.Controls.Border { Name: "ContentPanel" } contentPanel
                && HasVisualAncestor(contentPanel, "LayoutDocumentPaneControl"))
            {
                contentPanel.Background = bg;
                contentPanel.BorderBrush = bg;
                contentPanel.BorderThickness = new Thickness(0);
                contentPanel.Padding = new Thickness(0);
            }
            else if (ctl is System.Windows.Controls.ContentPresenter { Name: "PART_SelectedContentHost" } contentHost
                && HasVisualAncestor(contentHost, "LayoutDocumentPaneControl"))
            {
                contentHost.Margin = new Thickness(0);
            }
        }
    }

    private static IEnumerable<DependencyObject> EnumerateVisualDescendants(DependencyObject root)
    {
        if (root == null) yield break;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in EnumerateVisualDescendants(child)) yield return d;
        }
    }

    private static bool HasVisualAncestor(DependencyObject child, string typeName)
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent.GetType().Name == typeName)
                return true;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return false;
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
        // 진짜 종료는 트레이 컨텍스트 메뉴 "Promaker 종료" 사용 (그 경로는 _exitingFromTray=true 로 이 가드 우회).
        // PLAY 시점과 동일한 TrayConsentDialog 재사용 — "다시 묻지 않기" 도 공유.
        if (!_exitingFromTray
            && _vm.Simulation.SelectedRuntimeMode == RuntimeMode.Monitoring
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

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // LlmChat dispose 는 Window_Closing 에서 await 완료됨 (1d-4 D 정석 패턴).
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
        dockManager.ContentFloating -= OnDockManagerContentFloating;
        dockManager.ContentFloated -= OnDockManagerContentFloated;
        dockManager.ContentDocking -= OnDockManagerContentDocking;
        dockManager.ContentDocked -= OnDockManagerContentDocked;
        // PR-3 (XmlLayoutSerializer) 진입 시 dockManager.Layout 이 런타임 교체 가능 → null 가드.
        if (dockManager.Layout != null)
            dockManager.Layout.Updated -= OnDockLayoutUpdated;
        dockManager.LayoutChanged -= OnDockManagerLayoutChanged;
        dockManager.LayoutFloatingWindowControlCreated -= OnFloatingWindowCreated;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        llmChatAnchor.Hiding -= OnLlmChatHiding;

        // v7 PR-2a Q1 — anchor IsVisibleChanged listener 해지.
        explorerAnchor.IsVisibleChanged    -= OnAnchorIsVisibleChanged;
        simulationAnchor.IsVisibleChanged  -= OnAnchorIsVisibleChanged;
        propertyAnchor.IsVisibleChanged    -= OnAnchorIsVisibleChanged;
        historyAnchor.IsVisibleChanged     -= OnAnchorIsVisibleChanged;
        llmChatAnchor.IsVisibleChanged     -= OnAnchorIsVisibleChanged;

        foreach (var a in new[] { explorerAnchor, simulationAnchor, propertyAnchor, historyAnchor, llmChatAnchor })
            a.PropertyChanged -= OnAnchorPropertyChanged;

        // 트레이 아이콘 정리 — stale 잔존 방지.
        try { _trayService.Dispose(); } catch { /* ignore */ }
    }

}
