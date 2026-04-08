using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class HistoryPanel : UserControl
{
    public HistoryPanel()
    {
        InitializeComponent();
        HistoryListBox.SelectionChanged += OnSelectionChanged;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                HistoryListBox.ScrollIntoView(HistoryListBox.SelectedItem));
    }

    private void HistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: HistoryPanelItem item }
            && DataContext is MainViewModel vm)
            vm.JumpToHistoryCommand.Execute(item);
    }
}
