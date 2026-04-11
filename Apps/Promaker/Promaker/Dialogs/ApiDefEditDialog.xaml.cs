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
            if (existing.ActionType.IsPush)
            {
                PushRadio.IsChecked = true;
            }
            else if (existing.ActionType.IsPulse)
            {
                PulseRadio.IsChecked = true;
            }
            else if (existing.ActionType.IsTime)
            {
                TimeRadio.IsChecked = true;
                TimeValueBox.Text = ((ApiDefActionType.Time)existing.ActionType).Item.ToString();
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
        else if (TimeRadio.IsChecked == true)
        {
            if (!int.TryParse(TimeValueBox.Text, out var timeMs) || timeMs <= 0)
            {
                DialogHelpers.Warn("Time 값은 양의 정수여야 합니다.");
                return;
            }
            ActionType = ApiDefActionType.NewTime(timeMs);
        }
        else
        {
            ActionType = ApiDefActionType.Normal;
        }

        TxGuid = TxWorkCombo.SelectedItem is WorkDropdownItem { IsNone: false } tx ? tx.Id : null;
        RxGuid = RxWorkCombo.SelectedItem is WorkDropdownItem { IsNone: false } rx ? rx.Id : null;

        DialogResult = true;
    }

    private void TimeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (TimeValueBox != null)
            TimeValueBox.IsEnabled = true;
    }

    private void TimeRadio_Unchecked(object sender, RoutedEventArgs e)
    {
        if (TimeValueBox != null)
            TimeValueBox.IsEnabled = false;
    }

    private void TimeValueBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Only allow digits
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
    }

}
