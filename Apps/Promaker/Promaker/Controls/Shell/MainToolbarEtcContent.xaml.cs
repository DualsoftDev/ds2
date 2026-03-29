using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Promaker.Dialogs;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class MainToolbarEtcContent : UserControl
{
    public MainToolbarEtcContent()
    {
        InitializeComponent();
    }

    private void CloseUtilPopup(object sender, RoutedEventArgs e) => UtilMenuToggle.IsChecked = false;

    private void UtilMenuToggle_Checked(object sender, RoutedEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && DataContext is MainViewModel vm)
            vm.Is3DViewEnabled = true;
    }

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
