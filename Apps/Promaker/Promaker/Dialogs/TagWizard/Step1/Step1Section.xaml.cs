using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs;

/// <summary>
/// TagWizard Step 1 — 프로젝트 확인 + 선두 주소 설정.
/// Click handler 는 ancestor <see cref="TagWizardDialog"/> 로 routing (실 logic 은 dialog partial files
/// FlowBase.cs / SystemBase.cs / StepNavigation.cs 에 그대로).
/// </summary>
public partial class TagWizardStep1Section : UserControl
{
    public TagWizardStep1Section()
    {
        InitializeComponent();
    }

    private TagWizardDialog? Host => Window.GetWindow(this) as TagWizardDialog;

    private void SaveFlowBase_Click(object sender, RoutedEventArgs e) => Host?.SaveFlowBase_Click(sender, e);
    private void SaveSystemBase_Click(object sender, RoutedEventArgs e) => Host?.SaveSystemBase_Click(sender, e);
    private void AddSystemBaseRow_Click(object sender, RoutedEventArgs e) => Host?.AddSystemBaseRow_Click(sender, e);
    private void RemoveSystemBaseRow_Click(object sender, RoutedEventArgs e) => Host?.RemoveSystemBaseRow_Click(sender, e);
}
