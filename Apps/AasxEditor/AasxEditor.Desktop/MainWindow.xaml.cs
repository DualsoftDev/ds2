using System.Windows;

namespace AasxEditor.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        blazorWebView.Services = App.ServiceProvider;
    }
}
