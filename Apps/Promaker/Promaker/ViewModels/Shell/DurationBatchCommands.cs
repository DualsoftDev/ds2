using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenDurationBatchDialog()
    {
        if (!GuardSimulationSemanticEdit("Duration 일괄 변경"))
            return;

        var storeRows = _store.GetAllWorkDurationRows();
        var rows = storeRows
            .Select(r => new DurationRow(r.WorkId, r.SystemName, r.FlowName, r.WorkName, r.PeriodMs.ToString(), r.IsDeviceWork))
            .ToList();

        if (rows.Count == 0)
        {
            _dialogService.ShowWarning("편집 가능한 Work가 없습니다.");
            return;
        }

        var dialog = new DurationBatchDialog(rows, _currentFilePath);
        if (_dialogService.ShowDialog(dialog) != true)
            return;

        var changed = dialog.ChangedRows;
        if (changed.Count == 0)
            return;

        var changes = new List<(Guid, int)>();
        var invalidCount = 0;
        foreach (var row in changed)
        {
            if (int.TryParse(row.Duration, out var ms))
                changes.Add((row.WorkId, ms));
            else
                invalidCount++;
        }

        if (invalidCount > 0)
            _dialogService.ShowWarning($"{invalidCount}개의 Duration 값이 잘못되어 제외되었습니다.");

        if (changes.Count == 0)
        {
            StatusText = "적용할 유효한 Duration 변경이 없습니다.";
            return;
        }

        if (TryEditorAction(() => _store.UpdateWorkDurationsBatch(changes)))
        {
            StatusText = invalidCount > 0
                ? $"Duration 일괄 변경: {changes.Count}건 적용, {invalidCount}건 제외"
                : $"Duration 일괄 변경: {changes.Count}건 적용됨";
        }
    }
}
