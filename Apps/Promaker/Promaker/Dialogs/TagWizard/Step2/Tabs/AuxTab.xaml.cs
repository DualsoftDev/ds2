using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Promaker.Dialogs;

public partial class TagWizardAuxTab : UserControl
{
    public TagWizardAuxTab() => InitializeComponent();

    private TagWizardDialog? Host => Window.GetWindow(this) as TagWizardDialog;

    private void AddAuxPortRow_Click(object s, RoutedEventArgs e) => Host?.AddAuxPortRow_Click(s, e);
    private void RemoveAuxPortRow_Click(object s, RoutedEventArgs e) => Host?.RemoveAuxPortRow_Click(s, e);
    private void MoveAuxRowUp_Click(object s, RoutedEventArgs e) => Host?.MoveAuxRowUp_Click(s, e);
    private void MoveAuxRowDown_Click(object s, RoutedEventArgs e) => Host?.MoveAuxRowDown_Click(s, e);
    private void EditAuxPortCondition_Click(object s, RoutedEventArgs e) => Host?.EditAuxPortCondition_Click(s, e);
    private void AuxRowSelector_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
        => Host?.AuxRowSelector_PreviewMouseLeftButtonDown(s, e);
    private void AuxFilter_Changed(object s, RoutedEventArgs e) => Host?.AuxFilter_Changed(s, e);
}
