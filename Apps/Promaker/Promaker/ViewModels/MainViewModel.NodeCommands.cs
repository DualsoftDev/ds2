using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void AddProject()
    {
        var name = DialogHelpers.PromptName("New Project", "NewProject");
        if (name is null) return;
        TryEditorAction(() => _store.AddProject(name));
    }

    [RelayCommand]
    private void AddSystem()
    {
        var name = DialogHelpers.PromptName("New System", "NewSystem");
        if (name is null) return;
        var (selType, selId, tabKind, tabRoot) = SnapshotContext();
        TryEditorAction(() => _store.AddSystemResolved(
            name, _activeTreePane == TreePaneKind.Control,
            selType, selId, tabKind, tabRoot));
    }

    [RelayCommand]
    private void AddFlow()
    {
        var name = DialogHelpers.PromptName("New Flow", "NewFlow");
        if (name is null) return;
        var (selType, selId, tabKind, tabRoot) = SnapshotContext();
        TryEditorAction(() => _store.AddFlowResolved(
            name, selType, selId, tabKind, tabRoot));
    }

    private (EntityKind? SelectedEntityKind, Guid? SelectedEntityId, TabKind? ActiveTabKind, Guid? ActiveTabRootId) SnapshotContext() =>
        (SelectedNode?.EntityType, SelectedNode?.Id, ActiveTab?.Kind, ActiveTab?.RootId);

    [RelayCommand]
    private void AddWork()
    {
        var flowId = ResolveTargetId(EntityKind.Flow, TabKind.Flow);
        if (flowId is not { } id) return;

        var name = DialogHelpers.PromptName("New Work", "NewWork");
        if (name is null) return;

        if (!TryEditorFunc(() => _store.AddWork(name, id), out Guid workId, fallback: Guid.Empty))
            return;
        if (workId == Guid.Empty) return;

        var pos = ConsumeAddPosition();
        TryEditorAction(() => _store.MoveEntities([new MoveEntityRequest(workId, pos)]));
    }

    [RelayCommand]
    private void AddCall()
    {
        var workId = ResolveTargetId(EntityKind.Work, TabKind.Work);
        if (workId is not { } targetWorkId)
            return;

        var dialog = new CallCreateDialog(
            apiNameFilter =>
            {
                if (!TryEditorRef(
                        () => _store.FindApiDefsByName(apiNameFilter),
                        out var matches))
                    return [];

                return matches;
            })
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        var basePos = ConsumeAddPosition();

        if (dialog.IsDeviceMode)
        {
            var existingIds = CanvasNodes.Select(n => n.Id).ToHashSet();
            TryEditorAction(
                () => _store.AddCallsWithDeviceResolved(EntityKind.Work, targetWorkId, targetWorkId, dialog.CallNames, true));
            RequestRebuildAll(() =>
            {
                var newCalls = CanvasNodes
                    .Where(n => n.EntityType == EntityKind.Call && !existingIds.Contains(n.Id))
                    .ToList();
                if (newCalls.Count > 0)
                {
                    var requests = newCalls
                        .Select((n, i) => new MoveEntityRequest(n.Id, CascadePosition(basePos, i)))
                        .ToList();
                    TryMoveEntitiesFromCanvas(requests);
                }
            });
        }
        else
        {
            if (TryEditorFunc(
                    () => _store.AddCallWithLinkedApiDefs(
                        targetWorkId,
                        dialog.DevicesAlias,
                        dialog.ApiName,
                        dialog.SelectedApiDefs.Select(m => m.ApiDefId)),
                    out Guid callId,
                    fallback: Guid.Empty)
                && callId != Guid.Empty)
            {
                TryEditorAction(() => _store.MoveEntities([new MoveEntityRequest(callId, basePos)]));
            }
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (_orderedArrowSelection.Count > 0)
        {
            if (TryEditorAction(() => _store.RemoveArrows(_orderedArrowSelection)))
                ClearArrowSelection();
            return;
        }

        if (_orderedNodeSelection.Count > 0)
        {
            var selections = _orderedNodeSelection.Select(k => Tuple.Create(k.EntityKind, k.Id));
            TryEditorAction(() => _store.RemoveEntities(selections));
            return;
        }

        if (SelectedNode is { } node)
            TryEditorAction(
                () => _store.RemoveEntities(new[] { Tuple.Create(node.EntityType, node.Id) }));
    }

    [RelayCommand]
    private void RenameSelected(string newName)
    {
        if (SelectedNode is null || string.IsNullOrWhiteSpace(newName))
            return;

        TryEditorAction(
            () => _store.RenameEntity(SelectedNode.Id, SelectedNode.EntityType, newName));
    }

    [RelayCommand]
    private void CopySelected()
    {
        var candidates = _orderedNodeSelection.Count > 0
            ? _orderedNodeSelection
            : SelectedNode is { } single
                ? [new SelectionKey(single.Id, single.EntityType)]
                : (IReadOnlyList<SelectionKey>)[];

        if (!TryEditorFunc(
                () => _store.ValidateCopySelection(candidates),
                out CopyValidationResult result,
                fallback: CopyValidationResult.NothingToCopy))
            return;

        if (result.IsMixedTypes)
        {
            MessageBox.Show("같은 종류의 항목만 복사할 수 있습니다.", "복사 불가",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (result.IsMixedParents)
        {
            MessageBox.Show("서로 다른 위치에 있는 항목은 함께 복사할 수 없습니다.", "복사 불가",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (result is not CopyValidationResult.Ok ok)
            return;

        var validated = ok.Item;
        _clipboardSelection.Clear();
        foreach (var key in validated)
            _clipboardSelection.Add(key);

        StatusText = $"Copied {_clipboardSelection.Count} {validated[0].EntityKind}(s).";
    }

    [RelayCommand]
    private void PasteCopied()
    {
        if (_clipboardSelection.Count == 0)
            return;

        var target = ResolvePasteTarget();
        if (!target.HasValue)
            return;

        var batchType = _clipboardSelection[0].EntityKind;

        // Work/Call 붙여넣기 시 System이 선택된 경우 → Flow를 선택하도록 안내
        if (target.Value.EntityType == EntityKind.System
            && (batchType == EntityKind.Work || batchType == EntityKind.Call))
        {
            MessageBox.Show(
                "붙여넣기 대상으로 Flow를 선택하세요.",
                "붙여넣기 불가",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TryEditorRef(
                () => _store.PasteEntities(
                    batchType,
                    _clipboardSelection.Select(k => k.Id),
                    target.Value.EntityType,
                    target.Value.EntityId),
                out var pastedIds))
            return;

        if (pastedIds.Length > 0)
        {
            StatusText = $"Pasted {pastedIds.Length} {batchType}(s).";
            var idSet = new HashSet<Guid>(pastedIds);
            RequestRebuildAll(() =>
            {
                ClearNodeSelection();
                foreach (var node in CanvasNodes.Where(n => idSet.Contains(n.Id)))
                    SelectNodeFromCanvas(node, ctrlPressed: true, shiftPressed: false);
            });
        }
    }

    private (EntityKind EntityType, Guid EntityId)? ResolvePasteTarget()
    {
        if (SelectedNode is { } selected)
            return (selected.EntityType, selected.Id);

        if (ActiveTab is not { } tab)
            return null;

        return (EntityHierarchyQueries.entityKindForTabKind(tab.Kind), tab.RootId);
    }

    private const int CascadeOffset = 30;

    private Xywh ConsumeAddPosition()
    {
        var pos = PendingAddPosition;
        PendingAddPosition = null;

        if (pos is { } p)
            return CascadePosition(p, 0);

        if (GetViewportCenterRequested?.Invoke() is { } center)
            return CascadePosition(
                new Xywh(
                    (int)center.X - UiDefaults.DefaultNodeWidth / 2,
                    (int)center.Y - UiDefaults.DefaultNodeHeight / 2,
                    UiDefaults.DefaultNodeWidth, UiDefaults.DefaultNodeHeight),
                0);

        return UiDefaults.createDefaultNodeBounds();
    }

    private Xywh CascadePosition(Xywh basePos, int index)
    {
        var x = basePos.X + CascadeOffset * index;
        var y = basePos.Y + CascadeOffset * index;

        while (CanvasNodes.Any(n =>
                   Math.Abs(n.X - x) < 10 && Math.Abs(n.Y - y) < 10))
        {
            x += CascadeOffset;
            y += CascadeOffset;
        }

        return new Xywh(x, y, basePos.W, basePos.H);
    }

    private Guid? ResolveTargetId(EntityKind selectedEntityType, TabKind activeTabKind)
    {
        if (SelectedNode is { EntityType: var type } node && type == selectedEntityType)
            return node.Id;

        if (ActiveTab is { Kind: var kind } tab && kind == activeTabKind)
            return tab.RootId;

        return null;
    }
}
