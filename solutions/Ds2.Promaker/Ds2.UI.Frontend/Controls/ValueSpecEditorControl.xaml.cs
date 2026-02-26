using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Ds2.UI.Frontend.Controls;

public partial class ValueSpecEditorControl : UserControl
{
    // 인덱스 매핑: 0=Undefined, 1=bool, 2=int8, 3=int16, 4=int32, 5=int64,
    //              6=uint8, 7=uint16, 8=uint32, 9=uint64, 10=float32, 11=float64, 12=string
    private const int IdxUndefined = 0;
    private const int IdxBool      = 1;
    private const int IdxInt8      = 2;
    private const int IdxInt16     = 3;
    private const int IdxInt32     = 4;
    private const int IdxInt64     = 5;
    private const int IdxUInt8     = 6;
    private const int IdxUInt16    = 7;
    private const int IdxUInt32    = 8;
    private const int IdxUInt64    = 9;
    private const int IdxFloat32   = 10;
    private const int IdxFloat64   = 11;
    private const int IdxString    = 12;

    public ValueSpecEditorControl()
    {
        InitializeComponent();
        DataTypeCombo.SelectedIndex = IdxUndefined;
    }

    private void OnDataTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataTypeCombo is null) return;
        ApplyDataTypeVisibility(DataTypeCombo.SelectedIndex);
    }

    private void ApplyDataTypeVisibility(int index)
    {
        var isBool = index == IdxBool;
        var isUndefined = index == IdxUndefined;
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

        if (typeIndex == IdxUndefined)
            return;

        if (typeIndex == IdxBool)
        {
            bool.TryParse(raw, out bool bVal);
            TrueRadio.IsChecked  = bVal;
            FalseRadio.IsChecked = !bVal;
            return;
        }

        ValueTextBox.Text = raw;
    }

    /// <summary>Load a ValueSpec text into the editor controls.</summary>
    public void LoadFromText(string text)
    {
        var raw = (text ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(raw) || raw.Equals("Undefined", StringComparison.OrdinalIgnoreCase))
        {
            DataTypeCombo.SelectedIndex = IdxUndefined;
            return;
        }

        if (bool.TryParse(raw, out bool bVal))
        {
            DataTypeCombo.SelectedIndex = IdxBool;
            TrueRadio.IsChecked = bVal;
            FalseRadio.IsChecked = !bVal;
            return;
        }

        // 정수 계열: 좁은 범위 우선
        if (sbyte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = IdxInt8;
            ValueTextBox.Text = raw;
            return;
        }
        if (short.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = IdxInt16;
            ValueTextBox.Text = raw;
            return;
        }
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = IdxInt32;
            ValueTextBox.Text = raw;
            return;
        }
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = IdxInt64;
            ValueTextBox.Text = raw;
            return;
        }
        if (byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = IdxUInt8;
            ValueTextBox.Text = raw;
            return;
        }
        if (ushort.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = IdxUInt16;
            ValueTextBox.Text = raw;
            return;
        }
        if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = IdxUInt32;
            ValueTextBox.Text = raw;
            return;
        }
        if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = IdxUInt64;
            ValueTextBox.Text = raw;
            return;
        }

        // 실수 계열
        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = IdxFloat32;
            ValueTextBox.Text = raw;
            return;
        }
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            DataTypeCombo.SelectedIndex = IdxFloat64;
            ValueTextBox.Text = raw;
            return;
        }

        // String fallback
        DataTypeCombo.SelectedIndex = IdxString;
        ValueTextBox.Text = raw;
    }

    /// <summary>Get the current ValueSpec as a text string for storage.</summary>
    public string GetText()
    {
        var idx = DataTypeCombo?.SelectedIndex ?? IdxUndefined;
        return idx switch
        {
            IdxUndefined => "Undefined",
            IdxBool      => TrueRadio?.IsChecked == true ? "true" : "false",
            IdxFloat32   => EnsureDecimalPoint(ValueTextBox?.Text?.Trim() ?? string.Empty),
            IdxFloat64   => EnsureDecimalPoint(ValueTextBox?.Text?.Trim() ?? string.Empty),
            _            => ValueTextBox?.Text?.Trim() ?? "Undefined"
        };
    }

    // 실수 타입 선택 시 소수점 없는 정수값에 ".0"을 추가하여 정수와 구분 보장
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
