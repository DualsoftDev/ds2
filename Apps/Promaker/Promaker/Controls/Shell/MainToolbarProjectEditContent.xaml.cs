using System.Windows;
using System.Windows.Controls;

namespace Promaker.Controls;

public partial class MainToolbarProjectEditContent : UserControl
{
    public MainToolbarProjectEditContent()
    {
        InitializeComponent();
    }

    private void CloseSavePopup(object sender, RoutedEventArgs e)
    {
        SaveMenuToggle.IsChecked = false;
    }

    private void CloseAddPopup(object sender, RoutedEventArgs e)
    {
        AddToggleBtn.IsChecked = false;
    }

    private void CloseModelPopup(object sender, RoutedEventArgs e)
    {
        ModelToggleBtn.IsChecked = false;
    }
}
