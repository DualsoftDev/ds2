using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenDurationBatchDialog()
    {
        var storeRows = _store.GetAllWorkDurationRows();
        var rows = storeRows
            .Select(r => new DurationRow(r.WorkId, r.FlowName, r.WorkName, r.PeriodMs.ToString()))
            .ToList();

        if (rows.Count == 0)
        {
            _dialogService.ShowWarning("편집 가능한 Work가 없습니다.");
            return;
        }

        var dialog = new DurationBatchDialog(rows);
        if (_dialogService.ShowDialog(dialog) != true)
            return;

        var changed = dialog.ChangedRows;
        if (changed.Count == 0)
            return;

        var changes = new List<(Guid, int)>();
        foreach (var row in changed)
        {
            if (int.TryParse(row.Duration, out var ms))
                changes.Add((row.WorkId, ms));
        }

        if (changes.Count > 0 && TryEditorAction(() => _store.UpdateWorkDurationsBatch(changes)))
            StatusText = $"Duration 일괄 변경: {changes.Count}건 적용됨";
    }
}
