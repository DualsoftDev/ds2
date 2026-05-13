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
        // 컬럼 인덱스: 0=삭제, 1=ApiDef, 2=InTag, 3=InAddress, 4=UseSensor, 5=InSpec, 6=OutTag, 7=OutAddress, 8=OutSpec, 9=저장
        var columns = ApiCallsDataGrid.Columns;
        if (columns.Count >= 10)
        {
            columns[2].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // InTag
            columns[4].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // UseSensor
            columns[5].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // InSpec
            columns[6].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // OutTag
            columns[8].Visibility = ShowAllFields ? Visibility.Visible : Visibility.Collapsed; // OutSpec
        }
    }
}
