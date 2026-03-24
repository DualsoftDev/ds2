using System.Windows;
using System.Windows.Controls;

namespace Promaker.Controls;

public partial class MainToolbar : UserControl
{
    public MainToolbar()
    {
        InitializeComponent();
    }

    // Save popup menu click closes the popup
    private void CloseSavePopup(object sender, RoutedEventArgs e)
    {
        SaveMenuToggle.IsChecked = false;
    }

    private void CloseEditPopup(object sender, RoutedEventArgs e)
    {
        EditMenuToggle.IsChecked = false;
    }

    private void CloseModelPopup(object sender, RoutedEventArgs e)
    {
        ModelMenuToggle.IsChecked = false;
    }

    private void CloseDataPopup(object sender, RoutedEventArgs e)
    {
        DataMenuToggle.IsChecked = false;
    }
}
