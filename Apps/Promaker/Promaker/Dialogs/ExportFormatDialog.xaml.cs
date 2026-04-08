using System.Windows;
using Microsoft.Win32;

namespace Promaker.Dialogs;

public enum ExportFormat
{
    Csv,
    Excel
}

public partial class ExportFormatDialog : Window
{
    public ExportFormat SelectedFormat { get; private set; }
    public string? TemplatePath { get; private set; }
    public bool UseTemplate { get; private set; }

    public ExportFormatDialog()
    {
        InitializeComponent();
        SelectedFormat = ExportFormat.Excel;  // 기본값: Excel

        // TODO: 템플릿 기능 비활성화 - 나중에 사용자 포맷 파싱 구현 후 활성화
        // 사용자 Excel 포맷을 상세하게 읽어서 정확한 위치에 출력하는 기능 필요
        // CsvRadio.Checked += (_, _) => UpdateTemplateVisibility();
        // ExcelRadio.Checked += (_, _) => UpdateTemplateVisibility();
    }

    private void UpdateTemplateVisibility()
    {
        // TODO: 템플릿 기능 - 현재 비활성화됨
        // 제대로 구현하려면:
        // 1. 사용자 Excel 템플릿의 구조 분석
        // 2. 헤더 위치, 데이터 시작 행 파악
        // 3. 기존 서식 유지하면서 데이터만 삽입
        // 4. 수식, 차트 등 유지
        TemplateOptionsPanel.Visibility = Visibility.Collapsed;

        // 나중에 구현 시 활성화:
        // TemplateOptionsPanel.Visibility = ExcelRadio.IsChecked == true
        //     ? Visibility.Visible
        //     : Visibility.Collapsed;
    }

    private void UseTemplateCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var isChecked = UseTemplateCheckBox.IsChecked == true;
        TemplatePathBox.IsEnabled = isChecked;
        BrowseTemplateButton.IsEnabled = isChecked;
    }

    private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new OpenFileDialog
        {
            Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
            Title = "Excel 템플릿 선택"
        };

        if (openDialog.ShowDialog() == true)
        {
            TemplatePathBox.Text = openDialog.FileName;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (CsvRadio.IsChecked == true)
        {
            SelectedFormat = ExportFormat.Csv;
            UseTemplate = false;
            TemplatePath = null;
        }
        else if (ExcelRadio.IsChecked == true)
        {
            SelectedFormat = ExportFormat.Excel;
            UseTemplate = UseTemplateCheckBox.IsChecked == true;
            TemplatePath = UseTemplate ? TemplatePathBox.Text : null;
        }

        DialogResult = true;
    }
}
