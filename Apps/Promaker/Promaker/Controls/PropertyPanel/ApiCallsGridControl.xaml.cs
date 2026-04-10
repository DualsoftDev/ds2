using System.Windows;
using System.Windows.Controls;

namespace Promaker.Controls;

public partial class ApiCallsGridControl : UserControl
{
    public static readonly DependencyProperty ShowAllFieldsProperty =
        DependencyProperty.Register(
            nameof(ShowAllFields),
            typeof(bool),
            typeof(ApiCallsGridControl),
            new PropertyMetadata(false, OnShowAllFieldsChanged));

    public bool ShowAllFields
    {
        get => (bool)GetValue(ShowAllFieldsProperty);
        set => SetValue(ShowAllFieldsProperty, value);
    }

    public ApiCallsGridControl()
    {
        InitializeComponent();
        UpdateColumnVisibility();
    }

    private static void OnShowAllFieldsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ApiCallsGridControl control)
        {
            control.UpdateColumnVisibility();
        }
    }

    private void UpdateColumnVisibility()
    {
        if (ApiCallsDataGrid == null) return;

        // 간략보기: 이름(ApiDef), OutTag, OutAddress만 표시
        // 컬럼 인덱스: 0=삭제, 1=ApiDef, 2=OutTag, 3=OutAddress, 4=InTag, 5=InAddress, 6=OutSpec, 7=InSpec, 8=저장
        var columns = ApiCallsDataGrid.Columns;
        if (columns.Count >= 9)
        {
            // InTag, InAddress, OutSpec, InSpec 숨김
            columns[4].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // InTag
            columns[5].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // InAddress
            columns[6].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // OutSpec
            columns[7].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // InSpec
        }
    }
}
