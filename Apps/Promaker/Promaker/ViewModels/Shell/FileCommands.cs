using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Microsoft.Win32;
using Promaker.Dialogs;
using Promaker.Presentation;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private const string FileFilter =
        "All Supported (*.sdf;*.json;*.aasx;*.md)|*.sdf;*.json;*.aasx;*.md|SDF Files (*.sdf)|*.sdf|JSON Files (*.json)|*.json|AASX Files (*.aasx)|*.aasx|Mermaid Files (*.md)|*.md";

    private static bool HasExtension(string path, string extension) =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsAasx(string path) => HasExtension(path, FileExtensions.Aasx);

    private static bool IsMermaid(string path) => HasExtension(path, FileExtensions.Mermaid);

    private bool TryRunFileOperation(string operation, Action action, Func<Exception, string> warnMessage)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"{operation} failed", ex);
            _dialogService.ShowWarning(warnMessage(ex));
            return false;
        }
    }

    private static string JoinLines(IEnumerable<string> lines) => string.Join("\n", lines);

    private bool TryGetResult<T, TError>(FSharpResult<T, TError> result, Func<TError, string> formatError, out T value)
    {
        if (result.IsError)
        {
            _dialogService.ShowWarning(formatError(result.ErrorValue));
            value = default!;
            return false;
        }

        value = result.ResultValue;
        return true;
    }

    private void CompleteOpen(string filePath, string kind)
    {
        _store.ClearHistory();
        _currentFilePath = filePath;
        IsDirty = false;
        HasProject = true;
        UpdateTitle();
        Log.Info($"{kind} opened: {filePath}");
        StatusText = $"Opened: {Path.GetFileName(filePath)}";
        RequestRebuildAll(AfterFileLoad);
    }

    private void ReplaceOpenedStore(string filePath, DsStore store, string kind)
    {
        _store.ReplaceStore(store);
        CompleteOpen(filePath, kind);
    }

    private void CompleteSave(string filePath, string kind)
    {
        _currentFilePath = filePath;
        IsDirty = false;
        UpdateTitle();
        StatusText = "Saved.";
        Log.Info($"{kind} saved: {filePath}");
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (!ConfirmDiscardChanges()) return;

        var dlg = new OpenFileDialog { Filter = FileFilter };
        if (dlg.ShowDialog() != true) return;

        OpenFilePath(dlg.FileName);
    }

    /// <summary>지정된 경로의 파일을 연다. 드래그 &amp; 드롭에서도 재사용.</summary>
    internal void OpenFilePath(string fileName)
    {
        if (IsMermaid(fileName))
        {
            TryRunFileOperation(
                $"Open Mermaid '{fileName}'",
                () =>
                {
                    if (!TryGetResult(
                            Ds2.Mermaid.MermaidImporter.loadProjectFromFile(fileName),
                            errors => $"Mermaid 불러오기 실패:\n{JoinLines(errors)}",
                            out var store))
                        return;

                    PrepareForLoadedStore();
                    ReplaceOpenedStore(fileName, store, "Mermaid");
                },
                ex => $"Mermaid 불러오기 실패: {ex.Message}");
        }
        else if (IsAasx(fileName))
        {
            TryRunFileOperation(
                $"Open AASX '{fileName}'",
                () =>
                {
                    if (!AasxImporter.importIntoStore(_store, fileName))
                    {
                        Log.Warn($"AASX open failed: empty result ({fileName})");
                        _dialogService.ShowWarning("Failed to open AASX file.");
                        return;
                    }

                    PrepareForLoadedStore();
                    CompleteOpen(fileName, "AASX");
                },
                ex => $"Failed to open AASX: {ex.Message}");
        }
        else
        {
            TryRunFileOperation(
                $"Open file '{fileName}'",
                () =>
                {
                    var json = File.ReadAllText(fileName);
                    if (Ds2.Store.Compat.LegacyJsonImport.isLegacyJsonFormat(json))
                    {
                        var newStore = new DsStore();
                        if (!Ds2.Store.Compat.LegacyJsonImport.importLegacyJson(newStore, json))
                        {
                            _dialogService.ShowWarning("레거시 JSON 불러오기에 실패했습니다.");
                            return;
                        }
                        _store.ReplaceStore(newStore);
                    }
                    else
                    {
                        _store.LoadFromFile(fileName);
                    }
                    PrepareForLoadedStore();
                    CompleteOpen(fileName, "File");
                },
                ex => $"Failed to open file: {ex.Message}");
        }
    }

    private void AfterFileLoad()
    {
        // Control Tree 전체 확장
        ExpandAllNodes(ControlTreeRoots);

        // 첫 번째 System 캔버스를 띄움 (Flow 하이라이트 없이)
        var firstSystem = TreeNodeSearch
            .EnumerateNodes(ControlTreeRoots)
            .FirstOrDefault(node => node.EntityType == EntityKind.System);

        if (firstSystem is not null)
            Canvas.OpenCanvasTab(firstSystem.Id, EntityKind.System);
    }

    private static void ExpandAllNodes(IEnumerable<EntityNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = true;
            ExpandAllNodes(node.Children);
        }
    }

    [RelayCommand]
    private void ShowProjectSettings()
    {
        var project = HasProject
            ? DsQuery.allProjects(_store).Head
            : null;
        var properties = project?.Properties ?? new Ds2.Core.ProjectProperties();
        var dlg = new ProjectPropertiesDialog(project?.Name ?? "", properties);
        var accepted = _dialogService.ShowDialog(dlg) == true;
        if (!accepted) return;

        if (project is null) return;

        TryEditorAction(() =>
        {
            var nextProjectName = dlg.ResultProjectName ?? project.Name;
            if (!string.Equals(project.Name, nextProjectName, StringComparison.Ordinal))
                _store.RenameEntity(project.Id, EntityKind.Project, nextProjectName);

            _store.UpdateProjectProperties(
                dlg.ResultIriPrefix ?? "",
                dlg.ResultGlobalAssetId ?? "",
                dlg.ResultAuthor ?? "",
                dlg.ResultVersion ?? "",
                dlg.ResultDescription ?? "",
                dlg.ResultSplitDeviceAasx);
        });
        StatusText = "프로젝트 속성이 변경되었습니다.";
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void SaveFile()
    {
        TrySaveFile();
    }

    private bool TrySaveFile()
    {
        if (_currentFilePath is null)
        {
            return TrySaveFileAs();
        }

        return SaveToPath(_currentFilePath);
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void SaveFileAs()
    {
        TrySaveFileAs();
    }

    private bool TrySaveFileAs()
    {
        var projects = DsQuery.allProjects(_store);
        var suggestedName = !projects.IsEmpty ? projects.Head.Name : "project";
        var dlg = new SaveFileDialog
        {
            Filter = FileFilter,
            DefaultExt = FileExtensions.Sdf,
            FileName = suggestedName
        };

        if (dlg.ShowDialog() != true) return false;

        return SaveToPath(dlg.FileName);
    }

    private bool SaveToPath(string filePath)
    {
        if (IsMermaid(filePath))
        {
            try
            {
                var result = Ds2.Mermaid.MermaidExporter.saveProjectToFile(_store, filePath);
                return SaveOutcomeFlow.TryCompleteMermaidSave(
                    result,
                    _dialogService.ShowWarning,
                    () => CompleteSave(filePath, "Mermaid"));
            }
            catch (Exception ex)
            {
                Log.Error($"Save Mermaid '{filePath}' failed", ex);
                _dialogService.ShowWarning($"Mermaid 저장 실패: {ex.Message}");
                return false;
            }
        }

        if (IsAasx(filePath))
        {
            try
            {
                var exported = AasxExporter.exportFromStore(_store, filePath);
                if (!exported)
                    Log.Warn($"AASX save failed: no project ({filePath})");

                return SaveOutcomeFlow.TryCompleteAasxSave(
                    exported,
                    _dialogService.ShowWarning,
                    "No project available for AASX save.",
                    () => CompleteSave(filePath, "AASX"));
            }
            catch (Exception ex)
            {
                Log.Error($"Save AASX '{filePath}' failed", ex);
                _dialogService.ShowWarning($"Failed to save AASX: {ex.Message}");
                return false;
            }
        }

        try
        {
            _store.SaveToFile(filePath);
            CompleteSave(filePath, "File");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Save file '{filePath}' failed", ex);
            _dialogService.ShowWarning($"Failed to save file: {ex.Message}");
            return false;
        }
    }
}
