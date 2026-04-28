using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
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

        public RunRow(Scenario s)
        {
            Scenario = s;
            var meta = s.Meta ?? new SimulationResultSnapshotTypes.SimulationMeta();
            Name = meta.ScenarioName ?? "";
            RunDate = meta.RunDate.ToString("yyyy-MM-dd HH:mm:ss");
            Duration_s = meta.RunDuration_s;
            if (Microsoft.FSharp.Core.FSharpOption<SimulationResultSnapshotTypes.KpiThroughput>.get_IsSome(s.Throughput))
            {
                ThroughputPerHour = s.Throughput.Value.ThroughputPerHour;
                AvgCt = s.Throughput.Value.AverageCycleTime_s;
            }
            OEE = s.OeeItems.Count > 0 ? s.OeeItems.Average(o => o.OEE) : 0.0;
            PerTokenCount = s.PerTokenKpis.Count;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? p = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private readonly SimulationPanelState _sim;
    private readonly TechnicalDataTypes.TechnicalData _td;
    private readonly ObservableCollection<RunRow> _rows = new();
    private bool _suppressCheck;

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

        // CapturedRuns 가 비어있으면 (예: AASX 신규 import 후), 기존 SimulationResult 로 1줄 hydrate.
        var sources = _sim.CapturedRuns.ToList();
        if (sources.Count == 0
            && Microsoft.FSharp.Core.FSharpOption<Scenario>.get_IsSome(_td.SimulationResult))
        {
            sources.Add(_td.SimulationResult.Value);
        }

        foreach (var s in sources) _rows.Add(new RunRow(s));

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

    private void ShowDetails(Scenario scenario)
    {
        if (scenario == null)
        {
            DetailText.Text = "";
            PerTokenGrid.ItemsSource = null;
            CycleTimeGrid.ItemsSource = null;
            return;
        }

        var meta = scenario.Meta ?? new SimulationResultSnapshotTypes.SimulationMeta();
        var sb = new StringBuilder();
        sb.AppendLine($"Simulator: {meta.SimulatorName} v{meta.SimulatorVersion}");
        sb.AppendLine($"ModelHash: {meta.Ds2ModelHash}");
        sb.AppendLine($"Seed: {(Microsoft.FSharp.Core.FSharpOption<int>.get_IsSome(meta.Seed) ? meta.Seed.Value.ToString() : "(none)")}");
        sb.AppendLine();
        sb.AppendLine($"CycleTimes: {scenario.CycleTimes.Count} items, "
                    + $"Constraints: {scenario.Constraints.Count}, "
                    + $"Resources: {scenario.ResourceUtilizations.Count}, "
                    + $"OEE: {scenario.OeeItems.Count}, "
                    + $"PerToken: {scenario.PerTokenKpis.Count}");

        if (Microsoft.FSharp.Core.FSharpOption<SimulationResultSnapshotTypes.KpiThroughput>.get_IsSome(scenario.Throughput))
        {
            var t = scenario.Throughput.Value;
            sb.AppendLine($"Throughput: {FmtD(t.ThroughputPerHour)}/h, Avg CT={FmtD(t.AverageCycleTime_s)}s, Total Units={t.TotalUnitsProduced}");
        }

        if (Microsoft.FSharp.Core.FSharpOption<SimulationResultSnapshotTypes.KpiCapacity>.get_IsSome(scenario.Capacity))
        {
            var c = scenario.Capacity.Value;
            sb.AppendLine($"Capacity: design={FmtD(c.DesignCapacity)}, effective={FmtD(c.EffectiveCapacity)}, "
                        + $"actual={FmtD(c.ActualCapacity)}, util(eff)={FmtD(c.EffectiveUtilization_pct)}%");
        }

        if (scenario.PerTokenKpis.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("토큰 유형별 요약:");
            foreach (var p in scenario.PerTokenKpis)
                sb.AppendLine($"  {p.OriginName} ({p.SpecLabel}): {p.CompletedCount}/{p.InstanceCount} 완주, "
                            + $"Avg={FmtD(p.AvgTraversalTime_s)}s, TP/h={FmtD(p.ThroughputPerHour)}");
        }

        DetailText.Text = sb.ToString();
        PerTokenGrid.ItemsSource = scenario.PerTokenKpis;
        CycleTimeGrid.ItemsSource = scenario.CycleTimes;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        if (!DialogHelpers.Confirm(this, "누적된 시뮬레이션 결과를 모두 삭제하시겠습니까?", "확인")) return;
        _sim.ClearAllCapturedRuns();
        Reload();
    }
}
