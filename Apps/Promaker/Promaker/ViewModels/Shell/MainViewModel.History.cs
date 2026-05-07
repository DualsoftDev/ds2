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
    [RelayCommand]
    private void JumpToHistory(HistoryPanelItem? item)
    {
        if (item is null || Simulation.IsSimulating)
            return;

        int clickedIdx = HistoryItems.IndexOf(item);
        if (clickedIdx < 0)
            return;

        int delta = clickedIdx - CurrentHistoryIndex;
        if (delta < 0)
        {
            _pasteCount = 0;
            TryEditorAction(() => _store.UndoTo(-delta));
        }
        else if (delta > 0)
        {
            _pasteCount = 0;
            TryEditorAction(() => _store.RedoTo(delta));
        }
        else
        {
            return;
        }

        if (clickedIdx == 0)
        {
            RequestRebuildAll(ActivateInitialSystemTab);
            return;
        }

        var targetIds = _store.TryGetUndoAffectedIds(0);
        RequestRebuildAll(() => ActivateCanvasForAffectedEntities(targetIds));
    }

    internal void ActivateInitialSystemTab()
    {
        var firstSystem = TreeNodeSearch
            .EnumerateNodes(ControlTreeRoots)
            .FirstOrDefault(node => node.EntityType == EntityKind.System);
        if (firstSystem is not null)
            Canvas.OpenCanvasTab(firstSystem.Id, EntityKind.System);
    }

    private void ActivateCanvasForAffectedEntities(IEnumerable<Guid>? affectedIds)
    {
        if (affectedIds is null)
            return;

        foreach (var entityId in affectedIds)
        {
            var kind = entityId switch
            {
                _ when _store.Works.ContainsKey(entityId) => EntityKind.Work,
                _ when _store.Flows.ContainsKey(entityId) => EntityKind.Flow,
                _ when _store.Systems.ContainsKey(entityId) => EntityKind.System,
                _ when _store.Calls.ContainsKey(entityId) => EntityKind.Call,
                _ => (EntityKind?)null
            };
            if (kind is null)
                continue;

            var parentInfo = EditorNavigation.TryOpenParentTabOrNull(_store, kind.Value, entityId);
            var directInfo = EditorNavigation.TryOpenTabForEntityOrNull(_store, kind.Value, entityId);
            var tabInfo = parentInfo ?? directInfo;
            if (tabInfo is null)
                continue;

            Canvas.OpenCanvasTab(tabInfo.RootId, tabInfo.Kind switch
            {
                TabKind.System => EntityKind.System,
                TabKind.Flow => EntityKind.Flow,
                TabKind.Work => EntityKind.Work,
                _ => EntityKind.System
            }, expandTree: false);
            return;
        }
    }

    private void RebuildHistoryItems(
        IEnumerable<string> undoLabels,
        IEnumerable<string> redoLabels)
    {
        var undoList = undoLabels.ToList();
        var redoList = redoLabels.ToList();
        CanUndo = undoList.Count > 0;
        CanRedo = redoList.Count > 0;
        IsDirty = undoList.Count > 0;

        HistoryItems.Clear();
        HistoryItems.Add(new HistoryPanelItem("(초기 상태)", isRedo: false));
        foreach (var label in Enumerable.Reverse(undoList))
            HistoryItems.Add(new HistoryPanelItem(label, isRedo: false));
        foreach (var label in redoList)
            HistoryItems.Add(new HistoryPanelItem(label, isRedo: true));
        CurrentHistoryIndex = undoList.Count;
        RefreshEditorCommandStates();
    }
}
