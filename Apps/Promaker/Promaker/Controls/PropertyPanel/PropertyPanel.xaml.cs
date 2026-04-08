using System.Windows.Controls;
using System.Windows.Input;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class PropertyPanel : UserControl
{
    public PropertyPanel()
    {
        InitializeComponent();
    }

    private PropertyPanelState? ViewModel => DataContext as PropertyPanelState;

    public void FocusNameEditorControl()
    {
        NameEditor.Focus();
        NameEditor.SelectAll();
    }

    private void NameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel?.CancelNameEdit();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        ApplyName();
        e.Handled = true;
    }

    private void ApplyName()
    {
        if (ViewModel?.ApplyNameCommand.CanExecute(null) != true) return;
        ViewModel.ApplyNameCommand.Execute(null);
    }
}
