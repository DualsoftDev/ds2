using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Ds2.Editor;

namespace Promaker.Dialogs;

public partial class UserTagEditDialog : Window
{
    private static readonly string[] PlcValueTypes =
        ["Bit", "Byte", "Word", "DWord", "Int16", "Int32", "Real", "String"];

    // (display, op id) — op id 는 F# UserTagHelpers.parseMatchOp 가 받는 키.
    private static readonly (string Display, string Op)[] BitMatchOps =
    [
        ("Rising Edge (0 → 1)",  "RisingEdge"),
        ("Falling Edge (1 → 0)", "FallingEdge"),
        ("값과 같아질 때 (=)",     "Eq"),
        ("값과 달라질 때 (≠)",     "Neq"),
        ("변할 때마다",            "Changed"),
    ];

    private static readonly (string Display, string Op)[] NumericMatchOps =
    [
        ("같아질 때 (=)",   "Eq"),
        ("달라질 때 (≠)",   "Neq"),
        ("초과 (>)",        "Gt"),
        ("이상 (≥)",        "Gte"),
        ("미만 (<)",        "Lt"),
        ("이하 (≤)",        "Lte"),
        ("변할 때마다",     "Changed"),
    ];

    private static readonly (string Display, string Op)[] StringMatchOps =
    [
        ("같아질 때 (=)", "Eq"),
        ("달라질 때 (≠)", "Neq"),
        ("변할 때마다",   "Changed"),
    ];

    private readonly HashSet<string> _existingNamesLower;
    private bool _initializing = true;

    public string TagName { get; private set; } = string.Empty;
    public string LogLevel { get; private set; } = "Info";
    public string TagAddress { get; private set; } = string.Empty;
    public string ValueType { get; private set; } = "Bit";
    public string MatchOp { get; private set; } = "RisingEdge";
    public string MatchValue { get; private set; } = string.Empty;

    public UserTagEditDialog(IEnumerable<string> existingNames, UserTagPanelItem? existing = null)
    {
        InitializeComponent();

        _existingNamesLower = new HashSet<string>(
            existingNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim().ToLowerInvariant())
            ?? Enumerable.Empty<string>());

        foreach (var vt in PlcValueTypes)
            ValueTypeCombo.Items.Add(vt);

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            AddressBox.Text = existing.TagAddress;

            switch (existing.LogLevel)
            {
                case "Warning": WarningRadio.IsChecked = true; break;
                case "Error":   ErrorRadio.IsChecked = true;   break;
                default:        InfoRadio.IsChecked = true;    break;
            }

            ValueTypeCombo.SelectedItem = PlcValueTypes.Contains(existing.ValueType)
                ? existing.ValueType
                : "Bit";

