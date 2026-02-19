using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Ds2.UI.Frontend.Dialogs;

public partial class ApiCallCreateDialog : Window
{
    public sealed class ApiDefChoice(Guid id, string displayName)
    {
        public Guid Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public override string ToString() => DisplayName;
    }

    public ApiCallCreateDialog(IReadOnlyList<ApiDefChoice> apiDefs)
    {
        InitializeComponent();

        var list = apiDefs?.ToList() ?? [];
        LinkedApiDefComboBox.ItemsSource = list;
        LinkedApiDefComboBox.DisplayMemberPath = nameof(ApiDefChoice.DisplayName);
        LinkedApiDefComboBox.SelectedIndex = list.Count > 0 ? 0 : -1;

        var canCreate = list.Count > 0;
        AddButton.IsEnabled = canCreate;
        HintText.Text = canCreate
            ? "This ApiCall will be linked to the selected device ApiDef above."
            : "No Device ApiDef found. Add ApiDef in Device System first.";
    }

    public Guid? SelectedApiDefId =>
        LinkedApiDefComboBox.SelectedItem is ApiDefChoice choice ? choice.Id : null;

    public string ApiCallName     => ApiCallNameTextBox.Text.Trim();
    public string OutputAddress   => OutputAddressTextBox.Text.Trim();
    public string InputAddress    => InputAddressTextBox.Text.Trim();
    public string ValueSpecText   => OutValueSpecTextBox.Text.Trim();
    public string InValueSpecText => InValueSpecTextBox.Text.Trim();

    private void EditOutValueSpec_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ValueSpecDialog(OutValueSpecTextBox.Text, "Out Spec 편집");
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
            OutValueSpecTextBox.Text = dialog.ValueSpecText;
    }

    private void EditInValueSpec_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ValueSpecDialog(InValueSpecTextBox.Text, "In Spec 편집");
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
            InValueSpecTextBox.Text = dialog.ValueSpecText;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
