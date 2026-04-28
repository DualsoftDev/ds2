using CommunityToolkit.Mvvm.Input;
using Promaker.Dialogs;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// I/O 조회 다이얼로그 — 다이얼로그가 IoQueryService 로 자체 생성/새로고침 한다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenIoBatchDialog()
    {
        _dialogService.ShowDialog(new IoBatchSettingsDialog(_store));
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
