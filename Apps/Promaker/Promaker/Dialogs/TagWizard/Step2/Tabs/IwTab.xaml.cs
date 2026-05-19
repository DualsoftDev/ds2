using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs;

public partial class TagWizardIwTab : UserControl
{
    public TagWizardIwTab() => InitializeComponent();

    private TagWizardDialog? Host => Window.GetWindow(this) as TagWizardDialog;

    private void AddIwRow_Click(object s, RoutedEventArgs e) => Host?.AddIwRow_Click(s, e);
    private void RemoveIwRow_Click(object s, RoutedEventArgs e) => Host?.RemoveIwRow_Click(s, e);
    private void MoveIwUp_Click(object s, RoutedEventArgs e) => Host?.MoveIwUp_Click(s, e);
    private void MoveIwDown_Click(object s, RoutedEventArgs e) => Host?.MoveIwDown_Click(s, e);
    private void ChunkedToggle_Changed(object s, RoutedEventArgs e) => Host?.ChunkedToggle_Changed(s, e);
    private void EditPreFbCondition_Click(object s, RoutedEventArgs e) => Host?.EditPreFbCondition_Click(s, e);
}
