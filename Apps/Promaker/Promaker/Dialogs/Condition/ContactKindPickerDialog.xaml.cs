using System.Windows;
using Ds2.Core;

namespace Promaker.Dialogs;

/// <summary>
/// SkipAction 조건에 Call 드래그&드롭 시 접점 종류(A접/B접) 선택 dialog.
/// Default = B접(NcContact, Not).
/// </summary>
public partial class ContactKindPickerDialog : Window
{
    public ContactKindPickerDialog()
    {
        InitializeComponent();
    }

    public ContactKind SelectedContactKind =>
        NoContactRadio.IsChecked == true
            ? ContactKind.NoContact
            : ContactKind.NcContact;

    private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
