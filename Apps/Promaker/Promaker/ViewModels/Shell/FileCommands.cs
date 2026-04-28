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
        // 레거시 파일 자동 복구 — OriginFlowId 누락 ApiCall 을 Call→Work→Flow 로 채움.
        var healed = Ds2.Core.CallValidation.healMissingOriginFlowIds(_store);
        if (healed > 0)
            Log.Info($"OriginFlowId auto-heal: {healed} ApiCall(s) restored from Call→Work→Flow chain.");
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
                    // 레거시 파일 자동 복구 — OriginFlowId 누락 ApiCall 을 Call→Work→Flow 로 채움.
                    // (과거 Panel.buildApiCall 경유 생성 시 미설정되던 버그의 뒤처리)
                    var healed = Ds2.Core.CallValidation.healMissingOriginFlowIds(_store);
                    if (healed > 0)
                        Log.Info($"OriginFlowId auto-heal: {healed} ApiCall(s) restored from Call→Work→Flow chain.");
                    PrepareForLoadedStore();
                    CompleteOpen(fileName, "File");
                },
                ex => $"Failed to open file: {ex.Message}");
        }
    }

    private void AfterFileLoad()
    {
        ExplorerRebindRequested?.Invoke();

        // Control Tree 전체 확장
        ExpandAllNodes(ControlTreeRoots);

        // 첫 번째 System 캔버스를 띄움 (Flow 하이라이트 없이)
        ActivateInitialSystemTab();

        // 3D 창이 열려있으면 창 내부 참조·DeviceTree·씬 모두 새 프로젝트로 재동기화
        ResyncView3DIfOpen();

        // AutoLayoutIfNeeded가 좌표 없는 노드에 자동 배치를 적용하면서
        // undo 항목을 생성할 수 있으므로, 로드 완료 후 초기 상태로 확정
        _store.ClearHistory();
        IsDirty = false;
        UpdateTitle();
    }

    private static void ExpandAllNodes(IEnumerable<EntityNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = true;
            ExpandAllNodes(node.Children);
        }
    }

    /// <summary>
    /// "시뮬레이션 결과 보기" 활성 조건 — "출력" 버튼과 동일하게 시뮬 결과 데이터가 있을 때만 활성.
    /// (HasProject 는 HasReportData 가 true 인 시점에는 자명하므로 별도 검사 생략 가능하지만 안전 차원에서 함께 체크.)
    /// </summary>
    private bool CanShowSimulationScenarios() => HasProject && Simulation.HasReportData;

    [RelayCommand(CanExecute = nameof(CanShowSimulationScenarios))]
    private void ShowSimulationScenarios()
    {
        var project = HasProject ? Queries.allProjects(_store).Head : null;
        if (project is null) return;

        // TechnicalData 가 없으면 표시할 게 없음 — 빈 상태로라도 열어 사용자가 확인 가능하도록 신규 생성
        Ds2.Core.TechnicalDataTypes.TechnicalData td;
        if (Microsoft.FSharp.Core.FSharpOption<Ds2.Core.TechnicalDataTypes.TechnicalData>.get_IsSome(project.TechnicalData))
        {
            td = project.TechnicalData.Value;
        }
        else
        {
            td = new Ds2.Core.TechnicalDataTypes.TechnicalData();
            project.TechnicalData = Microsoft.FSharp.Core.FSharpOption<Ds2.Core.TechnicalDataTypes.TechnicalData>.Some(td);
        }

        var dlg = new Promaker.Dialogs.SimulationScenariosDialog(Simulation, td);
        _dialogService.ShowDialog(dlg);
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
                // 시뮬 데이터가 있으면 AASX export 직전 자동으로 시나리오 박제 → Ds2.Core.TechnicalDataTypes.TechnicalData.SimulationResults
                try
                {
                    var captured = Simulation?.TryCaptureScenario(
                        $"AutoCapture_{DateTime.Now:yyyyMMdd_HHmmss}");
                    if (captured != null)
                        Log.Info($"AASX 저장 전 시뮬 시나리오 박제됨: {captured.Meta.ScenarioName}");
                }
                catch (Exception capEx)
                {
                    Log.Warn($"AASX 저장 전 시뮬 시나리오 박제 실패 (무시): {capEx.Message}");
                }

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
    /// 런타임 설정 Dialog 열기 — Runtime Mode 선택 + 좌·우 이미지 미리보기.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void ShowRuntimeSettings()
    {
        if (!GuardSimulationSemanticEdit("런타임 설정"))
            return;

        var dlg = new Promaker.Windows.RuntimeSettingDialog(this)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        _dialogService.ShowDialog(dlg);
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
