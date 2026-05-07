using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using log4net;
using Promaker.Presentation;
using Promaker.Services;
using Promaker.Resources;
using Promaker.Windows;

namespace Promaker.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MainViewModel));

    private DsStore _store;
    private readonly Dispatcher _dispatcher;
    private IDisposable? _eventSubscription;
    private string? _currentFilePath;
    private readonly List<SelectionKey> _clipboardSelection = [];
    private int _pasteCount;
    public bool HasClipboardData => _clipboardSelection.Count > 0;
    private bool _rebuildQueued;
    private readonly List<Action> _pendingRebuildActions = [];
    private View3DWindow? _view3DWindow;

    // Services
    private readonly IDialogService _dialogService;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _store = new DsStore();

        // Initialize services
        _dialogService = new DialogService();

        Selection = new SelectionState(new SelectionHost(this));
        CanvasManager = new SplitCanvasManager(() => new CanvasWorkspaceState(new CanvasHost(this)));
        CanvasManager.ActivePaneChanged = RefreshEditorCommandStates;
        Simulation = new SimulationPanelState(() => _store, _dispatcher,
            () => CanvasManager.AllPanes.SelectMany(p => p.CanvasNodes),
            () => FlattenTree(ControlTreeRoots).Concat(FlattenTree(DeviceTreeRoots)),
            value => StatusText = value);
        // "시뮬레이션 결과 보기" 활성 조건은 Simulation.HasReportData 와 연동.
        Simulation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SimulationPanelState.HasReportData))
                ShowSimulationScenariosCommand.NotifyCanExecuteChanged();
        };
        PropertyPanel = new PropertyPanelState(new PropertyPanelHost(this));
        Simulation.RuntimeIoChanged = ioValues => PropertyPanel.RefreshConditionRuntime(ioValues);
        WireEvents();
        LanguageManager.ApplySavedLanguage();
        RefreshThemeState();
        RefreshLanguageState();
        LoadRecentFiles();
        LoadSplitDeviceAasxSetting();
        LoadCreateDefaultEntitiesSetting();
        LoadIriPrefixSetting();

        // 외부 템플릿 폴더 초기화 제거 — AASX 내 FBTagMapPresets 가 단일 진실원이며,
        // 필요한 경우 TAG Wizard 가 일시 임시 디렉토리를 사용한다.

        // Pass 1.5 측정 자동화 — `--autostart-llm` 인자 시작 시 chat panel 자동 활성화.
        // McpHostService 가 LlmChatViewModel ctor 안에서 StartAsync → mcp config 파일이 즉시 작성됨.
        // 추가: --measure-prompt 가 있으면 IsReady 후 자동 prompt 전송, --measure-then-exit 면 turn 끝 후 self-close.
        if (App.StartupAutoOpenLlm)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                ToggleLlmChat();
                if (LlmChatVm != null && App.StartupMeasurePrompt != null)
                {
                    ScheduleMeasurePrompt(LlmChatVm, App.StartupMeasurePrompt, App.StartupMeasureThenExit);
                }
            }), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 측정 자동화: LlmChatViewModel.IsReady 가 true 되면 Input 에 prompt 적용 + SendCommand 실행.
    /// thenExit=true 이면 IsSending true→false transition 후 MainWindow.Close() (Closing 의 dirty check 는 autostart 에서 skip).
    /// </summary>
    private void ScheduleMeasurePrompt(LlmChatViewModel vm, string prompt, bool thenExit)
    {
        System.ComponentModel.PropertyChangedEventHandler? readyHandler = null;
        readyHandler = (_, e) =>
        {
            if (e.PropertyName != nameof(LlmChatViewModel.IsReady) || !vm.IsReady) return;
            vm.PropertyChanged -= readyHandler;
            vm.Input = prompt;
            if (vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);

            if (!thenExit) return;

            // SendCommand.Execute 의 동기 부분이 readyHandler 와 같은 dispatcher thread 에서 즉시 실행되어
            // IsSending=true 가 이미 emit 된 상태로 여기 도달. 따라서 wasSending 초기값을 현 IsSending 으로
            // 캐시 — 미캐시 시 후속 false transition 의 sendHandler 가 wasSending=false 로 단락 → Close 안됨.
            bool wasSending = vm.IsSending;
            System.ComponentModel.PropertyChangedEventHandler? sendHandler = null;
            sendHandler = (_, e2) =>
            {
                if (e2.PropertyName != nameof(LlmChatViewModel.IsSending)) return;
                if (vm.IsSending) { wasSending = true; return; }
                if (!wasSending) return;
                vm.PropertyChanged -= sendHandler;
                // 응답 마무리 (last AssistantDelta flush + Authoring "Executed" 로그) 의 background priority 작업이
                // 끝난 후 close. ApplicationIdle = 모든 priority 가 비었을 때 → log4net flush 충분.
                _dispatcher.BeginInvoke(new Action(() =>
                    Application.Current.MainWindow?.Close()), DispatcherPriority.ApplicationIdle);
            };
            vm.PropertyChanged += sendHandler;
        };
        vm.PropertyChanged += readyHandler;
    }

    /// <summary>
    /// 저장된 최근 파일 목록 로드
    /// </summary>
    private void LoadRecentFiles()
    {
        RecentFiles.Clear();
        var files = Services.RecentFilesManager.LoadRecentFiles();
        foreach (var file in files)
        {
            RecentFiles.Add(file);
        }
    }

    public ObservableCollection<EntityNode> ControlTreeRoots { get; } = [];
    public ObservableCollection<EntityNode> DeviceTreeRoots { get; } = [];
    public ObservableCollection<HistoryPanelItem> HistoryItems { get; } = [];
    public ObservableCollection<string> RecentFiles { get; } = [];
    public SplitCanvasManager CanvasManager { get; }
    public CanvasWorkspaceState Canvas => CanvasManager.Canvas;
    public SimulationPanelState Simulation { get; }
    public PropertyPanelState PropertyPanel { get; }
    public SelectionState Selection { get; }

    [ObservableProperty] private EntityNode? _selectedNode;
    [ObservableProperty] private string _title = AppInfo.TitleBase;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canRedo;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveFileAsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowProjectSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSystemCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddFlowCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddWorkCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddCallCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(AutoLayoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenIoBatchDialogCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenDurationBatchDialogCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenTokenSpecDialogCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectSelectedNodesCommand))]
    [NotifyCanExecuteChangedFor(nameof(Open3DViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowRuntimeSettingsCommand))]
    private bool _hasProject;
    [ObservableProperty] private bool _isDarkTheme = ThemeManager.CurrentTheme == AppTheme.Dark;
    [ObservableProperty] private string _themeButtonText = ThemeManager.CurrentTheme == AppTheme.Dark ? Strings.LightTheme : Strings.DarkTheme;
    [ObservableProperty] private string _themeButtonGlyph = ThemeManager.CurrentTheme == AppTheme.Dark ? "☀" : "☾";
    [ObservableProperty] private string _themeButtonToolTip = ThemeManager.CurrentTheme == AppTheme.Dark ? Strings.SwitchToLightTheme : Strings.SwitchToDarkTheme;
    [ObservableProperty] private string _languageButtonText = LanguageManager.CurrentLanguage == AppLanguage.Korean ? "ENG" : "KOR";
    [ObservableProperty] private string _languageButtonGlyph = "🌐";
    [ObservableProperty] private string _languageButtonToolTip = LanguageManager.CurrentLanguage == AppLanguage.Korean ? "Switch to English" : "한국어로 전환";
    [ObservableProperty] private int _currentHistoryIndex;
    [ObservableProperty] private ArrowNode? _selectedArrow;

    public static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>XAML 바인딩용 인스턴스 프록시 — DEBUG 빌드에서만 미완성 기능(예: PLC 생성) 노출.</summary>
    public bool IsDebugBuildInstance => IsDebugBuild;

    public Xywh? PendingAddPosition { get; set; }

    [ObservableProperty] private ArrowType _selectedConnectArrowType = ArrowType.Start;

    public Action? FocusNameEditorRequested { get; set; }
    public Action? SearchResetRequested { get; set; }
    public Action? ExplorerRebindRequested { get; set; }

    private bool CanFocusNameEditor() =>
        SelectedNode is not null && Selection.OrderedNodeSelection.Count <= 1;

    [RelayCommand(CanExecute = nameof(CanFocusNameEditor))]
    private void FocusNameEditor()
    {
        if (SelectedNode is not null)
        {
            PropertyPanel.BeginNameEditGuidance();
            FocusNameEditorRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        ThemeManager.ToggleTheme();
        RefreshThemeState();
        StatusText = IsDarkTheme ? Strings.DarkThemeApplied : Strings.LightThemeApplied;
    }

    /// <summary>
    /// 언어 전환 (Korean ↔ English)
    /// TODO: Phase 1-5 완료 후 언어 버튼 활성화
    /// - MainToolbar.xaml에서 Visibility="Collapsed" 제거
    /// - 현재는 숨겨진 상태로 유지 (개발 진행 중)
    /// </summary>
    [RelayCommand]
    private void ToggleLanguage()
    {
        LanguageManager.ToggleLanguage();
        RefreshLanguageState();
        RefreshThemeState(); // Refresh theme to update localized text
        StatusText = LanguageManager.CurrentLanguage == AppLanguage.English
            ? "English Language Applied"
            : "한국어 적용";
    }

    internal void HandleSelectionStateChanged()
    {
        PropertyPanel.SyncSelection(SelectedNode, Selection.OrderedNodeSelection);
        Simulation.SyncCanvasSelection(Selection.OrderedNodeSelection);
        RefreshEditorCommandStates();
    }

    partial void OnSelectedNodeChanged(EntityNode? value) => HandleSelectionStateChanged();

    partial void OnSelectedArrowChanged(ArrowNode? value) => RefreshEditorCommandStates();

    private void RefreshThemeState()
    {
        IsDarkTheme = ThemeManager.CurrentTheme == AppTheme.Dark;
        ThemeButtonText = IsDarkTheme ? Strings.LightTheme : Strings.DarkTheme;
        ThemeButtonGlyph = IsDarkTheme ? "☀" : "☾";
        ThemeButtonToolTip = IsDarkTheme ? Strings.SwitchToLightTheme : Strings.SwitchToDarkTheme;
    }

    private void RefreshLanguageState()
    {
        LanguageButtonText = LanguageManager.CurrentLanguage == AppLanguage.Korean ? "ENG" : "KOR";
        LanguageButtonGlyph = "🌐";
        LanguageButtonToolTip = LanguageManager.CurrentLanguage == AppLanguage.Korean
            ? "Switch to English"
            : "한국어로 전환";
    }

    private bool CanOpen3DView() => HasProject;

    [RelayCommand(CanExecute = nameof(CanUndoNow))]
    private void Undo()
    {
        if (!GuardSimulationSemanticEdit("Undo")) return;
        _pasteCount = 0;
        TryEditorAction(() => _store.Undo());
    }
    private bool CanUndoNow => CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedoNow))]
    private void Redo()
    {
        if (!GuardSimulationSemanticEdit("Redo")) return;
        _pasteCount = 0;
        TryEditorAction(() => _store.Redo());
    }
    private bool CanRedoNow => CanRedo;

    public void EditApiDefNode(Guid apiDefId) => PropertyPanel.EditApiDefNode(apiDefId);

}

public sealed class HistoryPanelItem(string label, bool isRedo)
{
    public string Label  { get; } = label;
    public bool   IsRedo { get; } = isRedo;
    /// <summary>
    /// 1d-4 F / m8 — LLM turn 식별. prefix SSOT = `LlmChatViewModel.LlmTurnLabelPrefix` ("LLM: ").
    /// HistoryPanel 의 좌측 색띠 / accent 색으로 시각화.
    /// </summary>
    public bool   IsLlmTurn => Label.StartsWith(LlmChatViewModel.LlmTurnLabelPrefix, StringComparison.Ordinal);
}
