using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Aasx;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.LlmAgent;
using Microsoft.FSharp.Core;
using Microsoft.Win32;
using Promaker.Dialogs;
using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private const string FileFilter =
        "All Supported (*.sdf;*.json;*.aasx;*.md;*.yaml;*.yml)|*.sdf;*.json;*.aasx;*.md;*.yaml;*.yml|SDF Files (*.sdf)|*.sdf|JSON Files (*.json)|*.json|AASX Files (*.aasx)|*.aasx|Mermaid Files (*.md)|*.md|YAML Files — lossy 공유 포맷 (*.yaml;*.yml)|*.yaml;*.yml";

    private static bool HasExtension(string path, string extension) =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsAasx(string path) => HasExtension(path, FileExtensions.Aasx);

    private static bool IsMermaid(string path) => HasExtension(path, FileExtensions.Mermaid);

    private static bool IsYaml(string path) =>
        HasExtension(path, FileExtensions.Yaml) || HasExtension(path, FileExtensions.YamlAlt);

    /// <summary>
    /// `.yaml` Open 직후 AfterFileLoad 가 IsDirty=false 로 덮어쓰지 않도록 lossy 표식.
    /// CompleteOpen→AfterFileLoad chain 의 마지막에 IsDirty=true 강제 후 즉시 reset.
    /// </summary>
    private bool _loadedAsLossy;

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
        RecordCurrentFileMTime();
        IsDirty = false;
        HasProject = true;
        LlmChatVm?.OnProjectOpened();
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
        RecordCurrentFileMTime();
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

    /// <summary>
    /// 지정된 경로의 파일을 연다. 드래그 &amp; 드롭에서도 재사용.
    /// 큰 프로젝트(AASX 등)는 수 초 걸릴 수 있으므로 BusyOverlay 를 먼저 렌더한 뒤
    /// Background 우선순위로 본 작업을 시작 → 사용자가 "로딩 중" 표시를 즉시 확인.
    /// 본 작업이 RequestRebuildAll 을 큐잉하면 그 rebuild 가 끝난 시점에 IsBusy=false.
    /// </summary>
    internal void OpenFilePath(string fileName)
    {
        BusyMessage = $"파일을 여는 중... {Path.GetFileName(fileName)}";
        IsBusy = true;

        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                OpenFilePathCore(fileName);
            }
            finally
            {
                // CompleteOpen 이 RequestRebuildAll 을 큐잉했으면 rebuild 완료 후 hide.
                // (실패 분기 / 빠른 분기에서는 큐잉이 없으므로 즉시 hide.)
                if (_rebuildQueued)
                    _pendingRebuildActions.Add(() => IsBusy = false);
                else
                    IsBusy = false;
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OpenFilePathCore(string fileName)
    {
        // _loadedAsLossy 는 yaml 분기에서만 set. 다른 분기 진입 직전 안전 reset.
        _loadedAsLossy = false;

        if (IsYaml(fileName))
        {
            TryRunFileOperation(
                $"Open YAML '{fileName}'",
                () =>
                {
                    var yamlText = File.ReadAllText(fileName, Encoding.UTF8);
                    var result = ModelProtocolYamlIO.loadStoreFromYamlText(yamlText);
                    if (!TryGetResult(
                            result,
                            err => $"YAML 불러오기 실패:\n\n{err}",
                            out var store))
                        return;

                    PrepareForLoadedStore();
                    _loadedAsLossy = true;
                    ReplaceOpenedStore(fileName, store, "YAML");
                },
                ex => $"YAML 불러오기 실패: {ex.Message}");
        }
        else if (IsMermaid(fileName))
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

        // YAML lossy 재구성 — 시뮬 결과·position·alias 잃음. 사용자에게 "영구 보존은 .sdf SaveAs" 시그널.
        // CompleteOpen→AfterFileLoad 가 IsDirty=false 로 덮어쓰는 race 의 후속 정정.
        if (_loadedAsLossy)
        {
            IsDirty = true;
            _loadedAsLossy = false;
        }

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

        // 시뮬레이션 진행 중이면 — 결과 보기 전에 시뮬레이션을 종료해야 함을 안내.
        if (Simulation.IsSimulating)
        {
            var proceed = Promaker.Dialogs.DialogHelpers.Confirm(
                Application.Current?.MainWindow,
                "시뮬레이션이 실행 중입니다.\n결과 보기 다이얼로그를 열려면 시뮬레이션을 종료해야 합니다.\n\n종료하시겠습니까?",
                "시뮬레이션 종료 확인");
            if (!proceed) return;

            if (Simulation.StopSimulationCommand.CanExecute(null))
                Simulation.StopSimulationCommand.Execute(null);
        }

        // SimulationResult 는 이제 Project 레벨 (이전엔 TechnicalData.SimulationResult).
        // SequenceSimulation 서브모델로 emit 됨.
        var dlg = new Promaker.Dialogs.SimulationScenariosDialog(Simulation, project);
        _dialogService.ShowDialog(dlg);
    }

    /// <summary>
    /// 프로젝트 메타 편집 (이름/작성자/버전/설명). 프로젝트가 열려 있을 때만 활성.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void ShowProjectProperties()
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
        });
        StatusText = "프로젝트 속성이 변경되었습니다.";
    }

    /// <summary>
    /// 환경(앱 전역) 설정 편집 — AASX / PLC / LLM / 프리셋. 프로젝트와 무관, 항상 활성.
    /// </summary>
    [RelayCommand]
    private void ShowApplicationSettings()
    {
        var dlg = new ApplicationSettingsDialog();
        var accepted = _dialogService.ShowDialog(dlg) == true;
        if (!accepted) return;

        // PR-B: LLM 탭 변경 사항이 있으면 LlmChatVm 의 메모리 _config 즉시 reload.
        if (dlg.LlmConfigChanged) LlmChatVm?.ReloadConfig();

        // 앱 설정으로 저장 (Editor mutation 없음 — 환경 설정은 Editor undo stack 무관)
        SetSplitDeviceAasx(dlg.ResultSplitDeviceAasx);
        SetCreateDefaultEntitiesOnEmptyAasx(dlg.ResultCreateDefaultEntities);
        SetIriPrefix(dlg.ResultIriPrefix);
        // PresetSystemTypes / PlcConfig / LlmConfig 는 Dialog 내부에서 이미 파일에 저장됨

        StatusText = "환경 설정이 변경되었습니다.";
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

    /// <summary>
    /// Promaker · DSPilot 공유 경로 (%ProgramData%\DualSoft\Shared\project.aasx) 로 AASX 저장.
    /// DSPilot 서비스가 같은 경로를 읽으므로 별도 업로드/설정 없이 모델이 동기화된다.
    /// 폴더가 없으면 자동 생성 (인스톨러가 보장하지만 클린 환경 대비).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void SaveToSharedLocation()
    {
        try
        {
            Directory.CreateDirectory(SharedPaths.SharedDirectory);
        }
        catch (Exception ex)
        {
            Log.Error($"공유 폴더 생성 실패: {SharedPaths.SharedDirectory}", ex);
            _dialogService.ShowWarning($"공유 폴더를 만들 수 없습니다:\n{SharedPaths.SharedDirectory}\n\n{ex.Message}");
            return;
        }

        var ok = SaveToPath(SharedPaths.AasxFilePath);
        if (ok)
        {
            RecentFilesManager.AddRecentFile(SharedPaths.AasxFilePath);
            _dispatcher.InvokeAsync(LoadRecentFiles);
            StatusText = $"DSPilot 공유 경로에 저장됨: {SharedPaths.AasxFilePath}";
        }
    }

    /// <summary>
    /// Hub 모드(Control/VirtualPlant/Monitoring) 시뮬레이션 시작 직전에 호출되는 자동 publish.
    /// 현재 store 를 DSPilot 공유 AASX 경로로 silent export — 다이얼로그/StatusText 변경 없음.
    /// 호출자(SimulationPanelState)가 실패 로그를 sim event log 로 남긴다.
    /// </summary>
    internal bool TryPublishAasxToSharedForDspilot()
    {
        if (!HasProject)
            return false;

        try
        {
            Directory.CreateDirectory(SharedPaths.SharedDirectory);
        }
        catch (Exception ex)
        {
            Log.Error($"공유 폴더 생성 실패 (auto publish): {SharedPaths.SharedDirectory}", ex);
            return false;
        }

        try
        {
            var exported = AasxExporter.exportFromStore(
                _store, SharedPaths.AasxFilePath, IriPrefix, SplitDeviceAasx, CreateDefaultEntitiesOnEmptyAasx);
            if (exported)
                Log.Info($"AASX auto-published to DSPilot shared path: {SharedPaths.AasxFilePath}");
            else
                Log.Warn($"AASX auto-publish: no project to export ({SharedPaths.AasxFilePath})");
            return exported;
        }
        catch (Exception ex)
        {
            Log.Error($"AASX auto-publish 실패: {SharedPaths.AasxFilePath}", ex);
            return false;
        }
    }

    private bool TrySaveFileAs()
    {
        var projects = Queries.allProjects(_store);
        var suggestedName = _currentFilePath is not null
            ? Path.GetFileNameWithoutExtension(_currentFilePath)
            : (!projects.IsEmpty ? projects.Head.Name : "project");

        // _currentFilePath 가 .yaml 인 상태에서 SaveAs default 가 .sdf 면 사용자 의도 위반 →
        // 현 경로 확장자 기준 동적 선택. 신규 프로젝트는 기존대로 .sdf.
        var defaultExt = _currentFilePath is null
            ? FileExtensions.Sdf
            : Path.GetExtension(_currentFilePath).ToLowerInvariant();

        var dlg = new SaveFileDialog
        {
            Filter = FileFilter,
            DefaultExt = string.IsNullOrEmpty(defaultExt) ? FileExtensions.Sdf : defaultExt,
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

                // 사용자 정의 AASX 템플릿 폴더 — 설정값을 export 직전에 set, 후 reset.
                var userTplFolder = Promaker.Presentation.AppSettingStore.LoadStringOrDefault(
                    Promaker.Services.SettingsPaths.AasxUserTemplatesFolder, "");
                var prevTplFolder = AasxExporter.UserTemplatesFolder;
                AasxExporter.UserTemplatesFolder =
                    string.IsNullOrWhiteSpace(userTplFolder) || !System.IO.Directory.Exists(userTplFolder)
                        ? Microsoft.FSharp.Core.FSharpOption<string>.None
                        : Microsoft.FSharp.Core.FSharpOption<string>.Some(userTplFolder);

                var exported = AasxExporter.exportFromStore(_store, filePath, IriPrefix, SplitDeviceAasx, CreateDefaultEntitiesOnEmptyAasx);

                AasxExporter.UserTemplatesFolder = prevTplFolder;

                // 사용자 폴더 SM 이 ds2 표준 SM 을 override 했는지 확인 → 사용자에게 상세 안내.
                if (exported)
                {
                    var overrides = AasxExporter.LastUserTemplateOverrides;
                    if (overrides != null && overrides.Any())
                    {
                        var lines = string.Join("\n",
                            overrides.Select(t => $"  • {t.Item1}  →  Submodel \"{t.Item2}\""));
                        var msg =
                            $"AASX 사용자 템플릿 폴더의 .aasx 파일이 ds2 기본 표준 Submodel 을 덮어썼습니다.\n\n" +
                            $"{lines}\n\n" +
                            $"⚠ 결과: 위 Submodel(들) 은 사용자 폴더의 .aasx 내용으로 출력되며,\n" +
                            $"     Promaker 의 입력 데이터(예: Nameplate 의 ManufacturerName/SerialNumber 등) 는 \n" +
                            $"     반영되지 않습니다.\n\n" +
                            $"폴더: {userTplFolder}\n" +
                            $"파일: {filePath}\n\n" +
                            $"의도한 동작이 아니라면, 사용자 템플릿 폴더에서 해당 파일을 제거하거나\n" +
                            $"Submodel idShort 를 ds2 표준과 다른 이름으로 변경하세요\n" +
                            $"(예: \"Nameplate\" → \"NameplateCustom\").";
                        Promaker.Dialogs.DialogHelpers.ShowThemedMessageBox(
                            msg, "AASX 사용자 템플릿 override 안내", System.Windows.MessageBoxButton.OK, "ⓘ");
                    }
                }
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

        if (IsYaml(filePath))
        {
            // 대형 store 의 yaml export 는 1-2초 소요 가능 → BusyOverlay.
            // export 실패 시 ShowWarning (modal) 이 BusyOverlay 위에 겹치지 않도록, dialog 노출 직전
            // BusyOverlay 복원. notice dialog (성공 분기) 도 같은 원칙 — overlay 해제 후 표시.
            var prevBusy = IsBusy;
            var prevMsg = BusyMessage;
            BusyMessage = "YAML 저장 중...";
            IsBusy = true;
            try
            {
                var text = ModelProtocolYamlIO.exportStoreToYamlText(_store);
                File.WriteAllText(filePath, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                BusyMessage = prevMsg;
                IsBusy = prevBusy;
                Log.Error($"Save YAML '{filePath}' failed", ex);
                _dialogService.ShowWarning($"YAML 저장 실패: {ex.Message}");
                return false;
            }
            BusyMessage = prevMsg;
            IsBusy = prevBusy;
            if (!ShouldSuppressYamlSaveNotice())
                ShowYamlSaveNoticeOnce();
            CompleteSave(filePath, "YAML");
            return true;
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

    private static bool ShouldSuppressYamlSaveNotice() =>
        AppSettingStore.LoadBoolOrDefault(SettingsPaths.YamlSaveNoticeShown, defaultValue: false);

    /// <summary>
    /// 최초 Save(.yaml) 1회 lossy 안내 dialog. "다시 보지 않기" 체크 시 AppSettingStore 에 영구 persistence.
    /// 메시지 톤: 위협 아닌 가치-기반 (AASX 안내 dialog 패턴).
    /// lossy 4-set = GUID / position / alias / 시뮬 결과 — SSOT / dialog / title bar 모두 sync.
    /// </summary>
    private static void ShowYamlSaveNoticeOnce()
    {
        const string Message =
            ".yaml 저장은 모델의 선언적 표현만 보존합니다.\n" +
            "  • GUID · 위치 · alias · 시뮬 결과는 저장되지 않으며, 다시 열 때 자동 재발행됩니다.\n\n" +
            "영구 보존 / 시뮬 결과 공유 / 위치 보존이 필요하면 .sdf 를 사용하세요.\n" +
            "YAML 은 사람이 읽고 LLM 이 다루기 좋은 공유 포맷입니다.";

        DialogHelpers.ShowThemedMessageBox(
            Application.Current?.MainWindow,
            Message,
            "YAML 저장 안내",
            MessageBoxButton.OK,
            DialogHelpers.IconInfo,
            showDontShowAgain: true,
            out var dontShowAgain,
            dontShowAgainLabel: "다시 보지 않기 (이 PC 영구)");

        if (dontShowAgain)
            AppSettingStore.SaveBool(SettingsPaths.YamlSaveNoticeShown, true);
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
