using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenIoBatchDialog()
    {
        var storeRows = _store.GetAllApiCallIORows();
        var rows = storeRows
            .Select(r => new IoBatchRow(
                r.CallId, r.ApiCallId, r.FlowName, r.WorkName, r.DeviceName, r.ApiName,
                r.InAddress, r.InSymbol, r.OutAddress, r.OutSymbol,
                r.OutDataType, r.InDataType))
            .ToList();

        if (rows.Count == 0)
        {
            _dialogService.ShowWarning("편집 가능한 ApiCall이 없습니다.");
            return;
        }

        var dialog = new IoBatchSettingsDialog(_store, rows, _currentFilePath, ApplyIoBatchChanges);
        _dialogService.ShowDialog(dialog);
    }

    private bool ApplyIoBatchChanges(IReadOnlyList<IoBatchRow> changed)
    {
        if (changed.Count == 0)
            return false;

        var changes = changed
            .Select(r => new ValueTuple<Guid, Ds2.Core.IOTag?, Ds2.Core.IOTag?>(
                r.ApiCallId,
                string.IsNullOrWhiteSpace(r.InAddress) && string.IsNullOrWhiteSpace(r.InSymbol) ? null : new Ds2.Core.IOTag(r.InSymbol ?? "", r.InAddress ?? "", ""),
                string.IsNullOrWhiteSpace(r.OutAddress) && string.IsNullOrWhiteSpace(r.OutSymbol) ? null : new Ds2.Core.IOTag(r.OutSymbol ?? "", r.OutAddress ?? "", "")))
            .ToList();

        if (!TryEditorAction(() => _store.UpdateApiCallIOTagsBatch(changes)))
            return false;

        StatusText = $"I/O 태그 일괄 변경: {changes.Count}건 적용됨";
        return true;
    }
}
