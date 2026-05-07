using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.Core;
using Promaker.ViewModels;
using Scenario = Ds2.Core.SimulationResultSnapshotTypes.SimulationScenario;

namespace Promaker.Dialogs;

/// <summary>
/// 누적된 시뮬 결과(<see cref="SimulationPanelState.CapturedRuns"/>) 를 그리드로 보여주고,
/// 체크박스로 단일 항목을 선택 → Project.TechnicalData.SimulationResult 갱신.
/// 디폴트는 최신(첫 행) 자동 선택.
/// </summary>
public partial class SimulationScenariosDialog : Window
{
    /// <summary>그리드 한 행을 표현하는 ViewModel — IsSelected 토글로 single-check 동작.</summary>
    public sealed class RunRow : INotifyPropertyChanged
    {
        public Scenario Scenario { get; }
        public string Name { get; }
        public string RunDate { get; }
        public double Duration_s { get; }
        public double ThroughputPerHour { get; }
        public double AvgCt { get; }
        public double OEE { get; }
        public int PerTokenCount { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public RunRow(Scenario s, System.Collections.Generic.HashSet<string>? eligibleWorkNames)
        {
            Scenario = s;
            var meta = s.Meta ?? new SimulationResultSnapshotTypes.SimulationMeta();
            Name = meta.ScenarioName ?? "";
            RunDate = meta.RunDate.ToString("yyyy-MM-dd HH:mm:ss");
            Duration_s = meta.RunDuration_s;
            if (Microsoft.FSharp.Core.FSharpOption<SimulationResultSnapshotTypes.KpiThroughput>.get_IsSome(s.Throughput))
                ThroughputPerHour = s.Throughput.Value.ThroughputPerHour;

            // Avg CT — Call 이 있는 active Work 만 대상으로 cycle 가중 평균.
            //   eligibleWorkNames == null (store 없음) 이면 fallback 으로 Throughput 의 AverageCycleTime_s 사용.
            AvgCt = ComputeFilteredAvgCt(s, eligibleWorkNames);

            // OEE — 동일 필터 (Active ∩ Call ≥ 1 Work) 의 단순 평균. 필터된 항목이 0 개면 전체 평균 fallback.
            OEE = ComputeFilteredOee(s, eligibleWorkNames);

            PerTokenCount = s.PerTokenKpis.Count;
        }

        private static double ComputeFilteredOee(
            Scenario s,
            System.Collections.Generic.HashSet<string>? eligibleWorkNames)
        {
            if (s.OeeItems.Count == 0) return 0.0;
            if (eligibleWorkNames is null)
                return s.OeeItems.Average(o => o.OEE);

            var matched = s.OeeItems.Where(o => eligibleWorkNames.Contains(o.ResourceName)).ToList();
            if (matched.Count > 0) return matched.Average(o => o.OEE);
            // Eligible Work 의 OEE 항목이 하나도 없으면 전체 평균 fallback (안전망).
            return s.OeeItems.Average(o => o.OEE);
        }

        private static double ComputeFilteredAvgCt(
            Scenario s,
            System.Collections.Generic.HashSet<string>? eligibleWorkNames)
        {
            if (eligibleWorkNames is null || s.CycleTimes.Count == 0)
            {
                return Microsoft.FSharp.Core.FSharpOption<SimulationResultSnapshotTypes.KpiThroughput>.get_IsSome(s.Throughput)
                    ? s.Throughput.Value.AverageCycleTime_s
                    : 0.0;
            }

            // CycleCount 로 가중 평균 — 사이클이 많은 Work 가 더 큰 가중치 가짐.
            double sumWeighted = 0.0;
            long totalCycles = 0;
            foreach (var k in s.CycleTimes)
            {
                if (!eligibleWorkNames.Contains(k.WorkName)) continue;
                if (k.CycleCount <= 0 || k.ActualCycleTime_s <= 0.0) continue;
                sumWeighted += k.ActualCycleTime_s * k.CycleCount;
                totalCycles += k.CycleCount;
            }
            return totalCycles > 0 ? sumWeighted / totalCycles : 0.0;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? p = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private readonly SimulationPanelState _sim;
    private readonly Project _project;
    private readonly ObservableCollection<RunRow> _rows = new();
    private bool _suppressCheck;
    /// <summary>
    /// "통계 집계 대상 Work" 집합 — Active 시스템에 속하면서 Call 이 ≥1 개인 Work 의 이름.
    /// AvgCT 계산, Work별 사이클 그리드 필터에 공통 적용.
    /// </summary>
    private System.Collections.Generic.HashSet<string>? _eligibleWorkNames;

    public SimulationScenariosDialog(SimulationPanelState sim, Project project)
    {
        InitializeComponent();
        _sim = sim ?? throw new ArgumentNullException(nameof(sim));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        RunsGrid.ItemsSource = _rows;
        Reload();
    }

    private static string FmtD(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private void Reload()
    {
        _rows.Clear();
        _eligibleWorkNames = BuildEligibleWorkNames();

        // CapturedRuns 가 비어있으면 (예: AASX 신규 import 후), 기존 SimulationResult 로 1줄 hydrate.
        var sources = _sim.CapturedRuns.ToList();
        if (sources.Count == 0
            && Microsoft.FSharp.Core.FSharpOption<Scenario>.get_IsSome(_project.SimulationResult))
        {
            sources.Add(_project.SimulationResult.Value);
        }

        foreach (var s in sources) _rows.Add(new RunRow(s, _eligibleWorkNames));

        if (_rows.Count == 0)
        {
            DetailText.Text = "(시뮬레이션 결과 없음)";
            PerTokenGrid.ItemsSource = null;
            CycleTimeGrid.ItemsSource = null;
            ClearButton.IsEnabled = false;
            return;
        }

        ClearButton.IsEnabled = true;

        // 디폴트: 현재 SimulationResult 와 같은 항목, 없으면 최신(=첫 행)
        RunRow? defaultRow = null;
        if (Microsoft.FSharp.Core.FSharpOption<Scenario>.get_IsSome(_project.SimulationResult))
        {
            var current = _project.SimulationResult.Value;
            defaultRow = _rows.FirstOrDefault(r => ReferenceEquals(r.Scenario, current));
        }
        defaultRow ??= _rows[0];

        _suppressCheck = true;
        defaultRow.IsSelected = true;
        _suppressCheck = false;

        RunsGrid.SelectedItem = defaultRow;
        ShowDetails(defaultRow.Scenario);
    }

    private void RunsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RunsGrid.SelectedItem is not RunRow row) return;
        SelectRow(row);
    }

    /// <summary>행을 단일 선택 상태로 만든다 (체크박스 자동 동기 + SimulationResult 적용 + 상세 표시).</summary>
    private void SelectRow(RunRow row)
    {
        if (_suppressCheck) return;
        _suppressCheck = true;
        foreach (var r in _rows)
            r.IsSelected = ReferenceEquals(r, row);
        _suppressCheck = false;

        _sim.ApplySelectedScenario(row.Scenario);
        ShowDetails(row.Scenario);
    }

    private void SaveCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressCheck) return;
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        if (cb.DataContext is not RunRow row) return;
        // 그리드 selection 도 동기 → IsSelected DataTrigger + Selection 양쪽에서 강조
        RunsGrid.SelectedItem = row;
        SelectRow(row);
    }

