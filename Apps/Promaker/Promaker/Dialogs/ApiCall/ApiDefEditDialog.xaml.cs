using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;

namespace Promaker.Dialogs;

public partial class ApiDefEditDialog : Window
{
    private readonly List<WorkDropdownItem> _workItems;

    public string ApiDefName { get; private set; } = string.Empty;
    public ActionType ActionType { get; private set; } = ActionType.NewNormal(FSharpOption<int>.None);
    public SensingType SensingType { get; private set; } = SensingType.NewNormal(FSharpOption<int>.None);
    public Guid? TxGuid { get; private set; }
    public Guid? RxGuid { get; private set; }
    public string Description { get; private set; } = string.Empty;

    public ApiDefEditDialog(IReadOnlyList<WorkDropdownItem> works, ApiDefPanelItem? existing = null)
    {
        InitializeComponent();

        var noneItem = new WorkDropdownItem(Guid.NewGuid(), "(없음)", isNone: true);
        _workItems = new[] { noneItem }.Concat(works).ToList();

        TxWorkCombo.ItemsSource = _workItems;
        RxWorkCombo.ItemsSource = _workItems;

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            ApplyActionTypeToRadio(existing.ActionType);
            ApplySensingTypeToRadio(existing.SensingType);
            DescriptionBox.Text = existing.Description;

            TxWorkCombo.SelectedItem = existing.TxWorkIdOrNull is { } txId
                ? _workItems.FirstOrDefault(w => w.Id == txId) ?? noneItem
                : noneItem;
            RxWorkCombo.SelectedItem = existing.RxWorkIdOrNull is { } rxId
                ? _workItems.FirstOrDefault(w => w.Id == rxId) ?? noneItem
                : noneItem;
        }
        else
        {
            TxWorkCombo.SelectedItem = noneItem;
            RxWorkCombo.SelectedItem = noneItem;
        }

        Loaded += (_, _) => NameBox.Focus();
    }

    private void ApplyActionTypeToRadio(ActionType action)
    {
        if (action.IsNormal)
        {
            ActionNormalRadio.IsChecked = true;
            var t = ((ActionType.Normal)action).timeMs;
            ActionNormalTimeCheck.IsChecked = FSharpOption<int>.get_IsSome(t);
            if (FSharpOption<int>.get_IsSome(t)) ActionNormalMsBox.Text = t.Value.ToString();
        }
        else if (action.IsPulse)
        {
            ActionPulseRadio.IsChecked = true;
            var t = ((ActionType.Pulse)action).timeMs;
            ActionPulseTimeCheck.IsChecked = FSharpOption<int>.get_IsSome(t);
            if (FSharpOption<int>.get_IsSome(t)) ActionPulseMsBox.Text = t.Value.ToString();
        }
        else if (action.IsLatch) ActionLatchRadio.IsChecked = true;
        else ActionVirtRadio.IsChecked = true;
    }

    private void ApplySensingTypeToRadio(SensingType sensing)
    {
        if (sensing.IsNormal)
        {
            SensingNormalRadio.IsChecked = true;
            var t = ((SensingType.Normal)sensing).timeMs;
            SensingNormalTimeCheck.IsChecked = FSharpOption<int>.get_IsSome(t);
            if (FSharpOption<int>.get_IsSome(t)) SensingNormalMsBox.Text = t.Value.ToString();
        }
        else if (sensing.IsLatch)
        {
            SensingLatchRadio.IsChecked = true;
            SensingLatchMsBox.Text = ((SensingType.Latch)sensing).timeMs.ToString();
        }
        else
        {
            SensingVirtRadio.IsChecked = true;
            SensingVirtMsBox.Text = ((SensingType.Virtual)sensing).timeMs.ToString();
        }
    }

    /// <summary>시간 사용 체크박스가 켜져 있으면 Some(ms), 아니면 None. 파싱 실패 시 false.</summary>
    private bool TryReadTimeOption(CheckBox timeCheck, TextBox msBox, string label, out FSharpOption<int> timeMs)
    {
        timeMs = FSharpOption<int>.None;
        if (timeCheck.IsChecked != true) return true;
        if (!TryParsePositive(msBox.Text, out var ms))
        {
            DialogHelpers.Warn($"{label} 시간(ms) 값은 양의 정수여야 합니다.");
            return false;
        }
        timeMs = FSharpOption<int>.Some(ms);
        return true;
    }

    private bool TryReadActionType(out ActionType action)
    {
        action = ActionType.NewNormal(FSharpOption<int>.None);
        if (ActionNormalRadio.IsChecked == true)
        {
            if (!TryReadTimeOption(ActionNormalTimeCheck, ActionNormalMsBox, "Normal", out var t)) return false;
            action = ActionType.NewNormal(t);
            return true;
        }
        if (ActionPulseRadio.IsChecked == true)
        {
            if (!TryReadTimeOption(ActionPulseTimeCheck, ActionPulseMsBox, "Pulse", out var t)) return false;
            action = ActionType.NewPulse(t);
            return true;
        }
        if (ActionLatchRadio.IsChecked == true) { action = ActionType.Latch; return true; }
        if (ActionVirtRadio.IsChecked == true)  { action = ActionType.Virtual; return true; }
        return true;
    }

    private bool TryReadSensingType(out SensingType sensing)
    {
        sensing = SensingType.NewNormal(FSharpOption<int>.None);
        if (SensingNormalRadio.IsChecked == true)
        {
            if (!TryReadTimeOption(SensingNormalTimeCheck, SensingNormalMsBox, "Normal", out var t)) return false;
            sensing = SensingType.NewNormal(t);
            return true;
        }
        if (SensingLatchRadio.IsChecked == true)
        {
            if (!TryParsePositive(SensingLatchMsBox.Text, out var ms)) { DialogHelpers.Warn("Latch 시간(ms) 값은 양의 정수여야 합니다."); return false; }
            sensing = SensingType.NewLatch(ms);
            return true;
        }
        if (SensingVirtRadio.IsChecked == true)
        {
            if (!TryParsePositive(SensingVirtMsBox.Text, out var ms)) { DialogHelpers.Warn("Virtual 시간(ms) 값은 양의 정수여야 합니다."); return false; }
            sensing = SensingType.NewVirtual(ms);
            return true;
        }
        return true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            DialogHelpers.Warn("이름을 입력해주세요.");
            return;
        }

        if (!TryReadActionType(out var action)) return;
        if (!TryReadSensingType(out var sensing)) return;

        ApiDefName = name;
        ActionType = action;
        SensingType = sensing;
        TxGuid = TxWorkCombo.SelectedItem is WorkDropdownItem { IsNone: false } tx ? tx.Id : null;
        RxGuid = RxWorkCombo.SelectedItem is WorkDropdownItem { IsNone: false } rx ? rx.Id : null;
        Description = DescriptionBox.Text.Trim();

        DialogResult = true;
    }

    private static bool TryParsePositive(string text, out int value)
        => int.TryParse(text, out value) && value > 0;

    // ms 박스/시간 라디오 enable 은 전부 XAML ElementName 바인딩으로 처리 — 코드비하인드 핸들러 불필요.

    private void DigitOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
    }
}
