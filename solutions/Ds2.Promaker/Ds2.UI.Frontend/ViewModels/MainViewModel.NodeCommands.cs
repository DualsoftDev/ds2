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
    private void AddProject() =>
        TryEditorAction("AddProject", () => _editor.AddProjectAndGetId("NewProject"));

    [RelayCommand]
    private void AddSystem()
    {
        var context = ResolveAddTargetContext();

        if (!TryEditorFunc(
                "TryResolveAddSystemTarget",
                () => _editor.TryResolveAddSystemTarget(
                    context.SelectedEntityType,
                    context.SelectedEntityId,
                    context.ActiveTabKind,
                    context.ActiveTabRootId),
                out FSharpOption<Guid>? targetProjectId,
                fallback: null))
            return;

        if (FSharpOption<Guid>.get_IsSome(targetProjectId))
            TryEditorAction(
                "AddSystem",
                () => _editor.AddSystemAndGetId("NewSystem", targetProjectId!.Value, isActive: _activeTreePane == TreePaneKind.Control));
    }

    [RelayCommand]
    private void AddFlow()
    {
        var context = ResolveAddTargetContext();

        if (!TryEditorFunc(
                "TryResolveAddFlowTarget",
                () => _editor.TryResolveAddFlowTarget(
                    context.SelectedEntityType,
                    context.SelectedEntityId,
                    context.ActiveTabKind,
                    context.ActiveTabRootId),
                out FSharpOption<Guid>? targetSystemId,
                fallback: null))
            return;

        if (FSharpOption<Guid>.get_IsSome(targetSystemId))
            TryEditorAction(
                "AddFlow",
                () => _editor.AddFlowAndGetId("NewFlow", targetSystemId!.Value));
    }

    [RelayCommand]
    private void AddWork()
    {
        if (TryResolveTargetId(EntityTypes.Flow, TabKind.Flow, out var flowId))
            TryEditorAction("AddWork", () => _editor.AddWorkAndGetId("NewWork", flowId));
    }

    [RelayCommand]
    private void AddCall()
    {
        if (!TryResolveTargetId(EntityTypes.Work, TabKind.Work, out var workId))
            return;

        var dialog = new CallCreateDialog(
            apiNameFilter =>
            {
                if (!TryEditorFunc(
                        "FindApiDefsByName",
                        () => _editor.FindApiDefsByName(apiNameFilter).ToList(),
                        out List<ApiDefMatch> matches,
                        fallback: []))
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
            if (!TryEditorFunc(
                    "TryFindProjectIdForEntity",
                    () => _editor.TryFindProjectIdForEntity(EntityTypes.Work, workId),
                    out FSharpOption<Guid>? projectIdOpt,
                    fallback: null))
                return;

            if (!FSharpOption<Guid>.get_IsSome(projectIdOpt))
            {
                Log.Error($"AddCall(DeviceMode) failed to resolve project id for workId={workId}");
                StatusText = "[ERROR] Failed to resolve project for selected Work.";
                return;
            }

            TryEditorAction(
                "AddCallsWithDevice",
                () => _editor.AddCallsWithDevice(projectIdOpt!.Value, workId, dialog.CallNames, createDeviceSystem: true));
        }
        else
        {
            TryEditorAction(
                "AddCallWithLinkedApiDefsAndGetId",
                () => _editor.AddCallWithLinkedApiDefsAndGetId(
                    workId,
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
            var arrowIds = _orderedArrowSelection.ToList();
            if (TryEditorAction("RemoveArrows", () => _editor.RemoveArrows(arrowIds)))
                ClearArrowSelection();
            return;
        }

        if (_orderedNodeSelection.Count > 0)
        {
            var selections = _orderedNodeSelection.Select(k => Tuple.Create(k.EntityType, k.Id));
            TryEditorAction("RemoveEntities", () => _editor.RemoveEntities(selections));
            return;
        }

        if (SelectedNode is { } node)
            TryEditorAction(
                "RemoveEntities",
                () => _editor.RemoveEntities(new[] { Tuple.Create(node.EntityType, node.Id) }));
    }

    [RelayCommand]
    private void RenameSelected(string newName)
    {
        if (SelectedNode is null || string.IsNullOrWhiteSpace(newName))
            return;

        TryEditorAction(
            "RenameEntity",
            () => _editor.RenameEntity(SelectedNode.Id, SelectedNode.EntityType, newName));
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

        // 혼합 타입 복사 금지
        var distinctTypes = selected.Select(k => k.EntityType).Distinct().ToList();
        if (distinctTypes.Count > 1)
        {
            MessageBox.Show(
                "같은 종류의 항목만 복사할 수 있습니다.",
                "복사 불가",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 서로 다른 부모에 속한 항목 혼합 복사 금지 (다중 선택인 경우만)
        if (selected.Count > 1)
        {
            var parents = new List<Guid?>();
            foreach (var k in selected)
            {
                if (!TryEditorFunc(
                        "GetEntityParentId",
                        () => _editor.GetEntityParentId(k.EntityType, k.Id),
                        out FSharpOption<Guid>? opt,
                        fallback: null))
                    return;
                parents.Add(FSharpOption<Guid>.get_IsSome(opt) ? opt.Value : (Guid?)null);
            }
            if (parents.Distinct().Count() > 1)
            {
                MessageBox.Show(
                    "서로 다른 위치에 있는 항목은 함께 복사할 수 없습니다.",
                    "복사 불가",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        var batchType = selected[0].EntityType;
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
        if (_clipboardSelection.Count == 0)
            return;

        var target = ResolvePasteTarget();
        if (!target.HasValue)
            return;

        var batchType = _clipboardSelection[0].EntityType;

        // Work/Call 붙여넣기 시 System이 선택된 경우 → Flow를 선택하도록 안내
        if (EntityTypes.Is(target.Value.EntityType, EntityTypes.System)
            && (EntityTypes.Is(batchType, EntityTypes.Work) || EntityTypes.Is(batchType, EntityTypes.Call)))
        {
            MessageBox.Show(
                "붙여넣기 대상으로 Flow를 선택하세요.",
                "붙여넣기 불가",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TryEditorFunc(
                "PasteEntities",
                () => _editor.PasteEntities(
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

    private (string EntityType, Guid EntityId)? ResolvePasteTarget()
    {
        if (SelectedNode is { } selected)
            return (selected.EntityType, selected.Id);

        if (ActiveTab is not { } tab)
            return null;

        var entityTypeOpt = PasteResolvers.entityTypeForTabKind(tab.Kind);
        if (!FSharpOption<string>.get_IsSome(entityTypeOpt))
            return null;

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
