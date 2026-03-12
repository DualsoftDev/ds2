using System.Windows;
using System.Windows.Controls;

namespace Promaker.Controls;

public partial class MainToolbar : UserControl
{
    public MainToolbar()
    {
        InitializeComponent();
    }

    // Add 팝업 내 메뉴 클릭 시 팝업 닫기
    private void CloseAddPopup(object sender, RoutedEventArgs e)
    {
        AddToggleBtn.IsChecked = false;
    }
}
