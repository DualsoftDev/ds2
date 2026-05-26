using System.Windows;
using System.Windows.Controls;
using Promaker.Presentation;
using Promaker.ViewModels.Logging;

namespace Promaker.Controls.Logging;

/// <summary>
/// 앱 전역 log4net 출력 view. DataContext 는 ctor 에서 AppLogState.Instance 로 직접 set
/// — 호출처가 DataContext 를 따로 주입할 필요 없음 (singleton).
/// 이전 (`SimulationPanel.xaml` 의 Log tab) 에서 별도 독립 dock pane (logAnchor) 으로 분리됨.
/// </summary>
public partial class AppLogView : UserControl
{
    public AppLogView()
    {
        InitializeComponent();
        DataContext = AppLogState.Instance;
    }

    private void AppLogCopyAll_Click(object sender, RoutedEventArgs e)
        => ClipboardUtil.Copy(AppLogListBox.Items);

    private void AppLogCopySelected_Click(object sender, RoutedEventArgs e)
        => ClipboardUtil.Copy(AppLogListBox.SelectedItems.Count > 0
            ? AppLogListBox.SelectedItems
            : AppLogListBox.Items);

    private void AppLogClear_Click(object sender, RoutedEventArgs e)
        => AppLogState.Instance.Clear();
}
