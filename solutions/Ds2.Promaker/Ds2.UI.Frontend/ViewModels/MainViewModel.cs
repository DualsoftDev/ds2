using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Ds2.UI.Frontend;
using Ds2.UI.Frontend.Dialogs;
using Microsoft.FSharp.Core;
using Microsoft.Win32;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private EditorApi _editor;
    private DsStore _store;
    private readonly Dispatcher _dispatcher;
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

    [ObservableProperty] private EntityNode? _selectedNode;
    [ObservableProperty] private CanvasTab? _activeTab;
    [ObservableProperty] private string _title = "Ds2 Promaker";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private ArrowNode? _selectedArrow;

    public EditorApi Editor => _editor;
    public DsStore Store => _store;

    partial void OnActiveTabChanged(CanvasTab? value)
    {
        foreach (var t in OpenTabs)
            t.IsActive = t == value;

        ClearArrowSelection();
        RefreshCanvasForActiveTab();
    }

    [RelayCommand] private void NewProject() => Reset(DsStore.empty());
    [RelayCommand] private void Undo() => _editor.Undo();
    [RelayCommand] private void Redo() => _editor.Redo();

    [RelayCommand]
    private void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        _editor.LoadFromFile(dlg.FileName);
        _currentFilePath = dlg.FileName;
        IsDirty = false;
        UpdateTitle();
    }

    [RelayCommand]
    private void SaveFile()
    {
        if (_currentFilePath is null)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                DefaultExt = ".json"
            };

            if (dlg.ShowDialog() != true) return;
            _currentFilePath = dlg.FileName;
        }

        _editor.SaveToFile(_currentFilePath);
        IsDirty = false;
        UpdateTitle();
        StatusText = "Saved.";
    }

    [RelayCommand]
    private void AddProject() => _editor.AddProject("NewProject");

    [RelayCommand]
    private void AddSystem()
    {
        var context = ResolveAddTargetContext();

        var targetProjectId = AddTargetQueries.tryResolveAddSystemTarget(
            _store,
            context.SelectedEntityType,
            context.SelectedEntityId,
            context.ActiveTabKind,
            context.ActiveTabRootId);

        if (FSharpOption<Guid>.get_IsSome(targetProjectId))
            _editor.AddSystem("NewSystem", targetProjectId.Value, isActive: _activeTreePane == TreePaneKind.Control);
    }

    [RelayCommand]
    private void AddFlow()
    {
        var context = ResolveAddTargetContext();

        var targetSystemId = AddTargetQueries.tryResolveAddFlowTarget(
            _store,
            context.SelectedEntityType,
            context.SelectedEntityId,
            context.ActiveTabKind,
            context.ActiveTabRootId);

        if (FSharpOption<Guid>.get_IsSome(targetSystemId))
            _editor.AddFlow("NewFlow", targetSystemId.Value);
    }

    [RelayCommand]
    private void AddWork()
    {
        if (TryResolveTargetId(EntityTypes.Flow, TabKind.Flow, out var flowId))
            _editor.AddWork("NewWork", flowId);
    }

    [RelayCommand]
    private void AddCall()
    {
        if (!TryResolveTargetId(EntityTypes.Work, TabKind.Work, out var workId)) return;

        var dialog = new CallCreateDialog(_store) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        if (dialog.IsDeviceMode)
        {
            var projectIdOpt = EntityHierarchyQueries.tryFindProjectIdForEntity(_store, EntityTypes.Work, workId);
            var projectId = FSharpOption<Guid>.get_IsSome(projectIdOpt) ? projectIdOpt.Value : Guid.Empty;
            _editor.AddCallsWithDevice(projectId, workId, dialog.CallNames, createDeviceSystem: true);
        }
        else
        {
            _editor.AddCallWithLinkedApiDefs(
                workId, dialog.DevicesAlias, dialog.ApiName,
                dialog.SelectedApiDefs.Select(m => m.ApiDefId));
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (_orderedArrowSelection.Count > 0)
        {
            var arrowIds = _orderedArrowSelection.ToList();
            _editor.RemoveArrows(arrowIds);
            ClearArrowSelection();
            return;
        }

        if (_orderedNodeSelection.Count > 0)
        {
            var key = _orderedNodeSelection[^1];
            _editor.RemoveEntity(key.EntityType, key.Id);
            return;
        }

        if (SelectedNode is { } node)
            _editor.RemoveEntity(node.EntityType, node.Id);
    }

    [RelayCommand]
    private void RenameSelected(string newName)
    {
        if (SelectedNode is null || string.IsNullOrWhiteSpace(newName)) return;
        _editor.RenameEntity(SelectedNode.Id, SelectedNode.EntityType, newName);
    }

    [RelayCommand]
    private void CopySelected()
    {
        var selected = _orderedNodeSelection
            .Where(k => PasteResolvers.isCopyableEntityType(k.EntityType))
            .ToList();

        if (selected.Count == 0 && SelectedNode is { } single && PasteResolvers.isCopyableEntityType(single.EntityType))
            selected.Add(new SelectionKey(single.Id, single.EntityType));

        if (selected.Count == 0)
            return;

        // Keep one entity type per copy batch to avoid ambiguous mixed-type paste behavior.
        var batchType = selected[0].EntityType;
        selected = selected.Where(k => k.EntityType == batchType).ToList();

        _clipboardSelection.Clear();
        foreach (var key in selected)
            if (!_clipboardSelection.Contains(key))
                _clipboardSelection.Add(key);

        StatusText = $"Copied {_clipboardSelection.Count} {batchType}(s).";
    }

    [RelayCommand]
    private void PasteCopied()
    {
        if (_clipboardSelection.Count == 0) return;

        var target = ResolvePasteTarget();
        if (!target.HasValue) return;

        var batchType = _clipboardSelection[0].EntityType;
        var pastedCount = _editor.PasteEntities(
            batchType,
            _clipboardSelection.Select(k => k.Id),
            target.Value.EntityType,
            target.Value.EntityId);

        if (pastedCount > 0)
            StatusText = $"Pasted {pastedCount} {batchType}(s).";
    }

    private void WireEvents()
    {
        var observable = (IObservable<EditorEvent>)_editor.OnEvent;
        observable.Subscribe(new ActionObserver<EditorEvent>(evt => _dispatcher.Invoke(() => HandleEvent(evt))));
    }

    private void HandleEvent(EditorEvent evt)
    {
        if (TryGetAddedEntityId(evt) is { } addedId)
        {
            RebuildAll();
            ExpandNodeAndAncestors(addedId);
            return;
        }

        if (evt is EditorEvent.EntityRenamed ren)
        {
            foreach (var n in CanvasNodes)
                if (n.Id == ren.id) n.Name = ren.newName;

            var tab = OpenTabs.FirstOrDefault(t => t.RootId == ren.id);
            if (tab is not null) tab.Title = ren.newName;

            RebuildAll();
            return;
        }

        if (evt is EditorEvent.WorkMoved wm)
        {
            ApplyNodeMove(wm.id, wm.newPos);
            return;
        }

        if (evt is EditorEvent.CallMoved cm)
        {
            ApplyNodeMove(cm.id, cm.newPos);
            return;
        }

        if (evt is EditorEvent.UndoRedoChanged ur)
        {
            CanUndo = ur.canUndo;
            CanRedo = ur.canRedo;
            IsDirty = ur.canUndo;
            UpdateTitle();
            return;
        }

        RebuildAll();
    }

    private static Guid? TryGetAddedEntityId(EditorEvent evt) =>
        evt switch
        {
            EditorEvent.SystemAdded systemAdded => systemAdded.Item.Id,
            EditorEvent.FlowAdded flowAdded => flowAdded.Item.Id,
            EditorEvent.WorkAdded workAdded => workAdded.Item.Id,
            EditorEvent.CallAdded callAdded => callAdded.Item.Id,
            _ => null
        };

    private (string EntityType, Guid EntityId)? ResolvePasteTarget()
    {
        if (SelectedNode is { } selected)
            return (selected.EntityType, selected.Id);

        if (ActiveTab is not { } tab) return null;

        var entityTypeOpt = PasteResolvers.entityTypeForTabKind(tab.Kind);
        if (!FSharpOption<string>.get_IsSome(entityTypeOpt)) return null;
        return (entityTypeOpt.Value, tab.RootId);
    }

    private bool TryResolveTargetId(string selectedEntityType, TabKind activeTabKind, out Guid targetId)
    {
        if (SelectedNode is { EntityType: var type } node && type == selectedEntityType)
        {
            targetId = node.Id;
            return true;
        }

        if (ActiveTab is { Kind: var kind } tab && kind == activeTabKind)
        {
            targetId = tab.RootId;
            return true;
        }

        targetId = Guid.Empty;
        return false;
    }

    private AddTargetContext ResolveAddTargetContext()
    {
        var selectedEntityType = SelectedNode is null ? null : FSharpOption<string>.Some(SelectedNode.EntityType);
        var selectedEntityId = SelectedNode is null ? null : FSharpOption<Guid>.Some(SelectedNode.Id);
        var activeTabKind = ActiveTab is null ? null : FSharpOption<TabKind>.Some(ActiveTab.Kind);
        var activeTabRootId = ActiveTab is null ? null : FSharpOption<Guid>.Some(ActiveTab.RootId);
        return new AddTargetContext(selectedEntityType, selectedEntityId, activeTabKind, activeTabRootId);
    }

    private void Reset(DsStore newStore)
    {
        _store = newStore;
        _editor = new EditorApi(_store, maxUndoSize: 100);
        WireEvents();

        _currentFilePath = null;
        IsDirty = false;
        CanUndo = false;
        CanRedo = false;

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

    private readonly record struct AddTargetContext(
        FSharpOption<string>? SelectedEntityType,
        FSharpOption<Guid>? SelectedEntityId,
        FSharpOption<TabKind>? ActiveTabKind,
        FSharpOption<Guid>? ActiveTabRootId);
}

public enum TreePaneKind
{
    Control,
    Device
}

public partial class CanvasTab : ObservableObject
{
    public CanvasTab(Guid rootId, TabKind kind, string title)
    {
        RootId = rootId;
        Kind = kind;
        _title = title;
    }

    public Guid RootId { get; }
    public TabKind Kind { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isActive;
}

file sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnCompleted() { }
    public void OnError(Exception error) { }
}
