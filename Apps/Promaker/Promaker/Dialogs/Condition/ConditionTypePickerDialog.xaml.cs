using System.Windows;
using Ds2.Core;

namespace Promaker.Dialogs;

public partial class ConditionTypePickerDialog : Window
{
    public ConditionTypePickerDialog()
    {
        InitializeComponent();
    }

    public ConditionType SelectedConditionType =>
        ComAuxRadio.IsChecked == true ? ConditionType.ComAux
        : SkipUnmatchRadio.IsChecked == true ? ConditionType.SkipUnmatch
        : ConditionType.AutoAux;

    private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
