using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using log4net;
using Promaker.Dialogs;
using Promaker.Presentation;
using Promaker.Services;
using Promaker.Resources;

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
        PropertyPanel = new PropertyPanelState(new PropertyPanelHost(this));
        WireEvents();
        LanguageManager.ApplySavedLanguage();
        RefreshThemeState();
        RefreshLanguageState();
    }

    public ObservableCollection<EntityNode> ControlTreeRoots { get; } = [];
    public ObservableCollection<EntityNode> DeviceTreeRoots { get; } = [];
    public ObservableCollection<HistoryPanelItem> HistoryItems { get; } = [];
    public SplitCanvasManager CanvasManager { get; }
    public CanvasWorkspaceState Canvas => CanvasManager.Canvas;
    public SimulationPanelState Simulation { get; }
    public PropertyPanelState PropertyPanel { get; }
    public SelectionState Selection { get; }

    [ObservableProperty] private EntityNode? _selectedNode;
    [ObservableProperty] private string _title = "Ds2 Promaker";
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
    [NotifyCanExecuteChangedFor(nameof(ConnectSelectedNodesCommand))]
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

    public Xywh? PendingAddPosition { get; set; }

    [ObservableProperty] private ArrowType _selectedConnectArrowType = ArrowType.Start;

    public Action? FocusNameEditorRequested { get; set; }

    private bool CanFocusNameEditor() => SelectedNode is not null;

    [RelayCommand(CanExecute = nameof(CanFocusNameEditor))]
    private void FocusNameEditor()
    {
        if (SelectedNode is not null)
            FocusNameEditorRequested?.Invoke();
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

    partial void OnSelectedNodeChanged(EntityNode? value)
    {
        PropertyPanel.SyncSelectedNode(value);
        Simulation.SyncCanvasSelection(Selection.OrderedNodeSelection);
        RefreshEditorCommandStates();
    }

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

    [RelayCommand]
    private void NewProject()
    {
        if (!ConfirmDiscardChanges())
            return;

        Reset();
        TryEditorAction(() => _store.AddProject("NewProject"));

        // 기본 System + Flow 자동 추가
        var projectId = DsQuery.allProjects(_store).Head.Id;
        var systemId = _store.AddSystem("NewSystem", projectId, isActive: true);
        _store.AddFlow("NewFlow", systemId);

        _store.ClearHistory();
        IsDirty = false;
        HasProject = true;
        UpdateTitle();
        StatusText = "New project created.";
        RefreshEditorCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => TryEditorAction(() => _store.Undo());

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => TryEditorAction(() => _store.Redo());

    public void EditApiDefNode(Guid apiDefId) => PropertyPanel.EditApiDefNode(apiDefId);

    private void Reset()
    {
        Simulation.ResetForNewStore();

        _store = new DsStore();
        WireEvents();

        _currentFilePath = null;
        IsDirty = false;
        HasProject = false;
        CanUndo = false;
        CanRedo = false;
        HistoryItems.Clear();
        HistoryItems.Add(new HistoryPanelItem("(초기 상태)", isRedo: false));
        CurrentHistoryIndex = 0;

        _clipboardSelection.Clear();
        Selection.Reset();
        CanvasManager.Reset();
        _rebuildQueued = false;
        _pendingRebuildActions.Clear();
        SelectedNode = null;
        SelectedArrow = null;

        RebuildAll();
        UpdateTitle();
        StatusText = "Ready";
        RefreshEditorCommandStates();
    }

    private bool ConfirmDiscardChanges()
    {
        if (!IsDirty)
            return true;

        var result = _dialogService.AskSaveChanges();
        return DiscardChangesFlow.ShouldProceed(result, TrySaveFileDuringDiscardCheck);
    }

    /// <summary>Window Closing 이벤트에서 호출</summary>
    public bool ConfirmDiscardChangesPublic() => ConfirmDiscardChanges();

    private void UpdateTitle()
    {
        var dirty = IsDirty ? " *" : "";
        var file = _currentFilePath is not null ? $" - {System.IO.Path.GetFileName(_currentFilePath)}" : "";
        Title = $"Ds2 Promaker{file}{dirty}";
    }

    internal void PrepareForLoadedStore()
    {
        Simulation.ResetForNewStore();
        _clipboardSelection.Clear();
        Selection.Reset();
        CanvasManager.Reset();
        SelectedNode = null;
        SelectedArrow = null;
        RefreshEditorCommandStates();
    }

    private bool TrySaveFileDuringDiscardCheck()
    {
        try
        {
            return TrySaveFile();
        }
        catch (Exception ex)
        {
            Log.Error("Save failed during discard check", ex);
            _dialogService.ShowWarning($"저장 실패: {ex.Message}");
            return false;
        }
    }

    [RelayCommand]
    private void JumpToHistory(HistoryPanelItem? item)
    {
        if (item is null) return;
        int clickedIdx = HistoryItems.IndexOf(item);
        if (clickedIdx < 0) return;
        int delta = clickedIdx - CurrentHistoryIndex;
        if (delta < 0)
            TryEditorAction(() => _store.UndoTo(-delta));
        else if (delta > 0)
            TryEditorAction(() => _store.RedoTo(delta));
    }

    private void RebuildHistoryItems(
        IEnumerable<string> undoLabels,
        IEnumerable<string> redoLabels)
    {
        var undoList = undoLabels.ToList();
        var redoList = redoLabels.ToList();
        CanUndo = undoList.Count > 0;
        CanRedo = redoList.Count > 0;
        IsDirty = undoList.Count > 0;

        HistoryItems.Clear();
        HistoryItems.Add(new HistoryPanelItem("(초기 상태)", isRedo: false));
        foreach (var label in Enumerable.Reverse(undoList))
            HistoryItems.Add(new HistoryPanelItem(label, isRedo: false));
        foreach (var label in redoList)
            HistoryItems.Add(new HistoryPanelItem(label, isRedo: true));
        CurrentHistoryIndex = undoList.Count;
        RefreshEditorCommandStates();
    }

    private void RebuildAll()
    {
        var prevSelection = Selection.OrderedNodeSelection.ToList();
        var prevSelectedArrowIds = Selection.OrderedArrowSelection.ToList();
        var expandedNodes = Selection.GetExpandedKeys();

        ControlTreeRoots.Clear();
        DeviceTreeRoots.Clear();

        if (!TryEditorRef(
                () => EditorTreeProjection.BuildTrees(_store),
                out var trees,
                statusOverride: "[ERROR] Failed to rebuild tree views."))
            return;

        foreach (var info in trees.Item1)
            ControlTreeRoots.Add(MapToEntityNode(info));
        foreach (var info in trees.Item2)
            DeviceTreeRoots.Add(MapToEntityNode(info));

        Selection.ApplyExpansionStateTo(ControlTreeRoots, expandedNodes);
        Selection.ApplyExpansionStateTo(DeviceTreeRoots, expandedNodes);

        CanvasManager.RebuildAllPanes();
        Simulation.RestoreSimStateToCanvas();
        Selection.RestoreSelection(prevSelection, prevSelectedArrowIds);
    }

    private void RequestRebuildAll(Action? afterRebuild = null)
    {
        if (afterRebuild is not null)
            _pendingRebuildActions.Add(afterRebuild);

        if (_rebuildQueued)
            return;

        _rebuildQueued = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _rebuildQueued = false;
            RebuildAll();

            if (_pendingRebuildActions.Count == 0)
                return;

            var actions = _pendingRebuildActions.ToArray();
            _pendingRebuildActions.Clear();
            foreach (var action in actions)
                action();
        }), DispatcherPriority.Background);
    }

    private static EntityNode MapToEntityNode(TreeNodeInfo info)
    {
        var parentId = info.ParentIdOrNull;
        var node = new EntityNode(info.Id, info.EntityKind, info.Name, parentId);
        foreach (var child in info.Children)
            node.Children.Add(MapToEntityNode(child));
        return node;
    }

    private static IEnumerable<EntityNode> FlattenTree(IEnumerable<EntityNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in FlattenTree(node.Children))
                yield return child;
        }
    }
}

public sealed class HistoryPanelItem(string label, bool isRedo)
{
    public string Label  { get; } = label;
    public bool   IsRedo { get; } = isRedo;
}
