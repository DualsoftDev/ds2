using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Reverse.Studio.Models;
using Ds2.Reverse.Studio.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Ds2.Reverse.Studio.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // Case selection
    [ObservableProperty] private bool isCaseInline = true;
    [ObservableProperty] private bool isCaseDag = false;
    [ObservableProperty] private bool isCaseMultiFlow = false;
    [ObservableProperty] private bool isCaseBranch = false;
    [ObservableProperty] private bool isCaseRecycle = false;
    [ObservableProperty] private bool isCasePlcCell = false;
    [ObservableProperty] private bool isCaseCapacity = false;
    [ObservableProperty] private bool isCaseAdversarial = false;

    // Case A params
    [ObservableProperty] private int nStages = 6;
    [ObservableProperty] private int capacity = 2;
    [ObservableProperty] private int lagMs = 300;
    [ObservableProperty] private int jitterMs = 20;

    // Case B params
    [ObservableProperty] private int nCalls = 12;
    [ObservableProperty] private double density = 0.25;
    [ObservableProperty] private double groupProb = 0.1;

    // Case C params
    [ObservableProperty] private int nFlows = 3;
    [ObservableProperty] private int stagesPerFlow = 4;

    // Case D params
    [ObservableProperty] private int nBranches = 3;
    [ObservableProperty] private double branchEntropy = 0.5;

    // Case E params
    [ObservableProperty] private int recycleStages = 5;
    [ObservableProperty] private double recycleProbability = 0.15;

    // Case F params
    [ObservableProperty] private bool plcUseRobot = true;
    [ObservableProperty] private bool plcUseConveyor = true;
    [ObservableProperty] private bool plcUseJig = true;

    // Case G params
    [ObservableProperty] private int capMinTokens = 1;
    [ObservableProperty] private int capMaxTokens = 5;

    // Case H params
    [ObservableProperty] private int advSpuriousCount = 3;
    [ObservableProperty] private double advNoiseLevel = 0.3;

    // Common
    [ObservableProperty] private int seed = 42;
    [ObservableProperty] private bool randomSeed = true;   // Generate 마다 새 seed
    [ObservableProperty] private int nCycles = 60;
    [ObservableProperty] private long cycleMs = 4000L;
    [ObservableProperty] private bool autoRun = true;
    [ObservableProperty] private bool autoTuneThreshold = false;
    /// <summary>시뮬레이션 cycle 사이 지연 (ms). 0 = 즉시.</summary>
    [ObservableProperty] private int simStepDelayMs = 50;
    [ObservableProperty] private int simProgressCycle = 0;

    // Status
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private string modelSummary = "no model";
    [ObservableProperty] private string simSummary = "not simulated";
    [ObservableProperty] private string reverseSummary = "not reversed";

    // Chart series (timeline)
    [ObservableProperty]
    private ObservableCollection<ISeries> timelineSeries = new();
    [ObservableProperty]
    private ObservableCollection<Axis> timelineXAxes = new()
    {
        new Axis { Name = "Time (ms)", LabelsPaint = new SolidColorPaint(SKColors.Gray) }
    };
    [ObservableProperty]
    private ObservableCollection<Axis> timelineYAxes = new()
    {
        new Axis { Name = "Call (lane)", LabelsPaint = new SolidColorPaint(SKColors.Gray) }
    };

    // Diff table
    [ObservableProperty] private ObservableCollection<ArrowDiff> diffs = new();
    [ObservableProperty] private string anomalySummary = "no anomalies";

    // Model graph data (Canvas 가 binding 으로 그릴 수도 있음 — 간단히 텍스트 dump)
    [ObservableProperty] private string modelGraphText = "(no model)";

    // Detection metrics
    [ObservableProperty] private double metricPrecision;
    [ObservableProperty] private double metricRecall;
    [ObservableProperty] private double metricF1;
    [ObservableProperty] private string metricCounts = "";

    private GeneratedModel? _currentModel;
    private List<CapturedEventRow>? _currentEvents;

    public MainViewModel()
    {
        if (AutoRun)
        {
            GenerateCommand.Execute(null);
        }
    }

    private bool _suppressCaseSync;
    private void SetCaseExclusive(string which)
    {
        if (_suppressCaseSync) return;
        _suppressCaseSync = true;
        try
        {
            if (which != "Inline") IsCaseInline = false;
            if (which != "Dag") IsCaseDag = false;
            if (which != "MultiFlow") IsCaseMultiFlow = false;
            if (which != "Branch") IsCaseBranch = false;
            if (which != "Recycle") IsCaseRecycle = false;
            if (which != "PlcCell") IsCasePlcCell = false;
            if (which != "Capacity") IsCaseCapacity = false;
            if (which != "Adversarial") IsCaseAdversarial = false;
        }
        finally { _suppressCaseSync = false; }
    }
    partial void OnIsCaseInlineChanged(bool value) { if (value) SetCaseExclusive("Inline"); }
    partial void OnIsCaseDagChanged(bool value) { if (value) SetCaseExclusive("Dag"); }
    partial void OnIsCaseMultiFlowChanged(bool value) { if (value) SetCaseExclusive("MultiFlow"); }
    partial void OnIsCaseBranchChanged(bool value) { if (value) SetCaseExclusive("Branch"); }
    partial void OnIsCaseRecycleChanged(bool value) { if (value) SetCaseExclusive("Recycle"); }
    partial void OnIsCasePlcCellChanged(bool value) { if (value) SetCaseExclusive("PlcCell"); }
    partial void OnIsCaseCapacityChanged(bool value) { if (value) SetCaseExclusive("Capacity"); }
    partial void OnIsCaseAdversarialChanged(bool value) { if (value) SetCaseExclusive("Adversarial"); }

    private ModelCase SelectedCase =>
        IsCaseAdversarial ? ModelCase.AdversarialMix
      : IsCaseCapacity ? ModelCase.CapacityVar
      : IsCasePlcCell ? ModelCase.PlcCell
      : IsCaseRecycle ? ModelCase.RecycleLoop
      : IsCaseMultiFlow ? ModelCase.MultiFlow
      : IsCaseBranch ? ModelCase.Branch
      : IsCaseDag ? ModelCase.StandaloneDag
      : ModelCase.InlineLine;

    [RelayCommand]
    private void Generate()
    {
        try
        {
            // Random seed 옵션: Generate 마다 새 seed 발생
            if (RandomSeed)
            {
                Seed = Random.Shared.Next(1, 1_000_000);
            }

            var opts = new GeneratorOptions(
                Case: SelectedCase,
                Seed: Seed,
                NStages: NStages,
                Capacity: Capacity,
                LagMs: LagMs,
                JitterMs: JitterMs,
                NCalls: NCalls,
                Density: Density,
                GroupProb: GroupProb,
                NFlows: NFlows,
                StagesPerFlow: StagesPerFlow,
                NBranches: NBranches,
                BranchEntropy: BranchEntropy,
                RecycleStages: RecycleStages,
                RecycleProbability: RecycleProbability,
                PlcUseRobot: PlcUseRobot,
                PlcUseConveyor: PlcUseConveyor,
                PlcUseJig: PlcUseJig,
                CapMinTokens: CapMinTokens,
                CapMaxTokens: CapMaxTokens,
                AdvSpuriousCount: AdvSpuriousCount,
                AdvNoiseLevel: AdvNoiseLevel);

            Status = $"⚙ Generating {SelectedCase} (seed={Seed}) ...";
            System.Windows.Application.Current?.Dispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.Render);

            _currentModel = GeneratorFactory.Generate(opts);
            UpdateModelView();
            Status = $"✓ Generated {_currentModel.CaseName} (seed={Seed}, "
                   + $"works={_currentModel.Store.Works.Count}, "
                   + $"calls={_currentModel.Store.Calls.Count}, "
                   + $"arrows={_currentModel.GroundTruth.Count})";
            // Generate 시 이전 차트/리포트 초기화 — Auto run 이면 즉시 다시 채워짐
            _currentEvents = null;
            TimelineSeries = new ObservableCollection<ISeries>();
            Diffs.Clear();
            MetricPrecision = 0;
            MetricRecall = 0;
            MetricF1 = 0;
            MetricCounts = "";
            SimSummary = "not simulated";
            ReverseSummary = "not reversed";

            if (AutoRun)
            {
                SimulateCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            Status = $"Generate failed: {ex.Message}";
        }
    }

    private int _simRunCounter = 0;

    [RelayCommand]
    private async Task SimulateAsync()
    {
        if (_currentModel == null) { Status = "⚠ Generate 먼저 — model 없음"; return; }
        try
        {
            _simRunCounter++;
            var simSeed = Random.Shared.Next(1, 1_000_000);
            Status = $"▶ Simulating run #{_simRunCounter} — {NCycles} cycles, delay={SimStepDelayMs}ms ...";

            // 1) 전체 events 미리 계산 (deterministic, cycle 별 분류용)
            var allEvents = SimulationService.Simulate(
                _currentModel, NCycles, CycleMs, simSeed, LagMs, JitterMs);

            // 2) Lane 인덱스 (이름 정렬)
            var laneByName = allEvents.Select(e => e.Name).Distinct().OrderBy(n => n)
                .Select((n, i) => (n, i))
                .ToDictionary(t => t.n, t => t.i);
            var labels = laneByName.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray();

            // 3) Lane 별 ObservableCollection<ObservablePoint?> — null 로 line break
            //    각 event: 2 points (T, lane) → (EndT, lane), 그 다음 null 로 끊기
            var pointsByLane = new Dictionary<int, ObservableCollection<ObservablePoint?>>();
            foreach (var lane in laneByName.Values)
                pointsByLane[lane] = new ObservableCollection<ObservablePoint?>();

            TimelineSeries = new ObservableCollection<ISeries>();
            var palette = new[]
            {
                SKColors.SteelBlue, SKColors.OrangeRed, SKColors.MediumSeaGreen,
                SKColors.DarkViolet, SKColors.Goldenrod, SKColors.CadetBlue,
                SKColors.HotPink, SKColors.SaddleBrown, SKColors.Teal,
                SKColors.IndianRed, SKColors.DarkTurquoise, SKColors.MediumPurple
            };
            foreach (var kv in laneByName.OrderBy(k => k.Value))
            {
                var col = palette[kv.Value % palette.Length];
                TimelineSeries.Add(new LineSeries<ObservablePoint?>
                {
                    Name = kv.Key,
                    Values = pointsByLane[kv.Value],
                    GeometrySize = 0,                                  // 점 안 그림 (line 만)
                    Stroke = new SolidColorPaint(col, 10),             // 굵은 bar 효과
                    Fill = null,
                    LineSmoothness = 0,
                    EnableNullSplitting = true                          // null 에서 line 끊기
                });
            }

            TimelineYAxes = new ObservableCollection<Axis>
            {
                new Axis { Name = "Call", Labels = labels,
                           LabelsPaint = new SolidColorPaint(SKColors.Gray), MinStep = 1 }
            };
            TimelineXAxes = new ObservableCollection<Axis>
            {
                new Axis { Name = "Time (ms)", LabelsPaint = new SolidColorPaint(SKColors.Gray) }
            };

            // 4) Cycle 단위 점진 플로팅 — 각 event 의 (start, end) line segment
            var eventsByCycle = allEvents
                .GroupBy(e => e.T / CycleMs)
                .OrderBy(g => g.Key)
                .ToList();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int cycleIdx = 0;
            foreach (var group in eventsByCycle)
            {
                cycleIdx++;
                foreach (var ev in group)
                {
                    var lane = laneByName[ev.Name];
                    var pts = pointsByLane[lane];
                    pts.Add(new ObservablePoint(ev.T, (double)lane));
                    pts.Add(new ObservablePoint(ev.EndT, (double)lane));
                    pts.Add(null);   // null = line break (다음 event 와 안 연결)
                }
                SimProgressCycle = cycleIdx;
                var totalEvents = pointsByLane.Sum(p => p.Value.Count / 3);
                Status = $"▶ Cycle {cycleIdx}/{NCycles} — {totalEvents} events plotted (sim #{_simRunCounter})";
                if (SimStepDelayMs > 0)
                    await Task.Delay(SimStepDelayMs);
            }
            sw.Stop();

            _currentEvents = allEvents;
            SimSummary = $"{allEvents.Count} events / {NCycles} cycles (sim #{_simRunCounter})";
            Status = $"✓ Simulated {allEvents.Count} events in {sw.ElapsedMilliseconds}ms (jitter seed={simSeed})";

            if (AutoRun)
            {
                ReverseCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            Status = $"✗ Simulate failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Reverse()
    {
        if (_currentModel == null) { Status = "⚠ Generate 먼저 — model 없음"; return; }
        if (_currentEvents == null) { Status = "⚠ Simulate 먼저 — events 없음"; return; }
        try
        {
            Status = $"🔍 Reverse engineering — {_currentEvents.Count} events, "
                   + $"{_currentModel.GroundTruth.Count} candidates ...";
            System.Windows.Application.Current?.Dispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.Render);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = ReverseService.Run(_currentModel, _currentEvents, CycleMs, AutoTuneThreshold);
            sw.Stop();

            MetricPrecision = r.Metrics.Precision;
            MetricRecall = r.Metrics.Recall;
            MetricF1 = r.Metrics.F1;
            MetricCounts = $"TP={r.Metrics.TP} FP={r.Metrics.FP} FN={r.Metrics.FN} " +
                          $"| noise={r.Metrics.NoiseLevel:F2} anomaly={r.Metrics.AnomalousCyclesCount}";
            ReverseSummary = $"F1={r.Metrics.F1:F3} (TP/FP/FN = {r.Metrics.TP}/{r.Metrics.FP}/{r.Metrics.FN})";

            Diffs.Clear();
            foreach (var d in r.Metrics.Diffs.Take(50))
                Diffs.Add(d);

            // Anomaly summary
            if (r.Metrics.AnomalousCycles?.Count > 0)
            {
                var top = r.Metrics.AnomalousCycles
                    .OrderByDescending(a => a.Score)
                    .Take(5)
                    .Select(a => $"#{a.CycleIdx}({a.Score:F1})");
                AnomalySummary = $"⚠ {r.Metrics.AnomalousCyclesCount} anomalous cycles — " +
                                $"top: {string.Join(", ", top)}";
            }
            else
            {
                AnomalySummary = "✓ no anomalies detected";
            }
            Status = $"✓ Reverse complete — F1={r.Metrics.F1:F3} "
                   + $"(TP={r.Metrics.TP} FP={r.Metrics.FP} FN={r.Metrics.FN}) "
                   + $"in {sw.ElapsedMilliseconds}ms";
        }
        catch (Exception ex)
        {
            Status = $"✗ Reverse failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SeedSweepAsync()
    {
        if (_currentModel == null) { Status = "⚠ Generate 먼저"; return; }
        try
        {
            Status = "▶ Seed sweep — 10 seed 실행 ...";
            var seeds = new[] { 1, 7, 42, 99, 314, 1024, 9999, 31337, 12345, 999999 };
            var results = new List<(int seed, double f1, int tp, int fp, int fn)>();
            for (int i = 0; i < seeds.Length; i++)
            {
                var seed = seeds[i];
                var events = SimulationService.Simulate(
                    _currentModel, NCycles, CycleMs, seed, LagMs, JitterMs);
                var rr = ReverseService.Run(_currentModel, events, CycleMs, AutoTuneThreshold);
                results.Add((seed, rr.Metrics.F1,
                            rr.Metrics.TP, rr.Metrics.FP, rr.Metrics.FN));
                Status = $"▶ Seed sweep — {i + 1}/{seeds.Length} done, current F1={rr.Metrics.F1:F3}";
                await Task.Delay(20);   // UI yield
            }
            var avgF1 = results.Average(r => r.f1);
            var minF1 = results.Min(r => r.f1);
            var maxF1 = results.Max(r => r.f1);
            var perfect = results.Count(r => r.f1 >= 0.999);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Seed Sweep ({_currentModel.CaseName}, {seeds.Length} seeds) ===");
            sb.AppendLine($"Avg F1: {avgF1:F4}, Min: {minF1:F4}, Max: {maxF1:F4}");
            sb.AppendLine($"Perfect (F1≥0.999): {perfect}/{seeds.Length}");
            sb.AppendLine();
            sb.AppendLine($"{"seed",10}  {"F1",6}  {"TP",4} {"FP",4} {"FN",4}");
            foreach (var r in results)
                sb.AppendLine($"{r.seed,10}  {r.f1,6:F3}  {r.tp,4} {r.fp,4} {r.fn,4}");
            ModelGraphText = sb.ToString();
            Status = $"✓ Seed sweep complete — avg F1={avgF1:F3}, perfect {perfect}/{seeds.Length}";
        }
        catch (Exception ex)
        {
            Status = $"✗ Seed sweep failed: {ex.Message}";
        }
    }

    private void UpdateModelView()
    {
        if (_currentModel == null) return;
        var m = _currentModel;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== {m.CaseName} ===");
        sb.AppendLine($"Works: {m.Store.Works.Count}, Calls: {m.Store.Calls.Count}, ApiDefs: {m.Store.ApiDefs.Count}");
        sb.AppendLine($"arrowCalls: {m.Store.ArrowCalls.Count}, arrowWorks: {m.Store.ArrowWorks.Count}");
        sb.AppendLine();
        sb.AppendLine("Works:");
        foreach (var w in m.Store.Works.Values) sb.AppendLine($"  {w.Name}");
        sb.AppendLine();
        sb.AppendLine($"GroundTruth arrows ({m.GroundTruth.Count}):");
        foreach (var a in m.GroundTruth.Take(40))
            sb.AppendLine($"  {a.Src} → {a.Tgt} [{a.Kind}]");
        ModelGraphText = sb.ToString();
        ModelSummary = $"{m.CaseName} — Works={m.Store.Works.Count}, Calls={m.Store.Calls.Count}, arrows={m.GroundTruth.Count}";
    }

    private void UpdateTimeline()
    {
        if (_currentEvents == null) return;
        // Build lane index per unique name (sorted by name for stable ordering)
        var laneByName = _currentEvents
            .Select(e => e.Name).Distinct().OrderBy(n => n)
            .Select((n, i) => (n, i))
            .ToDictionary(t => t.n, t => t.i);

        var points = _currentEvents
            .Select(ev => new ObservablePoint(ev.T, (double)laneByName[ev.Name]))
            .ToList();

        // ObservableCollection 재할당 — LiveCharts2 가 항상 갱신 감지
        TimelineSeries = new ObservableCollection<ISeries>
        {
            new ScatterSeries<ObservablePoint>
            {
                Values = points,
                GeometrySize = 6,
                Fill = new SolidColorPaint(SKColors.SteelBlue),
                Stroke = null
            }
        };

        var labels = laneByName.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray();
        TimelineYAxes = new ObservableCollection<Axis>
        {
            new Axis
            {
                Name = "Call",
                Labels = labels,
                LabelsPaint = new SolidColorPaint(SKColors.Gray),
                MinStep = 1
            }
        };
        TimelineXAxes = new ObservableCollection<Axis>
        {
            new Axis { Name = "Time (ms)", LabelsPaint = new SolidColorPaint(SKColors.Gray) }
        };
    }
}

