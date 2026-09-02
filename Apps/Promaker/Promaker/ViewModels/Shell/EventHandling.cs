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
                // 3D 뷰는 store snapshot(BuildScene)으로 device-by-flow 트리/카드의 Flow 이름을 한 번 굽고
                // live binding 이 없어, ApplyEntityRename 의 ID 패치로는 갱신되지 않는다(특히 Flow rename →
                // ContextBuilder 의 FlowName / 좌측 Flow 그룹 헤더). 같은 이유로 3D 재동기화한다.
                ResyncView3DIfOpen();
                // 개명은 이름 정책 위반을 만들거나 해소할 수 있다 — 하단 배지 재계산.
                RefreshNamePolicyLint();
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

            case EditorEvent.WorkPropsChanged wp:
                ApplyRefreshScope(RefreshScopeDecision.ForEditorEvent(evt));
                RefreshWorkConditionBadge(wp.id);
                Simulation.NotifyStoreChanged();
                return;

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
                // LLM ApplyImportPlan / Undo·Redo 이후 store 갱신 — HasProject 동기화 후 RefreshScope.All 로 RebuildAll.
                HasProject = Queries.allProjects(_store).Any();
                ApplyRefreshScope(RefreshScopeDecision.ForEditorEvent(evt));
                // RebuildAll 은 tree/canvas 만 재구축하고 3D(BuildScene)는 안 건드린다. Undo/Redo 로 Flow
                // rename 을 되돌릴 때(rename 은 light event 미부착 → StoreRefreshed 경로) 3D 가 stale 되므로 재동기화.
                ResyncView3DIfOpen();
                // undo/redo·임포트·프로젝트 닫기 모두 이 경로 — 이름 정책 배지도 함께 재계산.
                RefreshNamePolicyLint();
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

        if (TryEditorRef(() => ConditionQueries.GetCallConditionTypes(_store, callId), out var types))
            node.UpdateConditionTypes(types);
    }

    /// Work 의 조건(SkipAction 등) 변경을 캔버스 노드 배지에 반영.
    /// WorkPropsChanged 의 RefreshScope 에는 Canvas 가 없어(속성 변경으로 노드 set 이 안 바뀌므로)
    /// 캔버스가 재구축되지 않는다 — Call 과 같이 배지만 직접 patch 한다.
    private void RefreshWorkConditionBadge(Guid workId)
    {
        if (!TryEditorRef(() => ConditionQueries.GetResolvedWorkConditionTypes(_store, workId), out var types))
            return;

        // 원본 Work 를 참조하는 Reference 노드도 같은 조건을 표시하므로 함께 갱신
        // (배지 규칙 SSOT = GetResolvedWorkConditionTypes — reference 는 원본 조건을 따른다).
        foreach (var node in Canvas.CanvasNodes)
            if (node.Id == workId || node.ReferenceOfId == workId)
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
        // Flow rename 시 자식 Work 의 canvas node 와 열린 Work 탭 title 은 각자 work id 를 가져 flow
        // id(entityId)로는 위 UpdateMatching 에 안 걸린다. work.Name="{FlowPrefix}.{LocalName}" 이고
        // FlowPrefix 는 RenameEntity 가 이미 store 에 cascade 했으므로, 자식 Work 의 (갱신된) Name 으로
        // canvas node 와 Work 탭 title 을 동기화한다. entityId 가 Flow 가 아니면 worksOf 는 비어 no-op.
        foreach (var work in Queries.worksOf(entityId, _store))
        {
            UpdateMatching(Canvas.CanvasNodes, work.Id, static n => n.Id, static (n, value) => n.Name = value, work.Name);
            UpdateMatching(Canvas.OpenTabs, work.Id, static t => t.RootId, static (t, value) => t.Title = value, work.Name);
        }
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