            RebuildMatchOpItems(SelectedValueType());
            SelectMatchOp(existing.MatchOp);
            MatchValueBox.Text = existing.MatchValue ?? string.Empty;
        }
        else
        {
            ValueTypeCombo.SelectedItem = "Bit";
            RebuildMatchOpItems("Bit");
            SelectMatchOp("RisingEdge");
        }

        _initializing = false;
        UpdatePreview();

        Loaded += (_, _) => NameBox.Focus();
    }

    private string SelectedValueType() =>
        (ValueTypeCombo.SelectedItem as string) ?? "Bit";

    private void RebuildMatchOpItems(string valueType)
    {
        var prev = (MatchOpCombo.SelectedItem as MatchOpItem)?.Op;
        MatchOpCombo.Items.Clear();
        var ops = valueType switch
        {
            "Bit"    => BitMatchOps,
            "String" => StringMatchOps,
            _        => NumericMatchOps,
        };
        foreach (var (display, op) in ops)
            MatchOpCombo.Items.Add(new MatchOpItem(display, op));

        // 이전 선택 복구; 없으면 첫 항목.
        if (!string.IsNullOrEmpty(prev))
            SelectMatchOp(prev);
        if (MatchOpCombo.SelectedIndex < 0 && MatchOpCombo.Items.Count > 0)
            MatchOpCombo.SelectedIndex = 0;
    }

    private void SelectMatchOp(string op)
    {
        foreach (MatchOpItem item in MatchOpCombo.Items)
        {
            if (string.Equals(item.Op, op, StringComparison.OrdinalIgnoreCase))
            {
                MatchOpCombo.SelectedItem = item;
                return;
            }
        }
        // 못 찾으면 첫 번째 항목으로.
        if (MatchOpCombo.Items.Count > 0)
            MatchOpCombo.SelectedIndex = 0;
    }

    private string CurrentMatchOp() =>
        (MatchOpCombo.SelectedItem as MatchOpItem)?.Op ?? "RisingEdge";

    private void ValueTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        RebuildMatchOpItems(SelectedValueType());
        UpdatePreview();
    }

    private void MatchOpCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdatePreview();
    }

    private void MatchValueBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_initializing) return;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "(이름)" : NameBox.Text.Trim();
        var op = CurrentMatchOp();
        var mv = MatchValueBox.Text?.Trim() ?? string.Empty;
        var levelText = WarningRadio.IsChecked == true ? "Warning"
                      : ErrorRadio.IsChecked == true ? "Error"
                      : "Info";

        var condition = op switch
        {
            "RisingEdge"  => "값이 0 → 1 로 바뀔 때",
            "FallingEdge" => "값이 1 → 0 으로 바뀔 때",
            "Changed"     => "값이 바뀔 때마다",
            "Eq"          => $"값이 '{mv}' 와 같아질 때",
            "Neq"         => $"값이 '{mv}' 와 달라질 때",
            "Gt"          => $"값이 '{mv}' 보다 커질 때",
            "Gte"         => $"값이 '{mv}' 이상이 될 때",
            "Lt"          => $"값이 '{mv}' 보다 작아질 때",
            "Lte"         => $"값이 '{mv}' 이하가 될 때",
            _             => "?",
        };

        PreviewText.Text = $"'{name}': {condition} → {levelText} 알림 생성";

        var needsValue = op is "Eq" or "Neq" or "Gt" or "Gte" or "Lt" or "Lte";
        MatchValueBox.IsEnabled = needsValue;
        if (!needsValue) MatchValueBox.Opacity = 0.5;
        else MatchValueBox.Opacity = 1.0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            DialogHelpers.Warn("이름을 입력해주세요.");
            return;
        }

        if (_existingNamesLower.Contains(name.ToLowerInvariant()))
        {
            DialogHelpers.Warn($"'{name}' 이름이 이미 존재합니다.");
            return;
        }

        var addr = AddressBox.Text.Trim();
        if (string.IsNullOrEmpty(addr))
        {
            DialogHelpers.Warn("태그 주소를 입력해주세요.");
            return;
        }

        var op = CurrentMatchOp();
        var mv = MatchValueBox.Text?.Trim() ?? string.Empty;

        // 값이 필요한 op 인데 비어있으면 차단.
        var needsValue = op is "Eq" or "Neq" or "Gt" or "Gte" or "Lt" or "Lte";
        if (needsValue && string.IsNullOrEmpty(mv))
        {
            DialogHelpers.Warn("매칭 조건의 기준값을 입력해주세요.");
            return;
        }

        TagName = name;
        TagAddress = addr;

        if (WarningRadio.IsChecked == true) LogLevel = "Warning";
        else if (ErrorRadio.IsChecked == true) LogLevel = "Error";
        else LogLevel = "Info";

        ValueType = SelectedValueType();
        MatchOp = op;
        // edge / Changed 류는 값이 무의미 — 빈 문자열로 저장해 정의 직렬화 모호성을 줄임.
        MatchValue = needsValue ? mv : string.Empty;

        DialogResult = true;
    }

    private sealed record MatchOpItem(string Display, string Op)
    {
        public override string ToString() => Display;
    }
}
