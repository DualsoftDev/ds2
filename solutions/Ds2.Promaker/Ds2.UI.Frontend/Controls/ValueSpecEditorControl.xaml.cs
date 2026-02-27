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

    // bool은 LoadFromText/LoadFrom에서 이미 처리됨 — 숫자/문자 타입 인덱스만 추론
    private static int InferTypeIndex(string raw)
    {
        if (sbyte.TryParse(raw,  NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return IdxInt8;
        if (short.TryParse(raw,  NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return IdxInt16;
        if (int.TryParse(raw,    NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return IdxInt32;
        if (long.TryParse(raw,   NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return IdxInt64;
        if (byte.TryParse(raw,   NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return IdxUInt8;
        if (ushort.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return IdxUInt16;
        if (uint.TryParse(raw,   NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return IdxUInt32;
        if (ulong.TryParse(raw,  NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return IdxUInt64;
        if (float.TryParse(raw,  NumberStyles.Float,   CultureInfo.InvariantCulture, out _)) return IdxFloat32;
        if (double.TryParse(raw, NumberStyles.Float,   CultureInfo.InvariantCulture, out _)) return IdxFloat64;
        return IdxString;
    }

    private void InferDataTypeFromToken(string token) =>
        DataTypeCombo.SelectedIndex = InferTypeIndex(token.Trim());

    private void InferSingleDataType(string raw)
    {
        DataTypeCombo.SelectedIndex = InferTypeIndex(raw);
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

        return raw;
    }
}
