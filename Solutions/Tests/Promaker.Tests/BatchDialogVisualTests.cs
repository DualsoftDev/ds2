using System;
using System.Windows;
using System.Windows.Controls;
using Promaker.Dialogs;
using Xunit;

namespace Promaker.Tests;

public sealed class BatchDialogVisualTests
{
    [Fact]
    public void DurationBatchDialog_uses_visible_grid_lines_for_work_table()
    {
        StaTestRunner.Run(() =>
        {
            EnsureAppResources();

            var dialog = new DurationBatchDialog([
                new DurationRow(Guid.NewGuid(), "FlowA", "WorkA", "1000")
            ]);

            var grid = Assert.IsType<DataGrid>(dialog.FindName("WorkDurationGrid"));

            Assert.Equal(DataGridGridLinesVisibility.All, grid.GridLinesVisibility);
            Assert.Equal(new Thickness(1), grid.BorderThickness);
            Assert.NotNull(grid.CellStyle);
            Assert.NotNull(grid.ColumnHeaderStyle);
        });
    }

    [Fact]
    public void IoBatchSettingsDialog_uses_visible_grid_lines_for_io_table()
    {
        StaTestRunner.Run(() =>
        {
            EnsureAppResources();

            var dialog = new IoBatchSettingsDialog([
                new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "FlowA", "WorkA", "CallA", "I0.0", "IN_A", "Q0.0", "OUT_A", "memo")
            ]);

            var grid = Assert.IsType<DataGrid>(dialog.FindName("IoGrid"));

            Assert.Equal(DataGridGridLinesVisibility.All, grid.GridLinesVisibility);
            Assert.Equal(new Thickness(1), grid.BorderThickness);
            Assert.NotNull(grid.CellStyle);
            Assert.NotNull(grid.ColumnHeaderStyle);
        });
    }

    private static void EnsureAppResources()
    {
        if (Application.Current!.Resources.MergedDictionaries.Count > 0)
            return;

        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Promaker;component/Themes/Theme.Dark.xaml", UriKind.Relative)
        });
    }
}
