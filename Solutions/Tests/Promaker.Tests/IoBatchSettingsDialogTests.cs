using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Ds2.Core.Store;
using Promaker.Dialogs;
using Xunit;

namespace Promaker.Tests;

public sealed class IoBatchSettingsDialogTests
{
    private static IoBatchRow MakeRow(string flow, bool selected = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(),
            flow: flow, work: "W", device: "D", api: "A",
            inAddress: "%IX0.0.0", inSymbol: "in",
            outAddress: "%QX0.0.0", outSymbol: "out")
        {
            IsSelected = selected,
        };

    /// <summary>
    /// 핵심 로직: 다중 선택 상태에서 한 행 체크박스를 토글 → 선택된 모든 행에 동일 상태 적용.
    /// </summary>
    [Fact]
    public void ApplyCheckStateToSelectedRows_propagates_state_to_all_selected()
    {
        StaTestRunner.Run(() =>
        {
            var rows = new List<IoBatchRow>
            {
                MakeRow("F1"),
                MakeRow("F2"),
                MakeRow("F3"),
                MakeRow("F4"),
            };

            var grid = new DataGrid
            {
                ItemsSource = rows,
                SelectionMode = DataGridSelectionMode.Extended,
            };
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(rows[0]);
            grid.SelectedItems.Add(rows[1]);
            grid.SelectedItems.Add(rows[2]);

            // 행 0 의 체크박스가 체크된 시나리오 — 선택된 0,1,2 모두 IsSelected=true 가 되어야 함.
            BatchDialogHelper.ApplyCheckStateToSelectedRows(grid, rows[0], isChecked: true);

            Assert.True(rows[0].IsSelected);
            Assert.True(rows[1].IsSelected);
            Assert.True(rows[2].IsSelected);
            Assert.False(rows[3].IsSelected); // 선택 안 된 행은 영향 없음

            // 다시 unchecked 로 토글 → 선택된 행 모두 false.
            BatchDialogHelper.ApplyCheckStateToSelectedRows(grid, rows[1], isChecked: false);

            Assert.False(rows[0].IsSelected);
            Assert.False(rows[1].IsSelected);
            Assert.False(rows[2].IsSelected);
        });
    }

    /// <summary>
    /// 단일 선택 (또는 무선택) 시 → anchor 행만 상태 변경, 다른 행은 영향 없음.
    /// </summary>
    [Fact]
    public void ApplyCheckStateToSelectedRows_single_selection_only_changes_anchor()
    {
        StaTestRunner.Run(() =>
        {
            var rows = new List<IoBatchRow>
            {
                MakeRow("F1"),
                MakeRow("F2"),
            };

            var grid = new DataGrid
            {
                ItemsSource = rows,
                SelectionMode = DataGridSelectionMode.Extended,
            };
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(rows[0]);

            BatchDialogHelper.ApplyCheckStateToSelectedRows(grid, rows[0], isChecked: true);

            Assert.True(rows[0].IsSelected);
            Assert.False(rows[1].IsSelected);
        });
    }

    /// <summary>
    /// anchor 행이 선택 집합에 포함돼 있지 않으면 anchor 만 변경 (다중 선택 토글 제스처가 아님).
    /// </summary>
    [Fact]
    public void ApplyCheckStateToSelectedRows_anchor_not_in_selection_only_changes_anchor()
    {
        StaTestRunner.Run(() =>
        {
            var rows = new List<IoBatchRow>
            {
                MakeRow("F1"),
                MakeRow("F2"),
                MakeRow("F3"),
            };

            var grid = new DataGrid
            {
                ItemsSource = rows,
                SelectionMode = DataGridSelectionMode.Extended,
            };
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(rows[0]);
            grid.SelectedItems.Add(rows[1]);

            // anchor (rows[2]) 는 선택 집합에 없음 — 자기 자신만 변경.
            BatchDialogHelper.ApplyCheckStateToSelectedRows(grid, rows[2], isChecked: true);

            Assert.False(rows[0].IsSelected);
            Assert.False(rows[1].IsSelected);
            Assert.True(rows[2].IsSelected);
        });
    }

    /// <summary>
    /// IoBatchSettingsDialog 인스턴스 생성 → 다중 선택 후 RowCheckBox_Click 경로가
    /// 동일하게 동작하는지 확인 (통합 테스트). private 핸들러는 RoutedEvent 대신
    /// BatchDialogHelper 호출로 검증 — 핸들러가 호출하는 정확히 그 메서드.
    /// </summary>
    [Fact]
    public void Dialog_row_checkbox_applies_state_to_all_selected_rows()
    {
        StaTestRunner.Run(() =>
        {
            var store = DsStore.empty();
            var seedRows = new[]
            {
                MakeRow("F1"),
                MakeRow("F2"),
                MakeRow("F3"),
            };

            var dlg = new IoBatchSettingsDialog(store, seedRows);

            var gridField = typeof(IoBatchSettingsDialog)
                .GetField("IoGrid",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(gridField);
            var grid = (DataGrid)gridField!.GetValue(dlg)!;
            Assert.NotNull(grid);

            // 다이얼로그가 _rows 를 ObservableCollection 으로 복제하므로 grid 의 항목으로 작업.
            var displayed = grid.Items.Cast<IoBatchRow>().ToList();
            Assert.Equal(3, displayed.Count);

            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(displayed[0]);
            grid.SelectedItems.Add(displayed[1]);

            BatchDialogHelper.ApplyCheckStateToSelectedRows(grid, displayed[0], isChecked: true);

            Assert.True(displayed[0].IsSelected);
            Assert.True(displayed[1].IsSelected);
            Assert.False(displayed[2].IsSelected);

            dlg.Close();
        });
    }
}
