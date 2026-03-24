using System.Windows;
using System.Windows.Controls;

namespace Promaker.Controls;

public partial class MainToolbarToolsContent : UserControl
{
    public MainToolbarToolsContent()
    {
        InitializeComponent();
    }

    private void CloseDataPopup(object sender, RoutedEventArgs e)
    {
        DataToggleBtn.IsChecked = false;
    }
}
