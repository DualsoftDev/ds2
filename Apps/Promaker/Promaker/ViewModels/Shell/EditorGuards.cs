using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Dialogs;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private void HandleUiOperationException(
        string operation,
        Exception ex,
        string? statusOverride = null,
        bool warnDialog = false)
    {
        Log.Error($"UI operation failed: {operation}", ex);
        StatusText = statusOverride ?? $"[ERROR] {operation} failed. See log.";

        if (warnDialog || ex is InvalidOperationException)
            _dialogService.ShowWarning(ex.Message);
    }

    private bool TryEditorAction(
        Action action,
        string? statusOverride = null,
        bool warnDialog = false,
        [CallerArgumentExpression(nameof(action))] string operation = "")
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            HandleUiOperationException(operation, ex, statusOverride, warnDialog);
            return false;
        }
    }

    private bool TryEditorFunc<T>(
        Func<T> func,
        out T result,
        T fallback = default!,
        string? statusOverride = null,
        bool warnDialog = false,
        [CallerArgumentExpression(nameof(func))] string operation = "")
    {
        try
        {
            result = func();
            return true;
        }
        catch (Exception ex)
        {
            result = fallback;
            HandleUiOperationException(operation, ex, statusOverride, warnDialog);
            return false;
        }
    }

    private bool TryEditorRef<T>(
        Func<T> func,
        [NotNullWhen(true)] out T? result,
        string? statusOverride = null,
        bool warnDialog = false,
        [CallerArgumentExpression(nameof(func))] string operation = "")
        where T : class
    {
        if (TryEditorFunc(func, out var raw, fallback: default!, statusOverride, warnDialog, operation) && raw is not null)
        {
            result = raw;
            return true;
        }

        result = null;
        return false;
    }


    private bool TryGetSelectedNode(EntityKind entityType, [NotNullWhen(true)] out EntityNode? node)
    {
        node = SelectedNode is { } selected && selected.EntityType == entityType
            ? selected
            : null;
        return node is not null;
    }

    public bool TryMoveEntitiesFromCanvas(IReadOnlyList<MoveEntityRequest> requests) =>
        TryEditorAction(() => _store.MoveEntities(requests),
            statusOverride: "[ERROR] Failed to move selected nodes.");

    public bool CanMoveCallToWork(Guid callId, Guid targetWorkId) =>
        _store.CanMoveCallToWork(callId, targetWorkId);

    public bool TryMoveCallToWorkFromTree(Guid callId, Guid targetWorkId)
    {
        if (!GuardSimulationSemanticEdit("Call Work 이동")) return false;
        if (!TryEditorFunc(
                () => _store.MoveCallToWork(callId, targetWorkId),
                out bool moved,
                fallback: false,
                statusOverride: "[ERROR] Failed to move Call.",
                warnDialog: true))
            return false;

        if (moved)
            StatusText = "Call moved.";
        return moved;
    }

    /// <summary>다중 Call 을 target Work 로 drag&drop (또는 cut-paste). Same/cross flow 자동 분기 +
    /// cross-flow 인 경우 device system 처리 모드 다이얼로그.</summary>
    /// <param name="copyMode">true 면 copy(원본 유지), false 면 cut(원본 삭제).</param>
    public bool TryMoveCallsToWorkFromTree(IReadOnlyList<Guid> callIds, Guid targetWorkId, bool copyMode)
    {
        if (callIds.Count == 0) return false;
        if (!GuardSimulationSemanticEdit(copyMode ? "Call 복사" : "Call 이동")) return false;

        var targetWorkOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Work, EntityKind.Work, targetWorkId);
        if (targetWorkOpt is null) return false;
        var targetWork = _store.WorksReadOnly.TryGetValue(targetWorkOpt.Value, out var tw) ? tw : null;
        if (targetWork is null) return false;
        var targetFlowId = targetWork.ParentId;

        // 자기 자신이거나, source 와 target 의 flow 가 같은지 판단
        var isCrossFlow = callIds.Any(cid =>
            _store.CallsReadOnly.TryGetValue(cid, out var sc)
            && _store.WorksReadOnly.TryGetValue(sc.ParentId, out var sw)
            && sw.ParentId != targetFlowId);

        if (!isCrossFlow)
        {
            // Same flow → MoveCallsToWork (move, 1 undo step) or PasteEntities (copy)
            if (copyMode)
            {
                if (!TryEditorFunc(
                        () => _store.PasteEntities(
                            EntityKind.Call, callIds, EntityKind.Work, targetWorkId, 0),
                        out PasteResult result,
                        fallback: PasteResult.NewOk(Microsoft.FSharp.Collections.ListModule.Empty<Guid>()),
                        warnDialog: true))
                    return false;
                if (result is PasteResult.Blocked) { _dialogService.ShowWarning("Call 을 복사할 수 없습니다."); return false; }
                var ids = ((PasteResult.Ok)result).Item;
                StatusText = $"Copied {ids.Length} Call(s).";
                return true;
            }
            // Same flow → 한 transaction(=1 undo step)으로 일괄 이동
            if (!TryEditorFunc(() => _store.MoveCallsToWork(callIds, targetWorkId), out int moved, fallback: 0))
                return false;
            if (moved > 0) StatusText = $"Moved {moved} Call(s) within Flow.";
            return moved > 0;
        }

        // Cross flow → device mode 다이얼로그
        var mode = _dialogService.PromptCrossFlowDeviceMode(
            new CrossFlowDeviceModePromptContext(
                CallCount: callIds.Count,
                IsCut: !copyMode,
                Store: _store,
                SourceCallIds: callIds.ToArray(),
                TargetWorkId: targetWorkId));
        if (mode is null) return false;
        var resolvedMode = mode;

        if (copyMode)
        {
            if (!TryEditorFunc(
                    () => _store.PasteEntitiesWithMode(
                        callIds, EntityKind.Work, targetWorkId, 0, resolvedMode),
                    out PasteResult result,
                    fallback: PasteResult.NewOk(Microsoft.FSharp.Collections.ListModule.Empty<Guid>()),
                    warnDialog: true))
                return false;
            if (result is PasteResult.Blocked)
            {
                _dialogService.ShowWarning("Call 을 복사할 수 없습니다.");
                return false;
            }
            var ids = ((PasteResult.Ok)result).Item;
            StatusText = $"Copied {ids.Length} Call(s) across Flow ({resolvedMode}).";
            return true;
        }

        if (!TryEditorFunc(
                () => _store.MoveCallsAcrossFlow(callIds, targetWorkId, resolvedMode),
                out CrossFlowMoveResult moveResult,
                fallback: CrossFlowMoveResult.NewMoved(Microsoft.FSharp.Collections.ListModule.Empty<Guid>()),
                warnDialog: true))
            return false;

        if (moveResult is CrossFlowMoveResult.Blocked blocked)
        {
            ShowMoveValidationBlocked(blocked.Item);
            return false;
        }

        var movedIds = ((CrossFlowMoveResult.Moved)moveResult).newCallIds;
        StatusText = $"Moved {movedIds.Length} Call(s) across Flow ({resolvedMode}).";
        return true;
    }

    /// <summary>drag-over 표시용 — 같은 flow 든 다른 flow 든 *어떤 형태로든 이동 가능한지*.</summary>
    public bool CanMoveCallsToWorkFromTree(IReadOnlyList<Guid> callIds, Guid targetWorkId)
    {
        if (callIds.Count == 0) return false;
        if (!_store.WorksReadOnly.TryGetValue(targetWorkId, out var targetWork)) return false;
        var targetFlowId = targetWork.ParentId;
        foreach (var cid in callIds)
        {
            if (!_store.CallsReadOnly.TryGetValue(cid, out var sc)) return false;
            if (!_store.WorksReadOnly.TryGetValue(sc.ParentId, out var sw)) return false;
            // 같은 work 로의 이동은 의미 없음 (drop 거부)
            if (sc.ParentId == targetWorkId) return false;
            if (sw.ParentId == targetFlowId)
            {
                // same flow — same-flow CanMoveCallToWork 가드
                if (!_store.CanMoveCallToWork(cid, targetWorkId)) return false;
            }
            // cross flow 는 사전 가드를 dialog 후 MoveCallsAcrossFlow validation 으로 (UX 단순화).
        }
        return true;
    }

    /// <summary>다중 Work 를 target Flow 로 drag&drop (또는 cut-paste). copy=복제, move=이동(ParentId 변경).
    /// Work 는 Call 과 달리 cross-flow 라도 통째로 옮겨지므로 device 모드 다이얼로그가 필요 없다.</summary>
    /// <param name="copyMode">true 면 copy(원본 유지), false 면 move(원본 이동).</param>
    public bool TryMoveWorksToFlowFromTree(IReadOnlyList<Guid> workIds, Guid targetFlowId, bool copyMode)
    {
        if (workIds.Count == 0) return false;
        if (!GuardSimulationSemanticEdit(copyMode ? "Work 복사" : "Work 이동")) return false;

        var targetFlowOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Flow, EntityKind.Flow, targetFlowId);
        if (targetFlowOpt is null) return false;
        var flowId = targetFlowOpt.Value;

        if (copyMode)
        {
            // 복사 — 기존 device 처리(CloneSystem) PasteEntities (pasteWorksToFlowBatch 가 device 복제).
            if (!TryEditorFunc(
                    () => _store.PasteEntities(EntityKind.Work, workIds, EntityKind.Flow, flowId, 0),
                    out PasteResult result,
                    fallback: PasteResult.NewOk(Microsoft.FSharp.Collections.ListModule.Empty<Guid>()),
                    warnDialog: true))
                return false;
            if (result is PasteResult.Blocked) { _dialogService.ShowWarning("Work 를 복사할 수 없습니다."); return false; }
            var ids = ((PasteResult.Ok)result).Item;
            StatusText = $"Copied {ids.Length} Work(s).";
            return ids.Length > 0;
        }

        // 이동 — Work cross-flow = 내부 Call 전부 cross-flow → device mode 다이얼로그 후 MoveWorksAcrossFlow.
        if (!TryResolveWorkMoveDeviceMode(workIds, out var mode)) return false;
        TryEditorFunc(
            () => _store.MoveWorksAcrossFlow(workIds, flowId, mode),
            out var movedIds,
            fallback: Microsoft.FSharp.Collections.ListModule.Empty<Guid>());
        var moved = movedIds?.ToList() ?? new List<Guid>();
        if (moved.Count > 0) StatusText = $"Moved {moved.Count} Work(s) across Flow ({mode}).";
        else _dialogService.ShowWarning("이동할 Work 가 없습니다 (같은 Flow 이거나 잘못된 대상).");
        return moved.Count > 0;
    }

    /// <summary>drag-over 표시용 — Work 를 다른 Flow 로 이동 가능한지. 같은 Flow 로의 드롭은 거부.</summary>
    public bool CanMoveWorksToFlowFromTree(IReadOnlyList<Guid> workIds, Guid targetFlowId)
    {
        if (workIds.Count == 0) return false;
        if (!_store.FlowsReadOnly.ContainsKey(targetFlowId)) return false;
        // 하나라도 다른 Flow 에 속한 Work 면 이동 후보 (이름 충돌은 drop 시 MoveWorksToFlow 가 필터).
        foreach (var wid in workIds)
        {
            if (!_store.WorksReadOnly.TryGetValue(wid, out var w)) return false;
            if (w.ParentId == targetFlowId) return false;  // 이미 그 Flow — 의미 없음
        }
        return true;
    }

    /// <summary>다중 Flow 를 target System 으로 drag&drop (또는 cut-paste). copy=복제(PasteEntities),
    /// move=재부모화 이동(MoveFlowsToSystem — Work/Call id 보존, 내부 화살표 재부모화, 걸친 화살표 제거).
    /// 이동은 디바이스 참조가 그대로 유지되므로 device 모드 다이얼로그가 필요 없다.</summary>
    /// <param name="copyMode">true 면 copy(원본 유지), false 면 move(원본 이동).</param>
    public bool TryMoveFlowsToSystemFromTree(IReadOnlyList<Guid> flowIds, Guid targetSystemId, bool copyMode)
    {
        if (flowIds.Count == 0) return false;
        if (!GuardSimulationSemanticEdit(copyMode ? "Flow 복사" : "Flow 이동")) return false;

        if (!_store.SystemsReadOnly.ContainsKey(targetSystemId)) return false;

        if (copyMode)
        {
            if (!TryEditorFunc(
                    () => _store.PasteEntities(EntityKind.Flow, flowIds, EntityKind.System, targetSystemId, 0),
                    out PasteResult result,
                    fallback: PasteResult.NewOk(Microsoft.FSharp.Collections.ListModule.Empty<Guid>()),
                    warnDialog: true))
                return false;
            if (result is PasteResult.Blocked) { _dialogService.ShowWarning("Flow 를 복사할 수 없습니다."); return false; }
            var ids = ((PasteResult.Ok)result).Item;
            StatusText = $"Copied {ids.Length} Flow(s).";
            return ids.Length > 0;
        }

        TryEditorFunc(
            () => _store.MoveFlowsToSystem(flowIds, targetSystemId),
            out int moved, fallback: 0);
        if (moved > 0) StatusText = $"Moved {moved} Flow(s) to System.";
        else _dialogService.ShowWarning("이동할 Flow 가 없습니다 (이미 그 System 이거나 잘못된 대상).");
        return moved > 0;
    }

    /// <summary>drag-over 표시용 — Flow 를 다른 System 으로 이동/복사 가능한지. 같은 System 드롭은 거부.</summary>
    public bool CanMoveFlowsToSystemFromTree(IReadOnlyList<Guid> flowIds, Guid targetSystemId)
    {
        if (flowIds.Count == 0) return false;
        if (!_store.SystemsReadOnly.ContainsKey(targetSystemId)) return false;
        foreach (var fid in flowIds)
        {
            if (!_store.FlowsReadOnly.TryGetValue(fid, out var f)) return false;
            if (f.ParentId == targetSystemId) return false;  // 이미 그 System — 의미 없음
        }
        return true;
    }

    public bool TryReconnectArrowFromCanvas(Guid arrowId, bool replaceSource, Guid newEndpointId)
    {
        if (!GuardArrowEditByRuntimeMode("Arrow 재연결")) return false;
        if (!TryEditorFunc(
                () => _store.ReconnectArrow(arrowId, replaceSource, newEndpointId),
                out bool changed,
                fallback: false,
                statusOverride: "[ERROR] Failed to reconnect arrow."))
            return false;

        return changed;
    }

    public bool TryUpdateArrowType(Guid arrowId, ArrowType newArrowType)
    {
        if (!GuardArrowEditByRuntimeMode("Arrow Type 변경")) return false;
        return TryEditorFunc(
            () => _store.UpdateArrowType(arrowId, newArrowType),
            out bool _,
            fallback: false,
            statusOverride: "[ERROR] Failed to change arrow type.");
    }

    public int TryUpdateArrowTypesBatch(IReadOnlyList<Guid> arrowIds, ArrowType newArrowType)
    {
        if (arrowIds.Count == 0) return 0;
        if (!GuardArrowEditByRuntimeMode("Arrow Type 일괄 변경")) return 0;
        if (!TryEditorFunc(
                () => _store.UpdateArrowTypesBatch(arrowIds, newArrowType),
                out int changed,
                fallback: 0,
                statusOverride: "[ERROR] Failed to change arrow types."))
            return 0;
        return changed;
    }

    public bool TryReverseArrow(Guid arrowId)
    {
        if (!GuardArrowEditByRuntimeMode("Arrow 방향 반전")) return false;
        return TryEditorFunc(
            () => _store.ReverseArrow(arrowId),
            out bool _,
            fallback: false,
            statusOverride: "[ERROR] Failed to reverse arrow direction.");
    }

    public int TryReverseArrowsBatch(IReadOnlyList<Guid> arrowIds)
    {
        if (arrowIds.Count == 0) return 0;
        if (!GuardArrowEditByRuntimeMode("Arrow 일괄 방향 반전")) return 0;
        if (!TryEditorFunc(
                () => _store.ReverseArrowsBatch(arrowIds),
                out int changed,
                fallback: 0,
                statusOverride: "[ERROR] Failed to reverse arrows."))
            return 0;
        return changed;
    }

    public bool TryConnectNodesFromCanvas(Guid sourceId, Guid targetId, ArrowType arrowType)
    {
        if (!GuardArrowEditByRuntimeMode("Arrow 연결")) return false;

        if (Queries.getCall(sourceId, _store) is not null
            && Queries.getCall(targetId, _store) is not null
            && ConnectionQueries.wouldCreateCallCycle(_store, sourceId, targetId))
        {
            _dialogService.ShowWarning("Call 노드 간 순환 연결은 허용되지 않습니다.\n순환 구조가 필요한 경우 Work 레벨에서 연결해 주세요.");
            return false;
        }

        if (!TryEditorFunc(
                () => _store.ConnectSelectionInOrder(new Guid[] { sourceId, targetId }, arrowType),
                out int createdCount,
                fallback: 0,
                statusOverride: "[ERROR] Failed to connect selected nodes."))
            return false;

        return createdCount > 0;
    }
}
