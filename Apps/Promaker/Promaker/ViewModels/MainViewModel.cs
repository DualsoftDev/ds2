using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using log4net;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MainViewModel));

    private DsStore _store;
    private readonly Dispatcher _dispatcher;
    private IDisposable? _eventSubscription;
    private string? _currentFilePath;
    private TreePaneKind _activeTreePane = TreePaneKind.Control;
    private readonly List<SelectionKey> _clipboardSelection = [];
    private readonly List<SelectionKey> _orderedNodeSelection = [];
    private readonly List<Guid> _orderedArrowSelection = [];
    private SelectionKey? _selectionAnchor;
    private bool _rebuildQueued;
    private readonly List<Action> _pendingRebuildActions = [];

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _store = new DsStore();
        InitializePropertyPanelState();
        WireEvents();
    }

    public ObservableCollection<EntityNode> ControlTreeRoots { get; } = [];
    public ObservableCollection<EntityNode> DeviceTreeRoots { get; } = [];
    public ObservableCollection<EntityNode> CanvasNodes { get; } = [];
    public ObservableCollection<CanvasTab> OpenTabs { get; } = [];
    public ObservableCollection<ArrowNode> CanvasArrows { get; } = [];
    public ObservableCollection<HistoryPanelItem> HistoryItems { get; } = [];

    [ObservableProperty] private EntityNode? _selectedNode;
    [ObservableProperty] private CanvasTab? _activeTab;
    [ObservableProperty] private string _title = "Ds2 Promaker";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canRedo;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private int _currentHistoryIndex;
    [ObservableProperty] private ArrowNode? _selectedArrow;

    public static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    public Xywh? PendingAddPosition { get; set; }
    public Func<System.Windows.Point>? GetViewportCenterRequested { get; set; }

    public Action? FocusNameEditorRequested { get; set; }

    [RelayCommand]
    private void FocusNameEditor()
    {
        if (SelectedNode is not null)
            FocusNameEditorRequested?.Invoke();
    }

    [RelayCommand]
    private void NewProject()
    {
        if (!ConfirmDiscardChanges()) return;
        Reset();
    }
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => TryEditorAction(() => _store.Undo());
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => TryEditorAction(() => _store.Redo());

    private void Reset()
    {
        DisposeSimEngine();
        SimNodes.Clear();
        SimEventLog.Clear();

        _store = new DsStore();
        WireEvents();

        _currentFilePath = null;
        IsDirty = false;
        CanUndo = false;
        CanRedo = false;
        HistoryItems.Clear();
        HistoryItems.Add(new HistoryPanelItem("(초기 상태)", isRedo: false));
        CurrentHistoryIndex = 0;

        _clipboardSelection.Clear();
        _orderedNodeSelection.Clear();
        _orderedArrowSelection.Clear();
        _selectionAnchor = null;
        _rebuildQueued = false;
        _pendingRebuildActions.Clear();

        OpenTabs.Clear();
        ActiveTab = null;
        SelectedNode = null;
        SelectedArrow = null;

        RebuildAll();
        UpdateTitle();
        StatusText = "Ready";
    }

    /// IsDirty일 때 저장 여부 확인. true=계속 진행, false=취소.
    private bool ConfirmDiscardChanges()
    {
        if (!IsDirty) return true;

        var result = DialogHelpers.AskSaveChanges();
        if (result == System.Windows.MessageBoxResult.Yes)
        {
            SaveFile();
            return true;
        }
        return result == System.Windows.MessageBoxResult.No;
    }

    private void UpdateTitle()
    {
        var dirty = IsDirty ? " *" : "";
        var file = _currentFilePath is not null ? $" - {System.IO.Path.GetFileName(_currentFilePath)}" : "";
        Title = $"Ds2 Promaker{file}{dirty}";
    }
}
