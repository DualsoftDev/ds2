using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Ds2.Core.Store;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class MultiDeviceCallMigrationDialog : Window
{
    private readonly DsStore _store;
    public ObservableCollection<MultiDeviceCallSplitter.InvalidCallRow> Rows { get; } = new();

    public MultiDeviceCallMigrationDialog(DsStore store)
    {
        InitializeComponent();
        _store = store;
        RowsGrid.ItemsSource = Rows;
        Reload();
    }

    private void Reload()
    {
        Rows.Clear();
        foreach (var r in MultiDeviceCallSplitter.Scan(_store))
            Rows.Add(r);
        StatusText.Text = Rows.Count == 0
            ? "✓ 위반 Call 이 없습니다."
            : $"위반 Call {Rows.Count}건 — 적용할 항목을 체크하고 '적용' 누르세요.";
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in Rows) r.Selected = true;
        RowsGrid.Items.Refresh();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in Rows) r.Selected = false;
        RowsGrid.Items.Refresh();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selected = Rows.Where(r => r.Selected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "⚠ 선택된 행이 없습니다.";
            return;
        }

        try
        {
            var (splits, deletes) = MultiDeviceCallSplitter.Apply(_store, selected);
            StatusText.Text = $"✓ 분할 {splits}건, 삭제 {deletes}건 적용 완료. 재검사합니다.";
            Reload();

            if (Rows.Count == 0)
            {
                DialogResult = true;
                Close();
            }
        }
        catch (System.Exception ex)
        {
            StatusText.Text = $"❌ 적용 실패: {ex.Message}";
        }
    }
}
