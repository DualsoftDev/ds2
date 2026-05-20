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

        // v10 컬럼 (UseSensor 폐기 후): 0=삭제, 1=ApiDef, 2=InTag, 3=InAddress,
        // 4=InSpec, 5=OutTag, 6=OutAddress, 7=OutSpec, 8=저장(✎/✓)
        // 간략보기에서는 InTag(2)·InSpec(4)·OutTag(5)·OutSpec(7) 만 숨김.
        var columns = ApiCallsDataGrid.Columns;
        if (columns.Count < 8) return;

        var vis = ShowAllFields ? Visibility.Visible : Visibility.Collapsed;
        columns[2].Visibility = vis; // InTag
        columns[4].Visibility = vis; // InSpec
        columns[5].Visibility = vis; // OutTag
        columns[7].Visibility = vis; // OutSpec
    }
}
