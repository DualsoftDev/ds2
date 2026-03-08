using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Promaker.Dialogs;

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

        LinkedApiDefComboBox.ItemsSource = apiDefs;
        LinkedApiDefComboBox.DisplayMemberPath = nameof(ApiDefChoice.DisplayName);
        LinkedApiDefComboBox.SelectedIndex = apiDefs.Count > 0 ? 0 : -1;

        var canCreate = apiDefs.Count > 0;
        AddButton.IsEnabled = canCreate;
        HintText.Text = canCreate
            ? "This ApiCall will be linked to the selected device ApiDef above."
            : "No Device ApiDef found. Add ApiDef in Device System first.";
    }

    public Guid? SelectedApiDefId =>
        LinkedApiDefComboBox.SelectedItem is ApiDefChoice choice ? choice.Id : null;

    public string ApiCallName => ApiCallNameTextBox.Text.Trim();
    public string OutputAddress => OutputAddressTextBox.Text.Trim();
    public string InputAddress => InputAddressTextBox.Text.Trim();
    public string OutSpecText => OutValueSpecTextBox.Text.Trim();
    public int OutTypeIndex { get; private set; }
    public string InSpecText => InValueSpecTextBox.Text.Trim();
    public int InTypeIndex { get; private set; }

    private void EditOutValueSpec_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ValueSpecDialog(OutValueSpecTextBox.Text, OutTypeIndex, "Out Spec 편집");
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            OutValueSpecTextBox.Text = dialog.ValueSpecText;
            OutTypeIndex = dialog.TypeIndex;
        }
    }

    private void EditInValueSpec_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ValueSpecDialog(InValueSpecTextBox.Text, InTypeIndex, "In Spec 편집");
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            InValueSpecTextBox.Text = dialog.ValueSpecText;
            InTypeIndex = dialog.TypeIndex;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
