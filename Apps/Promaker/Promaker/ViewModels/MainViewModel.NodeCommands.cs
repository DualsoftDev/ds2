using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void AddProject() =>
        TryEditorAction(() => _store.AddProject("NewProject"));

    [RelayCommand]
    private void AddSystem()
    {
        var (selType, selId, tabKind, tabRoot) = SnapshotContext();
        TryEditorAction(() => _store.AddSystemResolved(
            "NewSystem", _activeTreePane == TreePaneKind.Control,
            selType, selId, tabKind, tabRoot));
    }

    [RelayCommand]
    private void AddFlow()
    {
        var (selType, selId, tabKind, tabRoot) = SnapshotContext();
        TryEditorAction(() => _store.AddFlowResolved(
            "NewFlow", selType, selId, tabKind, tabRoot));
    }

    private (EntityKind? SelectedEntityKind, Guid? SelectedEntityId, TabKind? ActiveTabKind, Guid? ActiveTabRootId) SnapshotContext() =>
        (SelectedNode?.EntityType, SelectedNode?.Id, ActiveTab?.Kind, ActiveTab?.RootId);

    [RelayCommand]
    private void AddWork()
    {
        var flowId = ResolveTargetId(EntityKind.Flow, TabKind.Flow);
        if (flowId is { } id)
            TryEditorAction(() => _store.AddWork("NewWork", id));
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

        if (dialog.IsDeviceMode)
        {
            TryEditorAction(
                () => _store.AddCallsWithDeviceResolved(EntityKind.Work, targetWorkId, targetWorkId, dialog.CallNames, true));
        }
        else
        {
            TryEditorAction(
                () => _store.AddCallWithLinkedApiDefs(
                    targetWorkId,
                    dialog.DevicesAlias,
                    dialog.ApiName,
                    dialog.SelectedApiDefs.Select(m => m.ApiDefId)));
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

        if (!TryEditorFunc(
                () => _store.PasteEntities(
                    batchType,
                    _clipboardSelection.Select(k => k.Id),
                    target.Value.EntityType,
                    target.Value.EntityId),
                out int pastedCount,
                fallback: 0))
            return;

        if (pastedCount > 0)
            StatusText = $"Pasted {pastedCount} {batchType}(s).";
    }

    private (EntityKind EntityType, Guid EntityId)? ResolvePasteTarget()
    {
        if (SelectedNode is { } selected)
            return (selected.EntityType, selected.Id);

        if (ActiveTab is not { } tab)
            return null;

        return (EntityHierarchyQueries.entityKindForTabKind(tab.Kind), tab.RootId);
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
