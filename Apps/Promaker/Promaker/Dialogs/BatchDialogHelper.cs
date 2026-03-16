using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Promaker.Dialogs;

/// <summary>
/// 배치 편집 다이얼로그(I/O, Duration)의 공통 로직.
/// </summary>
internal static class BatchDialogHelper
{
    internal static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = child;
        while (current != null)
        {
            if (current is T found) return found;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    internal static void DeselectOnEmptyAreaClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && e.OriginalSource is DependencyObject source)
        {
            if (FindParent<DataGridRow>(source) == null && FindParent<DataGridColumnHeader>(source) == null)
                grid.UnselectAll();
        }
    }

    internal static void CheckGridSelected<TRow>(DataGrid grid) where TRow : IBatchRow
    {
        foreach (var row in grid.SelectedItems.OfType<TRow>())
            row.IsSelected = true;
    }

    internal static void UncheckGridSelected<TRow>(DataGrid grid) where TRow : IBatchRow
    {
        foreach (var row in grid.SelectedItems.OfType<TRow>())
            row.IsSelected = false;
    }

    internal static void CheckAll<TRow>(IEnumerable<TRow> rows) where TRow : IBatchRow
    {
        foreach (var row in rows)
            row.IsSelected = true;
    }

    internal static void UncheckAll<TRow>(IEnumerable<TRow> rows) where TRow : IBatchRow
    {
        foreach (var row in rows)
            row.IsSelected = false;
    }

    internal static void UpdateSelectedCount<TRow>(IEnumerable<TRow> rows, TextBlock target)
        where TRow : IBatchRow
    {
        target.Text = rows.Count(r => r.IsSelected).ToString();
    }
}

internal interface IBatchRow : INotifyPropertyChanged
{
    bool IsSelected { get; set; }
}

public abstract class BatchRowBase : IBatchRow
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    protected void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
