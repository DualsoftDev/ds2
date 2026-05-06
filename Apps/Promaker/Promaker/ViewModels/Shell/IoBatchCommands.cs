using CommunityToolkit.Mvvm.Input;
using Promaker.Dialogs;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// I/O 조회 다이얼로그 — 다이얼로그가 IoQueryService 로 자체 생성/새로고침 한다.
    /// 진단 카드의 "FBTagMap 편집" 버튼은 시뮬레이션 가드 통과 후 TAG Wizard 를
    /// 해당 SystemType 으로 바로 열어준다 (편집 후 IO 조회는 자동 새로고침).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenIoBatchDialog()
    {
        _dialogService.ShowDialog(new IoBatchSettingsDialog(_store, OpenFBTagMapEditFor));
    }

    /// <summary>I/O 조회 → "FBTagMap 편집" 버튼 콜백.</summary>
    private void OpenFBTagMapEditFor(string? systemType)
    {
        if (!GuardSimulationSemanticEdit("FBTagMap 편집"))
            return;

        var wizard = new TagWizardDialog(_store);
        wizard.OpenAtFBTagMapForSystemType(systemType);
        _dialogService.ShowDialog(wizard);
    }

    /// <summary>
    /// I/O 생성 — 모드 선택 후 기본/고급 Wizard 로 라우팅.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenTagWizard()
    {
        if (!GuardSimulationSemanticEdit("IO 생성 (TAG Wizard)"))
            return;

        var picker = new TagWizardModeDialog();
        if (_dialogService.ShowDialog(picker) != true)
            return;

        switch (picker.SelectedMode)
        {
            case TagWizardModeDialog.WizardMode.Advanced:
                _dialogService.ShowDialog(new TagWizardDialog(_store));
                break;
            case TagWizardModeDialog.WizardMode.Basic:
            default:
                _dialogService.ShowDialog(new TagWizardBasicDialog(_store));
                break;
        }
    }
}
