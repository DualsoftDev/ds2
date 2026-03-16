using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Promaker.Dialogs;

public partial class CsvExportDialog : Window
{
    public CsvExportDialog(string projectName, string previewText, string defaultFileName)
    {
        InitializeComponent();

        ProjectText.Text = $"Project: {projectName}";
        PreviewText.Text = previewText;
        PathBox.Text = defaultFileName;

        Loaded += (_, _) =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        };
    }

    public string OutputPath => PathBox.Text.Trim();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName = string.IsNullOrWhiteSpace(OutputPath) ? "project.csv" : Path.GetFileName(OutputPath)
        };

        var dir = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            picker.InitialDirectory = dir;

        if (picker.ShowDialog() == true)
            PathBox.Text = picker.FileName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            MessageBox.Show(this, "내보내기 경로를 입력하세요.", "CSV 내보내기", MessageBoxButton.OK, MessageBoxImage.Information);
            PathBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
