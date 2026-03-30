using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Store;
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
            RequestRebuildAll(() => Selection.ExpandAndSelectNode(id));
            Simulation.NotifyStoreChanged();
            return;
        }

        switch (evt)
        {
            case EditorEvent.EntityRenamed ren:
                ApplyEntityRename(ren.id, ren.newName);
                return;

            case EditorEvent.HistoryChanged h:
                RebuildHistoryItems(h.undoLabels, h.redoLabels);
                UpdateTitle();
                return;

            case EditorEvent.WorkPropsChanged:
            case EditorEvent.ApiDefPropsChanged:
                PropertyPanel.Refresh();
                Simulation.NotifyStoreChanged();
                return;

            case EditorEvent.CallPropsChanged cp:
                PropertyPanel.Refresh();
                RefreshCallConditionBadge(cp.id);
                Simulation.NotifyStoreChanged();
                return;

            case EditorEvent.ArrowWorkAdded:
            case EditorEvent.ArrowWorkRemoved:
            case EditorEvent.ArrowCallAdded:
            case EditorEvent.ArrowCallRemoved:
            case { IsConnectionsChanged: true }:
                Simulation.NotifyConnectionsChanged();
                Canvas.RefreshCanvasForActiveTab();
                Selection.ApplyArrowSelectionVisuals();
                return;

            case { IsStoreRefreshed: true }:
                RequestRebuildAll();
                return;
        }

        if (!TryEditorFunc(
                () => _store.IsTreeStructuralEvent(evt),
                out var isTreeStructuralEvent,
                fallback: false))
            return;

        if (isTreeStructuralEvent)
        {
            RequestRebuildAll();
            Simulation.NotifyStoreChanged();
            return;
        }

        Log.Warn($"Unhandled event: {evt.GetType().Name}");
        StatusText = $"[WARN] Unhandled event: {evt.GetType().Name}";
        RequestRebuildAll();
    }

    private void RefreshCallConditionBadge(Guid callId)
    {
        var node = Canvas.CanvasNodes.FirstOrDefault(n => n.Id == callId);
        if (node is null) return;

        if (TryEditorRef(() => CallConditionQueries.GetCallConditionTypes(_store, callId), out var types))
            node.UpdateConditionTypes(types);
    }

    private void ApplyEntityRename(Guid entityId, string newName)
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
        UpdateMatching(Selection.EnumerateTreeNodes(), entityId, static n => n.Id, static (n, value) => n.Name = value, newName);
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
