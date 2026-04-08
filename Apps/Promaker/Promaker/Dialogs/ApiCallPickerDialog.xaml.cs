using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Promaker.Dialogs;

public partial class ApiCallPickerDialog : Window
{
    private readonly List<Choice> _choices;

    public ApiCallPickerDialog(IReadOnlyList<Choice> choices)
    {
        InitializeComponent();
        _choices = choices.ToList();
        foreach (var c in _choices) c.IsSelected = true;
        ItemsHost.ItemsSource = _choices;
        UpdateOkEnabled();
    }

    public IReadOnlyList<Guid> SelectedApiCallIds =>
        _choices.Where(c => c.IsSelected).Select(c => c.ApiCallId).ToList();

    private void CheckBox_Changed(object sender, RoutedEventArgs e) => UpdateOkEnabled();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var allSelected = _choices.All(c => c.IsSelected);
        foreach (var c in _choices) c.IsSelected = !allSelected;
        ItemsHost.ItemsSource = null;
        ItemsHost.ItemsSource = _choices;
        UpdateOkEnabled();
    }

    private void UpdateOkEnabled() => OkButton.IsEnabled = _choices.Any(c => c.IsSelected);

    private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public sealed class Choice(Guid apiCallId, string displayName)
    {
        public Guid ApiCallId { get; } = apiCallId;
        public string DisplayName { get; } = displayName;
        public bool IsSelected { get; set; }
    }
}
