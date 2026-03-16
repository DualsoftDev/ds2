using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.UI.Core;
using Microsoft.FSharp.Core;
using Microsoft.Win32;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private const string FileFilter =
        "All Supported (*.json;*.aasx;*.md)|*.json;*.aasx;*.md|JSON Files (*.json)|*.json|AASX Files (*.aasx)|*.aasx|Mermaid Files (*.md)|*.md";

    private static bool HasExtension(string path, string extension) =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsAasx(string path) => HasExtension(path, ".aasx");

    private static bool IsMermaid(string path) => HasExtension(path, ".md");

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
            DialogHelpers.Warn(warnMessage(ex));
            return false;
        }
    }

    private static string JoinLines(IEnumerable<string> lines) => string.Join("\n", lines);

    private static bool TryGetResult<T, TError>(FSharpResult<T, TError> result, Func<TError, string> formatError, out T value)
    {
        if (result.IsError)
        {
            DialogHelpers.Warn(formatError(result.ErrorValue));
            value = default!;
            return false;
        }

        value = result.ResultValue;
        return true;
    }

    private void CompleteOpen(string filePath, string kind)
    {
        _currentFilePath = filePath;
        IsDirty = false;
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

        var fileName = dlg.FileName;

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
                        DialogHelpers.Warn("Failed to open AASX file.");
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
                    _store.LoadFromFile(fileName);
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

        var firstSystem = TreeNodeSearch
            .EnumerateNodes(ControlTreeRoots)
            .FirstOrDefault(node => node.EntityType == EntityKind.System);

        if (firstSystem is not null)
            Canvas.OpenCanvasTab(firstSystem.Id, firstSystem.EntityType);
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
        var projects = DsQuery.allProjects(_store);
        if (projects.IsEmpty)
        {
            DialogHelpers.Warn("프로젝트가 없습니다.");
            return;
        }

        var project = projects.Head;
        var dlg = new ProjectPropertiesDialog(project.Properties);
        if (!DialogHelpers.ShowOwnedDialog(dlg)) return;

        TryEditorAction(() =>
        {
            var props = project.Properties;
            props.IriPrefix     = string.IsNullOrEmpty(dlg.ResultIriPrefix)     ? null : FSharpOption<string>.Some(dlg.ResultIriPrefix!);
            props.GlobalAssetId = string.IsNullOrEmpty(dlg.ResultGlobalAssetId) ? null : FSharpOption<string>.Some(dlg.ResultGlobalAssetId!);
            props.Author        = string.IsNullOrEmpty(dlg.ResultAuthor)        ? null : FSharpOption<string>.Some(dlg.ResultAuthor!);
            props.Version       = string.IsNullOrEmpty(dlg.ResultVersion)       ? null : FSharpOption<string>.Some(dlg.ResultVersion!);
            props.Description   = string.IsNullOrEmpty(dlg.ResultDescription)   ? null : FSharpOption<string>.Some(dlg.ResultDescription!);
        });
        StatusText = "프로젝트 속성이 변경되었습니다.";
    }

    [RelayCommand]
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

    [RelayCommand]
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
            DefaultExt = ".json",
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
                    DialogHelpers.Warn,
                    () => CompleteSave(filePath, "Mermaid"));
            }
            catch (Exception ex)
            {
                Log.Error($"Save Mermaid '{filePath}' failed", ex);
                DialogHelpers.Warn($"Mermaid 저장 실패: {ex.Message}");
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
                    DialogHelpers.Warn,
                    "No project available for AASX save.",
                    () => CompleteSave(filePath, "AASX"));
            }
            catch (Exception ex)
            {
                Log.Error($"Save AASX '{filePath}' failed", ex);
                DialogHelpers.Warn($"Failed to save AASX: {ex.Message}");
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
            DialogHelpers.Warn($"Failed to save file: {ex.Message}");
            return false;
        }
    }
}
