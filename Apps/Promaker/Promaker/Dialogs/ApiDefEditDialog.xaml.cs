using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Ds2.UI.Core;

namespace Promaker.Dialogs;

public partial class ApiDefEditDialog : Window
{
    private readonly List<WorkDropdownItem> _workItems;

    // 출력
    public string ApiDefName { get; private set; } = string.Empty;
    public bool IsPush { get; private set; } = false;
    public Guid? TxWorkId { get; private set; }
    public Guid? RxWorkId { get; private set; }
    public int Period { get; private set; }
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
            NormalRadio.IsChecked = !existing.IsPush;
            PushRadio.IsChecked = existing.IsPush;
            PeriodBox.Text = existing.Period.ToString();
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

        if (!int.TryParse(PeriodBox.Text.Trim(), out int period) || period < 0)
        {
            DialogHelpers.Warn("Period는 0 이상의 정수를 입력해주세요.");
            return;
        }

        ApiDefName = name;
        IsPush = PushRadio.IsChecked == true; // Normal=false, Push=true
        Period = period;
        Description = DescriptionBox.Text.Trim();

        TxWorkId = TxWorkCombo.SelectedItem is WorkDropdownItem { IsNone: false } tx ? tx.Id : null;
        RxWorkId = RxWorkCombo.SelectedItem is WorkDropdownItem { IsNone: false } rx ? rx.Id : null;

        DialogResult = true;
    }

}
