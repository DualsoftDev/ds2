using System;
using System.Collections.Generic;
using Ds2.UI.Core;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private void WireEvents()
    {
        var observable = (IObservable<EditorEvent>)_store.OnEvent;
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
                () => _store.TryGetAddedEntityId(evt),
                out FSharpOption<Guid>? addedIdOpt,
                fallback: null))
            return;

        if (addedIdOpt?.Value is { } addedId)
        {
            RequestRebuildAll(() => ExpandNodeAndAncestors(addedId));
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
            case EditorEvent.CallPropsChanged:
            case EditorEvent.ApiDefPropsChanged:
                RefreshPropertyPanel();
                return;

            case EditorEvent.ArrowWorkAdded:
            case EditorEvent.ArrowWorkRemoved:
            case EditorEvent.ArrowCallAdded:
            case EditorEvent.ArrowCallRemoved:
                RefreshCanvasForActiveTab();
                ApplyArrowSelectionVisuals();
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
            return;
        }

        Log.Warn($"Unhandled event: {evt.GetType().Name}");
        StatusText = $"[WARN] Unhandled event: {evt.GetType().Name}";
        RequestRebuildAll();
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

        UpdateMatching(CanvasNodes, entityId, static n => n.Id, static (n, value) => n.Name = value, newName);
        UpdateMatching(EnumerateTreeNodes(), entityId, static n => n.Id, static (n, value) => n.Name = value, newName);
        UpdateMatching(OpenTabs, entityId, static t => t.RootId, static (t, value) => t.Title = value, newName);

        if (SelectedNode is { Id: var selectedId } && selectedId == entityId)
        {
            NameEditorText = newName;
            IsNameDirty = false;
        }
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
