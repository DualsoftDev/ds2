using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private void WireEvents()
    {
        var observable = (IObservable<EditorEvent>)_store.ObserveEvents();
        _eventSubscription?.Dispose();
        _eventSubscription = observable.Subscribe(new ActionObserver<EditorEvent>(
            evt => _dispatcher.Invoke(() =>
            {
                try
                {
                    HandleEvent(evt);
                }
                catch (Exception ex)
                {
                    HandleUiOperationException(
                        $"HandleEvent({evt.GetType().Name})",
                        ex,
                        statusOverride: "[ERROR] Event processing failed. See log.");
                    RequestRebuildAll();
                }
            }),
            error => _dispatcher.Invoke(() =>
            {
                HandleUiOperationException(
                    "EditorEvent subscription",
                    error,
                    statusOverride: "[ERROR] Editor event subscription failed. See log.");
                RequestRebuildAll();
            })));
    }

    private void HandleEvent(EditorEvent evt)
    {
        if (!TryEditorFunc(
                () => _store.AddedEntityIdOrNull(evt),
                out Guid? addedId,
                fallback: null))
            return;

        if (addedId is { } id)
        {
            // 엔티티 추가 — Tree 확장 + RebuildAll + 시뮬 store 변경 알림. RefreshScope 는 All 매핑이지만
            // 추가 노드를 확장/선택하는 hook (ExpandAndSelectNode) 가 RebuildAll 콜백에 필요해 명시 처리.
            RequestRebuildAll(() => Selection.ExpandAndSelectNode(id));
            Simulation.NotifyStoreChanged();
            return;
        }

        // ── ID/payload 가 필요하거나 RefreshScope 외 부수효과가 있는 case ─────────────────────
        switch (evt)
        {
            case EditorEvent.EntityRenamed ren:
                // 이름만 갱신 — 전체 재구축 없이 tree/canvas/property panel 의 name 필드만 직접 patch.
                ApplyEntityRename(ren.id, ren.newName, ren.treeName);
                return;

            case EditorEvent.HistoryChanged h:
                RebuildHistoryItems(h.undoLabels, h.redoLabels);
                UpdateTitle();
                ApplyRefreshScope(RefreshScopeDecision.ForEditorEvent(evt));
                return;

            case EditorEvent.SystemPropsChanged:
                ApplyRefreshScope(RefreshScopeDecision.ForEditorEvent(evt));
                Simulation.NotifyStoreChanged();
                ResyncView3DIfOpen();
                return;

            case EditorEvent.WorkPropsChanged:
            case EditorEvent.ApiDefPropsChanged:
                ApplyRefreshScope(RefreshScopeDecision.ForEditorEvent(evt));
                Simulation.NotifyStoreChanged();
                return;

            case EditorEvent.CallPropsChanged cp:
                ApplyRefreshScope(RefreshScopeDecision.ForEditorEvent(evt));
                RefreshCallConditionBadge(cp.id);
                Simulation.NotifyStoreChanged();
                return;

            case EditorEvent.ArrowWorkAdded:
            case EditorEvent.ArrowWorkRemoved:
            case EditorEvent.ArrowCallAdded:
            case EditorEvent.ArrowCallRemoved:
            case { IsConnectionsChanged: true }:
                // 노드 visual을 보존하고 화살표 set만 diff 적용 (ApplyRefreshScope 의 Canvas 분기).
                // 추가로 시뮬 + 화살표 선택 visual 동기화.
                Simulation.NotifyConnectionsChanged();
                ApplyRefreshScope(RefreshScopeDecision.ForEditorEvent(evt));
                Selection.ApplyArrowSelectionVisuals();
                return;

            case EditorEvent.EntitiesMoved moved:
                // 이동된 노드 ID 가 payload — RefreshScope 로 일반화 불가, 직접 처리.
                CanvasManager.ApplyEntitiesMovedToAllPanes(new HashSet<Guid>(moved.ids));
                PropertyPanel.Refresh();
                return;

            case { IsStoreRefreshed: true }:
                // LLM ApplyImportPlan 이후 store 갱신 — HasProject 동기화 후 RefreshScope.All 로 RebuildAll.
                HasProject = Queries.allProjects(_store).Any();
                ApplyRefreshScope(RefreshScopeDecision.ForEditorEvent(evt));
                return;
        }

        // ── 그 외 모든 EditorEvent — RefreshScopeDecision 매핑 기반 단일 분기 ───────────────────
        var scope = RefreshScopeDecision.ForEditorEvent(evt);

        if (scope == RefreshScope.None)
        {
            Log.Warn($"Unhandled event: {evt.GetType().Name}");
            StatusText = $"[WARN] Unhandled event: {evt.GetType().Name}";
            RequestRebuildAll();
            return;
        }

        ApplyRefreshScope(scope);

        // Tree structural 변경 (System/Flow/Work/Call/ApiDef Added/Removed 등 — scope = All) 은 시뮬 store 갱신 알림.
        if (scope.Contains(RefreshScope.Tree) && scope.Contains(RefreshScope.PropertyPanel))
            Simulation.NotifyStoreChanged();
    }

    private void RefreshCallConditionBadge(Guid callId)
    {
        var node = Canvas.CanvasNodes.FirstOrDefault(n => n.Id == callId);
        if (node is null) return;

        if (TryEditorRef(() => CallConditionQueries.GetCallConditionTypes(_store, callId), out var types))
            node.UpdateConditionTypes(types);
    }

    private void ApplyEntityRename(Guid entityId, string newName, string treeName)
    {
        static void UpdateMatching<TItem>(
            IEnumerable<TItem> items,
            Guid targetId,
            Func<TItem, Guid> idSelector,
            Action<TItem, string> update,
            string value)
        {
            foreach (var item in items)
                if (idSelector(item) == targetId)
                    update(item, value);
        }

        UpdateMatching(Canvas.CanvasNodes, entityId, static n => n.Id, static (n, value) => n.Name = value, newName);
        UpdateMatching(Selection.EnumerateTreeNodes(), entityId, static n => n.Id, static (n, value) => n.Name = value, treeName);
        UpdateMatching(Canvas.OpenTabs, entityId, static t => t.RootId, static (t, value) => t.Title = value, newName);
        PropertyPanel.ApplyEntityRename(entityId, newName);
    }
}

file sealed class ActionObserver<T> : IObserver<T>
{
    private readonly Action<T> _onNext;
    private readonly Action<Exception>? _onError;

    public ActionObserver(Action<T> onNext, Action<Exception>? onError = null)
    {
        _onNext = onNext;
        _onError = onError;
    }

    public void OnNext(T value) => _onNext(value);
    public void OnCompleted() { }
    public void OnError(Exception error)
    {
        if (_onError is null) return;
        _onError(error);
    }
}
