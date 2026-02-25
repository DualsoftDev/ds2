using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Ds2.UI.Frontend.Controls;

public partial class ValueSpecEditorControl : UserControl
{
    public ValueSpecEditorControl()
    {
        InitializeComponent();
        DataTypeCombo.SelectedIndex = 0; // Undefined by default
    }

    private void OnDataTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataTypeCombo is null) return;
        ApplyDataTypeVisibility(DataTypeCombo.SelectedIndex);
    }

    private void ApplyDataTypeVisibility(int index)
    {
        // 0=Undefined, 1=bool, 2=int, 3=double, 4=string
        var isBool = index == 1;
        var isUndefined = index == 0;
        var isOther = !isBool && !isUndefined;

        if (BoolButtonsPanel is not null) BoolButtonsPanel.Visibility = isBool ? Visibility.Visible : Visibility.Collapsed;
        if (ValueTextBox is not null) ValueTextBox.Visibility = isOther ? Visibility.Visible : Visibility.Collapsed;
        if (UndefinedHint is not null) UndefinedHint.Visibility = isUndefined ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Load a ValueSpec text (e.g. "true", "10", "3.14", "text", "Undefined") into the editor controls.</summary>
    public void LoadFromText(string text)
    {
        var raw = (text ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(raw) || raw.Equals("Undefined", StringComparison.OrdinalIgnoreCase))
        {
            DataTypeCombo.SelectedIndex = 0; // Undefined
            return;
        }

        if (bool.TryParse(raw, out bool bVal))
        {
            DataTypeCombo.SelectedIndex = 1; // bool
            TrueRadio.IsChecked = bVal;
            FalseRadio.IsChecked = !bVal;
            return;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = 2; // int
            ValueTextBox.Text = raw;
            return;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = 3; // double
            ValueTextBox.Text = raw;
            return;
        }

        // String fallback
        DataTypeCombo.SelectedIndex = 4; // string
        ValueTextBox.Text = raw;
    }

    /// <summary>Get the current ValueSpec as a text string for storage.</summary>
    public string GetText()
    {
        var idx = DataTypeCombo?.SelectedIndex ?? 0;
        return idx switch
        {
            0 => "Undefined",
            1 => TrueRadio?.IsChecked == true ? "true" : "false",
            3 => EnsureDecimalPoint(ValueTextBox?.Text?.Trim() ?? string.Empty), // double
            _ => ValueTextBox?.Text?.Trim() ?? "Undefined"
        };
    }

    // double 타입 선택 시 소수점 없는 정수값에 ".0"을 추가하여 int와 구분 보장
    private static string EnsureDecimalPoint(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Equals("Undefined", StringComparison.OrdinalIgnoreCase))
            return "Undefined";
        return value.Contains('.') || value.Contains('E') || value.Contains('e')
            ? value
            : value + ".0";
    }
}
