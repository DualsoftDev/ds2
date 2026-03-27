using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Promaker.Dialogs;
using Xunit;

namespace Promaker.Tests;

public sealed class IoBatchSettingsDialogTests
{
    [Fact]
    public void Row_checkbox_applies_state_to_all_selected_rows()
    {
        StaTestRunner.Run(() =>
        {
            var row1 = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "Flow1", "Work1", "Call1", "", "", "", "", "");
            var row2 = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "Flow1", "Work1", "Call2", "", "", "", "", "");
            var row3 = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "Flow1", "Work2", "Call3", "", "", "", "", "");

            var dialog = new IoBatchSettingsDialog([row1, row2, row3]);
            dialog.Measure(new Size(1200, 800));
            dialog.Arrange(new Rect(0, 0, 1200, 800));
            dialog.UpdateLayout();

            var grid = (DataGrid)dialog.FindName("IoGrid")!;
            grid.SelectedItems.Add(row1);
            grid.SelectedItems.Add(row2);

            var handler = typeof(IoBatchSettingsDialog).GetMethod(
                "RowCheckBox_Click",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var checkBox = new CheckBox
            {
                DataContext = row1,
                IsChecked = true
            };

            handler.Invoke(dialog, [checkBox, new RoutedEventArgs(Button.ClickEvent, checkBox)]);

            Assert.True(row1.IsSelected);
            Assert.True(row2.IsSelected);
            Assert.False(row3.IsSelected);
        });
    }

    [Fact]
    public void Apply_button_is_enabled_only_when_dirty_and_does_not_close_after_apply()
    {
        StaTestRunner.Run(() =>
        {
            var row = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "Flow1", "Work1", "Call1", "", "", "", "", "");
            var applied = 0;

            var dialog = new IoBatchSettingsDialog([row], _ =>
            {
                applied++;
                return true;
            });

            dialog.Measure(new Size(1200, 800));
            dialog.Arrange(new Rect(0, 0, 1200, 800));
            dialog.UpdateLayout();

            var applyButton = (Button)dialog.FindName("ApplyButton")!;
            Assert.False(applyButton.IsEnabled);

            row.InAddress = "D100";
            Assert.True(applyButton.IsEnabled);

            var handler = typeof(IoBatchSettingsDialog).GetMethod(
                "Apply_Click",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            handler.Invoke(dialog, [applyButton, new RoutedEventArgs(Button.ClickEvent, applyButton)]);

            Assert.Equal(1, applied);
            Assert.False(applyButton.IsEnabled);
            Assert.False(row.IsChanged);
            Assert.Null(dialog.DialogResult);
        });
    }
}
