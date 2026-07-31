using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
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
        "All Supported (*.sdf;*.json;*.aasx;*.md;*.mmd;*.yaml;*.yml)|*.sdf;*.json;*.aasx;*.md;*.mmd;*.yaml;*.yml|SDF Files (*.sdf)|*.sdf|JSON Files (*.json)|*.json|AASX Files (*.aasx)|*.aasx|Mermaid Files (*.md;*.mmd)|*.md;*.mmd|YAML Files — lossy 공유 포맷 (*.yaml;*.yml)|*.yaml;*.yml";

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
    private bool CanShowSimulationScenarios() => HasProject && Simulation.Report.HasReportData;

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
    /// 환경(앱 전역) 설정 편집 — AASX / PLC / 프리셋. 프로젝트와 무관, 항상 활성.
    /// </summary>
    [RelayCommand]
    private void ShowApplicationSettings()
    {
        var dlg = new ApplicationSettingsDialog(ApplicationSettingsDialog.SettingsTab.General);
        if (_dialogService.ShowDialog(dlg) != true) return;

        AppSettings.SetSplitDeviceAasx(dlg.ResultSplitDeviceAasx);
        AppSettings.SetCreateDefaultEntitiesOnEmptyAasx(dlg.ResultCreateDefaultEntities);
        AppSettings.SetIriPrefix(dlg.ResultIriPrefix);
        AppSettings.SetEmbedPlcConnectionInAasx(dlg.ResultEmbedPlcConnection);

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
