using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs;

/// <summary>
/// TagWizard Step 3 — 신호 생성 + 적용 (IO/Dummy 미리보기, 매칭 실패, 오류 탭).
/// </summary>
public partial class TagWizardStep3Section : UserControl
{
    public TagWizardStep3Section() => InitializeComponent();

    private TagWizardDialog? Host => Window.GetWindow(this) as TagWizardDialog;

    private void ApplyPatterns_Click(object s, RoutedEventArgs e) => Host?.ApplyPatterns_Click(s, e);
}
