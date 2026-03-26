using CommunityToolkit.Mvvm.Input;
using Ds2.CSV;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private void ImportCsvStore(DsStore store, string sourceName)
    {
        PrepareForLoadedStore();
        _store.ReplaceStore(store);
        _store.ClearHistory();
        _currentFilePath = null;
        IsDirty = false;
        HasProject = true;
        UpdateTitle();
        Log.Info($"CSV imported: {sourceName}");
        StatusText = $"CSV 불러오기 완료 ({sourceName})";
        RequestRebuildAll(AfterFileLoad);
    }

    private bool TryCreateCsvStore(out DsStore store, out string sourceName)
    {
        store = default!;
        sourceName = string.Empty;

        var dialog = new CsvImportDialog();
        if (_dialogService.ShowDialog(dialog) != true)
            return false;

        sourceName = dialog.SourceDisplayName;
        return TryGetResult(
            CsvImporter.loadProject(dialog.Document, dialog.ProjectName, dialog.SystemName),
            errors => $"CSV 불러오기 실패:\n{JoinLines(errors)}",
            out store);
    }

    [RelayCommand]
    private void ImportCsv()
    {
        if (!ConfirmDiscardChanges())
            return;

        TryRunFileOperation(
            "Import CSV",
            () =>
            {
                if (!TryCreateCsvStore(out var store, out var sourceName))
                    return;

                ImportCsvStore(store, sourceName);
            },
            ex => $"CSV 불러오기 실패: {ex.Message}");
    }

}
