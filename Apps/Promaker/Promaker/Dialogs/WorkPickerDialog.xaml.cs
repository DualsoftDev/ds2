using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace Promaker.Dialogs;

public partial class WorkPickerDialog : Window
{
    private readonly IReadOnlyList<WorkOption> _allWorks;
    private readonly ObservableCollection<WorkOption> _filtered = [];

    public WorkPickerDialog(IReadOnlyList<WorkOption> works)
    {
        InitializeComponent();
        _allWorks = works;
        WorkList.ItemsSource = _filtered;
        ApplyFilter();
    }

    public WorkOption? SelectedWork { get; private set; }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var sourceOnly = SourceOnlyCheckBox.IsChecked == true;

        _filtered.Clear();
        foreach (var work in _allWorks)
        {
            if (sourceOnly && !work.IsSource) continue;
            if (query.Length > 0
                && work.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            _filtered.Add(work);
        }

        CountRun.Text = _filtered.Count.ToString();
        OkButton.IsEnabled = WorkList.SelectedItem is WorkOption;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyFilter();

    private void SourceOnly_Changed(object sender, RoutedEventArgs e) =>
        ApplyFilter();

    private void WorkList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OkButton.IsEnabled = WorkList.SelectedItem is WorkOption;

    private void WorkList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (WorkList.SelectedItem is WorkOption)
            OK_Click(sender, new RoutedEventArgs());
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (WorkList.SelectedItem is not WorkOption work) return;
        SelectedWork = work;
        DialogResult = true;
    }
}
