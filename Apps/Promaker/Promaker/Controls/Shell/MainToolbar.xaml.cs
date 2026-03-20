using System.Windows;
using System.Windows.Controls;

namespace Promaker.Controls;

public partial class MainToolbar : UserControl
{
    public MainToolbar()
    {
        InitializeComponent();
    }

    // Save 팝업 내 메뉴 클릭 시 팝업 닫기
    private void CloseSavePopup(object sender, RoutedEventArgs e)
    {
        SaveMenuToggle.IsChecked = false;
    }

    private void CloseToolPopup(object sender, RoutedEventArgs e)
    {
        ToolToggleBtn.IsChecked = false;
    }

    // Report 팝업 내 메뉴 클릭 시 팝업 닫기
    private void CloseReportPopup(object sender, RoutedEventArgs e)
    {
        ReportToggleBtn.IsChecked = false;
    }


    // Model 팝업 내 메뉴 클릭 시 팝업 닫기
    private void CloseModelPopup(object sender, RoutedEventArgs e)
    {
        ModelToggleBtn.IsChecked = false;
    }
}
