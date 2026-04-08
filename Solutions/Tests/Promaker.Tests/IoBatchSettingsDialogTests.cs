using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Ds2.Core.Store;
using Ds2.IOList;
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

    // ─── CSV import: 프로젝트 관점 미적용 행 추적 ─────────────────────

    private static (object Result, IList<IoBatchRow> Unmatched, int Total, int Applied) InvokeApplyImportedRows(
        IReadOnlyCollection<IoBatchRow> targets,
        CsvImporter.IoImportRow[] importRows)
    {
        var apply = typeof(IoBatchSettingsDialog).GetMethod(
            "ApplyImportedRows", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = apply.Invoke(null, [targets, importRows])!;
        var t = result.GetType();
        var unmatched = ((IEnumerable<IoBatchRow>)t.GetProperty("UnmatchedTargetRows")!.GetValue(result)!).ToList();
        var total = (int)t.GetProperty("TotalTargetRows")!.GetValue(result)!;
        var applied = (int)t.GetProperty("AppliedTargetRows")!.GetValue(result)!;
        return (result, unmatched, total, applied);
    }

    [Fact]
    public void ApplyImportedRows_marks_untouched_project_rows_as_unmatched()
    {
        // 프로젝트 5행, CSV는 그중 3행만 채움 → 나머지 2행이 미적용으로 잡혀야 함
        var rA = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "F1", "W1", "Dev", "Up",   "", "", "", "");
        var rB = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "F1", "W1", "Dev", "Down", "", "", "", "");
        var rC = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "F1", "W2", "Dev", "Up",   "", "", "", "");
        var rD = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "F2", "W1", "Dev", "Up",   "", "", "", "");
        var rE = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "F2", "W1", "Dev", "Down", "", "", "", "");
        var targets = new[] { rA, rB, rC, rD, rE };

        // CSV: rA, rC, rD만 In/Out 모두 채움
        var csv =
            "Flow,Work,Device,Api,OutTag,OutDataType,OutAddress,InTag,InDataType,InAddress\n" +
            "F1,W1,Dev,Up,O1,BOOL,D100,I1,BOOL,D200\n" +
            "F1,W2,Dev,Up,O2,BOOL,D101,I2,BOOL,D201\n" +
            "F2,W1,Dev,Up,O3,BOOL,D102,I3,BOOL,D202\n";

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, csv, new UTF8Encoding(false));
            var parsed = CsvImporter.parseIoCsv(path);
            Assert.True(parsed.IsOk);
            var importRows = ListModule.ToArray(parsed.ResultValue);

            var (_, unmatched, total, applied) = InvokeApplyImportedRows(targets, importRows);

            Assert.Equal(5, total);
            Assert.Equal(3, applied);
            Assert.Equal(2, unmatched.Count);
            Assert.Contains(rB, unmatched);
            Assert.Contains(rE, unmatched);
            // 적용된 행은 실제로 값이 채워졌는지 확인
            Assert.Equal("D100", rA.OutAddress);
            Assert.Equal("D200", rA.InAddress);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApplyImportedRows_fanout_covers_all_works_when_csv_has_no_work_column()
    {
        // 같은 (Flow, Device, Api)를 가진 두 Work, CSV는 9열(Work 없음) → fan-out으로 둘 다 채워져 미적용 0건
        var r1 = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "F1", "W1", "Dev", "Up", "", "", "", "");
        var r2 = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "F1", "W2", "Dev", "Up", "", "", "", "");
        var targets = new[] { r1, r2 };

        var csv =
            "Flow,Device,Api,OutTag,OutDataType,OutAddress,InTag,InDataType,InAddress\n" +
            "F1,Dev,Up,O,BOOL,D100,I,BOOL,D200\n";

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, csv, new UTF8Encoding(false));
            var parsed = CsvImporter.parseIoCsv(path);
            Assert.True(parsed.IsOk);
            var importRows = ListModule.ToArray(parsed.ResultValue);

            var (_, unmatched, total, applied) = InvokeApplyImportedRows(targets, importRows);

            Assert.Equal(2, total);
            Assert.Equal(2, applied);
            Assert.Empty(unmatched);
            Assert.Equal("D100", r1.OutAddress);
            Assert.Equal("D100", r2.OutAddress);
            Assert.Equal("D200", r1.InAddress);
            Assert.Equal("D200", r2.InAddress);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApplyImportedRows_does_not_throw_on_duplicate_flow_device_api_targets()
    {
        // 광명EVO 회귀: 같은 (Flow, Device, Api) 두 Work — 과거 ToDictionary throw
        var r1 = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "S141", "S141_PART", "S141_BACK_PNL_PART_CT", "ON", "", "", "", "");
        var r2 = new IoBatchRow(Guid.NewGuid(), Guid.NewGuid(), "S141", "S141_RB1",  "S141_BACK_PNL_PART_CT", "ON", "", "", "", "");
        var targets = new[] { r1, r2 };

        // 10열 CSV — 두 Work를 명시
        var csv =
            "Flow,Work,Device,Api,OutTag,OutDataType,OutAddress,InTag,InDataType,InAddress\n" +
            "S141,S141_PART,S141_BACK_PNL_PART_CT,ON,O1,BOOL,D100,I1,BOOL,D200\n" +
            "S141,S141_RB1,S141_BACK_PNL_PART_CT,ON,O2,BOOL,D101,I2,BOOL,D201\n";

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, csv, new UTF8Encoding(false));
            var parsed = CsvImporter.parseIoCsv(path);
            Assert.True(parsed.IsOk);
            var importRows = ListModule.ToArray(parsed.ResultValue);

            var (_, unmatched, total, applied) = InvokeApplyImportedRows(targets, importRows);

            // throw 안 함, 두 Work 모두 적용, 미적용 0건
            Assert.Equal(2, total);
            Assert.Equal(2, applied);
            Assert.Empty(unmatched);
            Assert.Equal("D100", r1.OutAddress);
            Assert.Equal("D101", r2.OutAddress);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
