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
                r.CallId, r.ApiCallId, r.FlowName, r.DeviceName, r.ApiName,
                r.InAddress, r.InSymbol, r.OutAddress, r.OutSymbol,
                r.OutDataType, r.InDataType))
            .ToList();

        if (rows.Count == 0)
        {
            _dialogService.ShowWarning("편집 가능한 ApiCall이 없습니다.");
            return;
        }

        var dialog = new IoBatchSettingsDialog(_store, rows, _currentFilePath);
        if (_dialogService.ShowDialog(dialog) != true)
            return;

        var changed = dialog.ChangedRows;
        if (changed.Count == 0)
            return;

        var changes = changed
            .Select(r => new ValueTuple<Guid, string, string, string, string>(
                r.ApiCallId, r.InAddress, r.InSymbol, r.OutAddress, r.OutSymbol))
            .ToList();

        if (TryEditorAction(() => _store.UpdateApiCallIOTagsBatch(changes)))
            StatusText = $"I/O 태그 일괄 변경: {changes.Count}건 적용됨";
    }
}
