using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs;

public partial class TagWizardMwTab : UserControl
{
    public TagWizardMwTab() => InitializeComponent();

    private TagWizardDialog? Host => Window.GetWindow(this) as TagWizardDialog;

    private void AddMwRow_Click(object s, RoutedEventArgs e) => Host?.AddMwRow_Click(s, e);
    private void RemoveMwRow_Click(object s, RoutedEventArgs e) => Host?.RemoveMwRow_Click(s, e);
    private void MoveMwUp_Click(object s, RoutedEventArgs e) => Host?.MoveMwUp_Click(s, e);
    private void MoveMwDown_Click(object s, RoutedEventArgs e) => Host?.MoveMwDown_Click(s, e);
    private void ChunkedToggle_Changed(object s, RoutedEventArgs e) => Host?.ChunkedToggle_Changed(s, e);
    private void EditPreFbCondition_Click(object s, RoutedEventArgs e) => Host?.EditPreFbCondition_Click(s, e);
}
