using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class PropertyPanel : UserControl
{
    public PropertyPanel()
    {
        InitializeComponent();
    }

    private PropertyPanelState? ViewModel => DataContext as PropertyPanelState;

    public void FocusNameEditorControl()
    {
        NameEditor.Focus();
        NameEditor.SelectAll();
    }

    private void NameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel?.CancelNameEdit();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        ApplyName();
        e.Handled = true;
    }

    private void ApplyName()
    {
        if (ViewModel?.ApplyNameCommand.CanExecute(null) != true) return;
        ViewModel.ApplyNameCommand.Execute(null);
    }

    /// <summary>
    /// 내부 그리드(DataGrid 등) 위에서 휠 스크롤 시, 그 그리드가 해당 방향으로 더 스크롤할 수 없으면
    /// 외부 ScrollViewer 가 스크롤되도록 위임. 내부가 스크롤 가능하면 기본 동작 유지.
    /// </summary>
    private void PropertyScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        // PropertyScroll 자체와 PropertyScroll 사이의 첫 ScrollViewer 를 탐색.
        var inner = FindAncestorScrollViewer(src);
        if (inner != null && inner != PropertyScroll)
        {
            bool atTop    = inner.VerticalOffset <= 0.5;
            bool atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
            bool noContent = inner.ScrollableHeight <= 0;
            // 내부가 스크롤 불가 (내용 없음) 이거나 경계에서 그 방향으로 더 스크롤 불가 → 외부 스크롤.
            bool delegateOut =
                noContent
                || (e.Delta > 0 && atTop)
                || (e.Delta < 0 && atBottom);
            if (!delegateOut) return;   // 내부가 처리하도록 둠.
        }
        // 외부 ScrollViewer 스크롤 + 이벤트 소진 (내부 재발생 방지).
        PropertyScroll.ScrollToVerticalOffset(PropertyScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>src 의 visual 조상 중 PropertyScroll 직전까지의 ScrollViewer (내부 그리드 ScrollViewer) 검색.</summary>
    private ScrollViewer? FindAncestorScrollViewer(DependencyObject src)
    {
        var cur = src;
        while (cur != null && cur != PropertyScroll)
        {
            if (cur is ScrollViewer sv) return sv;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }
}
