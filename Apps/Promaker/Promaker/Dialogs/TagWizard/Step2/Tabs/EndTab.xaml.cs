using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs;

public partial class TagWizardEndTab : UserControl
{
    public TagWizardEndTab() => InitializeComponent();

    private TagWizardDialog? Host => Window.GetWindow(this) as TagWizardDialog;

    private void AddEndPortRow_Click(object s, RoutedEventArgs e) => Host?.AddEndPortRow_Click(s, e);
    private void RemoveEndPortRow_Click(object s, RoutedEventArgs e) => Host?.RemoveEndPortRow_Click(s, e);
}
