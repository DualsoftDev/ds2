using System.Windows;

namespace Promaker.Dialogs;

/// <summary>TAG Wizard 모드 선택 다이얼로그 — 기본 / 고급 라우팅.</summary>
public partial class TagWizardModeDialog : Window
{
    public enum WizardMode { Basic, Advanced }

    /// <summary>사용자가 "다음" 을 눌렀을 때 선택된 모드. DialogResult=true 일 때만 유효.</summary>
    public WizardMode SelectedMode { get; private set; } = WizardMode.Basic;

    public TagWizardModeDialog()
    {
        InitializeComponent();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = AdvancedRadio.IsChecked == true ? WizardMode.Advanced : WizardMode.Basic;
        DialogResult = true;
        Close();
    }
}
