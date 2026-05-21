using System.Windows;

namespace Promaker.Dialogs;

/// <summary>"파일(PLC, CSV)로부터 모델만들기" 진입점 launcher.
/// 사용자가 PLC 심볼 / 일반 CSV 중 하나 선택 → 호출자가 적절한 다이얼로그로 분기.</summary>
public partial class FileImportLauncherDialog : Window
{
    public enum ImportMode { PlcSymbol, GenericCsv }

    public ImportMode SelectedMode { get; private set; } = ImportMode.PlcSymbol;

    public FileImportLauncherDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = PlcRadio.IsChecked == true
            ? ImportMode.PlcSymbol
            : ImportMode.GenericCsv;
        DialogResult = true;
    }
}
