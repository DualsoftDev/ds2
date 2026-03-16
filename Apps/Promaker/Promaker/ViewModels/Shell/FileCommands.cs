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

    private static bool IsAasx(string path) =>
        Path.GetExtension(path).Equals(".aasx", StringComparison.OrdinalIgnoreCase);

    private static bool IsMermaid(string path) =>
        Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);

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
                    var result = Ds2.Mermaid.MermaidImporter.loadProjectFromFile(fileName);
                    if (result.IsError)
                    {
                        DialogHelpers.Warn($"Mermaid 불러오기 실패:\n{string.Join("\n", result.ErrorValue)}");
                        return;
                    }

                    _store.ReplaceStore(result.ResultValue);
                    PrepareForLoadedStore();
                    _currentFilePath = fileName;
                    IsDirty = false;
                    UpdateTitle();
                    Log.Info($"Mermaid opened: {fileName}");
                    StatusText = $"Opened: {Path.GetFileName(fileName)}";
                    RequestRebuildAll(AfterFileLoad);
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
                    _currentFilePath = fileName;
                    IsDirty = false;
                    UpdateTitle();
                    Log.Info($"AASX opened: {fileName}");
                    StatusText = $"Opened: {Path.GetFileName(fileName)}";
                    RequestRebuildAll(AfterFileLoad);
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
                    _currentFilePath = fileName;
                    IsDirty = false;
                    UpdateTitle();
                    Log.Info($"File opened: {fileName}");
                    StatusText = $"Opened: {Path.GetFileName(fileName)}";
                    RequestRebuildAll(AfterFileLoad);
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

        SaveToPath(_currentFilePath);
        return true;
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

        _currentFilePath = dlg.FileName;
        SaveToPath(_currentFilePath);
        return true;
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
                    () =>
                    {
                        _currentFilePath = filePath;
                        IsDirty = false;
                        UpdateTitle();
                        StatusText = "Saved.";
                        Log.Info($"Mermaid saved: {filePath}");
                    });
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
                    () =>
                    {
                        IsDirty = false;
                        UpdateTitle();
                        StatusText = "Saved.";
                        Log.Info($"AASX saved: {filePath}");
                    });
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
            IsDirty = false;
            UpdateTitle();
            StatusText = "Saved.";
            Log.Info($"File saved: {filePath}");
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
