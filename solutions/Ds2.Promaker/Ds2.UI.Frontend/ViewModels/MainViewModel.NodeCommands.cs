using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using Ds2.UI.Frontend.Dialogs;
using Microsoft.FSharp.Core;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void AddProject() => _editor.AddProjectAndGetId("NewProject");

    [RelayCommand]
    private void AddSystem()
    {
        var context = ResolveAddTargetContext();

        var targetProjectId = _editor.TryResolveAddSystemTarget(
            context.SelectedEntityType,
            context.SelectedEntityId,
            context.ActiveTabKind,
            context.ActiveTabRootId);

        if (FSharpOption<Guid>.get_IsSome(targetProjectId))
            _editor.AddSystemAndGetId("NewSystem", targetProjectId.Value, isActive: _activeTreePane == TreePaneKind.Control);
    }

    [RelayCommand]
    private void AddFlow()
    {
        var context = ResolveAddTargetContext();

        var targetSystemId = _editor.TryResolveAddFlowTarget(
            context.SelectedEntityType,
            context.SelectedEntityId,
            context.ActiveTabKind,
            context.ActiveTabRootId);

        if (FSharpOption<Guid>.get_IsSome(targetSystemId))
            _editor.AddFlowAndGetId("NewFlow", targetSystemId.Value);
    }

    [RelayCommand]
    private void AddWork()
    {
        if (TryResolveTargetId(EntityTypes.Flow, TabKind.Flow, out var flowId))
            _editor.AddWorkAndGetId("NewWork", flowId);
    }

    [RelayCommand]
    private void AddCall()
    {
        if (!TryResolveTargetId(EntityTypes.Work, TabKind.Work, out var workId)) return;

        var dialog = new CallCreateDialog(
            apiNameFilter => _editor.FindApiDefsByName(apiNameFilter).ToList())
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        if (dialog.IsDeviceMode)
        {
            var projectIdOpt = _editor.TryFindProjectIdForEntity(EntityTypes.Work, workId);
            var projectId = FSharpOption<Guid>.get_IsSome(projectIdOpt) ? projectIdOpt.Value : Guid.Empty;
            _editor.AddCallsWithDevice(projectId, workId, dialog.CallNames, createDeviceSystem: true);
        }
        else
        {
            _editor.AddCallWithLinkedApiDefsAndGetId(
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
            var selections = _orderedNodeSelection.Select(k => Tuple.Create(k.EntityType, k.Id));
            _editor.RemoveEntities(selections);
            return;
        }

        if (SelectedNode is { } node)
            _editor.RemoveEntities(new[] { Tuple.Create(node.EntityType, node.Id) });
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
        var seen = new HashSet<SelectionKey>();
        foreach (var key in selected)
            if (seen.Add(key))
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

    private readonly record struct AddTargetContext(
        FSharpOption<string>? SelectedEntityType,
        FSharpOption<Guid>? SelectedEntityId,
        FSharpOption<TabKind>? ActiveTabKind,
        FSharpOption<Guid>? ActiveTabRootId);
}
