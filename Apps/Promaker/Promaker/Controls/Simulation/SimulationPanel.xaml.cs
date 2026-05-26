using System.Windows;
using System.Windows.Controls;
using Promaker.Presentation;

namespace Promaker.Controls;

public partial class SimulationPanel : UserControl
{
    public SimulationPanel()
    {
        InitializeComponent();
    }

    private void EventLogCopyAll_Click(object sender, RoutedEventArgs e)
    {
        ClipboardUtil.Copy(EventLogListBox.Items);
    }

    private void EventLogCopySelected_Click(object sender, RoutedEventArgs e)
    {
        ClipboardUtil.Copy(EventLogListBox.SelectedItems.Count > 0
            ? EventLogListBox.SelectedItems
            : EventLogListBox.Items);
    }

    private void EventLogClear_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SimulationPanelState vm)
            vm.SimEventLog.Clear();
    }
}
