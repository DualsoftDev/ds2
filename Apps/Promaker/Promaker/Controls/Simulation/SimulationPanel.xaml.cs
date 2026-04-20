using System.Collections;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Promaker.Controls;

public partial class SimulationPanel : UserControl
{
    public SimulationPanel()
    {
        InitializeComponent();
    }

    private void EventLogCopyAll_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(EventLogListBox.Items);
    }

    private void EventLogCopySelected_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(EventLogListBox.SelectedItems.Count > 0
            ? EventLogListBox.SelectedItems
            : EventLogListBox.Items);
    }

    private void EventLogClear_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SimulationPanelState vm)
            vm.SimEventLog.Clear();
    }

    private static void CopyToClipboard(IList items)
    {
        if (items.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var item in items)
            sb.AppendLine(item?.ToString() ?? "");
        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch
        {
            // 클립보드 접근 실패 시 무시
        }
    }
}
