using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Ds2.Editor;

namespace Promaker.Dialogs;

public partial class UserTagEditDialog : Window
{
    private static readonly string[] PlcValueTypes =
        ["Bit", "Byte", "Word", "DWord", "Int16", "Int32", "Real", "String"];

    private readonly HashSet<string> _existingNamesLower;

    // 출력
    public string TagName { get; private set; } = string.Empty;
    public string LogLevel { get; private set; } = "Info";
    public string TagAddress { get; private set; } = string.Empty;
    public string ValueType { get; private set; } = "Bit";

    public UserTagEditDialog(IEnumerable<string> existingNames, UserTagPanelItem? existing = null)
    {
        InitializeComponent();

        // 같은 System 내 다른 태그들의 이름 (편집 중인 자기 자신은 제외해서 전달받음)
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
        }
        else
        {
            ValueTypeCombo.SelectedItem = "Bit";
        }

        Loaded += (_, _) => NameBox.Focus();
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

        TagName = name;
        TagAddress = addr;

        if (WarningRadio.IsChecked == true) LogLevel = "Warning";
        else if (ErrorRadio.IsChecked == true) LogLevel = "Error";
        else LogLevel = "Info";

        ValueType = ValueTypeCombo.SelectedItem as string ?? "Bit";

        DialogResult = true;
    }
}
