using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
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
    private readonly List<SelectionKey> _clipboardSelection = [];
    private bool _rebuildQueued;
    private readonly List<Action> _pendingRebuildActions = [];

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _store = new DsStore();
        Selection = new SelectionState(new SelectionHost(this));
        Canvas = new CanvasWorkspaceState(new CanvasHost(this));
        Simulation = new SimulationPanelState(() => _store, _dispatcher, Canvas.CanvasNodes, value => StatusText = value);
        PropertyPanel = new PropertyPanelState(new PropertyPanelHost(this));
        WireEvents();
    }

    public ObservableCollection<EntityNode> ControlTreeRoots { get; } = [];
    public ObservableCollection<EntityNode> DeviceTreeRoots { get; } = [];
    public ObservableCollection<HistoryPanelItem> HistoryItems { get; } = [];
    public CanvasWorkspaceState Canvas { get; }
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
    [ObservableProperty] private int _currentHistoryIndex;
    [ObservableProperty] private ArrowNode? _selectedArrow;

    public static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    public Xywh? PendingAddPosition { get; set; }

    public Action? FocusNameEditorRequested { get; set; }

    [RelayCommand]
    private void FocusNameEditor()
    {
        if (SelectedNode is not null)
            FocusNameEditorRequested?.Invoke();
    }

    partial void OnSelectedNodeChanged(EntityNode? value)
    {
        PropertyPanel.SyncSelectedNode(value);
    }

    [RelayCommand]
    private void NewProject()
    {
        if (!ConfirmDiscardChanges())
            return;

        Reset();
        TryEditorAction(() => _store.AddProject("NewProject"));
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
        CanUndo = false;
        CanRedo = false;
        HistoryItems.Clear();
        HistoryItems.Add(new HistoryPanelItem("(초기 상태)", isRedo: false));
        CurrentHistoryIndex = 0;

        _clipboardSelection.Clear();
        Selection.Reset();
        Canvas.Reset();
        _rebuildQueued = false;
        _pendingRebuildActions.Clear();
        SelectedNode = null;
        SelectedArrow = null;

        RebuildAll();
        UpdateTitle();
        StatusText = "Ready";
    }

    private bool ConfirmDiscardChanges()
    {
        if (!IsDirty)
            return true;

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

    public sealed class CanvasHost
    {
        private readonly MainViewModel _owner;

        public CanvasHost(MainViewModel owner)
        {
            _owner = owner;
        }

        public DsStore Store => _owner._store;
        public SelectionState Selection => _owner.Selection;
        public EntityNode? SelectedNode => _owner.SelectedNode;
        public ObservableCollection<EntityNode> ControlTreeRoots => _owner.ControlTreeRoots;
        public ObservableCollection<EntityNode> DeviceTreeRoots => _owner.DeviceTreeRoots;

        public bool TryFunc<T>(Func<T> func, out T value, T fallback, string? statusOverride = null) =>
            _owner.TryEditorFunc(func, out value, fallback, statusOverride: statusOverride);

        public bool TryRef<T>(Func<T> func, out T value, string? statusOverride = null) where T : class
        {
            if (_owner.TryEditorRef(func, out var resolved, statusOverride: statusOverride))
            {
                value = resolved;
                return true;
            }

            value = null!;
            return false;
        }

        public void RequestRebuildAll(Action? afterRebuild = null) => _owner.RequestRebuildAll(afterRebuild);

        public void SetStatusText(string text) => _owner.StatusText = text;

        public void ExpandNodeAndAncestors(Guid nodeId) => _owner.Selection.ExpandNodeAndAncestors(nodeId);

        public void SelectNodeFromCanvas(EntityNode node, bool ctrlPressed, bool shiftPressed) =>
            _owner.Selection.SelectNodeFromCanvas(node, ctrlPressed, shiftPressed);
    }

    public sealed class PropertyPanelHost
    {
        private readonly MainViewModel _owner;

        public PropertyPanelHost(MainViewModel owner)
        {
            _owner = owner;
        }

        public EntityNode? SelectedNode => _owner.SelectedNode;
        public DsStore Store => _owner._store;

        public bool TryAction(Action action) => _owner.TryEditorAction(action);

        public bool TryFunc<T>(Func<T> func, out T value, T fallback) =>
            _owner.TryEditorFunc(func, out value, fallback);

        public bool TryRef<T>(Func<T> func, out T value) where T : class
        {
            if (_owner.TryEditorRef(func, out var resolved))
            {
                value = resolved;
                return true;
            }

            value = null!;
            return false;
        }

        public void SetStatusText(string text) => _owner.StatusText = text;

        public void RenameSelected(string newName) => _owner.RenameSelectedCommand.Execute(newName);

        public void OpenParentCanvasAndFocusNode(Guid entityId, EntityKind entityKind) =>
            _owner.Canvas.OpenParentCanvasAndFocusNode(entityId, entityKind);

        public bool ShowOwnedDialog(Window dialog)
        {
            if (Application.Current.MainWindow is { } owner)
                dialog.Owner = owner;

            return dialog.ShowDialog() == true;
        }
    }

    public sealed class SelectionHost
    {
        private readonly MainViewModel _owner;

        public SelectionHost(MainViewModel owner)
        {
            _owner = owner;
        }

        public DsStore Store => _owner._store;
        public ObservableCollection<EntityNode> ControlTreeRoots => _owner.ControlTreeRoots;
        public ObservableCollection<EntityNode> DeviceTreeRoots => _owner.DeviceTreeRoots;
        public ObservableCollection<EntityNode> CanvasNodes => _owner.Canvas.CanvasNodes;
        public ObservableCollection<ArrowNode> CanvasArrows => _owner.Canvas.CanvasArrows;

        public EntityNode? SelectedNode
        {
            get => _owner.SelectedNode;
            set => _owner.SelectedNode = value;
        }

        public ArrowNode? SelectedArrow
        {
            get => _owner.SelectedArrow;
            set => _owner.SelectedArrow = value;
        }

        public void SetStatusText(string text) => _owner.StatusText = text;

        public bool TryFunc<T>(Func<T> func, out T value, T fallback, string? statusOverride = null) =>
            _owner.TryEditorFunc(func, out value, fallback, statusOverride: statusOverride);
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
    }

    private void RebuildAll()
    {
        var prevSelection = Selection.OrderedNodeSelection.ToList();
        var prevSelectedArrowIds = Selection.OrderedArrowSelection.ToList();
        var expandedNodes = Selection.GetExpandedKeys();

        ControlTreeRoots.Clear();
        DeviceTreeRoots.Clear();

        if (!TryEditorRef(
                () => _store.BuildTrees(),
                out var trees,
                statusOverride: "[ERROR] Failed to rebuild tree views."))
            return;

        foreach (var info in trees.Item1)
            ControlTreeRoots.Add(MapToEntityNode(info));
        foreach (var info in trees.Item2)
            DeviceTreeRoots.Add(MapToEntityNode(info));

        Selection.ApplyExpansionStateTo(ControlTreeRoots, expandedNodes);
        Selection.ApplyExpansionStateTo(DeviceTreeRoots, expandedNodes);

        var deadTabs = new List<CanvasTab>();
        foreach (var t in Canvas.OpenTabs)
        {
            var title = Canvas.ResolveTabTitle(t);
            if (title is null)
                deadTabs.Add(t);
            else
                t.Title = title;
        }

        foreach (var t in deadTabs)
            Canvas.OpenTabs.Remove(t);

        if (Canvas.ActiveTab is not null && !Canvas.OpenTabs.Contains(Canvas.ActiveTab))
            Canvas.ActiveTab = Canvas.OpenTabs.Count > 0 ? Canvas.OpenTabs[0] : null;

        Canvas.RefreshCanvasForActiveTab();
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
}

public sealed class HistoryPanelItem(string label, bool isRedo)
{
    public string Label  { get; } = label;
    public bool   IsRedo { get; } = isRedo;
}
