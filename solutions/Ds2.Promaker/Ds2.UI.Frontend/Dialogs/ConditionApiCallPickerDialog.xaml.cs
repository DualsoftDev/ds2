using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Ds2.UI.Frontend.Dialogs;

public partial class ConditionApiCallPickerDialog : Window
{
    public record ApiCallChoice(Guid Id, string DisplayName);

    public ConditionApiCallPickerDialog(IReadOnlyList<ApiCallChoice> choices)
    {
        InitializeComponent();
        PickerListBox.ItemsSource = choices;
        PickerListBox.DisplayMemberPath = nameof(ApiCallChoice.DisplayName);
    }

    public IReadOnlyList<Guid> SelectedApiCallIds { get; private set; } = [];

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        var selected = PickerListBox.SelectedItems
            .Cast<ApiCallChoice>()
            .Select(x => x.Id)
            .ToList();

        if (selected.Count == 0)
            return;

        SelectedApiCallIds = selected;
        DialogResult = true;
    }
}
