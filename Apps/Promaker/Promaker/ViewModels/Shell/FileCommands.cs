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
    private void ShowApplicationSettings() => OpenApplicationSettings(ApplicationSettingsDialog.SettingsTab.Aasx);

    /// <summary>
    /// LLM Chat 패널 헤더의 ⚙ 버튼에서 호출. 같은 ApplicationSettingsDialog 를 LLM 탭으로 바로 열기.
    /// (AS-2: 진입점 통일 + 별도 mini dialog 신설 회피)
    /// </summary>
    [RelayCommand]
    private void ShowLlmSettings() => OpenApplicationSettings(ApplicationSettingsDialog.SettingsTab.Llm);

    private void OpenApplicationSettings(ApplicationSettingsDialog.SettingsTab initialTab)
    {
        var dlg = new ApplicationSettingsDialog(initialTab);
        var accepted = _dialogService.ShowDialog(dlg) == true;

        // UserPromptsTouched: 디스크 *.md 변경 가능성은 OK/Cancel 무관 — 사용자가 폴더에서 이미 편집했을 수 있음.
        // RefreshPrompts() 는 Codex instructions.md 재기록만 (다른 provider 는 Phase1c 가 매 호출 LoadComposed 라 자동 반영).
        if (dlg.UserPromptsTouched) LlmChatVm?.RefreshPrompts();

        if (!accepted) return;

        // PR-B: LLM 탭 변경 사항이 있으면 LlmChatVm 의 메모리 _config 즉시 reload (provider 재구성 포함).
        if (dlg.LlmConfigChanged) LlmChatVm?.ReloadConfig();

        // 앱 설정으로 저장 (Editor mutation 없음 — 환경 설정은 Editor undo stack 무관)
        SetSplitDeviceAasx(dlg.ResultSplitDeviceAasx);
        SetCreateDefaultEntitiesOnEmptyAasx(dlg.ResultCreateDefaultEntities);
        SetIriPrefix(dlg.ResultIriPrefix);
        // PresetSystemTypes / PlcConfig / LlmConfig 는 Dialog 내부에서 이미 파일에 저장됨

        StatusText = "환경 설정이 변경되었습니다.";
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
