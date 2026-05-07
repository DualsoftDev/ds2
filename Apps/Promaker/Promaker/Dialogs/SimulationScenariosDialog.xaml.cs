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
    private readonly TechnicalDataTypes.TechnicalData _td;
    private readonly ObservableCollection<RunRow> _rows = new();
    private bool _suppressCheck;
    /// <summary>
    /// "통계 집계 대상 Work" 집합 — Active 시스템에 속하면서 Call 이 ≥1 개인 Work 의 이름.
    /// AvgCT 계산, Work별 사이클 그리드 필터에 공통 적용.
    /// </summary>
    private System.Collections.Generic.HashSet<string>? _eligibleWorkNames;

    public SimulationScenariosDialog(SimulationPanelState sim, TechnicalDataTypes.TechnicalData td)
    {
        InitializeComponent();
        _sim = sim ?? throw new ArgumentNullException(nameof(sim));
        _td = td ?? throw new ArgumentNullException(nameof(td));
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
            && Microsoft.FSharp.Core.FSharpOption<Scenario>.get_IsSome(_td.SimulationResult))
        {
            sources.Add(_td.SimulationResult.Value);
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
        if (Microsoft.FSharp.Core.FSharpOption<Scenario>.get_IsSome(_td.SimulationResult))
        {
            var current = _td.SimulationResult.Value;
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
        _td.SimulationResult = Microsoft.FSharp.Core.FSharpOption<Scenario>.None;

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

