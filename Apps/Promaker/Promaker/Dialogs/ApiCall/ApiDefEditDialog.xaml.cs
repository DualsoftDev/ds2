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
    public ActionType ActionType { get; private set; } = ActionType.NewReal(SignalMode.Level, FSharpOption<TimePolicy>.None);
    public SensingType SensingType { get; private set; } = SensingType.NewReal(SignalMode.Level, FSharpOption<TimePolicy>.None);
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
        if (action.IsReal)
        {
            var real = (ActionType.Real)action;
            var hasMs = FSharpOption<TimePolicy>.get_IsSome(real.Item2);
            var ms = hasMs ? real.Item2.Value.Ms : 0;
            if (real.Item1 == SignalMode.Level && !hasMs)        ActionNormalRadio.IsChecked = true;
            else if (real.Item1 == SignalMode.OneShot && !hasMs) ActionPulseRadio.IsChecked = true;
            else if (real.Item1 == SignalMode.Latched && !hasMs) ActionSetRadio.IsChecked = true;
            else if (real.Item1 == SignalMode.Level && hasMs)   { ActionTimeAppendRadio.IsChecked = true; ActionTimeAppendMsBox.Text = ms.ToString(); }
            else if (real.Item1 == SignalMode.OneShot && hasMs) { ActionPulseHoldRadio.IsChecked = true; ActionPulseHoldMsBox.Text = ms.ToString(); }
        }
        else
        {
            var virt = (ActionType.Virtual)action;
            if (FSharpOption<TimePolicy>.get_IsSome(virt.Item))
            {
                ActionVirtPlusRadio.IsChecked = true;
                ActionVirtPlusMsBox.Text = virt.Item.Value.Ms.ToString();
            }
            else
            {
                ActionVirtRadio.IsChecked = true;
            }
        }
    }

    private void ApplySensingTypeToRadio(SensingType sensing)
    {
        if (sensing.IsReal)
        {
            var real = (SensingType.Real)sensing;
            var hasMs = FSharpOption<TimePolicy>.get_IsSome(real.Item2);
            var ms = hasMs ? real.Item2.Value.Ms : 0;
            if (real.Item1 == SignalMode.Level && !hasMs)        SensingNormalRadio.IsChecked = true;
            else if (real.Item1 == SignalMode.OneShot && !hasMs) SensingEdgeRadio.IsChecked = true;
            else if (real.Item1 == SignalMode.Latched && !hasMs) SensingLatchedRadio.IsChecked = true;
            else if (real.Item1 == SignalMode.Level && hasMs)   { SensingDebounceRadio.IsChecked = true; SensingDebounceMsBox.Text = ms.ToString(); }
            else if (real.Item1 == SignalMode.OneShot && hasMs) { SensingEdgeStableRadio.IsChecked = true; SensingEdgeStableMsBox.Text = ms.ToString(); }
        }
        else
        {
            var virt = (SensingType.Virtual)sensing;
            if (FSharpOption<TimePolicy>.get_IsSome(virt.Item))
            {
                SensingVirtPlusRadio.IsChecked = true;
                SensingVirtPlusMsBox.Text = virt.Item.Value.Ms.ToString();
            }
            else
            {
                SensingVirtRadio.IsChecked = true;
            }
        }
    }

    private bool TryReadActionType(out ActionType action)
    {
        action = ActionType.NewReal(SignalMode.Level, FSharpOption<TimePolicy>.None);
        if (ActionNormalRadio.IsChecked == true)        { action = ActionType.NewReal(SignalMode.Level, FSharpOption<TimePolicy>.None); return true; }
        if (ActionPulseRadio.IsChecked == true)         { action = ActionType.NewReal(SignalMode.OneShot, FSharpOption<TimePolicy>.None); return true; }
        if (ActionSetRadio.IsChecked == true)           { action = ActionType.NewReal(SignalMode.Latched, FSharpOption<TimePolicy>.None); return true; }
        if (ActionTimeAppendRadio.IsChecked == true)    { if (!TryParsePositive(ActionTimeAppendMsBox.Text, out var ms))    { DialogHelpers.Warn("timeAppend ms 값은 양의 정수여야 합니다."); return false; } action = ActionType.NewReal(SignalMode.Level, FSharpOption<TimePolicy>.Some(TimePolicy.NewAppend(ms))); return true; }
        if (ActionPulseHoldRadio.IsChecked == true)     { if (!TryParsePositive(ActionPulseHoldMsBox.Text, out var ms))     { DialogHelpers.Warn("pulseHold ms 값은 양의 정수여야 합니다."); return false; } action = ActionType.NewReal(SignalMode.OneShot, FSharpOption<TimePolicy>.Some(TimePolicy.NewAppend(ms))); return true; }
        if (ActionVirtRadio.IsChecked == true)          { action = ActionType.NewVirtual(FSharpOption<TimePolicy>.None); return true; }
        if (ActionVirtPlusRadio.IsChecked == true)      { if (!TryParsePositive(ActionVirtPlusMsBox.Text, out var ms))      { DialogHelpers.Warn("virtPlus ms 값은 양의 정수여야 합니다."); return false; } action = ActionType.NewVirtual(FSharpOption<TimePolicy>.Some(TimePolicy.NewAppend(ms))); return true; }
        return true;
    }

    private bool TryReadSensingType(out SensingType sensing)
    {
        sensing = SensingType.NewReal(SignalMode.Level, FSharpOption<TimePolicy>.None);
        if (SensingNormalRadio.IsChecked == true)       { sensing = SensingType.NewReal(SignalMode.Level, FSharpOption<TimePolicy>.None); return true; }
        if (SensingEdgeRadio.IsChecked == true)         { sensing = SensingType.NewReal(SignalMode.OneShot, FSharpOption<TimePolicy>.None); return true; }
        if (SensingLatchedRadio.IsChecked == true)      { sensing = SensingType.NewReal(SignalMode.Latched, FSharpOption<TimePolicy>.None); return true; }
        if (SensingDebounceRadio.IsChecked == true)     { if (!TryParsePositive(SensingDebounceMsBox.Text, out var ms))     { DialogHelpers.Warn("debounce ms 값은 양의 정수여야 합니다."); return false; } sensing = SensingType.NewReal(SignalMode.Level, FSharpOption<TimePolicy>.Some(TimePolicy.NewAppend(ms))); return true; }
        if (SensingEdgeStableRadio.IsChecked == true)   { if (!TryParsePositive(SensingEdgeStableMsBox.Text, out var ms))   { DialogHelpers.Warn("edgeStable ms 값은 양의 정수여야 합니다."); return false; } sensing = SensingType.NewReal(SignalMode.OneShot, FSharpOption<TimePolicy>.Some(TimePolicy.NewAppend(ms))); return true; }
        if (SensingVirtRadio.IsChecked == true)         { sensing = SensingType.NewVirtual(FSharpOption<TimePolicy>.None); return true; }
        if (SensingVirtPlusRadio.IsChecked == true)     { if (!TryParsePositive(SensingVirtPlusMsBox.Text, out var ms))     { DialogHelpers.Warn("virtPlus ms 값은 양의 정수여야 합니다."); return false; } sensing = SensingType.NewVirtual(FSharpOption<TimePolicy>.Some(TimePolicy.NewAppend(ms))); return true; }
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

    private void ActionTimeAppendRadio_Checked(object sender, RoutedEventArgs e)   => SetEnabled(ActionTimeAppendMsBox, true);
    private void ActionTimeAppendRadio_Unchecked(object sender, RoutedEventArgs e) => SetEnabled(ActionTimeAppendMsBox, false);
    private void ActionPulseHoldRadio_Checked(object sender, RoutedEventArgs e)    => SetEnabled(ActionPulseHoldMsBox, true);
    private void ActionPulseHoldRadio_Unchecked(object sender, RoutedEventArgs e)  => SetEnabled(ActionPulseHoldMsBox, false);
    private void ActionVirtPlusRadio_Checked(object sender, RoutedEventArgs e)     => SetEnabled(ActionVirtPlusMsBox, true);
    private void ActionVirtPlusRadio_Unchecked(object sender, RoutedEventArgs e)   => SetEnabled(ActionVirtPlusMsBox, false);
    private void SensingDebounceRadio_Checked(object sender, RoutedEventArgs e)    => SetEnabled(SensingDebounceMsBox, true);
    private void SensingDebounceRadio_Unchecked(object sender, RoutedEventArgs e)  => SetEnabled(SensingDebounceMsBox, false);
    private void SensingEdgeStableRadio_Checked(object sender, RoutedEventArgs e)  => SetEnabled(SensingEdgeStableMsBox, true);
    private void SensingEdgeStableRadio_Unchecked(object sender, RoutedEventArgs e)=> SetEnabled(SensingEdgeStableMsBox, false);
    private void SensingVirtPlusRadio_Checked(object sender, RoutedEventArgs e)    => SetEnabled(SensingVirtPlusMsBox, true);
    private void SensingVirtPlusRadio_Unchecked(object sender, RoutedEventArgs e)  => SetEnabled(SensingVirtPlusMsBox, false);

    private static void SetEnabled(TextBox? box, bool enabled)
    {
        if (box is not null) box.IsEnabled = enabled;
    }

    private void DigitOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
    }
}
