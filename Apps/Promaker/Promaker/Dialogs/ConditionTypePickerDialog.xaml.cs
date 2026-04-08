using System.Windows;
using Ds2.Core;

namespace Promaker.Dialogs;

public partial class ConditionTypePickerDialog : Window
{
    public ConditionTypePickerDialog()
    {
        InitializeComponent();
    }

    public CallConditionType SelectedConditionType =>
        ComAuxRadio.IsChecked == true ? CallConditionType.ComAux
        : SkipUnmatchRadio.IsChecked == true ? CallConditionType.SkipUnmatch
        : CallConditionType.AutoAux;

    private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
