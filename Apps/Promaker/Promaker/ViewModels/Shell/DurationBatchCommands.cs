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
            .Select(r => new DurationRow(
                r.WorkId,
                r.SystemName,
                r.FlowName,
                r.WorkName,
                r.PeriodMs.ToString(),
                r.MinDurationMs.ToString(),
                r.MaxDurationMs.ToString(),
                r.IsDeviceWork))
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

        var changes = new List<(Guid, int?, int?, int?)>();
        var invalidCount = 0;
        foreach (var row in changed)
        {
            if (TryParseDurationCell(row.Duration, out var durationMs)
                && TryParseDurationCell(row.MinDuration, out var minDurationMs)
                && TryParseDurationCell(row.MaxDuration, out var maxDurationMs))
            {
                changes.Add((row.WorkId, durationMs, minDurationMs, maxDurationMs));
            }
            else
            {
                invalidCount++;
            }
        }

        if (invalidCount > 0)
            _dialogService.ShowWarning($"{invalidCount}개의 Duration/Range 값이 잘못되어 제외되었습니다.");

        if (changes.Count == 0)
        {
            StatusText = "적용할 유효한 Duration/Range 변경이 없습니다.";
            return;
        }

        if (TryEditorAction(() => _store.UpdateWorkDurationRangesBatch(changes)))
        {
            // 사이드카 정합 — 바뀐 Min/Max 와 어긋난 실측 확정(calibration-state) 해제. 침묵 stale 방지.
            var cleared = Promaker.Shared.CalibrationSidecar.ReconcileAfterDurationWrite(
                changes.Select(c => (c.Item1, c.Item3, c.Item4)).ToList());
            var clearedSuffix = cleared > 0 ? $" (실측 확정 {cleared}건 해제 — 재확정 필요)" : "";
            StatusText = invalidCount > 0
                ? $"Duration/Range 일괄 변경: {changes.Count}건 적용, {invalidCount}건 제외{clearedSuffix}"
                : $"Duration/Range 일괄 변경: {changes.Count}건 적용됨{clearedSuffix}";
        }
    }

    private static bool TryParseDurationCell(string value, out int? ms)
    {
        ms = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (!int.TryParse(value.Trim(), out var parsed))
            return false;

        ms = parsed > 0 ? parsed : null;
        return true;
    }
}
