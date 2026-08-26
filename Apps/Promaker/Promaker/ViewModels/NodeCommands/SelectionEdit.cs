using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// 붙여넣기/이동류 편집 작업의 BusyOverlay 실행 헬퍼 — 오버레이를 먼저 렌더(Yield)한 뒤
    /// 본 작업을 실행하고, 트리 리빌드가 큐잉됐으면 리빌드 완료 시점에 해제 (파일 열기 패턴).
    /// 다이얼로그가 필요한 분기는 다이얼로그를 먼저 다 받은 뒤 store 작업만 이 헬퍼로 감쌀 것
    /// (BusyOverlay 가 입력을 차단하므로 오버레이와 모달이 겹치면 안 된다).
    /// </summary>
    private async void RunEditorWorkWithBusy(string busyMessage, Action work)
    {
        BusyMessage = busyMessage;
        IsBusy = true;
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Background);
            work();
        }
        catch (Exception ex)
        {
            _dialogService.ShowWarning($"작업 실패:\n{ex.Message}");
        }
        finally
        {
            if (_rebuildQueued)
                _pendingRebuildActions.Add(() => IsBusy = false);
            else
                IsBusy = false;
        }
    }

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

        // #229: 디바이스(Passive) Action Work 의 이름 변경은 SSOT(ApiDef)로 cascade 되도록 라우팅.
        // (RenameEntitySmart 가 Work↔ApiDef 여부를 판정 — 일반 Work/기타 엔티티는 기존 RenameEntity 그대로.)
        if (TryEditorAction(
                () => _store.RenameEntitySmart(SelectedNode.Id, SelectedNode.EntityType, newName)))
            StatusText = $"Renamed to '{newName}'.";
    }

    /// <summary>Device 탭 System(디바이스) 노드 → 디바이스/Action 일괄 이름 변경 다이얼로그.
    /// [확인] 시 디바이스명 + 변경된 Action 이름들을 단일 트랜잭션(RenameDeviceBatch = Undo 1단계)으로
    /// cascade 적용한다. 영향 미리보기/검증은 P2 SSOT(CollectRenameImpact)를 다이얼로그가 공유한다.</summary>
    private void OpenDeviceRenameDialog(Guid systemId, string deviceName)
    {
        if (!GuardSimulationSemanticEdit("디바이스 이름 변경"))
            return;

        // ApiDef(Action) 공급원 = P2/Panel SSOT. FSharpList → IReadOnlyList 변환(다이얼로그 생성자 계약).
        if (!TryEditorFunc(
                () => _store.GetApiDefsForSystem(systemId),
                out var apiDefs,
                fallback: Microsoft.FSharp.Collections.ListModule.Empty<ApiDefPanelItem>()))
            return;

        var dialog = new Promaker.Dialogs.DeviceRenameDialog(_store, systemId, deviceName, apiDefs.ToList());
        if (_dialogService.ShowDialog(dialog) != true)
            return;

        var newDeviceName = dialog.NewDeviceNameOption;
        var apiRenames = dialog.BuildApiRenames();

        // 변경 사항이 전혀 없으면(디바이스명·Action명 모두 그대로) no-op.
        if (FSharpOption<string>.get_IsNone(newDeviceName) && apiRenames.IsEmpty)
        {
            StatusText = "변경된 이름이 없습니다.";
            return;
        }

        if (TryEditorFunc(
                () => _store.RenameDeviceBatch(systemId, newDeviceName, apiRenames),
                out int changedCount,
                fallback: 0))
        {
            StatusText = changedCount > 0
                ? $"디바이스 일괄 이름 변경: {changedCount}건 적용됨"
                : "적용할 변경이 없습니다.";
        }
    }

    /// <summary>Control 트리 Call 노드 → 그 Call 이 참조하는 디바이스의 일괄 이름변경 다이얼로그로 유도.
    /// (Call 이름 뒷부분=Action명 은 속성창에서 read-only 라 정식 변경 경로로 안내.)
    /// dummy/orphan(디바이스 미해결) Call 은 조용히 무시 — 현행 속성창 이동만 유효.</summary>
    private void OpenDeviceRenameDialogForCall(Guid callId)
    {
        if (!TryEditorFunc(
                () => _store.TryGetDeviceSystemOfCall(callId),
                out var deviceOpt,
                fallback: FSharpOption<DsSystem>.None))
            return;

        if (FSharpOption<DsSystem>.get_IsSome(deviceOpt))
            OpenDeviceRenameDialog(deviceOpt.Value.Id, deviceOpt.Value.Name);
    }

    [RelayCommand(CanExecute = nameof(CanCopySelected))]
    private void CopySelected()
    {
        var candidates = Selection.OrderedNodeSelection.Count > 0
            ? Selection.OrderedNodeSelection
            : SelectedNode is { } single
                ? [new SelectionKey(single.Id, single.EntityType)]
                : (IReadOnlyList<SelectionKey>)[];

        // System/디바이스 노드 → OS 클립보드 패키지(인스턴스 간 복사) 경로.
        // 내부 클립보드(Flow/Work/Call) 경로와 별개 — isCopyableEntityKind 는 그대로 유지(무회귀).
        if (candidates.Count > 0 && candidates.All(k => k.EntityKind == EntityKind.System))
        {
            CopySystemsToOsClipboard(candidates);
            return;
        }

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
        // Cut(이동)은 Call/Work/Flow 지원. Flow 는 MoveFlowsToSystem 재부모화(id 보존)로 이동.
        var candidates = Selection.OrderedNodeSelection.Count > 0
            ? Selection.OrderedNodeSelection
            : SelectedNode is { } single
                ? new[] { new SelectionKey(single.Id, single.EntityType) }
                : Array.Empty<SelectionKey>();
        return candidates.Count > 0
            && candidates.All(k => k.EntityKind == EntityKind.Call
                                || k.EntityKind == EntityKind.Work
                                || k.EntityKind == EntityKind.Flow);
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
        if (validated[0].EntityKind != EntityKind.Call
            && validated[0].EntityKind != EntityKind.Work
            && validated[0].EntityKind != EntityKind.Flow)
        {
            _dialogService.ShowWarning("Cut(이동) 은 Call/Work/Flow 만 지원합니다.");
            return;
        }

        _clipboardSelection.Clear();
        _clipboardIsCut = true;
        _pasteCount = 0;
        foreach (var key in validated)
            _clipboardSelection.Add(key);

        // Cut visual: 클립보드 entry 들을 디밍/반투명 표시
        Selection.ApplyCutPendingVisuals(_clipboardSelection);

        StatusText = $"Cut {_clipboardSelection.Count} {validated[0].EntityKind}(s) — Ctrl+V to move.";
        RefreshEditorCommandStates();
    }

    private bool CanCancelCut() => _clipboardIsCut && _clipboardSelection.Count > 0;

    /// <summary>Cut(잘라내기) 대기 상태 취소 — clipboard 비우고 흐림(IsCutPending) 시각 복원.
    /// ESC 또는 (paste 전) Ctrl+Z 로 호출. Copy clipboard 는 visual 이 없어 대상 아님.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelCut))]
    private void CancelCut()
    {
        if (!_clipboardIsCut) return;
        _clipboardSelection.Clear();
        _clipboardIsCut = false;
        Selection.ApplyCutPendingVisuals([]);
        StatusText = "잘라내기 취소됨.";
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
            // 내부 클립보드가 비었으면 OS 클립보드의 시스템 패키지(다른 인스턴스 복사분) 시도
            if (TryPasteSystemPackageFromOsClipboard())
                return;
            StatusText = "Clipboard is empty.";
            return;
        }

        // Cut clipboard: source 엔티티들이 그 동안 삭제되었는지 검증. 사라졌으면 자동 clear + 안내.
        // Cut 은 Call/Work/Flow 지원 — 종류에 맞는 store 맵으로 확인 (Work 를 CallsReadOnly 로
        // 보면 항상 missing 으로 판정돼 Work cut/paste 가 통째로 막힌다 — 회귀 가드).
        if (_clipboardIsCut)
        {
            var cutKind = _clipboardSelection[0].EntityKind;
            var missing = _clipboardSelection.Where(k =>
                cutKind == EntityKind.Work ? !_store.WorksReadOnly.ContainsKey(k.Id)
                : cutKind == EntityKind.Flow ? !_store.FlowsReadOnly.ContainsKey(k.Id)
                : !_store.CallsReadOnly.ContainsKey(k.Id)).ToList();
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
            if (_clipboardIsCut)
                RunEditorWorkWithBusy("Flow 이동 중...", () => DispatchFlowMove(target.Value));
            else
                PasteFlowsWithRename(target.Value);   // 이름 프롬프트 후 내부에서 busy 래핑
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
            RunEditorWorkWithBusy("Call 이동 중...", () => DispatchCallMoveSameFlow(target.Value));
            return;
        }

        // Cut Work → target Flow 로 이동 (MoveWorksToFlow, 1 undo step)
        if (batchType == EntityKind.Work && _clipboardIsCut)
        {
            DispatchWorkMove(target.Value);
            return;
        }

        var pasteIndex = _pasteCount * _clipboardSelection.Count;
        RunEditorWorkWithBusy($"{batchType} 붙여넣는 중...", () =>
        {
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
        });
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
        // 디바이스 모드 다이얼로그를 받은 뒤의 store 작업만 busy — 오버레이/모달 겹침 방지.
        RunEditorWorkWithBusy("Call 붙여넣는 중...", () => ExecuteCallAcrossFlows(target, resolvedMode));
    }

    private void ExecuteCallAcrossFlows(
        (EntityKind EntityType, Guid EntityId) target, CrossFlowDeviceMode resolvedMode)
    {
        var targetWorkOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Work, target.EntityType, target.EntityId);
        if (targetWorkOpt is null) return;
        var targetWorkId = targetWorkOpt.Value;

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

    /// <summary>Work 들의 내부 Call 을 모아 cross-flow device mode 다이얼로그를 띄운다.
    /// Work cross-flow = 내부 Call 전부 cross-flow 이므로 Call 이동과 동일한 device 처리 선택이 필요.
    /// Call 이 없으면 device 무관 → 다이얼로그 없이 CloneSystem. 사용자가 취소하면 false.</summary>
    private bool TryResolveWorkMoveDeviceMode(IReadOnlyList<Guid> workIds, out CrossFlowDeviceMode mode)
    {
        mode = CrossFlowDeviceMode.CloneSystem;
        var callIds = workIds
            .SelectMany(wid => Queries.callsOf(wid, _store))
            .Select(c => c.Id)
            .ToArray();
        if (callIds.Length == 0) return true;
        var m = _dialogService.PromptCrossFlowDeviceMode(
            new CrossFlowDeviceModePromptContext(
                CallCount: callIds.Length,
                IsCut: true,
                Store: _store,
                SourceCallIds: callIds,
                TargetWorkId: Guid.Empty));   // Work 이동엔 특정 target Work 없음 — rename 사전검사 스킵
        if (m is null) return false;          // 다이얼로그 취소
        mode = m;
        return true;
    }

    /// <summary>Cut Work → target Flow 로 cross-flow 이동. 내부 Call device 처리 모드 선택 후
    /// MoveWorksAcrossFlow(paste+원본 cascade-remove). no-op 은 F# 가 제외.</summary>
    private void DispatchWorkMove((EntityKind EntityType, Guid EntityId) target)
    {
        var targetFlowOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Flow, target.EntityType, target.EntityId);
        if (targetFlowOpt is null) return;

        var workIds = _clipboardSelection.Select(k => k.Id).ToArray();
        if (!TryResolveWorkMoveDeviceMode(workIds, out var mode)) return;

        // 디바이스 모드 다이얼로그를 받은 뒤의 store 작업만 busy — 오버레이/모달 겹침 방지.
        var targetFlowId = targetFlowOpt.Value;
        var resolvedMode = mode;
        RunEditorWorkWithBusy("Work 이동 중...", () =>
        {
            TryEditorFunc(
                () => _store.MoveWorksAcrossFlow(workIds, targetFlowId, resolvedMode),
                out var movedIds,
                fallback: Microsoft.FSharp.Collections.ListModule.Empty<Guid>());
            var moved = movedIds?.ToList() ?? new List<Guid>();
            if (moved.Count > 0)
            {
                _clipboardSelection.Clear();
                _clipboardIsCut = false;
                Selection.ApplyCutPendingVisuals([]);
                ApplyPasteSelection(moved, $"Moved {moved.Count} Work(s) across Flow ({resolvedMode}).");
                RefreshEditorCommandStates();
            }
            else
            {
                StatusText = "Nothing moved (invalid target).";
            }
        });
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

    /// <summary>Cut Flow → target System 으로 재부모화 이동 (MoveFlowsToSystem, 1 undo step).
    /// Work/Call id 가 보존되므로 디바이스 모드 다이얼로그가 필요 없다.</summary>
    private void DispatchFlowMove((EntityKind EntityType, Guid EntityId) target)
    {
        var targetSystemIdOpt = StoreHierarchyQueries.resolveTarget(
            _store, EntityKind.System, target.EntityType, target.EntityId);
        if (targetSystemIdOpt is null)
        {
            StatusText = "이동 대상 System 을 선택하세요.";
            return;
        }

        TryEditorFunc(
            () => _store.MoveFlowsToSystem(_clipboardSelection.Select(k => k.Id), targetSystemIdOpt.Value),
            out int moved, fallback: 0);
        if (moved > 0)
        {
            _clipboardSelection.Clear();
            _clipboardIsCut = false;
            Selection.ApplyCutPendingVisuals([]);
            StatusText = $"Moved {moved} Flow(s) to System.";
            RefreshEditorCommandStates();
        }
        else
        {
            StatusText = "Nothing moved (이미 그 System 이거나 잘못된 대상).";
        }
    }

    private void PasteFlowsWithRename((EntityKind EntityType, Guid EntityId) target)
    {
        var targetSystemIdOpt = StoreHierarchyQueries.resolveTarget(
            _store, EntityKind.System, target.EntityType, target.EntityId);

        // 1) 이름 프롬프트를 먼저 전부 수집 — busy 오버레이와 모달이 겹치지 않게.
        //    같은 배치에서 이미 정한 이름도 제안 목록에 반영 (코어 nextUniqueName 이 최종 가드).
        var plans = new List<(Guid FlowId, Guid SysId, string NewName)>();
        var skippedMissingFlows = 0;
        foreach (var key in _clipboardSelection)
        {
            if (!_store.FlowsReadOnly.TryGetValue(key.Id, out var srcFlow))
            {
                skippedMissingFlows++;
                continue;
            }

            var sysId = targetSystemIdOpt != null ? targetSystemIdOpt.Value : srcFlow.ParentId;
            var existingNames = Queries.flowsOf(sysId, _store).Select(f => f.Name)
                .Concat(plans.Where(p => p.SysId == sysId).Select(p => p.NewName))
                .ToList();
            var suggestedName = GetUniqueName(srcFlow.Name, existingNames, "_");
            var newName = _dialogService.PromptName("Flow 복사 — 새 이름", suggestedName);
            if (newName is null) return;
            plans.Add((key.Id, sysId, newName));
        }

        // 2) 실제 붙여넣기(트리 복제 + 리빌드)는 busy 아래에서.
        var skipped = skippedMissingFlows;
        RunEditorWorkWithBusy("Flow 붙여넣는 중...", () =>
        {
            var pastedFlowIds = new List<Guid>();
            foreach (var (flowId, sysId, newName) in plans)
            {
                if (!TryEditorRef(
                        () => _store.PasteFlowWithRename(flowId, sysId, newName),
                        out var resultOpt))
                    return;

                if (resultOpt != null)
                    pastedFlowIds.Add(resultOpt.Value);
            }

            var workIds = pastedFlowIds
                .SelectMany(fId => Queries.worksOf(fId, _store))
                .Select(w => w.Id)
                .ToList();

            var status = skipped > 0
                ? $"Pasted {pastedFlowIds.Count} Flow(s); skipped {skipped} missing Flow(s)."
                : $"Pasted {pastedFlowIds.Count} Flow(s).";
            ApplyPasteSelection(workIds, status);
        });
    }
}
