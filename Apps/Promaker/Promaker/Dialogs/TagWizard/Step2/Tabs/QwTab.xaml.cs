using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs;

public partial class TagWizardQwTab : UserControl
{
    public TagWizardQwTab() => InitializeComponent();

    private TagWizardDialog? Host => Window.GetWindow(this) as TagWizardDialog;

    private void AddQwRow_Click(object s, RoutedEventArgs e) => Host?.AddQwRow_Click(s, e);
    private void RemoveQwRow_Click(object s, RoutedEventArgs e) => Host?.RemoveQwRow_Click(s, e);
    private void MoveQwUp_Click(object s, RoutedEventArgs e) => Host?.MoveQwUp_Click(s, e);
    private void MoveQwDown_Click(object s, RoutedEventArgs e) => Host?.MoveQwDown_Click(s, e);
    private void ChunkedToggle_Changed(object s, RoutedEventArgs e) => Host?.ChunkedToggle_Changed(s, e);
    private void EditPreFbCondition_Click(object s, RoutedEventArgs e) => Host?.EditPreFbCondition_Click(s, e);
}
