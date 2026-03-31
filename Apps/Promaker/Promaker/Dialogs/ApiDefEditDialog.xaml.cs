using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;

namespace Promaker.Dialogs;

public partial class ApiDefEditDialog : Window
{
    private readonly List<WorkDropdownItem> _workItems;

    // 출력
    public string ApiDefName { get; private set; } = string.Empty;
    public ApiDefProperties ResultProperties { get; private set; } = new();

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

        var txId = TxWorkCombo.SelectedItem is WorkDropdownItem { IsNone: false } tx ? tx.Id : (Guid?)null;
        var rxId = RxWorkCombo.SelectedItem is WorkDropdownItem { IsNone: false } rx ? rx.Id : (Guid?)null;
        var desc = DescriptionBox.Text.Trim();

        ResultProperties = new ApiDefProperties
        {
            IsPush = PushRadio.IsChecked == true,
            TxGuid = txId.HasValue ? FSharpOption<Guid>.Some(txId.Value) : null,
            RxGuid = rxId.HasValue ? FSharpOption<Guid>.Some(rxId.Value) : null,
            Description = string.IsNullOrEmpty(desc) ? null : FSharpOption<string>.Some(desc),
        };

        DialogResult = true;
    }

}
