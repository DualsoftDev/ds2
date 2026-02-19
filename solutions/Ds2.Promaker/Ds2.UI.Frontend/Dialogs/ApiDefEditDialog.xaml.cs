using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Ds2.Core;
using Ds2.UI.Core;

namespace Ds2.UI.Frontend.Dialogs;

public partial class ApiDefEditDialog : Window
{
    private readonly List<WorkDropdownItem> _workItems;

    // 출력
    public string ApiDefName  { get; private set; } = string.Empty;
    public bool   IsPush      { get; private set; } = true;
    public Guid?  TxWorkId    { get; private set; }
    public Guid?  RxWorkId    { get; private set; }
    public int    Duration    { get; private set; }
    public string Memo        { get; private set; } = string.Empty;

    public ApiDefEditDialog(IReadOnlyList<WorkDropdownItem> works, ApiDefPanelItem? existing = null)
    {
        InitializeComponent();

        var noneItem = new WorkDropdownItem(Guid.Empty, "(없음)");
        _workItems = new[] { noneItem }.Concat(works).ToList();

        TxWorkCombo.ItemsSource = _workItems;
        RxWorkCombo.ItemsSource = _workItems;

        if (existing is not null)
        {
            NameBox.Text        = existing.Name;
            PushRadio.IsChecked = existing.IsPush;
            PollRadio.IsChecked = !existing.IsPush;
            DurationBox.Text    = existing.Duration.ToString();
            MemoBox.Text        = existing.Memo;

            TxWorkCombo.SelectedItem = _workItems.FirstOrDefault(w => w.Id == existing.TxWorkIdOrEmpty) ?? noneItem;
            RxWorkCombo.SelectedItem = _workItems.FirstOrDefault(w => w.Id == existing.RxWorkIdOrEmpty) ?? noneItem;
        }
        else
        {
            TxWorkCombo.SelectedItem = noneItem;
            RxWorkCombo.SelectedItem = noneItem;
        }

        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnDurationPreviewInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Warn("이름을 입력해주세요.");
            return;
        }

        if (!int.TryParse(DurationBox.Text.Trim(), out int duration) || duration < 0)
        {
            Warn("Duration은 0 이상의 정수를 입력해주세요.");
            return;
        }

        ApiDefName = name;
        IsPush     = PushRadio.IsChecked == true;
        Duration   = duration;
        Memo       = MemoBox.Text.Trim();

        var tx = TxWorkCombo.SelectedItem as WorkDropdownItem;
        TxWorkId = tx is not null && tx.Id != Guid.Empty ? tx.Id : null;

        var rx = RxWorkCombo.SelectedItem as WorkDropdownItem;
        RxWorkId = rx is not null && rx.Id != Guid.Empty ? rx.Id : null;

        DialogResult = true;
    }

    private void Warn(string message) =>
        MessageBox.Show(message, "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
}
