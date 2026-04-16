using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (Selection.OrderedArrowSelection.Count > 0)
        {
            var count = Selection.OrderedArrowSelection.Count;
            if (TryEditorAction(() => _store.RemoveArrows(Selection.OrderedArrowSelection)))
            {
                Selection.ClearArrowSelection();
                StatusText = $"Deleted {count} arrow(s).";
            }
            return;
        }

        if (Selection.OrderedNodeSelection.Count > 0)
        {
            if (!GuardSimulationSemanticEdit("노드 삭제"))
                return;

            var selections = Selection.OrderedNodeSelection
                .Where(k => k.EntityKind != EntityKind.Project)
                .Select(k => Tuple.Create(k.EntityKind, k.Id))
                .ToList();
            if (selections.Count > 0 && TryEditorAction(() => _store.RemoveEntities(selections)))
                StatusText = $"Deleted {selections.Count} item(s).";
            return;
        }

        if (SelectedNode is { EntityType: not EntityKind.Project } node)
        {
            if (!GuardSimulationSemanticEdit("노드 삭제"))
                return;

            if (TryEditorAction(
                    () => _store.RemoveEntities(new[] { Tuple.Create(node.EntityType, node.Id) })))
                StatusText = "Deleted 1 item.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddReferenceWork()
    {
        if (!GuardSimulationSemanticEdit("레퍼런스 Work 추가"))
            return;

        if (SelectedNode is not { EntityType: EntityKind.Work } node) return;
        TryEditorAction(() => _store.AddReferenceWork(node.Id));
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddReferenceCall()
    {
        if (!GuardSimulationSemanticEdit("레퍼런스 Call 추가"))
            return;

        if (SelectedNode is not { EntityType: EntityKind.Call } node) return;
        TryEditorAction(() => _store.AddReferenceCall(node.Id));
    }

    [RelayCommand]
    private void RenameSelected(string newName)
    {
        if (SelectedNode is null)
            return;

        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusText = "Name cannot be empty.";
            return;
        }

        if (!GuardSimulationSemanticEdit("이름 변경"))
            return;

        if (TryEditorAction(
                () => _store.RenameEntity(SelectedNode.Id, SelectedNode.EntityType, newName)))
            StatusText = $"Renamed to '{newName}'.";
    }

    [RelayCommand(CanExecute = nameof(CanCopySelected))]
    private void CopySelected()
    {
        var candidates = Selection.OrderedNodeSelection.Count > 0
            ? Selection.OrderedNodeSelection
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
            _dialogService.ShowWarning("같은 종류의 항목만 복사할 수 있습니다.");
            return;
        }
        if (result.IsMixedParents)
        {
            _dialogService.ShowWarning("서로 다른 위치에 있는 항목은 함께 복사할 수 없습니다.");
            return;
        }
        if (result is not CopyValidationResult.Ok ok)
        {
            StatusText = "Nothing to copy.";
            return;
        }

        var validated = ok.Item;
        _clipboardSelection.Clear();
        _pasteCount = 0;
        foreach (var key in validated)
            _clipboardSelection.Add(key);

        StatusText = $"Copied {_clipboardSelection.Count} {validated[0].EntityKind}(s).";
        RefreshEditorCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanPasteCopied))]
    private void PasteCopied()
    {
        if (!GuardSimulationSemanticEdit("붙여넣기"))
            return;

        if (_clipboardSelection.Count == 0)
        {
            StatusText = "Clipboard is empty.";
            return;
        }

        var target = ResolvePasteTarget();
        if (!target.HasValue)
        {
            StatusText = "Select a paste target or open a canvas tab.";
            return;
        }

        var batchType = _clipboardSelection[0].EntityKind;
        if (target.Value.EntityType == EntityKind.System
            && (batchType == EntityKind.Work || batchType == EntityKind.Call))
        {
            _dialogService.ShowWarning("붙여넣기 대상으로 Flow를 선택하세요.");
            return;
        }

        if (batchType == EntityKind.Flow)
        {
            PasteFlowsWithRename(target.Value);
            return;
        }

        var pasteIndex = _pasteCount * _clipboardSelection.Count;
        if (!TryEditorFunc(
                () => _store.PasteEntities(
                    batchType,
                    _clipboardSelection.Select(k => k.Id),
                    target.Value.EntityType,
                    target.Value.EntityId,
                    pasteIndex),
                out PasteResult pasteResult,
                fallback: PasteResult.NewOk(Microsoft.FSharp.Collections.ListModule.Empty<Guid>())))
            return;

        if (pasteResult is PasteResult.Blocked blocked)
        {
            if (blocked.Item.IsSameWorkPaste)
                _dialogService.ShowWarning("같은 Work 내의 Call은 복사할 수 없습니다.");
            else if (blocked.Item.IsDuplicateCallInWork)
                _dialogService.ShowWarning("대상 Work에 이미 동일한 이름의 Call이 존재합니다.");
            return;
        }

        var pastedIds = ((PasteResult.Ok)pasteResult).Item;
        _pasteCount++;
        ApplyPasteSelection(pastedIds, $"Pasted {pastedIds.Length} {batchType}(s).");
    }

    private void PasteFlowsWithRename((EntityKind EntityType, Guid EntityId) target)
    {
        var pastedFlowIds = new List<Guid>();
        var skippedMissingFlows = 0;
        var targetSystemIdOpt = StoreHierarchyQueries.resolveTarget(
            _store, EntityKind.System, target.EntityType, target.EntityId);

        foreach (var key in _clipboardSelection)
        {
            if (!_store.FlowsReadOnly.TryGetValue(key.Id, out var srcFlow))
            {
                skippedMissingFlows++;
                continue;
            }

            var sysId = targetSystemIdOpt != null ? targetSystemIdOpt.Value : srcFlow.ParentId;
            var existingNames = Queries.flowsOf(sysId, _store).Select(f => f.Name).ToList();
            var suggestedName = GetUniqueName(srcFlow.Name, existingNames, "_");
            var newName = _dialogService.PromptName("Flow 복사 — 새 이름", suggestedName);
            if (newName is null) return;

            if (!TryEditorRef(
                    () => _store.PasteFlowWithRename(key.Id, sysId, newName),
                    out var resultOpt))
                return;

            if (resultOpt != null)
                pastedFlowIds.Add(resultOpt.Value);
        }

        var workIds = pastedFlowIds
            .SelectMany(fId => Queries.worksOf(fId, _store))
            .Select(w => w.Id)
            .ToList();

        var status = skippedMissingFlows > 0
            ? $"Pasted {pastedFlowIds.Count} Flow(s); skipped {skippedMissingFlows} missing Flow(s)."
            : $"Pasted {pastedFlowIds.Count} Flow(s).";
        ApplyPasteSelection(workIds, status);
    }
}
