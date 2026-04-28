using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace Promaker.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"버전 {version}";
        CopyrightText.Text = $"\u00a9 {DateTime.Now.Year} Dual Inc";
    }

    private void Url_Click(object sender, MouseButtonEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://dualsoft.com") { UseShellExecute = true });
    }
}
