using System.Windows;
using System.Windows.Controls;
using Promaker.Dialogs;

namespace Promaker.Controls;

public partial class MainToolbarEtcContent : UserControl
{
    public MainToolbarEtcContent()
    {
        InitializeComponent();
    }

    private void CloseUtilPopup(object sender, RoutedEventArgs e) => UtilMenuToggle.IsChecked = false;

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog();
        if (Application.Current.MainWindow is { } owner)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        dialog.ShowDialog();
    }
}
