using System;
using System.Windows;
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
        FileWatcher.ResetMTime();
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

    /// <summary>"파일(PLC, CSV)로부터 모델만들기" 통합 진입점.
    /// 사용자에게 모드 선택 (PLC 심볼 / 일반 CSV) 후 적절한 흐름으로 분기.
    /// PLC 심볼: SymbolWizardDialog (Mitsubishi CSV / XG5000 XML 자동 추론 + dsev2 매칭 룰).
    /// 일반 CSV: CsvImportDialog (기존 모델 데이터 import).</summary>
    [RelayCommand]
    private void ImportCsv()
    {
        if (!ConfirmDiscardChanges())
            return;

        // 모드 선택 launcher — 라디오 버튼 두 개 (PLC 심볼 / 일반 CSV).
        var launcher = new FileImportLauncherDialog { Owner = Application.Current?.MainWindow };
        if (launcher.ShowDialog() != true)
            return;

        if (launcher.SelectedMode == FileImportLauncherDialog.ImportMode.PlcSymbol)
        {
            if (!GuardSimulationSemanticEdit("PLC 심볼 → 모델 생성"))
                return;

            var importStore = new DsStore();
            var wizard = new SymbolWizardDialog(importStore);
            if (_dialogService.ShowDialog(wizard) == true)
                ImportCsvStore(importStore, wizard.SourceDisplayName);
            return;
        }

        // 일반 CSV (기존 흐름)
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
        string suggestedName;
        if (_currentFilePath is not null)
        {
            suggestedName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath);
        }
        else
        {
            var projects = Queries.allProjects(_store);
            suggestedName = !projects.IsEmpty ? projects.Head.Name : "project";
        }

        var dialog = new SaveFileDialog
        {
            Title = "CSV 내보내기",
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            DefaultExt = FileExtensions.Csv,
            FileName = $"{suggestedName}.csv"
        };

        if (dialog.ShowDialog() != true)
            return;

        if (ExportCsvToPath(dialog.FileName))
            CsvFileHelper.PromptOpenAfterExport(dialog.FileName, title: "프로젝트 CSV 내보내기");
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
