using System.Windows;
using System.Windows.Controls;
using Idx = Ds2.Editor.ValueSpecTypeIndex;

namespace Promaker.Controls;

public partial class ValueSpecEditorControl : UserControl
{

    internal const string UndefinedText = "Undefined";

    // ConditionTypeCombo 인덱스
    private const int CtxSingle   = 0;
    private const int CtxMultiple = 1;
    private const int CtxRanges   = 2;

    // Ranges는 읽기 전용 — 원본 텍스트 보존
    private string _rangesText = string.Empty;

    public ValueSpecEditorControl()
    {
        InitializeComponent();
        DataTypeCombo.SelectedIndex = Idx.Undefined;
    }

    private void OnDataTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataTypeCombo is null) return;
        var idx = DataTypeCombo.SelectedIndex;
        ApplyDataTypeVisibility(idx);

        if (ConditionTypeCombo is null) return;
        if (idx == Idx.Bool || idx == Idx.Undefined)
        {
            ConditionTypeCombo.SelectedIndex = CtxSingle;
            ConditionTypeCombo.IsEnabled = false;
        }
        else
        {
            ConditionTypeCombo.IsEnabled = true;
        }
    }

    private void OnConditionTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ValueTextBox is null) return;
        ValueTextBox.IsReadOnly = ConditionTypeCombo?.SelectedIndex == CtxRanges;
    }

    private void ApplyDataTypeVisibility(int index)
    {
        var isBool = index == Idx.Bool;
        var isUndefined = index == Idx.Undefined;
        var isOther = !isBool && !isUndefined;

        if (BoolButtonsPanel is not null) BoolButtonsPanel.Visibility = isBool ? Visibility.Visible : Visibility.Collapsed;
        if (ValueTextBox is not null) ValueTextBox.Visibility = isOther ? Visibility.Visible : Visibility.Collapsed;
        if (UndefinedHint is not null) UndefinedHint.Visibility = isUndefined ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Load a ValueSpec text with an explicit type index (avoids float32/float64 ambiguity).</summary>
    public void LoadFrom(string text, int typeIndex)
    {
        var raw = (text ?? string.Empty).Trim();
        DataTypeCombo.SelectedIndex = typeIndex;

        if (typeIndex == Idx.Undefined)
        {
            ConditionTypeCombo.SelectedIndex = CtxSingle;
            return;
        }

        if (typeIndex == Idx.Bool)
        {
            ConditionTypeCombo.SelectedIndex = CtxSingle;
            ConditionTypeCombo.IsEnabled = false;
            bool.TryParse(raw, out bool bVal);
            TrueRadio.IsChecked  = bVal;
            FalseRadio.IsChecked = !bVal;
            return;
        }

        // Ranges: ".." 포함
        if (raw.Contains(".."))
        {
            _rangesText = raw;
            ConditionTypeCombo.SelectedIndex = CtxRanges;
            ValueTextBox.Text = raw;
            ValueTextBox.IsReadOnly = true;
            return;
        }

        // Multiple: 쉼표 포함
        if (raw.Contains(','))
        {
            ConditionTypeCombo.SelectedIndex = CtxMultiple;
            ValueTextBox.Text = raw;
            ValueTextBox.IsReadOnly = false;
            return;
        }

        // Single
        ConditionTypeCombo.SelectedIndex = CtxSingle;
        ValueTextBox.Text = raw;
        ValueTextBox.IsReadOnly = false;
    }

    /// <summary>Get the current DataType index (0=Undefined … 12=string).</summary>
    public int GetTypeIndex() => DataTypeCombo?.SelectedIndex ?? Idx.Undefined;

    /// <summary>Get the current ValueSpec as a text string for storage.</summary>
    public string GetText()
    {
        var idx = DataTypeCombo?.SelectedIndex ?? Idx.Undefined;
        if (idx == Idx.Undefined) return UndefinedText;
        if (idx == Idx.Bool) return TrueRadio?.IsChecked == true ? "true" : "false";

        var condIdx = ConditionTypeCombo?.SelectedIndex ?? CtxSingle;

        // Ranges: 원본 텍스트 그대로 반환 (편집 불가)
        if (condIdx == CtxRanges) return string.IsNullOrEmpty(_rangesText) ? "Undefined" : _rangesText;

        var raw = ValueTextBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw)) return UndefinedText;

        return raw;
    }
}
