using System.IO;
using System.Text;
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

    private static string BuildCsvExportPreview(DsStore store)
    {
        var projects = DsQuery.allProjects(store);
        if (projects.IsEmpty)
            return "내보낼 프로젝트가 없습니다.";

        var project = projects.Head;
        var activeSystems = DsQuery.activeSystemsOf(project.Id, store);
        var flowCount = activeSystems.Sum(system => DsQuery.flowsOf(system.Id, store).Length);
        var workCount = activeSystems.Sum(system =>
            DsQuery.flowsOf(system.Id, store).Sum(flow => DsQuery.worksOf(flow.Id, store).Length));
        var callCount = activeSystems.Sum(system =>
            DsQuery.flowsOf(system.Id, store).Sum(flow =>
                DsQuery.worksOf(flow.Id, store).Sum(work => DsQuery.callsOf(work.Id, store).Length)));

        var sb = new StringBuilder();
        sb.AppendLine($"✓ Active System: {activeSystems.Length}개");
        sb.AppendLine($"✓ Flow: {flowCount}개");
        sb.AppendLine($"✓ Work: {workCount}개");
        sb.AppendLine($"✓ Call: {callCount}개");
        sb.AppendLine();
        sb.Append($"총 {callCount}개의 항목이 CSV로 내보내집니다.");
        return sb.ToString();
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
        var project = DsQuery.allProjects(_store).Head;
        var dialog = new CsvExportDialog(
            project.Name,
            BuildCsvExportPreview(_store),
            $"{project.Name}.csv");

        if (_dialogService.ShowDialog(dialog) != true)
            return;

        TryRunFileOperation(
            $"Export CSV '{dialog.OutputPath}'",
            () =>
            {
                if (!TryGetResult(
                        CsvExporter.saveProjectToFile(_store, dialog.OutputPath),
                        error => error,
                        out _))
                    return;

                StatusText = $"CSV 내보내기 완료 ({Path.GetFileName(dialog.OutputPath)})";
                Log.Info($"CSV exported: {dialog.OutputPath}");
            },
            ex => $"CSV 내보내기 실패: {ex.Message}");
    }
}
