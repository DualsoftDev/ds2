using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;

namespace Promaker.Dialogs;

public partial class ApiDefEditDialog : Window
{
    private readonly List<WorkDropdownItem> _workItems;

    // 출력
    public string ApiDefName { get; private set; } = string.Empty;
    public ApiDefActionType ActionType { get; private set; } = ApiDefActionType.Normal;
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

            // Set radio button based on ActionType
            if (ApiDefActionType.Push.Equals(existing.ActionType))
            {
                PushRadio.IsChecked = true;
            }
            else if (existing.ActionType.IsPulse)
            {
                PulseRadio.IsChecked = true;
            }
            else if (existing.ActionType.IsTimeTotal)
            {
                TimeTotalRadio.IsChecked = true;
                TimeTotalMsBox.Text = ((ApiDefActionType.TimeTotal)existing.ActionType).Item.ToString();
            }
            else if (existing.ActionType.IsTimeAppend)
            {
                TimeAppendRadio.IsChecked = true;
                TimeAppendMsBox.Text = ((ApiDefActionType.TimeAppend)existing.ActionType).Item.ToString();
            }
            else if (existing.ActionType.IsMultiAction)
            {
                MultiActionRadio.IsChecked = true;
                var ma = (ApiDefActionType.MultiAction)existing.ActionType;
                MultiActionCountBox.Text = ma.Item1.ToString();
                MultiActionIntervalBox.Text = ma.Item2.ToString();
            }
            else // Normal
            {
                NormalRadio.IsChecked = true;
            }

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

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            DialogHelpers.Warn("이름을 입력해주세요.");
            return;
        }

        ApiDefName = name;

        // Determine ActionType from radio buttons
        if (PushRadio.IsChecked == true)
        {
            ActionType = ApiDefActionType.Push;
        }
        else if (PulseRadio.IsChecked == true)
        {
            ActionType = ApiDefActionType.Pulse;
        }
        else if (TimeTotalRadio.IsChecked == true)
        {
            if (!TryParsePositive(TimeTotalMsBox.Text, out var ms))
            {
                DialogHelpers.Warn("TimeTotal ms 값은 양의 정수여야 합니다.");
                return;
            }
            ActionType = ApiDefActionType.NewTimeTotal(ms);
        }
        else if (TimeAppendRadio.IsChecked == true)
        {
            if (!TryParsePositive(TimeAppendMsBox.Text, out var ms))
            {
                DialogHelpers.Warn("TimeAppend ms 값은 양의 정수여야 합니다.");
                return;
            }
            ActionType = ApiDefActionType.NewTimeAppend(ms);
        }
        else if (MultiActionRadio.IsChecked == true)
        {
            if (!TryParsePositive(MultiActionCountBox.Text, out var count))
            {
                DialogHelpers.Warn("MultiAction count 값은 양의 정수여야 합니다.");
                return;
            }
            if (!TryParsePositive(MultiActionIntervalBox.Text, out var interval))
            {
                DialogHelpers.Warn("MultiAction interval 값은 양의 정수여야 합니다.");
                return;
            }
            ActionType = ApiDefActionType.NewMultiAction(count, interval);
        }
        else
        {
            ActionType = ApiDefActionType.Normal;
        }

        TxGuid = TxWorkCombo.SelectedItem is WorkDropdownItem { IsNone: false } tx ? tx.Id : null;
        RxGuid = RxWorkCombo.SelectedItem is WorkDropdownItem { IsNone: false } rx ? rx.Id : null;
        Description = DescriptionBox.Text.Trim();

        DialogResult = true;
    }

    private static bool TryParsePositive(string text, out int value)
        => int.TryParse(text, out value) && value > 0;

    private void TimeTotalRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (TimeTotalMsBox != null) TimeTotalMsBox.IsEnabled = true;
    }

    private void TimeTotalRadio_Unchecked(object sender, RoutedEventArgs e)
    {
        if (TimeTotalMsBox != null) TimeTotalMsBox.IsEnabled = false;
    }

    private void TimeAppendRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (TimeAppendMsBox != null) TimeAppendMsBox.IsEnabled = true;
    }

    private void TimeAppendRadio_Unchecked(object sender, RoutedEventArgs e)
    {
        if (TimeAppendMsBox != null) TimeAppendMsBox.IsEnabled = false;
    }

    private void MultiActionRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (MultiActionCountBox != null) MultiActionCountBox.IsEnabled = true;
        if (MultiActionIntervalBox != null) MultiActionIntervalBox.IsEnabled = true;
    }

    private void MultiActionRadio_Unchecked(object sender, RoutedEventArgs e)
    {
        if (MultiActionCountBox != null) MultiActionCountBox.IsEnabled = false;
        if (MultiActionIntervalBox != null) MultiActionIntervalBox.IsEnabled = false;
    }

    private void DigitOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
    }
}
