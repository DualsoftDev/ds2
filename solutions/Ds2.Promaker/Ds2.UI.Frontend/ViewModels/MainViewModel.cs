using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using log4net;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MainViewModel));

    private EditorApi _editor;
    private DsStore _store;
    private readonly Dispatcher _dispatcher;
    private IDisposable? _eventSubscription;
    private string? _currentFilePath;
    private TreePaneKind _activeTreePane = TreePaneKind.Control;
    private readonly List<SelectionKey> _clipboardSelection = [];
    private readonly List<SelectionKey> _orderedNodeSelection = [];
    private readonly List<Guid> _orderedArrowSelection = [];
    private SelectionKey? _selectionAnchor;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _store = DsStore.empty();
        _editor = new EditorApi(_store, maxUndoSize: 100);
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

    public EditorApi Editor => _editor;

    [RelayCommand] private void NewProject() => Reset(DsStore.empty());
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => TryEditorAction("Undo", () => _editor.Undo());
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => TryEditorAction("Redo", () => _editor.Redo());

    private void Reset(DsStore newStore)
    {
        _store = newStore;
        _editor = new EditorApi(_store, maxUndoSize: 100);
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

        OpenTabs.Clear();
        ActiveTab = null;
        SelectedNode = null;
        SelectedArrow = null;

        RebuildAll();
        UpdateTitle();
        StatusText = "Ready";
    }

    private void UpdateTitle()
    {
        var dirty = IsDirty ? " *" : "";
        var file = _currentFilePath is not null ? $" - {System.IO.Path.GetFileName(_currentFilePath)}" : "";
        Title = $"Ds2 Promaker{file}{dirty}";
    }
}
