using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;

namespace Ds2.UI.Frontend.ViewModels;

public sealed class HistoryPanelItem(string label, bool isRedo)
{
    public string Label  { get; } = label;
    public bool   IsRedo { get; } = isRedo;
}

public partial class MainViewModel
{
    [RelayCommand]
    private void JumpToHistory(HistoryPanelItem? item)
    {
        if (item is null) return;
        int clickedIdx = HistoryItems.IndexOf(item);
        if (clickedIdx < 0) return;
        int delta = clickedIdx - CurrentHistoryIndex;
        if (delta < 0) _editor.UndoTo(-delta);
        else if (delta > 0) _editor.RedoTo(delta);
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
    }
}
