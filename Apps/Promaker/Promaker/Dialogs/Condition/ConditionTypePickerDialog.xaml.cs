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
        : SkipActionRadio.IsChecked == true ? ConditionType.SkipAction
        : ConditionType.AutoAux;

    /// <summary>SkipAction 일 때만 의미 있는 접점 종류. 그 외 유형이면 null.</summary>
    public ContactKind? SelectedContactKind =>
        SkipActionRadio.IsChecked == true
            ? (ContactKindCombo.SelectedIndex == 1 ? ContactKind.NoContact : ContactKind.NcContact)
            : null;

    private void ConditionType_Changed(object sender, RoutedEventArgs e)
    {
        if (ContactKindPanel is not null)
            ContactKindPanel.IsEnabled = SkipActionRadio.IsChecked == true;
    }

    private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