    private Scenario? _currentScenario;

    private void ShowDetails(Scenario scenario)
    {
        _currentScenario = scenario;
        if (scenario == null)
        {
            DetailText.Text = "";
            PerTokenGrid.ItemsSource = null;
            CycleTimeGrid.ItemsSource = null;
            WorkChartItems.ItemsSource = null;
            ClearCallTimeline("(Work 행을 클릭하면 그 Work 내부의 Call 동작 시간 막대를 표시합니다)");
            return;
        }

        DetailText.Text = BuildDetailText(scenario);
        PerTokenGrid.ItemsSource = scenario.PerTokenKpis;
        var filtered = FilterToActiveWorksTyped(scenario.CycleTimes);
        CycleTimeGrid.ItemsSource = filtered;
        WorkChartItems.ItemsSource = BuildWorkChartRows(filtered);
        ClearCallTimeline("(Work 행을 클릭하면 그 Work 내부의 Call 동작 시간 막대를 표시합니다)");
    }

    /// <summary>현재 timeline 그릴 데이터 — 윈도우 리사이즈 시 다시 그리는 캐시.</summary>
    private CallTimelineModel? _currentTimeline;

    /// <summary>Work 차트의 한 행을 클릭하면 그 Work 의 Call 들의 시간 순서 Gantt 를 우측에 표시.</summary>
    private void WorkChartRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WorkChartRow row) return;

        // store 에서 이 Work 의 Call 이름 집합 도출.
        var callNamesInWork = ResolveCallNamesForWork(row.WorkName);
        if (callNamesInWork.Count == 0)
        {
            ClearCallTimeline($"{row.WorkName} — 이 Work 에 등록된 Call 이 없습니다.");
            return;
        }

        // live SimulationReport 에서 해당 Call 들의 raw segments 추출.
        var report = _sim.CurrentReport();
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
        // SelectionChanged 가 BuildCallTimelineUi 를 다시 트리거하지 않도록 일시 차단.
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
                GapRankCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                {
                    Content = $"{i + 1}순위 ({g.Duration_s:F2}s)",
                    Tag = i,
                });
            }
            GapRankCombo.SelectedIndex = 0;  // 기본 1순위.
        }
        finally { _suppressGapRankChange = false; }
    }

    private bool _suppressGapRankChange;
    private void GapRankCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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

    private System.Collections.Generic.HashSet<string> ResolveCallNamesForWork(string workName)
    {
        var result = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        try
        {
            var store = _sim.StoreReadOnly;
            var projects = Ds2.Core.Store.Queries.allProjects(store);
            if (projects.IsEmpty) return result;
            var work = Ds2.Core.Store.Queries.activeWorksOf(projects.Head.Id, store)
                .FirstOrDefault(w => string.Equals(w.Name, workName, StringComparison.Ordinal));
            if (work is null) return result;
            foreach (var c in Ds2.Core.Store.Queries.callsOf(work.Id, store))
                result.Add(c.Name);
        }
        catch { /* best-effort */ }
        return result;
    }

    /// <summary>Call 시간순 Gantt 데이터 모델.</summary>
    private sealed class CallTimelineModel
    {
        public required IReadOnlyList<string> CallNames { get; init; }
        public required IReadOnlyList<TimelineBar> Going { get; init; }
        public required TimelineBar? LongestGoingBar { get; init; }
        /// <summary>Call 간 Gap 중 1순위(가장 긴) + 2순위 — 시간순 정렬 후 인접 막대 사이의 dead time.</summary>
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

    /// <summary>
    /// Call entries → 각 Call 당 1 막대 (= Going 평균 지속 시간) 로 단순화.
    /// 행 순서는 그 Call 의 첫 Going 시작 시각 오름차순 → 좌상단에서 우하단으로 bar 가 cascade.
    ///
    /// 강조:
    ///   • 가장 긴 평균 동작 (avgDuration 최대) 1개 막대 → 빨강.
    ///   • 가장 긴 Call 간 Gap → 시간순 정렬한 두 인접 행의 (next.firstStart − prev.barEnd) 최대 구간.
    /// </summary>
    private static CallTimelineModel BuildCallTimeline(
        IReadOnlyList<Ds2.Runtime.Report.Model.ReportEntry> callEntries,
        DateTime simStart,
        DateTime simEnd)
    {
        // 각 Call 의 G 세그먼트 집계.
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
                // simStart 이전(homing 등)에 시작한 Call 은 0 으로 클램프 — 음수면 막대가 0 폭으로 클립되어 안 보임.
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
            // 한 번도 동작하지 않은 Call (G 세그먼트 0) 은 제외.
            .Where(x => x.GoingCount > 0)
            // 첫 Going 시작 시각 오름차순 — 좌상단 → 우하단 cascade 효과.
            .OrderBy(x => x.FirstStart_s)
            .ThenBy(x => x.Entry.Name, StringComparer.Ordinal)
            .ToList();

        var callNames = aggregated.Select(x => x.Entry.Name).ToList();

        // 행마다 1 막대.
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

        // 인접한 두 행의 (next.firstStart − prev.barEnd) > 0 인 모든 gap 수집 후 Top 2 선택.
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
            var g = topGapsRaw[rank];
            topGaps.Add(new TimelineBar
            {
                RowIndex = -1,
                Start_s = g.prevEnd,
                Duration_s = g.gap_s,
                Tooltip =
                    $"Call 간 Gap — {rank + 1}순위\n"
                    + $"  {g.prevName} 종료 → {g.nextName} 시작\n"
                    + $"  지속 : {g.gap_s:F2}s",
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
    /// 모든 행의 star 합 = totalSec 로 동일 → 행 사이 시간 픽셀 스케일이 자동 일치 (어긋남 불가).
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

        const double LabelColWidth = 180.0;
        const double RowHeight = 22.0;
        // 시간이 너무 짧은 막대도 시각적으로 보이도록, BarStar 의 minimum floor (총 길이의 1.5%).
        var minBarStar = Math.Max(totalSec * 0.015, 0.001);

        // ── Gap 하이라이트 overlay — 콤보박스에서 선택된 단일 순위만 표시. ──
        var selectedRank = -1;
        if (GapRankCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag is int idx)
            selectedRank = idx;

        if (selectedRank >= 0 && selectedRank < _currentTimeline.TopGaps.Count)
        {
            var gap = _currentTimeline.TopGaps[selectedRank];
            if (gap.Duration_s > 0.0)
            {
                CallTimelineGapOverlay.Visibility = Visibility.Visible;

                var gapGrid = new Grid { IsHitTestVisible = true };
                gapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColWidth) });
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

        // ── 행마다 단일 Grid (라벨 + 막대 같은 행) ──
        for (int i = 0; i < _currentTimeline.Going.Count; i++)
        {
            var bar = _currentTimeline.Going[i];
            var name = i < _currentTimeline.CallNames.Count ? _currentTimeline.CallNames[i] : "";
            var isLongest = ReferenceEquals(bar, _currentTimeline.LongestGoingBar);

            var rowGrid = new Grid { Height = RowHeight };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColWidth) });

            // Leading (시작 전 빈 공간) / Bar (평균 동작) / Trailing (끝나고 남은 시간).
            var leading = Math.Max(0.0, bar.Start_s);
            var width = Math.Max(minBarStar, bar.Duration_s);  // 최소 1.5% star 보장
            var trailing = Math.Max(0.001, totalSec - leading - width);

            // Star value 0 은 폭 0 으로 클립되므로 0.001 이상 보장.
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

    /// <summary>
    /// Work 분석 차트 행 생성 — Cycles, G(Going), IdleGap 시간을 막대 비율로 표현.
    /// 모든 행은 동일한 max(G+IdleGap) 기준으로 정규화 → 시각적 비교 가능.
    /// </summary>
    private static IReadOnlyList<WorkChartRow> BuildWorkChartRows(
        IEnumerable<SimulationResultSnapshotTypes.KpiCycleTime> filtered)
    {
        var items = filtered.ToList();
        if (items.Count == 0) return Array.Empty<WorkChartRow>();

        // G_total = ActualCycleTime × Cycles  (Going 시간 합).
        double GTotal(SimulationResultSnapshotTypes.KpiCycleTime k) =>
            k.ActualCycleTime_s * Math.Max(0, k.CycleCount);

        var maxTotal = items.Max(k => GTotal(k) + Math.Max(0.0, k.IdleGapBetweenCycles_s));
        if (maxTotal <= 0.0) maxTotal = 1.0;

        // 정렬: 총 활동 시간 (G+IdleGap) 내림차순.
        return items
            .Select(k =>
            {
                var g = GTotal(k);
                var gap = Math.Max(0.0, k.IdleGapBetweenCycles_s);
                var empty = Math.Max(0.0, maxTotal - g - gap);
                // GridLength star 단위로 비율 표현. 0 인 셀은 0-star 로 두면 폭 0 이 됨.
                System.Windows.GridLength Star(double v) =>
                    v <= 0.0 ? new System.Windows.GridLength(0.0001, System.Windows.GridUnitType.Star)
                             : new System.Windows.GridLength(v, System.Windows.GridUnitType.Star);

                var eff = k.EfficiencyRate_pct;
                Brush effColor;
                if (eff >= 95.0) effColor = new SolidColorBrush(Color.FromRgb(0xEF, 0x4B, 0x4B));      // 빨강 — 병목
                else if (eff >= 70.0) effColor = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)); // 주황
                else effColor = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));                  // 초록 — 여유
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
                        "Eff(%) = G / (G + IdleGap) × 100\n" +
                        "  • 95% ↑ : 시스템 한계 = 병목 (빨강)\n" +
                        "  • 70~95% : 약간 여유 (주황)\n" +
                        "  • 70% ↓ : 대기 多, 여유 (초록)",
                };
            })
            .OrderByDescending(r => RowTotal(r, items, maxTotal))
            .ToList();

        // 정렬용 — row 의 G+Gap 비율 (star 값 합) 으로 비교.
        static double RowTotal(WorkChartRow r,
            List<SimulationResultSnapshotTypes.KpiCycleTime> _, double __) =>
            r.GoingWidth.Value + r.IdleGapWidth.Value;
    }

    /// <summary>같은 필터를 강타입 리스트로 반환 (chart 와 grid 모두에서 재사용).</summary>
    private List<SimulationResultSnapshotTypes.KpiCycleTime> FilterToActiveWorksTyped(
        IEnumerable<SimulationResultSnapshotTypes.KpiCycleTime> all)
    {
        if (_eligibleWorkNames is null) return all.ToList();
        return all.Where(k => _eligibleWorkNames.Contains(k.WorkName)).ToList();
    }

    /// <summary>요약 탭에 표시 + 복사 가능한 가독성 좋은 텍스트 생성.</summary>
    private static string BuildDetailText(Scenario scenario)
    {
        var meta = scenario.Meta ?? new SimulationResultSnapshotTypes.SimulationMeta();
        var sb = new StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine($"  시나리오: {meta.ScenarioName}");
        sb.AppendLine($"  실행 시각: {meta.RunDate:yyyy-MM-dd HH:mm:ss}    Duration: {FmtD(meta.RunDuration_s)} s");
        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine();

        // ── 메타 ──────────────────────────────────────────────────
        sb.AppendLine("[ 시뮬레이터 정보 ]");
        sb.AppendLine($"  Simulator   : {meta.SimulatorName} v{meta.SimulatorVersion}");
        sb.AppendLine($"  ModelHash   : {meta.Ds2ModelHash}");
        sb.AppendLine($"  Seed        : {(Microsoft.FSharp.Core.FSharpOption<int>.get_IsSome(meta.Seed) ? meta.Seed.Value.ToString() : "(none)")}");
        sb.AppendLine();

        // ── 처리량 (Throughput) ───────────────────────────────────
        if (Microsoft.FSharp.Core.FSharpOption<SimulationResultSnapshotTypes.KpiThroughput>.get_IsSome(scenario.Throughput))
        {
            var t = scenario.Throughput.Value;
            sb.AppendLine("[ 처리량 (Throughput) ]");
            sb.AppendLine($"  완주 토큰   : {t.TotalUnitsProduced} 개");
            sb.AppendLine($"  시간당      : {FmtD(t.ThroughputPerHour)} /h");
            sb.AppendLine($"  평균 CT     : {FmtD(t.AverageCycleTime_s)} s    (모든 Work 사이클 평균)");
            sb.AppendLine();
        }

        // ── 용량 (Capacity) ───────────────────────────────────────
        if (Microsoft.FSharp.Core.FSharpOption<SimulationResultSnapshotTypes.KpiCapacity>.get_IsSome(scenario.Capacity))
        {
            var c = scenario.Capacity.Value;
            sb.AppendLine("[ 용량 (Capacity) ]");
            sb.AppendLine($"  Design      : {FmtD(c.DesignCapacity)}");
            sb.AppendLine($"  Effective   : {FmtD(c.EffectiveCapacity)}");
            sb.AppendLine($"  Actual      : {FmtD(c.ActualCapacity)}");
            sb.AppendLine($"  Util(eff)   : {FmtD(c.EffectiveUtilization_pct)} %");
            sb.AppendLine();
        }

        // ── 데이터 수집 통계 ───────────────────────────────────────
        sb.AppendLine("[ 데이터 수집 ]");
        sb.AppendLine($"  CycleTimes  : {scenario.CycleTimes.Count} 항목");
        sb.AppendLine($"  PerToken    : {scenario.PerTokenKpis.Count} 종류");
        sb.AppendLine($"  OEE 항목    : {scenario.OeeItems.Count}");
        sb.AppendLine($"  Constraints : {scenario.Constraints.Count}");
        sb.AppendLine($"  Resources   : {scenario.ResourceUtilizations.Count}");
        sb.AppendLine();

        // ── 토큰 유형별 ───────────────────────────────────────────
        if (scenario.PerTokenKpis.Count > 0)
        {
            sb.AppendLine("[ 토큰 유형별 요약 ]   (Source 생성 → Sink 소멸)");
            sb.AppendLine();

            int idx = 0;
            foreach (var p in scenario.PerTokenKpis)
            {
                idx++;
                sb.AppendLine($"  {idx}. {p.OriginName}  ({p.SpecLabel})");
                sb.AppendLine($"     완주        : {p.CompletedCount} / {p.InstanceCount}");
                sb.AppendLine($"     통과 시간   : Avg {FmtD(p.AvgTraversalTime_s)} s    Min {FmtD(p.MinTraversalTime_s)} s    Max {FmtD(p.MaxTraversalTime_s)} s");
                sb.AppendLine($"     TP/h        : {FmtD(p.ThroughputPerHour)}");

                if (p.WorkBreakdown != null && p.WorkBreakdown.Count > 0)
                {
                    var sumAvg = p.WorkBreakdown.Sum(b => b.AvgGoingTime_s);
                    sb.AppendLine($"     경유 Work   : {p.WorkBreakdown.Count} 개   (Σ avg ≈ {FmtD(sumAvg)} s)");
                    foreach (var b in p.WorkBreakdown)
                    {
                        sb.AppendLine($"        • {b.WorkName,-32}  visits={b.VisitCount,-3}  avgGoing={FmtD(b.AvgGoingTime_s)} s");
                    }
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private void CopyDetail_Click(object sender, RoutedEventArgs e)
    {
        var text = DetailText.Text ?? "";
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            CopyHintText.Text = "✓ 클립보드에 복사됨";
        }
        catch (Exception ex)
        {
            CopyHintText.Text = $"복사 실패: {ex.Message}";
        }
    }

    private System.Collections.IEnumerable FilterToActiveWorks(
        System.Collections.Generic.IEnumerable<SimulationResultSnapshotTypes.KpiCycleTime> all)
    {
        if (_eligibleWorkNames is null) return all;
        return all.Where(k => _eligibleWorkNames.Contains(k.WorkName)).ToList();
    }

    /// <summary>
    /// 통계 집계 대상 Work 이름 집합 — Active 시스템에 속하고 Call 을 ≥1 개 가진 Work.
    /// store/project 가 없으면 null 반환 (호출부에서 fallback 처리).
    /// </summary>
    private System.Collections.Generic.HashSet<string>? BuildEligibleWorkNames()
    {
        try
        {
            var store = _sim.StoreReadOnly;
            var projects = Ds2.Core.Store.Queries.allProjects(store);
            if (projects.IsEmpty) return null;

            var set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var w in Ds2.Core.Store.Queries.activeWorksOf(projects.Head.Id, store))
            {
                var calls = Ds2.Core.Store.Queries.callsOf(w.Id, store);
                if (!calls.IsEmpty)
                    set.Add(w.Name);
            }
            return set;
        }
        catch { return null; }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        if (!DialogHelpers.Confirm(this, "누적된 시뮬레이션 결과를 모두 삭제하시겠습니까?\n(시나리오·토큰별·Work별 모두 초기화)", "확인")) return;

        // 누적 Run 큐 초기화
        _sim.ClearAllCapturedRuns();

        // TechnicalData.SimulationResult 초기화 — 그렇지 않으면 Reload 가 이 값으로 1줄 hydrate 하여
        //   토큰별/Work별 그리드가 비워지지 않음.
        _project.SimulationResult = Microsoft.FSharp.Core.FSharpOption<Scenario>.None;

        // 상세 그리드 즉시 비우기 (Reload 의 빈-상태 분기가 이미 처리하지만 명시적으로 안전하게)
        DetailText.Text = "(시뮬레이션 결과 없음)";
        PerTokenGrid.ItemsSource = null;
        CycleTimeGrid.ItemsSource = null;
        WorkChartItems.ItemsSource = null;
        ClearCallTimeline("(시뮬레이션 결과 없음)");

        Reload();
    }
}

/// <summary>Work 분석 차트의 한 행 — 막대 비율은 GridLength(*) 로 표현.</summary>
public sealed class WorkChartRow
{
    public string WorkName { get; set; } = "";
    public string CyclesLabel { get; set; } = "";
    public System.Windows.GridLength GoingWidth { get; set; }
    public System.Windows.GridLength IdleGapWidth { get; set; }
    public System.Windows.GridLength EmptyWidth { get; set; }
    public string EfficiencyLabel { get; set; } = "";
    public System.Windows.Media.Brush EfficiencyColor { get; set; } =
        System.Windows.Media.Brushes.White;
    public string GoingTooltip { get; set; } = "";
    public string IdleGapTooltip { get; set; } = "";
    public string EfficiencyTooltip { get; set; } = "";
}

