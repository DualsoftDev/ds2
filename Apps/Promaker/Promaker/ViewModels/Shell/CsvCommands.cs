using CommunityToolkit.Mvvm.Input;
using Ds2.CSV;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.Win32;
using Promaker.Dialogs;
using Promaker.Presentation;

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

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void ExportCsv()
    {
        var projects = Queries.allProjects(_store);
        var suggestedName = !projects.IsEmpty ? projects.Head.Name : "project";
        var dialog = new SaveFileDialog
        {
            Title = "CSV 내보내기",
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            DefaultExt = FileExtensions.Csv,
            FileName = $"{suggestedName}.csv"
        };

        if (dialog.ShowDialog() != true)
            return;

        ExportCsvToPath(dialog.FileName);
    }

    private bool ExportCsvToPath(string filePath)
    {
        try
        {
            var result = CsvExporter.saveProjectToFile(_store, filePath);
            if (result.IsOk)
            {
                Log.Info($"CSV exported: {filePath}");
                StatusText = $"CSV 내보내기 완료 ({filePath})";
                return true;
            }

            _dialogService.ShowWarning(result.ErrorValue);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"CSV export failed: {filePath}", ex);
            _dialogService.ShowWarning($"CSV 내보내기 실패: {ex.Message}");
            return false;
        }
    }

}
