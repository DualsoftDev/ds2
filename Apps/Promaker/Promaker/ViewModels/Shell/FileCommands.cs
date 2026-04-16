using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Microsoft.Win32;
using Promaker.Dialogs;
using Promaker.Presentation;
using Promaker.Services;

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

        // 최근 파일 목록에 추가
        RecentFilesManager.AddRecentFile(filePath);
        _dispatcher.InvokeAsync(LoadRecentFiles);

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
        if (!GuardSimulationSemanticEdit("파일 열기"))
            return;

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
                    var result = AasxImporter.importIntoStoreWithError(_store, fileName);
                    if (result.IsError)
                    {
                        Log.Warn($"AASX open failed: {result.ErrorValue}");
                        _dialogService.ShowWarning($"AASX 파일 열기 실패:\n\n{result.ErrorValue}");
                        return;
                    }

                    PrepareForLoadedStore();
                    CompleteOpen(fileName, "AASX");
                },
                ex => $"AASX 파일 열기 실패:\n\n{ex.Message}");
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

        // 첫 번째 System 캔버스를 띄움 (Flow 하이라이트 없이)
        ActivateInitialSystemTab();

        // 3D 창이 열려있으면 씬 자동 재빌드
        if (_view3DWindow is { IsVisible: true })
        {
            var projectId = Queries.allProjects(_store).Head.Id;
            _ = Simulation.ThreeD.BuildScene(_store, projectId);
        }
    }

    private static void ExpandAllNodes(IEnumerable<EntityNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = true;
            ExpandAllNodes(node.Children);
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void ShowProjectSettings()
    {
        var project = HasProject
            ? Queries.allProjects(_store).Head
            : null;

        if (project is null) return;

        var dlg = new ProjectPropertiesDialog(project.Name, _store);
        var accepted = _dialogService.ShowDialog(dlg) == true;
        if (!accepted) return;

        TryEditorAction(() =>
        {
            var nextProjectName = dlg.ResultProjectName ?? project.Name;
            if (!string.Equals(project.Name, nextProjectName, StringComparison.Ordinal))
                _store.RenameEntity(project.Id, EntityKind.Project, nextProjectName);

            _store.UpdateProjectProperties(
                dlg.ResultAuthor,
                dlg.ResultDateTime,
                dlg.ResultVersion);

            // 앱 설정으로 저장
            SetSplitDeviceAasx(dlg.ResultSplitDeviceAasx);
            SetCreateDefaultEntitiesOnEmptyAasx(dlg.ResultCreateDefaultEntities);
            SetIriPrefix(dlg.ResultIriPrefix);
            // PresetSystemTypes는 Dialog 내부에서 이미 파일에 저장됨
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
        var projects = Queries.allProjects(_store);
        var suggestedName = _currentFilePath is not null
            ? Path.GetFileNameWithoutExtension(_currentFilePath)
            : (!projects.IsEmpty ? projects.Head.Name : "project");
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
                var exported = AasxExporter.exportFromStore(_store, filePath, IriPrefix, SplitDeviceAasx, CreateDefaultEntitiesOnEmptyAasx);
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

    /// <summary>
    /// 최근 파일 열기
    /// </summary>
    [RelayCommand]
    private void OpenRecentFile(string filePath)
    {
        if (!ConfirmDiscardChanges()) return;

        if (!File.Exists(filePath))
        {
            _dialogService.ShowWarning($"파일을 찾을 수 없습니다:\n{filePath}");
            // 목록에서 제거
            RecentFiles.Remove(filePath);
            return;
        }

        OpenFilePath(filePath);
    }
}
