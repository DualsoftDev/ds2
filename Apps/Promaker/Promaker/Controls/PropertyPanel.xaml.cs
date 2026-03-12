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

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public void FocusNameEditorControl()
    {
        NameEditor.Focus();
        NameEditor.SelectAll();
    }

    private void ApplyName_Click(object sender, System.Windows.RoutedEventArgs e) => ApplyName();

    private void NameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyName();
        e.Handled = true;
    }

    private void ApplyName()
    {
        if (ViewModel?.SelectedNode is null) return;

        var newName = ViewModel.NameEditorText.Trim();
        if (!string.IsNullOrEmpty(newName))
            ViewModel.RenameSelectedCommand.Execute(newName);
    }
}
