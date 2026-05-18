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
        _vm.Simulation.RequestTrayHide = () =>
        {
            var tooltip = $"Promaker — Monitoring 동작중 (port {_vm.Simulation.MonitoringHubAddress})";
            _trayService.HideToTray(this, tooltip);
        };
        _vm.Simulation.RequestTrayRestore = () => _trayService.RestoreWindow();

        // Monitoring + 실 PLC PLAY 가 성공하면 DSPilot 웹 대시보드 자동 실행.
        // 트레이 컨텍스트 메뉴 "DSPilot 접속" 도 동일 동작 → 동일 launcher 사용.
        _vm.Simulation.RequestDspilotOpen = DspilotLauncher.Open;
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

    private void OnDockLayoutUpdated(object? sender, EventArgs e)
    {
        QueueDockPaneExtentUpdate();
    }

    private void OnDockManagerContentFloating(object? sender, ContentFloatingEventArgs e)
    {
        TraceDock($"ContentFloating content={ContentDesc(e.Content)}", e.Content as LayoutAnchorable, includeTree: true);
    }

    private void OnDockManagerContentFloated(object? sender, ContentFloatedEventArgs e)
    {
        TraceDock($"ContentFloated content={ContentDesc(e.Content)}", e.Content as LayoutAnchorable, includeTree: true);
    }

    private void OnDockManagerContentDocked(object? sender, ContentDockedEventArgs e)
    {
        TraceDock($"ContentDocked content={ContentDesc(e.Content)}", e.Content as LayoutAnchorable, includeTree: true);
        QueueDockPaneExtentUpdate();
        // floating window 가 close 되면서 OS focus 가 직전 응용 (e.g. VS / 브라우저) 으로 넘어가
        // MainWindow 가 뒤로 가려지는 회귀 차단. floating dispose timing 통과 위해 Background priority 로 defer.
        // Topmost 토글은 Windows SetForegroundWindow restriction 우회용 well-known 패턴.
        Dispatcher.BeginInvoke(new Action(BringMainWindowToFront), DispatcherPriority.Background);
    }

    private void BringMainWindowToFront()
    {
        if (!IsVisible) return;
        // Topmost=true → z-order 최상단으로 commit. 같은 메서드 안에서 즉시 false 로 되돌리면
        // OS 가 z-order 변경을 commit 하기 전에 해제되어 효과가 사라진다 → 한 dispatcher cycle 뒤 해제.
        // topmost 성질을 영구 유지하지는 않음 (사용자 의도).
        Topmost = true;
        Activate();
        Dispatcher.BeginInvoke(new Action(() => Topmost = false),
            DispatcherPriority.ApplicationIdle);
    }

    // VM → View 단방향 (3속성 동시 set). 재진입 가드로 IsVisibleChanged 중복 raise → VM 역류 차단.
    // Edge case (`Apps/Promaker/Docs/todo-dock-layout.md` §3.3): LlmChatVm==null (consent 거부) → 안전 가드로 hide 유지.
    private void SyncLlmChatAnchorFromVm()
    {
        if (_suppressLlmChatSync) return;
        _suppressLlmChatSync = true;
        try
        {
            bool show = _vm.IsLlmChatVisible && _vm.LlmChatVm != null;
            llmChatAnchor.IsVisible = show;
            llmChatAnchor.IsActive = show;
            llmChatAnchor.IsSelected = show;
        }
        finally { _suppressLlmChatSync = false; }
    }

    // v7 PR-2a Q1/F2 — anchor 가 hidden 시 그 anchor 의 LayoutAnchorablePane DockWidth/DockHeight 잔존 문제.
    // v18 — dock/float 중간 Parent=null 순간에 즉시 0 으로 바꾸면 AvalonDock drop target 이 그 0 값을 새 group
    // DockHeight/DockWidth 로 복사해 Properties/History 가 함께 사라질 수 있다. layout settle 이후 한 번만 반영한다.
    private void OnAnchorIsVisibleChanged(object? sender, EventArgs e)
    {
        TraceDock($"IsVisibleChanged anchor={ContentDesc(sender as LayoutContent)}", sender as LayoutAnchorable);
        QueueDockPaneExtentUpdate();
    }

    private void QueueDockPaneExtentUpdate()
    {
        // reentrance guard — UpdateDockPaneExtents 안의 DockWidth/Height/ComputeVisibility 재할당이
        // Layout.Updated 를 다시 발화시켜 동일 frame 안에서 추가 큐가 들어오는 feedback loop 차단.
        if (_dockPaneExtentUpdateQueued || _inDockPaneUpdate) return;
        _dockPaneExtentUpdateQueued = true;
        TraceDock("QueueDockPaneExtentUpdate queued");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _dockPaneExtentUpdateQueued = false;
            _inDockPaneUpdate = true;
            try
            {
                TraceDock("UpdateDockPaneExtents begin", includeTree: true);
                UpdateDockPaneExtents();
                TraceDock("UpdateDockPaneExtents end", includeTree: true);
            }
            finally
            {
                _inDockPaneUpdate = false;
            }
        }), DispatcherPriority.ApplicationIdle);
    }

    private void UpdateDockPaneExtents()
    {
        NormalizeDockLayoutAfterMutation();

        SetDockWidthIfAttached(explorerPane, ExplorerDefaultW);
        SetDockHeightIfAttached(simulationPane, SimulationDefaultH);
        SetDockHeightIfAttached(historyPane, HistoryDefaultH);
        // v10 hotfix — llmChatPane / propertyPane 은 명시적 DockHeight 없는 fill pane (마지막 위치).
        // anchor.IsVisible=false 만으로는 LayoutAnchorablePane 영역이 시각 잔존 (4.74.1 회귀) → pane DockHeight 도 toggle.
        SetDockHeightIfAttached(llmChatPane, StarLength);
        SetDockHeightIfAttached(propertyPane, StarLength);

        if (IsDockedInMainLayout(rightPanel))
        {
            var nextRight = HasVisibleDockedAnchorable(rightPanel) ? RightDefaultW : ZeroLength;
            if (rightPanel.DockWidth != nextRight) rightPanel.DockWidth = nextRight;
        }

        RecomputeDockVisibility();
    }

    private void NormalizeDockLayoutAfterMutation()
    {
        int removedGroups = RemoveEmptyAnchorablePaneGroups();
        RecomputeDockVisibility();
        if (removedGroups > 0)
            TraceDock($"NormalizeDockLayout removedEmptyAnchorablePaneGroups={removedGroups}", includeTree: true);
    }

    private int RemoveEmptyAnchorablePaneGroups()
    {
        var groups = dockManager.Layout?.Descendents()
            .OfType<LayoutAnchorablePaneGroup>()
            .Where(g => g.ChildrenCount == 0 && IsDockedInMainLayout(g))
            .ToArray();
        if (groups is not { Length: > 0 }) return 0;

        int removed = 0;
        foreach (var group in groups)
        {
            if (group.Parent is not ILayoutGroup parent) continue;
            int index = parent.IndexOfChild(group);
            if (index < 0) continue;

            parent.RemoveChildAt(index);
            removed++;
        }

        return removed;
    }

    private void RecomputeDockVisibility()
    {
        var layout = dockManager.Layout;
        if (layout == null) return;

        foreach (var element in layout.Descendents().OfType<ILayoutElement>().Reverse().ToArray())
            RecomputeElementVisibility(element);
    }

    private static void RecomputeElementVisibility(ILayoutElement element)
    {
        switch (element)
        {
            case LayoutAnchorablePane pane:
                pane.ComputeVisibility();
                break;
            case LayoutDocumentPane pane:
                pane.ComputeVisibility();
                break;
            case LayoutAnchorablePaneGroup group:
                group.ComputeVisibility();
                break;
            case LayoutDocumentPaneGroup group:
                group.ComputeVisibility();
                break;
            case LayoutPanel panel:
                panel.ComputeVisibility();
                break;
            case ILayoutElementWithVisibility visible:
                visible.ComputeVisibility();
                break;
        }
    }

    private void SetDockWidthIfAttached(LayoutAnchorablePane pane, GridLength visibleLength)
    {
        if (!IsDockedInMainLayout(pane)) return;
        var next = ShouldKeepPaneExtent(pane) ? visibleLength : ZeroLength;
        if (pane.DockWidth != next) pane.DockWidth = next;
    }

    private void SetDockHeightIfAttached(LayoutAnchorablePane pane, GridLength visibleLength)
    {
        if (!IsDockedInMainLayout(pane)) return;
        var next = ShouldKeepPaneExtent(pane) ? visibleLength : ZeroLength;
        if (pane.DockHeight != next) pane.DockHeight = next;
    }

    private bool ShouldKeepPaneExtent(LayoutAnchorablePane pane)
    {
        if (pane.IsVisible) return true;
        var keepPlaceholder = pane.ChildrenCount == 0
            && _dockPlacements.Any(kv =>
                kv.Key.IsFloating
                && ReferenceEquals(kv.Value.PaneElement, pane));
        if (keepPlaceholder)
            TraceDock($"KeepPaneExtentForFloatingPlaceholder pane={ElementDesc(pane)}");
        return keepPlaceholder;
    }

    private bool IsDockedInMainLayout(ILayoutElement element)
    {
        return element.Root == dockManager.Layout
            && element.FindParent<LayoutFloatingWindow>() == null;
    }

    private bool HasVisibleDockedAnchorable(ILayoutElement element)
    {
        return element.Descendents().OfType<LayoutAnchorable>()
            .Any(a => a.IsVisible && a.FindParent<LayoutFloatingWindow>() == null);
    }

    private void TraceDock(string reason, LayoutAnchorable? focus = null, bool includeTree = false)
    {
        // dock layout 진단 trace — Layout.Updated 가 매 frame 단위로 발화 가능하므로 운영 환경 noise 회피 위해 Debug 레벨.
        if (!Log.IsDebugEnabled) return;

        int seq = ++_dockTraceSeq;
        Log.Debug($"[DockTrace #{seq}] {reason} focus={ContentDesc(focus)} active={ContentDesc(dockManager.Layout?.ActiveContent)} " +
                  $"anchors=[{AnchorStates()}] panes=[{PaneStates()}] floating=[{FloatingStates()}]");
        if (includeTree)
            Log.Debug($"[DockTrace #{seq} tree]{Environment.NewLine}{LayoutTree()}");
    }

    private string AnchorStates()
    {
        return string.Join("; ", new[] { explorerAnchor, propertyAnchor, historyAnchor, simulationAnchor, llmChatAnchor }
            .Select(a => $"{a.ContentId}:vis={a.IsVisible},hidden={a.IsHidden},float={a.IsFloating},active={a.IsActive},sel={a.IsSelected},parent={ElementDesc(a.Parent as ILayoutElement)},path={ElementPath(a)}"));
    }

    private string PaneStates()
    {
        return string.Join("; ", new[]
        {
            PaneState("explorerPane", explorerPane),
            PaneState("propertyPane", propertyPane),
            PaneState("historyPane", historyPane),
            PaneState("simulationPane", simulationPane),
            PaneState("llmChatPane", llmChatPane),
            PaneState("rightPanel", rightPanel)
        });
    }

    private string PaneState(string name, ILayoutElement element)
    {
        var container = element as ILayoutContainer;
        var children = container == null ? "" : string.Join(",", container.Children.Select(ElementDesc));
        return $"{name}:{ElementDesc(element)},root={RootDesc(element)},floating={element.FindParent<LayoutFloatingWindow>() != null}," +
               $"parent={ElementDesc(element.Parent as ILayoutElement)},w={GridLengthDesc(DockWidthOf(element))},h={GridLengthDesc(DockHeightOf(element))},children=[{children}]";
    }

    private string FloatingStates()
    {
        var floating = dockManager.Layout?.FloatingWindows;
        if (floating == null) return "null";
        return string.Join("; ", floating.Select(f => $"{ElementDesc(f)} visible={FloatingVisibleDesc(f)} children=[{ChildrenDesc(f)}]"));
    }

    private static string ChildrenDesc(ILayoutContainer container)
    {
        return string.Join(",", container.Children.Select(ElementDesc));
    }

    private static string FloatingVisibleDesc(LayoutFloatingWindow floatingWindow)
    {
        return floatingWindow switch
        {
            LayoutAnchorableFloatingWindow f => f.IsVisible.ToString(),
            LayoutDocumentFloatingWindow f => f.IsVisible.ToString(),
            _ => "n/a"
        };
    }

    private string LayoutTree()
    {
        if (dockManager.Layout == null) return "(layout null)";
        var sb = new StringBuilder();
        AppendLayoutTree(sb, dockManager.Layout, 0);
        return sb.ToString().TrimEnd();
    }

    private static void AppendLayoutTree(StringBuilder sb, ILayoutElement element, int depth)
    {
        sb.Append(' ', depth * 2);
        sb.Append(ElementDesc(element));
        sb.Append(" root=");
        sb.Append(RootDesc(element));
        sb.Append(" w=");
        sb.Append(GridLengthDesc(DockWidthOf(element)));
        sb.Append(" h=");
        sb.Append(GridLengthDesc(DockHeightOf(element)));
        if (element is ILayoutPanelElement panelElement)
        {
            sb.Append(" visible=");
            sb.Append(panelElement.IsVisible);
        }
        sb.AppendLine();

        if (element is not ILayoutContainer container) return;
        foreach (var child in container.Children.OfType<ILayoutElement>())
            AppendLayoutTree(sb, child, depth + 1);
    }

    private string ElementPath(ILayoutElement? element)
    {
        if (element == null) return "null";
        var items = new List<string>();
        for (var current = element; current != null; current = current.Parent as ILayoutElement)
            items.Add(ElementDesc(current));
        items.Reverse();
        return string.Join("/", items);
    }

    private static string ContentDesc(LayoutContent? content)
    {
        return content == null
            ? "null"
            : $"{content.ContentId ?? content.Title}:{content.GetType().Name}";
    }

    private static string ElementDesc(ILayoutElement? element)
    {
        return element switch
        {
            null => "null",
            LayoutAnchorable a => $"{a.ContentId ?? a.Title}:Anchorable",
            LayoutDocument d => $"{d.ContentId ?? d.Title}:Document",
            LayoutAnchorablePane p => $"AnchorablePane(n={p.ChildrenCount})",
            LayoutDocumentPane p => $"DocumentPane(n={p.ChildrenCount})",
            LayoutAnchorablePaneGroup g => $"AnchorablePaneGroup({g.Orientation},n={g.ChildrenCount})",
            LayoutDocumentPaneGroup g => $"DocumentPaneGroup({g.Orientation},n={g.ChildrenCount})",
            LayoutPanel p => $"LayoutPanel({p.Orientation},n={p.ChildrenCount})",
            LayoutAnchorableFloatingWindow f => $"AnchorableFloatingWindow(n={f.ChildrenCount})",
            LayoutDocumentFloatingWindow f => $"DocumentFloatingWindow(n={f.ChildrenCount})",
            LayoutRoot => "LayoutRoot",
            _ => element.GetType().Name
        };
    }

    private static string RootDesc(ILayoutElement? element)
    {
        return element?.Root switch
        {
            null => "null",
            LayoutRoot => "main",
            var root => root.GetType().Name
        };
    }

    private static GridLength? DockWidthOf(ILayoutElement? element)
    {
        return element switch
        {
            LayoutAnchorablePane p => p.DockWidth,
            LayoutDocumentPane p => p.DockWidth,
            LayoutAnchorablePaneGroup g => g.DockWidth,
            LayoutDocumentPaneGroup g => g.DockWidth,
            LayoutPanel p => p.DockWidth,
            _ => null
        };
    }

    private static GridLength? DockHeightOf(ILayoutElement? element)
    {
        return element switch
        {
            LayoutAnchorablePane p => p.DockHeight,
            LayoutDocumentPane p => p.DockHeight,
            LayoutAnchorablePaneGroup g => g.DockHeight,
            LayoutDocumentPaneGroup g => g.DockHeight,
            LayoutPanel p => p.DockHeight,
            _ => null
        };
    }

    private static string GridLengthDesc(GridLength? value)
    {
        if (value == null) return "n/a";
        var v = value.Value;
        if (v.IsAuto) return "Auto";
        if (v.IsStar) return $"{v.Value:0.###}*";
        return $"{v.Value:0.###}px";
    }

    // floating → docked 복귀 시 직전 dock 위치 복원.
    // docked 상태의 anchor (pane + index) 를 _dockPlacements 에 갱신하고,
    // Dock command / Show fallback 이 들어오면 AvalonDock 기본 후보 대신 해당 위치로 직접 reparent.
    // IsFloating=true 동안은 갱신 skip → 직전 docked 위치 보존.
    private static string PaneDesc(object? parent)
    {
        if (parent == null) return "null";
        if (parent is LayoutAnchorablePane p)
            return $"AnchorablePane(n={p.Children.Count})";
        return parent.GetType().Name;
    }

    private void OnAnchorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LayoutAnchorable anchor) return;
        if (e.PropertyName == "IsFloating")
        {
            TraceDock($"AnchorPropertyChanged IsFloating anchor={ContentDesc(anchor)} parent={ElementDesc(anchor.Parent as ILayoutElement)}", anchor, includeTree: true);
            QueueDockPaneExtentUpdate();
            return;
        }
        if (e.PropertyName != "Parent") return;
        TraceDock($"AnchorPropertyChanged Parent anchor={ContentDesc(anchor)} parent={ElementDesc(anchor.Parent as ILayoutElement)}", anchor, includeTree: true);
        CaptureDockPlacement(anchor);
        QueueDockPaneExtentUpdate();
    }

    // anchor 가 main dock layout 안에 docked 상태일 때 그 pane + index 를 기록한다.
    // - IsFloating=true / floating window pane / float 생성 중 transient pane(Root null): skip
    // - LayoutDocumentPane 도 기록해 사용자가 canvas 쪽에 tab docking 한 위치를 보존
    private void CaptureDockPlacement(LayoutAnchorable anchor)
    {
        if (anchor.IsFloating)
        {
            TraceDock($"CaptureDockPlacement skipped floating anchor={ContentDesc(anchor)}", anchor);
            return;
        }
        if (anchor.Parent is not ILayoutPane pane)
        {
            TraceDock($"CaptureDockPlacement skipped non-pane parent anchor={ContentDesc(anchor)} parent={ElementDesc(anchor.Parent as ILayoutElement)}", anchor);
            return;
        }
        if (anchor.Parent is not ILayoutGroup paneGroup)
        {
            TraceDock($"CaptureDockPlacement skipped non-group parent anchor={ContentDesc(anchor)} parent={ElementDesc(anchor.Parent as ILayoutElement)}", anchor);
            return;
        }
        if (pane is not ILayoutElement paneElement)
        {
            TraceDock($"CaptureDockPlacement skipped pane not layout element anchor={ContentDesc(anchor)} parent={PaneDesc(anchor.Parent)}", anchor);
            return;
        }
        if (paneElement.Root != dockManager.Layout)
        {
            TraceDock($"CaptureDockPlacement skipped other-root anchor={ContentDesc(anchor)} parent={ElementDesc(paneElement)} root={RootDesc(paneElement)}", anchor);
            return;
        }
        if (paneElement.FindParent<LayoutFloatingWindow>() != null)
        {
            TraceDock($"CaptureDockPlacement skipped floating parent anchor={ContentDesc(anchor)} parent={ElementDesc(paneElement)}", anchor);
            return;
        }

        int idx = paneGroup.IndexOfChild(anchor);
        if (idx < 0)
        {
            TraceDock($"CaptureDockPlacement skipped missing child anchor={ContentDesc(anchor)} pane={ElementDesc(paneElement)}", anchor);
            return;
        }

        var parentGroup = paneElement.Parent as ILayoutGroup;
        int paneIndex = parentGroup?.IndexOfChild(paneElement) ?? -1;
        var parentGroupElement = parentGroup as ILayoutElement;
        var parentParentGroup = parentGroupElement?.Parent as ILayoutGroup;
        int parentGroupIndex = parentParentGroup?.IndexOfChild(parentGroupElement) ?? -1;

        _dockPlacements[anchor] = new DockAnchorPlacement(
            paneElement,
            paneGroup,
            parentGroup,
            paneIndex,
            idx,
            parentGroupElement,
            parentParentGroup,
            parentGroupIndex);
        TraceDock($"CaptureDockPlacement captured anchor={ContentDesc(anchor)} pane={ElementDesc(paneElement)} childIndex={idx} paneIndex={paneIndex}", anchor);
    }

    private void OnDockManagerContentDocking(object? sender, ContentDockingEventArgs e)
    {
        TraceDock($"ContentDocking content={ContentDesc(e.Content)}", e.Content as LayoutAnchorable, includeTree: true);
        if (e.Content is not LayoutAnchorable anchor) return;
        if (!anchor.IsFloating)
        {
            TraceDock($"ContentDocking ignored non-floating anchor={ContentDesc(anchor)}", anchor);
            return;
        }
        if (!TryDockAnchorAtCapturedPlacement(anchor))
        {
            TraceDock($"ContentDocking fallback AvalonDock Dock anchor={ContentDesc(anchor)}", anchor, includeTree: true);
            return;
        }

        e.Cancel = true;
        dockManager.Layout?.CollectGarbage();
        TraceDock($"ContentDocking handled by captured placement anchor={ContentDesc(anchor)}", anchor, includeTree: true);
    }

    private bool TryDockAnchorAtCapturedPlacement(LayoutAnchorable anchor)
    {
        TraceDock($"TryDockAnchorAtCapturedPlacement begin anchor={ContentDesc(anchor)}", anchor, includeTree: true);
        if (!_dockPlacements.TryGetValue(anchor, out var placement))
        {
            TraceDock($"TryDockAnchorAtCapturedPlacement no-placement anchor={ContentDesc(anchor)}", anchor);
            return false;
        }

        if (!EnsureDockPaneAttached(placement))
        {
            TraceDock($"TryDockAnchorAtCapturedPlacement ensure-failed anchor={ContentDesc(anchor)} placementPane={ElementDesc(placement.PaneElement)}", anchor, includeTree: true);
            return false;
        }

        if (placement.PaneElement.FindParent<LayoutFloatingWindow>() != null)
        {
            TraceDock($"TryDockAnchorAtCapturedPlacement placement-floating anchor={ContentDesc(anchor)} placementPane={ElementDesc(placement.PaneElement)}", anchor, includeTree: true);
            return false;
        }

        if (ReferenceEquals(anchor.Parent, placement.PaneGroup))
        {
            anchor.IsSelected = true;
            anchor.IsActive = true;
            CaptureDockPlacement(anchor);
            QueueDockPaneExtentUpdate();
            TraceDock($"TryDockAnchorAtCapturedPlacement already-at-placement anchor={ContentDesc(anchor)}", anchor, includeTree: true);
            return true;
        }

        int insertIndex = Math.Clamp(placement.ChildIndex, 0, placement.PaneGroup.ChildrenCount);
        placement.PaneGroup.InsertChildAt(insertIndex, anchor);
        anchor.IsSelected = true;
        anchor.IsActive = true;

        CaptureDockPlacement(anchor);
        QueueDockPaneExtentUpdate();
        TraceDock($"TryDockAnchorAtCapturedPlacement inserted anchor={ContentDesc(anchor)} index={insertIndex}", anchor, includeTree: true);
        return true;
    }

    private static bool EnsureDockPaneAttached(DockAnchorPlacement placement)
    {
        if (placement.PaneElement.Parent != null
            && placement.PaneElement.Root != null
            && placement.PaneElement.FindParent<LayoutFloatingWindow>() == null)
            return true;

        if (placement.ParentGroup is null)
            return false;

        if (placement.ParentGroupElement is { Parent: null }
            && placement.ParentParentGroup is not null)
        {
            int parentGroupIndex = placement.ParentGroupIndex;
            if (parentGroupIndex < 0 || parentGroupIndex > placement.ParentParentGroup.ChildrenCount)
                parentGroupIndex = placement.ParentParentGroup.ChildrenCount;

            placement.ParentParentGroup.InsertChildAt(parentGroupIndex, placement.ParentGroupElement);
        }

        if (!ReferenceEquals(placement.PaneElement.Parent, placement.ParentGroup))
        {
            int paneIndex = placement.PaneIndex;
            if (paneIndex < 0 || paneIndex > placement.ParentGroup.ChildrenCount)
                paneIndex = placement.ParentGroup.ChildrenCount;

            placement.ParentGroup.InsertChildAt(paneIndex, placement.PaneElement);
        }

        return placement.PaneElement.Parent != null
            && placement.PaneElement.Root != null
            && placement.PaneElement.FindParent<LayoutFloatingWindow>() == null;
    }

    private sealed class DockAnchorPlacement(
        ILayoutElement paneElement,
        ILayoutGroup paneGroup,
        ILayoutGroup? parentGroup,
        int paneIndex,
        int childIndex,
        ILayoutElement? parentGroupElement,
        ILayoutGroup? parentParentGroup,
        int parentGroupIndex)
    {
        public ILayoutElement PaneElement { get; } = paneElement;
        public ILayoutGroup PaneGroup { get; } = paneGroup;
        public ILayoutGroup? ParentGroup { get; } = parentGroup;
        public int PaneIndex { get; } = paneIndex;
        public int ChildIndex { get; } = childIndex;
        public ILayoutElement? ParentGroupElement { get; } = parentGroupElement;
        public ILayoutGroup? ParentParentGroup { get; } = parentParentGroup;
        public int ParentGroupIndex { get; } = parentGroupIndex;
    }

    private sealed class DockLayoutUpdateStrategy(MainWindow owner) : ILayoutUpdateStrategy
    {
        public bool BeforeInsertAnchorable(
            LayoutRoot layout,
            LayoutAnchorable anchorableToShow,
            ILayoutContainer destinationContainer)
        {
            owner.TraceDock($"LayoutUpdateStrategy.BeforeInsertAnchorable anchor={ContentDesc(anchorableToShow)} destination={ElementDesc(destinationContainer as ILayoutElement)}", anchorableToShow, includeTree: true);
            return owner.TryDockAnchorAtCapturedPlacement(anchorableToShow);
        }

        public void AfterInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableShown)
        {
            owner.TraceDock($"LayoutUpdateStrategy.AfterInsertAnchorable anchor={ContentDesc(anchorableShown)}", anchorableShown, includeTree: true);
            owner.CaptureDockPlacement(anchorableShown);
            owner.QueueDockPaneExtentUpdate();
        }

        public bool BeforeInsertDocument(
            LayoutRoot layout,
            LayoutDocument anchorableToShow,
            ILayoutContainer destinationContainer) => false;

        public void AfterInsertDocument(LayoutRoot layout, LayoutDocument anchorableShown)
        {
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

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
        DragDropYamlIcon.Visibility = Visibility.Collapsed;

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

    // v14 — floating window 생성 시 1회 호출. XAML implicit Style 미적용 회피.
    // v14 hotfix — LayoutFloatingWindowControl.ResizeBorderThickness DP 직접 set 은
    // 내부적으로 WindowChrome.ResizeBorderThickness 에 set 하는데 AvalonDock 가 attach 한 chrome 이
    // frozen Freezable (theme shared 인스턴스) 이라 "읽기 전용 상태" 예외 발생 → clone 후 SetWindowChrome.
    private void OnFloatingWindowCreated(object? sender, LayoutFloatingWindowControlCreatedEventArgs e)
    {
        var w = e.LayoutFloatingWindowControl;
        TraceDock($"FloatingWindowCreated window={w.GetType().Name} model={ElementDesc(w.Model)}", includeTree: true);
        w.OwnedByDockingManagerWindow = false;            // main 활성화 시 z-order 뒤로 (Owner 해제)
        w.Topmost = false;                                 // VS 표준
        if (Application.Current.TryFindResource("PrimaryBackgroundBrush") is Brush bg)
            w.Background = bg;                             // out-of-focus title bar 다크화
        DeferAttachFloatingResizeGripHook(w);

        // WindowChrome 은 Loaded 후 attach 됨 — Loaded 시점 또는 즉시 (이미 loaded) 처리.
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

    // v14.3 — AvalonDock 의 WindowChrome attach 가 Loaded 이후 ApplicationIdle 시점에 일어날 수 있음.
    // v16 — System.Windows.Shell.WindowChrome 이 아니라 AvalonDock 이 사용하는 Microsoft.Windows.Shell.WindowChrome 이어야
    // title bar button 의 IsHitTestVisibleInChrome 판정이 유지된다.
    private static void DeferApplyChromeThickness(Window w)
    {
        w.Dispatcher.BeginInvoke(new Action(() => ApplyChromeThickness(w)),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
        w.Dispatcher.BeginInvoke(new Action(() => ApplyChromeThickness(w)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // v15 — directional ResizeBorderThickness:
    //   - top 만 4px (CaptionHeight=16 보다 작게 → 캡션 버튼 영역 침범 회피)
    //   - 좌/우는 title bar 버튼과 충돌하지 않도록 기본에 가깝게 유지
    //   - 하단 좌/우 코너는 Win32 hit-test hook 으로 별도 확장
    private const double FloatingResizeBorderSide = 8.0;
    private const double FloatingResizeBorderBottom = 10.0;
    private const double FloatingResizeBorderTop = 4.0;

    private static void ApplyChromeThickness(Window w)
    {
        var chrome = Microsoft.Windows.Shell.WindowChrome.GetWindowChrome(w);
        var origin = "existing";
        if (chrome == null)
        {
            // v14.3 — AvalonDock 의 WindowChrome attach 가 우리 Loaded 보다 늦거나 다른 path 인 경우.
            // Metro theme default 값(CaptionHeight=24, CornerRadius=0, GlassFrameThickness=0) 재현.
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
