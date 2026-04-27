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

        // 간략보기: ApiDef, InAddress, OutAddress만 표시
        // 컬럼 인덱스: 0=삭제, 1=ApiDef, 2=InTag, 3=InAddress, 4=InSpec, 5=OutTag, 6=OutAddress, 7=OutSpec, 8=저장
        var columns = ApiCallsDataGrid.Columns;
        if (columns.Count >= 9)
        {
            columns[2].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // InTag
            columns[4].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // InSpec
            columns[5].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // OutTag
            columns[7].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // OutSpec
        }
    }
}
