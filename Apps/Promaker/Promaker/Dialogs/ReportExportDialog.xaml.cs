using System.Windows;
using ReportExportFormat = Ds2.Runtime.Report.Model.ExportFormat;

namespace Promaker.Dialogs;

public partial class ReportExportDialog : Window
{
    public ReportExportFormat SelectedFormat { get; private set; } = ReportExportFormat.Csv;
    public bool OpenAfter { get; private set; } = true;

    public ReportExportDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (RadioExcel.IsChecked == true) SelectedFormat = ReportExportFormat.Excel;
        else if (RadioHtml.IsChecked == true) SelectedFormat = ReportExportFormat.Html;
        else SelectedFormat = ReportExportFormat.Csv;

        OpenAfter = OpenAfterExport.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
