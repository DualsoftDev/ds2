using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.Core;

namespace Promaker.Dialogs;

public partial class SimulationScenariosDialog
{
    /// <summary>현재 timeline 그릴 데이터 — 윈도우 리사이즈 시 다시 그리는 캐시.</summary>
    private CallTimelineModel? _currentTimeline;
    private bool _suppressGapRankChange;

    /// <summary>Work 차트의 한 행을 클릭하면 그 Work 의 Call 들의 시간 순서 Gantt 를 우측에 표시.</summary>
    private void WorkChartRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WorkChartRow row) return;

        var callNamesInWork = ResolveCallNamesForWork(row.WorkName);
        if (callNamesInWork.Count == 0)
        {
            ClearCallTimeline($"{row.WorkName} — 이 Work 에 등록된 Call 이 없습니다.");
            return;
        }

        var report = _sim.Report.CurrentReport();
        if (report is null || report.Entries is null)
        {
            ClearCallTimeline($"{row.WorkName} — 시뮬레이션 raw 리포트 없음 (시뮬을 먼저 실행해 주세요).");
            return;
        }

        var callEntries = report.Entries
            .Where(e => string.Equals(e.Type, "Call", StringComparison.Ordinal)
                        && callNamesInWork.Contains(e.Name))
            .ToList();
        if (callEntries.Count == 0)
        {
            ClearCallTimeline($"{row.WorkName} — Call 활동 기록 없음 (시뮬에서 호출되지 않았습니다).");
            return;
        }

        _currentTimeline = BuildCallTimeline(callEntries, report.Metadata.StartTime, report.Metadata.EndTime);
        CallDetailWorkName.Text =
            $"{row.WorkName}   —   Call {_currentTimeline.CallNames.Count} 개   "
            + $"기간 {_currentTimeline.TotalSeconds:F2}s   "
            + $"가장 긴 평균 동작 {_currentTimeline.LongestGoing_s:F2}s   "
            + $"Gap 후보 {_currentTimeline.TopGaps.Count} 개";
        CallDetailLegend.Visibility = Visibility.Visible;
        PopulateGapRankCombo();
        BuildCallTimelineUi();
    }

    /// <summary>현재 timeline 의 TopGaps 개수만큼 콤보박스 옵션 1순위 ~ N순위(최대 10) 채우기.</summary>
    private void PopulateGapRankCombo()
    {
        _suppressGapRankChange = true;
        try
        {
            GapRankCombo.Items.Clear();
            if (_currentTimeline is null || _currentTimeline.TopGaps.Count == 0)
            {
                GapRankCombo.IsEnabled = false;
                return;
            }

            GapRankCombo.IsEnabled = true;
            for (int i = 0; i < _currentTimeline.TopGaps.Count; i++)
            {
                var g = _currentTimeline.TopGaps[i];
                GapRankCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{i + 1}순위 ({g.Duration_s:F2}s)",
                    Tag = i,
                });
            }

            GapRankCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressGapRankChange = false;
        }
    }

    private void GapRankCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGapRankChange) return;
        BuildCallTimelineUi();
    }

    private void ClearCallTimeline(string message)
    {
        _currentTimeline = null;
        CallTimelineHost.Children.Clear();
        CallTimelineGapOverlay.Children.Clear();
        CallTimelineGapOverlay.ColumnDefinitions.Clear();
        CallTimelineGapOverlay.Visibility = Visibility.Collapsed;
        CallDetailWorkName.Text = message;
        CallDetailLegend.Visibility = Visibility.Collapsed;
    }

    private HashSet<string> ResolveCallNamesForWork(string workName)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var store = _sim.StoreReadOnly;
            var projects = Ds2.Core.Store.Queries.allProjects(store);
            if (projects.IsEmpty) return result;

            var work = Ds2.Core.Store.Queries.activeWorksOf(projects.Head.Id, store)
                .FirstOrDefault(w => string.Equals(w.Name, workName, StringComparison.Ordinal));
            if (work is null) return result;

            foreach (var call in Ds2.Core.Store.Queries.callsOf(work.Id, store))
                result.Add(call.Name);
        }
        catch
        {
        }

        return result;
    }

    /// <summary>Call 시간순 Gantt 데이터 모델.</summary>
    private sealed class CallTimelineModel
    {
        public required IReadOnlyList<string> CallNames { get; init; }
        public required IReadOnlyList<TimelineBar> Going { get; init; }
        public required TimelineBar? LongestGoingBar { get; init; }
        public required IReadOnlyList<TimelineBar> TopGaps { get; init; }
        public required DateTime StartTime { get; init; }
        public required DateTime EndTime { get; init; }
        public double TotalSeconds => (EndTime - StartTime).TotalSeconds;
        public double LongestGoing_s => LongestGoingBar is null ? 0.0 : LongestGoingBar.Duration_s;
        public double LongestGap_s => TopGaps.Count == 0 ? 0.0 : TopGaps[0].Duration_s;
    }

    /// <summary>Timeline 의 1개 막대 — 시작/길이(초) + 어느 행(Call) 인지.</summary>
    private sealed class TimelineBar
    {
        public required int RowIndex { get; init; }
        public required double Start_s { get; init; }
        public required double Duration_s { get; init; }
        public required string Tooltip { get; init; }
    }

    private static CallTimelineModel BuildCallTimeline(
        IReadOnlyList<Ds2.Runtime.Report.Model.ReportEntry> callEntries,
        DateTime simStart,
        DateTime simEnd)
    {
        var aggregated = callEntries
            .Select(entry =>
            {
                var goings = entry.Segments
                    .Where(s => string.Equals(s.State, "G", StringComparison.Ordinal)
                             && s.DurationSeconds > 0.0)
                    .ToList();
                double firstStart = goings.Count == 0
                    ? double.MaxValue
                    : (goings.Min(s => s.StartTime) - simStart).TotalSeconds;
                if (firstStart < 0.0 && firstStart > double.MinValue) firstStart = 0.0;
                double avgDur = goings.Count == 0 ? 0.0 : goings.Average(s => s.DurationSeconds);
                return new
                {
                    Entry = entry,
                    GoingCount = goings.Count,
                    FirstStart_s = firstStart,
                    AvgDuration_s = avgDur,
                };
            })
            .Where(x => x.GoingCount > 0)
            .OrderBy(x => x.FirstStart_s)
            .ThenBy(x => x.Entry.Name, StringComparer.Ordinal)
            .ToList();

        var callNames = aggregated.Select(x => x.Entry.Name).ToList();
        var bars = new List<TimelineBar>(aggregated.Count);
        TimelineBar? longestGoing = null;

        for (int row = 0; row < aggregated.Count; row++)
        {
            var x = aggregated[row];
            var bar = new TimelineBar
            {
                RowIndex = row,
                Start_s = x.FirstStart_s,
                Duration_s = x.AvgDuration_s,
                Tooltip =
                    $"{x.Entry.Name}\n"
                    + $"  사이클 수    : {x.GoingCount}\n"
                    + $"  첫 동작 시작 : +{x.FirstStart_s:F2}s\n"
                    + $"  평균 지속    : {x.AvgDuration_s:F2}s",
            };
            bars.Add(bar);
            if (longestGoing is null || bar.Duration_s > longestGoing.Duration_s)
                longestGoing = bar;
        }

        var allGaps = new List<(double gap_s, double prevEnd, string prevName, string nextName)>();
        for (int i = 1; i < bars.Count; i++)
        {
            var prev = bars[i - 1];
            var next = bars[i];
            var prevEnd = prev.Start_s + prev.Duration_s;
            var gap_s = next.Start_s - prevEnd;
            if (gap_s <= 0.0) continue;
            allGaps.Add((gap_s, prevEnd, aggregated[i - 1].Entry.Name, aggregated[i].Entry.Name));
        }

        var topGapsRaw = allGaps
            .OrderByDescending(g => g.gap_s)
            .Take(10)
            .ToList();

        var topGaps = new List<TimelineBar>(topGapsRaw.Count);
        for (int rank = 0; rank < topGapsRaw.Count; rank++)
        {
            var gap = topGapsRaw[rank];
            topGaps.Add(new TimelineBar
            {
                RowIndex = -1,
                Start_s = gap.prevEnd,
                Duration_s = gap.gap_s,
                Tooltip =
                    $"Call 간 Gap — {rank + 1}순위\n"
                    + $"  {gap.prevName} 종료 → {gap.nextName} 시작\n"
                    + $"  지속 : {gap.gap_s:F2}s",
            });
        }

        return new CallTimelineModel
        {
            CallNames = callNames,
            Going = bars,
            LongestGoingBar = longestGoing,
            TopGaps = topGaps,
            StartTime = simStart,
            EndTime = simEnd,
        };
    }

    /// <summary>
    /// 시간순 Call Gantt 차트 UI 빌드 — 라벨과 막대가 한 Grid 행 안에 들어가는 구조.
    /// 각 행 Grid 의 4개 컬럼 [Label 180px | Leading | Bar | Trailing] 중 뒤 3개는 star sizing.
    /// 모든 행의 star 합 = totalSec 로 동일 → 행 사이 시간 픽셀 스케일이 자동 일치.
    /// </summary>
    private void BuildCallTimelineUi()
    {
        CallTimelineHost.Children.Clear();
        CallTimelineGapOverlay.Children.Clear();
        CallTimelineGapOverlay.ColumnDefinitions.Clear();
        CallTimelineGapOverlay.Visibility = Visibility.Collapsed;

        if (_currentTimeline is null || _currentTimeline.Going.Count == 0) return;

        var totalSec = _currentTimeline.TotalSeconds;
        if (totalSec <= 0.0) totalSec = 1.0;

        const double labelColWidth = 180.0;
        const double rowHeight = 22.0;
        var minBarStar = Math.Max(totalSec * 0.015, 0.001);

        var selectedRank = -1;
        if (GapRankCombo?.SelectedItem is ComboBoxItem cbi && cbi.Tag is int idx)
            selectedRank = idx;

        if (selectedRank >= 0 && selectedRank < _currentTimeline.TopGaps.Count)
        {
            var gap = _currentTimeline.TopGaps[selectedRank];
            if (gap.Duration_s > 0.0)
            {
                CallTimelineGapOverlay.Visibility = Visibility.Visible;

                var gapGrid = new Grid { IsHitTestVisible = true };
                gapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelColWidth) });
                gapGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(Math.Max(0.001, gap.Start_s), GridUnitType.Star),
                });
                gapGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(Math.Max(0.001, gap.Duration_s), GridUnitType.Star),
                });
                gapGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(Math.Max(0.001, totalSec - gap.Start_s - gap.Duration_s), GridUnitType.Star),
                });

                var gapRect = new System.Windows.Shapes.Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(0x66, 0xFB, 0xBF, 0x24)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)),
                    StrokeThickness = 1,
                    ToolTip = gap.Tooltip,
                };
                Grid.SetColumn(gapRect, 2);
                gapGrid.Children.Add(gapRect);
                CallTimelineGapOverlay.Children.Add(gapGrid);
            }
        }

        for (int i = 0; i < _currentTimeline.Going.Count; i++)
        {
            var bar = _currentTimeline.Going[i];
            var name = i < _currentTimeline.CallNames.Count ? _currentTimeline.CallNames[i] : "";
            var isLongest = ReferenceEquals(bar, _currentTimeline.LongestGoingBar);

            var rowGrid = new Grid { Height = rowHeight };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelColWidth) });

            var leading = Math.Max(0.0, bar.Start_s);
            var width = Math.Max(minBarStar, bar.Duration_s);
            var trailing = Math.Max(0.001, totalSec - leading - width);

            rowGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.001, leading), GridUnitType.Star),
            });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(width, GridUnitType.Star),
            });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(trailing, GridUnitType.Star),
            });

            var label = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("PrimaryTextBrush"),
                FontSize = 12,
                ToolTip = name,
            };
            Grid.SetColumn(label, 0);
            rowGrid.Children.Add(label);

            var barRect = new Border
            {
                Background = isLongest
                    ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
                    : new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
                BorderBrush = isLongest ? new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)) : null,
                BorderThickness = new Thickness(isLongest ? 1 : 0),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = bar.Tooltip + (isLongest ? "\n  ★ 이 Work 의 가장 긴 동작" : ""),
            };
            Grid.SetColumn(barRect, 2);
            rowGrid.Children.Add(barRect);

            CallTimelineHost.Children.Add(rowGrid);
        }
    }

    private static IReadOnlyList<WorkChartRow> BuildWorkChartRows(
        IEnumerable<SimulationResultSnapshotTypes.KpiCycleTime> filtered)
    {
        var items = filtered.ToList();
        if (items.Count == 0) return Array.Empty<WorkChartRow>();

        double GTotal(SimulationResultSnapshotTypes.KpiCycleTime k) =>
            k.ActualCycleTime_s * Math.Max(0, k.CycleCount);

        var maxTotal = items.Max(k => GTotal(k) + Math.Max(0.0, k.IdleGapBetweenCycles_s));
        if (maxTotal <= 0.0) maxTotal = 1.0;

        return items
            .Select(k =>
            {
                var g = GTotal(k);
                var gap = Math.Max(0.0, k.IdleGapBetweenCycles_s);
                var empty = Math.Max(0.0, maxTotal - g - gap);
                GridLength Star(double v) =>
                    v <= 0.0
                        ? new GridLength(0.0001, GridUnitType.Star)
                        : new GridLength(v, GridUnitType.Star);

                var eff = k.EfficiencyRate_pct;
                Brush effColor;
                if (eff >= 95.0) effColor = new SolidColorBrush(Color.FromRgb(0xEF, 0x4B, 0x4B));
                else if (eff >= 70.0) effColor = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
                else effColor = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                effColor.Freeze();

                return new WorkChartRow
                {
                    WorkName = k.WorkName ?? "",
                    CyclesLabel = $"×{k.CycleCount}",
                    GoingWidth = Star(g),
                    IdleGapWidth = Star(gap),
                    EmptyWidth = Star(empty),
                    EfficiencyLabel = $"{eff:F1}%",
                    EfficiencyColor = effColor,
                    GoingTooltip = $"Going 합: {g:F2}s   ({k.CycleCount} 사이클 × 평균 {k.ActualCycleTime_s:F2}s)",
                    IdleGapTooltip = $"IdleGap 합: {gap:F2}s — 사이클 사이 대기 시간",
                    EfficiencyTooltip =
                        "Eff(%) = G / (G + IdleGap) × 100\n"
                        + "  • 95% ↑ : 시스템 한계 = 병목 (빨강)\n"
                        + "  • 70~95% : 약간 여유 (주황)\n"
                        + "  • 70% ↓ : 대기 多, 여유 (초록)",
                };
            })
            .OrderByDescending(r => r.GoingWidth.Value + r.IdleGapWidth.Value)
            .ToList();
    }

    /// <summary>같은 필터를 강타입 리스트로 반환 (chart 와 grid 모두에서 재사용).</summary>
    private List<SimulationResultSnapshotTypes.KpiCycleTime> FilterToActiveWorksTyped(
        IEnumerable<SimulationResultSnapshotTypes.KpiCycleTime> all)
    {
        if (_eligibleWorkNames is null) return all.ToList();
        return all.Where(k => _eligibleWorkNames.Contains(k.WorkName)).ToList();
    }

}
