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
        "All Supported (*.json;*.aasx)|*.json;*.aasx|JSON Files (*.json)|*.json|AASX Files (*.aasx)|*.aasx";

    private static bool IsAasx(string path) =>
        Path.GetExtension(path).Equals(".aasx", StringComparison.OrdinalIgnoreCase);

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

        if (IsAasx(fileName))
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
        if (_currentFilePath is null)
        {
            var projects = DsQuery.allProjects(_store);
            var suggestedName = !projects.IsEmpty ? projects.Head.Name : "project";
            var dlg = new SaveFileDialog
            {
                Filter = FileFilter,
                DefaultExt = ".json",
                FileName = suggestedName
            };

            if (dlg.ShowDialog() != true) return;
            _currentFilePath = dlg.FileName;
        }

        var filePath = _currentFilePath;

        if (IsAasx(filePath))
        {
            TryRunFileOperation(
                $"Save AASX '{filePath}'",
                () =>
                {
                    if (!AasxExporter.exportFromStore(_store, filePath))
                    {
                        Log.Warn($"AASX save failed: no project ({filePath})");
                        DialogHelpers.Warn("No project available for AASX save.");
                        return;
                    }

                    IsDirty = false;
                    UpdateTitle();
                    StatusText = "Saved.";
                    Log.Info($"AASX saved: {filePath}");
                },
                ex => $"Failed to save AASX: {ex.Message}");
        }
        else
        {
            TryRunFileOperation(
                $"Save file '{filePath}'",
                () =>
                {
                    _store.SaveToFile(filePath);
                    IsDirty = false;
                    UpdateTitle();
                    StatusText = "Saved.";
                    Log.Info($"File saved: {filePath}");
                },
                ex => $"Failed to save file: {ex.Message}");
        }
    }
}
