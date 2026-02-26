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

    // ConditionTypeCombo 인덱스
    private const int CtxSingle   = 0;
    private const int CtxMultiple = 1;
    private const int CtxRanges   = 2;

    // Ranges는 읽기 전용 — 원본 텍스트 보존
    private string _rangesText = string.Empty;

    public ValueSpecEditorControl()
    {
        InitializeComponent();
        DataTypeCombo.SelectedIndex = IdxUndefined;
    }

    private void OnDataTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataTypeCombo is null) return;
        var idx = DataTypeCombo.SelectedIndex;
        ApplyDataTypeVisibility(idx);

        if (ConditionTypeCombo is null) return;
        if (idx == IdxBool || idx == IdxUndefined)
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
        {
            ConditionTypeCombo.SelectedIndex = CtxSingle;
            return;
        }

        if (typeIndex == IdxBool)
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

    /// <summary>Load a ValueSpec text into the editor controls.</summary>
    public void LoadFromText(string text)
    {
        var raw = (text ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(raw) || raw.Equals("Undefined", StringComparison.OrdinalIgnoreCase))
        {
            DataTypeCombo.SelectedIndex = IdxUndefined;
            ConditionTypeCombo.SelectedIndex = CtxSingle;
            return;
        }

        if (bool.TryParse(raw, out bool bVal))
        {
            DataTypeCombo.SelectedIndex = IdxBool;
            ConditionTypeCombo.SelectedIndex = CtxSingle;
            ConditionTypeCombo.IsEnabled = false;
            TrueRadio.IsChecked = bVal;
            FalseRadio.IsChecked = !bVal;
            return;
        }

        // Ranges 패턴 감지
        if (raw.Contains(".."))
        {
            _rangesText = raw;
            ConditionTypeCombo.SelectedIndex = CtxRanges;
            ValueTextBox.Text = raw;
            ValueTextBox.IsReadOnly = true;
            InferDataTypeFromToken(raw.Split(['.'], 2)[0].TrimStart('(', '['));
            return;
        }

        // Multiple 패턴 감지
        if (raw.Contains(','))
        {
            ConditionTypeCombo.SelectedIndex = CtxMultiple;
            ValueTextBox.Text = raw;
            ValueTextBox.IsReadOnly = false;
            InferDataTypeFromToken(raw.Split(',')[0].Trim());
            return;
        }

        // Single
        ConditionTypeCombo.SelectedIndex = CtxSingle;
        ValueTextBox.IsReadOnly = false;
        InferSingleDataType(raw);
    }

    // 첫 토큰으로 데이터 타입 추론 (Multiple/Ranges 첫 값 기준)
    private void InferDataTypeFromToken(string token)
    {
        var t = token.Trim();
        if (sbyte.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxInt8; return; }
        if (short.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxInt16; return; }
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxInt32; return; }
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxInt64; return; }
        if (byte.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxUInt8; return; }
        if (ushort.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxUInt16; return; }
        if (uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxUInt32; return; }
        if (ulong.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxUInt64; return; }
        if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxFloat32; return; }
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxFloat64; return; }
        DataTypeCombo.SelectedIndex = IdxString;
    }

    private void InferSingleDataType(string raw)
    {
        if (sbyte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxInt8;   ValueTextBox.Text = raw; return; }
        if (short.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxInt16;  ValueTextBox.Text = raw; return; }
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxInt32;  ValueTextBox.Text = raw; return; }
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxInt64;  ValueTextBox.Text = raw; return; }
        if (byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxUInt8;  ValueTextBox.Text = raw; return; }
        if (ushort.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxUInt16; ValueTextBox.Text = raw; return; }
        if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxUInt32; ValueTextBox.Text = raw; return; }
        if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxUInt64; ValueTextBox.Text = raw; return; }
        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxFloat32; ValueTextBox.Text = raw; return; }
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            { DataTypeCombo.SelectedIndex = IdxFloat64; ValueTextBox.Text = raw; return; }
        DataTypeCombo.SelectedIndex = IdxString;
        ValueTextBox.Text = raw;
    }

    /// <summary>Get the current DataType index (0=Undefined … 12=string).</summary>
    public int GetTypeIndex() => DataTypeCombo?.SelectedIndex ?? IdxUndefined;

    /// <summary>Get the current ValueSpec as a text string for storage.</summary>
    public string GetText()
    {
        var idx = DataTypeCombo?.SelectedIndex ?? IdxUndefined;
        if (idx == IdxUndefined) return "Undefined";
        if (idx == IdxBool) return TrueRadio?.IsChecked == true ? "true" : "false";

        var condIdx = ConditionTypeCombo?.SelectedIndex ?? CtxSingle;

        // Ranges: 원본 텍스트 그대로 반환 (편집 불가)
        if (condIdx == CtxRanges) return string.IsNullOrEmpty(_rangesText) ? "Undefined" : _rangesText;

        var raw = ValueTextBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw)) return "Undefined";

        // float32/float64: 소수점 보장 (Multiple인 경우 각 항목에 적용)
        if (idx == IdxFloat32 || idx == IdxFloat64)
        {
            if (condIdx == CtxMultiple)
            {
                var parts = raw.Split(',');
                return string.Join(", ", Array.ConvertAll(parts, p => EnsureDecimalPoint(p.Trim())));
            }
            return EnsureDecimalPoint(raw);
        }

        return raw;
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
