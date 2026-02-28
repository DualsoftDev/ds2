using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Ds2.UI.Core;
using log4net;
using Microsoft.FSharp.Core;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
    private void WireEvents()
    {
        var observable = (IObservable<EditorEvent>)_editor.OnEvent;
        _eventSubscription?.Dispose();
        _eventSubscription = observable.Subscribe(new ActionObserver<EditorEvent>(
            evt => _dispatcher.Invoke(() => HandleEvent(evt)),
            error => _dispatcher.Invoke(() =>
            {
                Log.Error("EditorEvent 구독자 에러", error);
                StatusText = $"[ERROR] {error.Message}";
                RebuildAll();
            })));
    }

    private void HandleEvent(EditorEvent evt)
    {
        var addedIdOpt = _editor.TryGetAddedEntityId(evt);
        if (FSharpOption<Guid>.get_IsSome(addedIdOpt))
        {
            RebuildAll();
            ExpandNodeAndAncestors(addedIdOpt.Value);
            return;
        }

        switch (evt)
        {
            case EditorEvent.EntityRenamed ren:
                ApplyEntityRename(ren.id, ren.newName);
                return;

            case EditorEvent.WorkMoved wm:
                ApplyNodeMove(wm.id, wm.newPos);
                return;

            case EditorEvent.CallMoved cm:
                ApplyNodeMove(cm.id, cm.newPos);
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
                RebuildAll();
                return;
        }

        if (_editor.IsTreeStructuralEvent(evt))
        {
            RebuildAll();
            return;
        }

        Log.Warn($"Unhandled event: {evt.GetType().Name}");
        StatusText = $"[WARN] Unhandled event: {evt.GetType().Name}";
        RebuildAll();
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
