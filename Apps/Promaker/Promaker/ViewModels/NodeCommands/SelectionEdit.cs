using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (Selection.OrderedArrowSelection.Count > 0)
        {
            if (!GuardArrowEditByRuntimeMode("Arrow 삭제"))
                return;
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
        _clipboardIsCut = false;
        _pasteCount = 0;
        foreach (var key in validated)
            _clipboardSelection.Add(key);

        // Copy 는 cut 아님 — 이전 cut visual 이 남아있다면 clear
        Selection.ApplyCutPendingVisuals([]);

        StatusText = $"Copied {_clipboardSelection.Count} {validated[0].EntityKind}(s).";
        RefreshEditorCommandStates();
    }

    private bool CanCutSelected()
    {
        if (!CanCopySelected())
            return false;
        // Cut 은 Call 만 지원 (1차 release). Flow/Work cut 은 cascade 폭이 커서 별도 명세 필요.
        var candidates = Selection.OrderedNodeSelection.Count > 0
            ? Selection.OrderedNodeSelection
            : SelectedNode is { } single
                ? new[] { new SelectionKey(single.Id, single.EntityType) }
                : Array.Empty<SelectionKey>();
        return candidates.Count > 0 && candidates.All(k => k.EntityKind == EntityKind.Call);
    }

    [RelayCommand(CanExecute = nameof(CanCutSelected))]
    private void CutSelected()
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
            _dialogService.ShowWarning("같은 종류의 항목만 이동할 수 있습니다.");
            return;
        }
        if (result.IsMixedParents)
        {
            _dialogService.ShowWarning("서로 다른 위치에 있는 항목은 함께 이동할 수 없습니다.");
            return;
        }
        if (result is not CopyValidationResult.Ok ok)
        {
            StatusText = "Nothing to cut.";
            return;
        }

        var validated = ok.Item;
        if (validated[0].EntityKind != EntityKind.Call)
        {
            _dialogService.ShowWarning("Cut(이동) 은 Call 만 지원합니다.");
            return;
        }

        _clipboardSelection.Clear();
        _clipboardIsCut = true;
        _pasteCount = 0;
        foreach (var key in validated)
            _clipboardSelection.Add(key);

        // Cut visual: 클립보드 entry 들을 디밍/반투명 표시
        Selection.ApplyCutPendingVisuals(_clipboardSelection);

        StatusText = $"Cut {_clipboardSelection.Count} Call(s) — Ctrl+V to move.";
        RefreshEditorCommandStates();
    }

    /// <summary>
    /// target Work 의 Flow 가 clipboard Call 들의 *어느 source Flow 와도 다른지*. 다르면 cross-flow paste/move 분기 → 다이얼로그 필요.
    /// </summary>
    private bool IsTargetInDifferentFlow((EntityKind EntityType, Guid EntityId) target)
    {
        var targetWorkOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Work, target.EntityType, target.EntityId);
        if (targetWorkOpt is null) return false;
        var targetWorkId = targetWorkOpt.Value;
        if (!_store.WorksReadOnly.TryGetValue(targetWorkId, out var targetWork)) return false;
        var targetFlowId = targetWork.ParentId;
        foreach (var key in _clipboardSelection)
        {
            if (!_store.CallsReadOnly.TryGetValue(key.Id, out var call)) continue;
            if (!_store.WorksReadOnly.TryGetValue(call.ParentId, out var sourceWork)) continue;
            if (sourceWork.ParentId != targetFlowId) return true;
        }
        return false;
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

        // Cut clipboard: source Call 들이 그 동안 삭제되었는지 검증. 사라졌으면 자동 clear + 안내.
        if (_clipboardIsCut)
        {
            var missing = _clipboardSelection.Where(k => !_store.CallsReadOnly.ContainsKey(k.Id)).ToList();
            if (missing.Count > 0)
            {
                _clipboardSelection.Clear();
                _clipboardIsCut = false;
                Selection.ApplyCutPendingVisuals([]);
                StatusText = "Cut 대상이 그 사이 삭제되어 클립보드를 비웠습니다.";
                RefreshEditorCommandStates();
                return;
            }
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

        // Call 분기 — cross-flow 일 때는 device system 처리 모드 다이얼로그 분기
        if (batchType == EntityKind.Call && IsTargetInDifferentFlow(target.Value))
        {
            DispatchCallAcrossFlows(target.Value);
            return;
        }

        // Cut + same flow (다른 Work) → MoveCallsToWork (1 undo step 일괄 이동)
        if (batchType == EntityKind.Call && _clipboardIsCut)
        {
            DispatchCallMoveSameFlow(target.Value);
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

    /// <summary>
    /// Cross-flow Call paste/move — device system 처리 모드 다이얼로그 → F# 호출.
    /// Cut clipboard 면 MoveCallsAcrossFlow, copy 면 PasteEntitiesWithMode.
    /// </summary>
    private void DispatchCallAcrossFlows((EntityKind EntityType, Guid EntityId) target)
    {
        var targetWorkOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Work, target.EntityType, target.EntityId);
        if (targetWorkOpt is null) return;
        var targetWorkId = targetWorkOpt.Value;

        var mode = _dialogService.PromptCrossFlowDeviceMode(
            new CrossFlowDeviceModePromptContext(
                CallCount: _clipboardSelection.Count,
                IsCut: _clipboardIsCut,
                Store: _store,
                SourceCallIds: _clipboardSelection.Select(k => k.Id).ToArray(),
                TargetWorkId: targetWorkId));
        if (mode is null) return;  // cancelled

        var resolvedMode = mode;
        if (_clipboardIsCut)
        {
            if (!TryEditorFunc(
                    () => _store.MoveCallsAcrossFlow(
                        _clipboardSelection.Select(k => k.Id), targetWorkId, resolvedMode),
                    out CrossFlowMoveResult result,
                    fallback: CrossFlowMoveResult.NewMoved(Microsoft.FSharp.Collections.ListModule.Empty<Guid>())))
                return;
            if (result is CrossFlowMoveResult.Blocked blocked)
            {
                ShowMoveValidationBlocked(blocked.Item);
                return;
            }
            var movedIds = ((CrossFlowMoveResult.Moved)result).newCallIds;
            _clipboardSelection.Clear();
            _clipboardIsCut = false;
            Selection.ApplyCutPendingVisuals([]);
            ApplyPasteSelection(movedIds, $"Moved {movedIds.Length} Call(s) across Flow ({resolvedMode}).");
        }
        else
        {
            var pasteIndex = _pasteCount * _clipboardSelection.Count;
            if (!TryEditorFunc(
                    () => _store.PasteEntitiesWithMode(
                        _clipboardSelection.Select(k => k.Id), target.EntityType, target.EntityId, pasteIndex, resolvedMode),
                    out PasteResult result,
                    fallback: PasteResult.NewOk(Microsoft.FSharp.Collections.ListModule.Empty<Guid>())))
                return;
            if (result is PasteResult.Blocked blocked)
            {
                if (blocked.Item.IsSameWorkPaste)
                    _dialogService.ShowWarning("같은 Work 내의 Call은 복사할 수 없습니다.");
                else if (blocked.Item.IsDuplicateCallInWork)
                    _dialogService.ShowWarning("대상 Work에 이미 동일한 이름의 Call이 존재합니다.");
                return;
            }
            var pastedIds = ((PasteResult.Ok)result).Item;
            _pasteCount++;
            ApplyPasteSelection(pastedIds, $"Pasted {pastedIds.Length} Call(s) across Flow ({resolvedMode}).");
        }
    }

    /// <summary>Cut Call + same flow 의 다른 Work — same-flow MoveCallsToWork 로 1 undo step 일괄 이동.</summary>
    private void DispatchCallMoveSameFlow((EntityKind EntityType, Guid EntityId) target)
    {
        var targetWorkOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Work, target.EntityType, target.EntityId);
        if (targetWorkOpt is null) return;
        var targetWorkId = targetWorkOpt.Value;

        TryEditorFunc(
            () => _store.MoveCallsToWork(_clipboardSelection.Select(k => k.Id).ToArray(), targetWorkId),
            out int moved, fallback: 0);
        if (moved > 0)
        {
            _clipboardSelection.Clear();
            _clipboardIsCut = false;
            Selection.ApplyCutPendingVisuals([]);
            StatusText = $"Moved {moved} Call(s) within Flow.";
            RefreshEditorCommandStates();
        }
        else
        {
            StatusText = "Nothing moved (duplicate name or invalid target).";
        }
    }

    private void ShowMoveValidationBlocked(CrossFlowMoveValidation v)
    {
        if (v.IsSameWorkMove)
            _dialogService.ShowWarning("이미 같은 Work 안에 있는 Call 은 이동할 수 없습니다.");
        else if (v is CrossFlowMoveValidation.DuplicateNamesInTarget dup)
            _dialogService.ShowWarning($"대상 Work 에 이미 동일한 이름의 Call 이 있습니다 ({dup.Item.Length}개).");
        else if (v is CrossFlowMoveValidation.RenameModeConflicts conflicts)
            _dialogService.ShowWarning($"Prefix 교체 모드 충돌: {conflicts.Item.Length}개 device. Device 복사 모드를 사용하세요.");
        else
            _dialogService.ShowWarning("이동할 수 없습니다.");
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
