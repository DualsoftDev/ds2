using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

        InitLlmAutostart();
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
