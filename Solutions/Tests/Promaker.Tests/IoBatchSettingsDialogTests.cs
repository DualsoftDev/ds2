using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Ds2.Core.Store;
using Microsoft.FSharp.Collections;
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
            var store = new DsStore();
            var row1 = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "Flow1", "Work1", "Dev1", "Api1", "", "", "", "");
            var row2 = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "Flow1", "Work1", "Dev1", "Api2", "", "", "", "");
            var row3 = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "Flow1", "Work2", "Dev2", "Api3", "", "", "", "");

            var dialog = new IoBatchSettingsDialog(store, [row1, row2, row3]);
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
    public void Apply_button_is_always_enabled_and_invokes_callback()
    {
        StaTestRunner.Run(() =>
        {
            var store = new DsStore();
            var row = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "Flow1", "Work1", "Dev1", "Api1", "", "", "", "");
            var applied = 0;

            var dialog = new IoBatchSettingsDialog(store, [row], null, _ =>
            {
                applied++;
                return true;
            });

            dialog.Measure(new Size(1200, 800));
            dialog.Arrange(new Rect(0, 0, 1200, 800));
            dialog.UpdateLayout();

            var applyButton = (Button)dialog.FindName("ApplyButton")!;
            Assert.True(applyButton.IsEnabled);
        });
    }

    [Fact]
    public void ParseIoCsv_parses_10col_template_with_work()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(
                path,
                string.Join(
                    Environment.NewLine,
                    "Flow,Work,Device,Api,OutSymbol,OutDataType,OutAddress,InSymbol,InDataType,InAddress",
                    "FlowA,WorkA,DeviceA,ApiA,OUT_TAG,BOOL,D100,IN_TAG,INT,D200"),
                new UTF8Encoding(false));

            var result = Ds2.IOList.CsvImporter.parseIoCsv(path);

            Assert.False(result.IsError);

            var rows = ListModule.ToArray(result.ResultValue);
            Assert.Equal(2, rows.Length);

            Assert.Contains(rows, row =>
                row.Direction == "Output" &&
                row.FlowName == "FlowA" &&
                row.DeviceName == "DeviceA" &&
                row.ApiName == "ApiA" &&
                row.VarName == "OUT_TAG" &&
                row.DataType == "BOOL" &&
                row.Address == "D100");

            Assert.Contains(rows, row =>
                row.Direction == "Input" &&
                row.FlowName == "FlowA" &&
                row.DeviceName == "DeviceA" &&
                row.ApiName == "ApiA" &&
                row.VarName == "IN_TAG" &&
                row.DataType == "INT" &&
                row.Address == "D200");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseIoCsv_parses_legacy_9col_template_without_work()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(
                path,
                string.Join(
                    Environment.NewLine,
                    "Flow,Device,Api,OutSymbol,OutDataType,OutAddress,InSymbol,InDataType,InAddress",
                    "FlowA,DeviceA,ApiA,OUT_TAG,BOOL,D100,IN_TAG,INT,D200"),
                new UTF8Encoding(false));

            var result = Ds2.IOList.CsvImporter.parseIoCsv(path);

            Assert.False(result.IsError);

            var rows = ListModule.ToArray(result.ResultValue);
            Assert.Equal(2, rows.Length);

            Assert.Contains(rows, row =>
                row.Direction == "Output" &&
                row.FlowName == "FlowA" &&
                row.DeviceName == "DeviceA" &&
                row.ApiName == "ApiA");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Csv_import_apply_updates_matching_rows_case_insensitively()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(
                path,
                string.Join(
                    Environment.NewLine,
                    "Flow,Work,Device,Api,OutSymbol,OutDataType,OutAddress,InSymbol,InDataType,InAddress",
                    "flowa,worka,devicea,apia,OUT_TAG,BOOL,D100,IN_TAG,INT,D200",
                    "FlowB,WorkB,DeviceB,ApiB,OUT_B,BOOL,D300,IN_B,BOOL,D400"),
                new UTF8Encoding(false));

            var parseResult = Ds2.IOList.CsvImporter.parseIoCsv(path);
            Assert.False(parseResult.IsError);

            var importRows = ListModule.ToArray(parseResult.ResultValue);
            var target = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "FlowA", "WorkA", "DeviceA", "ApiA", "", "", "", "");
            var untouched = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "FlowX", "WorkX", "DeviceX", "ApiX", "", "", "", "");

            var applyMethod = typeof(IoBatchSettingsDialog).GetMethod(
                "ApplyImportedRows",
                BindingFlags.Static | BindingFlags.NonPublic)!;

            var matched = (int)applyMethod.Invoke(null, [new[] { target, untouched }, importRows])!;

            Assert.Equal(2, matched);
            Assert.Equal("OUT_TAG", target.OutSymbol);
            Assert.Equal("BOOL", target.OutDataType);
            Assert.Equal("D100", target.OutAddress);
            Assert.Equal("IN_TAG", target.InSymbol);
            Assert.Equal("INT", target.InDataType);
            Assert.Equal("D200", target.InAddress);
            Assert.True(target.IsChanged);

            Assert.Equal("", untouched.OutSymbol);
            Assert.Equal("", untouched.InSymbol);
            Assert.False(untouched.IsChanged);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
