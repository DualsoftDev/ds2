using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;
using Ds2.Runtime.Sim.Model;
using Ds2.Runtime.Sim.Report;
using Ds2.Runtime.Sim.Report.Model;
using Ds2.Store;
using Ds2.Editor;
using log4net;
using Promaker.Dialogs;
using Promaker.Presentation;

namespace Promaker.ViewModels;

/// <summary>시뮬레이션 패널과 툴바의 시뮬레이션 상태/명령을 담당합니다.</summary>
public partial class SimulationPanelState : ObservableObject
{
    private static readonly ILog SimLog = LogManager.GetLogger("Simulation");

    private readonly Func<DsStore> _storeProvider;
    private readonly Dispatcher _dispatcher;
    private readonly Func<IEnumerable<EntityNode>> _allCanvasNodes;
    private readonly Func<IEnumerable<EntityNode>> _allTreeNodes;
    private readonly Action<string> _setStatusText;
    private ISimulationEngine? _simEngine;
    private DateTime _simStartTime = DateTime.Now;
    private readonly List<StateChangeRecord> _stateChangeRecords = [];
    private readonly StateCache _stateCache = new();
    private readonly HashSet<string> _suppressedWarnings = [];
    private readonly HashSet<Guid> _warningGuids = [];

    private static class SimText
    {
        public const string Resumed = "시뮬레이션 재개";
        public const string Started = "시뮬레이션 시작";
        public const string Paused = "시뮬레이션 일시정지";
        public const string Stopped = "시뮬레이션 중지";
        public const string Completed = "시뮬레이션 완료";
        public const string Reset = "시뮬레이션 리셋";
        public const string ResetLog = "시뮬레이션 리셋 (F5/정지)";
        public const string ReportEmpty = "내보낼 시뮬레이션 데이터가 없습니다.";
        public const string ReportDialogTitle = "시뮬레이션 리포트 내보내기";

        public static string SimulationError(string message) => $"시뮬레이션 오류: {message}";
        public static string ManualWorkStarted(string name) => $"Work 수동 시작: {name}";
        public static string ManualWorkReset(string name) => $"Work 수동 리셋: {name}";
        public static string ReportSaved(string path) => $"리포트 저장 완료: {path}";
        public static string ReportSaveFailed(string message) => $"리포트 저장 실패: {message}";
        public static string ReportError(string message) => $"리포트 오류: {message}";
        public static string StateCode(Status4 state) => Status4Visuals.ShortCode(state);
    }

    public SimulationPanelState(
        Func<DsStore> storeProvider,
        Dispatcher dispatcher,
        Func<IEnumerable<EntityNode>> allCanvasNodes,
        Func<IEnumerable<EntityNode>> allTreeNodes,
        Action<string> setStatusText)
    {
        _storeProvider = storeProvider;
        _dispatcher = dispatcher;
        _allCanvasNodes = allCanvasNodes;
        _allTreeNodes = allTreeNodes;
        _setStatusText = setStatusText;
    }

    private DsStore Store => _storeProvider();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSpeed))]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeedTokenCommand))]
    private bool _isSimulating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSpeed))]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeedTokenCommand))]
    private bool _isSimPaused;

    /// <summary>속도/TimeIgnore 변경 가능 여부 (정지 또는 일시정지 상태에서만).</summary>
    public bool CanChangeSpeed => !IsSimulating || IsSimPaused;

    [ObservableProperty] private double _simSpeed = 1.0;
    [ObservableProperty] private bool _simTimeIgnore;
    [ObservableProperty] private string _simClock = "00:00:00.000";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportXlsxCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportHtmlCommand))]
    private bool _hasReportData;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    private SimWorkItem? _selectedSimWork;

    partial void OnSelectedSimWorkChanged(SimWorkItem? value)
    {
        if (value is not null)
            _lastSelectedWorkId = value.Guid;
    }

    public ObservableCollection<SimNodeRow> SimNodes { get; } = [];
    public ObservableCollection<string> SimEventLog { get; } = [];
    public ObservableCollection<SimWorkItem> SimWorkItems { get; } = [];
    public GanttChartState GanttChart { get; } = new();

    /// <summary>캔버스 노드 선택 시 드롭다운 동기화 (다중 선택 시 첫 번째 Work)</summary>
    public void SyncCanvasSelection(IReadOnlyList<SelectionKey> orderedSelection)
    {
        if (!IsSimulating) return;
        foreach (var key in orderedSelection)
        {
            if (key.EntityKind != EntityKind.Work) continue;
            var match = SimWorkItems.FirstOrDefault(item => item.Guid == key.Id);
            if (match is not null)
            {
                SelectedSimWork = match;
                return;
            }
        }
    }

    private void ApplySimulationUiState(
        bool? ganttRunning = null,
        bool? isSimulating = null,
        bool? isSimPaused = null,
        string? statusText = null,
        string? logText = null)
    {
        if (ganttRunning.HasValue)
            GanttChart.IsRunning = ganttRunning.Value;
        if (isSimulating.HasValue)
            IsSimulating = isSimulating.Value;
        if (isSimPaused.HasValue)
            IsSimPaused = isSimPaused.Value;
        if (!string.IsNullOrWhiteSpace(statusText))
            SetSimStatus(statusText, logText);
        else if (!string.IsNullOrWhiteSpace(logText))
            AddSimLog(logText);
    }

    [RelayCommand(CanExecute = nameof(CanStartSimulation))]
    private void StartSimulation()
    {
        if (IsSimulating && IsSimPaused)
        {
            _simEngine?.Resume();
            ApplySimulationUiState(
                ganttRunning: true,
                isSimPaused: false,
                statusText: SimText.Resumed);
            return;
        }

        try
        {
            var index = SimIndexModule.build(Store, 10);
            _simEngine?.Dispose();
            _simEngine = new EventDrivenEngine(index);

            WireSimEvents();
            InitSimNodes();
            InitTokenSources();

            _simStartTime = DateTime.Now;
            _stateChangeRecords.Clear();
            _suppressedWarnings.Clear();
            HasReportData = false;
            SimEventLog.Clear();

            GanttChart.Reset(_simStartTime);
            InitGanttEntries();
            GanttChart.IsRunning = true;

            // 그래프 검증 경고 (전체 수집 → 한 번에 표시)
            _warningGuids.Clear();
            var sections = new List<GraphWarningSection>();
            CollectWarning(sections, "순환 데드락 위험", WarningSeverity.Red,
                GraphValidator.findDeadlockCandidates(index),
                "(해당 Work의 Start 선행조건에 순환 후속 Work가 포함되어 있습니다)");
            CollectWarning(sections, "Source 자동 시작 불가", WarningSeverity.Red,
                GraphValidator.findSourcesWithPredecessors(index),
                "(predecessor가 있어 자동 시작되지 않습니다. 순환 경로에 있으면 데드락이 발생합니다)");
            CollectGroupIgnoreWarning(sections, index);
            CollectTokenUnreachableWarning(sections, index);
            CollectWarning(sections, "Reset 연결 누락", WarningSeverity.Yellow,
                GraphValidator.findUnresetWorks(index));
            CollectWarning(sections, "Source 후보", WarningSeverity.Yellow,
                GraphValidator.findSourceCandidates(index),
                "(이 Work들을 Token Source로 지정하면 자동 시작/데드락 해소가 가능합니다)");
            CollectDurationWarning(sections, index);
            if (sections.Count > 0)
            {
                Dialogs.DialogHelpers.ShowGraphWarnings(sections);
            }
            ApplyWarningsToCanvas();

            _simEngine.ApplyInitialStates();
            _simEngine.Start();

            ApplySimStateToCanvas();
            ApplySimulationUiState(
                ganttRunning: true,
                isSimulating: true,
                isSimPaused: false,
                statusText: SimText.Started,
                logText: SimText.Started);
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation start failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
        }
    }

    private bool CanStartSimulation() => !IsSimulating || IsSimPaused;

    [RelayCommand(CanExecute = nameof(CanPauseSimulation))]
    private void PauseSimulation()
    {
        _simEngine?.Pause();
        ApplySimulationUiState(
            ganttRunning: false,
            isSimPaused: true,
            statusText: SimText.Paused,
            logText: SimText.Paused);
    }

    private bool CanPauseSimulation() => IsSimulating && !IsSimPaused;

    [RelayCommand(CanExecute = nameof(CanStopSimulation))]
    private void StopSimulation()
    {
        _simEngine?.Stop();
        ClearSimStateFromCanvas();
        ClearAllWarnings();
        ApplySimulationUiState(
            ganttRunning: false,
            isSimulating: false,
            isSimPaused: false,
            statusText: SimText.Stopped,
            logText: SimText.Stopped);
    }

    private bool CanStopSimulation() => IsSimulating;

    [RelayCommand(CanExecute = nameof(CanResetSimulation))]
    private void ResetSimulation()
    {
        _simEngine?.Reset();
        _simStartTime = DateTime.Now;
        ApplySimulationResetUiState(clearCollections: false);
        GanttChart.Reset(_simStartTime);
        InitGanttEntries();
        ApplySimulationUiState(
            statusText: SimText.Reset,
            logText: SimText.ResetLog);
    }

    private bool CanResetSimulation() => IsSimulating;

    partial void OnSimSpeedChanged(double value)
    {
        if (value == 0)
        {
            SimTimeIgnore = true;
            if (_simEngine is { } engine) engine.TimeIgnore = true;
        }
        else
        {
            SimTimeIgnore = false;
            if (_simEngine is { } engine)
            {
                engine.TimeIgnore = false;
                engine.SpeedMultiplier = value;
            }
        }
    }

    partial void OnSimTimeIgnoreChanged(bool value)
    {
        if (_simEngine is { } engine) engine.TimeIgnore = value;
    }

    /// <summary>시뮬레이션 중 store가 변경되었음을 알립니다. 경고창을 표시합니다.</summary>
    public void NotifyStoreChanged()
    {
        if (!IsSimulating) return;
        const string msg = "모델이 변경되었습니다.\n시뮬레이션 초기화 버튼을 눌러야 반영됩니다.";
        ShowPausedMessageBox(msg, "모델 변경 감지",
            System.Windows.MessageBoxButton.OK, "⚠", suppressKey: "StoreChanged");
    }

    public void ResetForNewStore()
    {
        DisposeSimEngine();
        _simStartTime = DateTime.Now;
        ApplySimulationResetUiState(clearCollections: true);
        GanttChart.Reset(_simStartTime);
        PopulateWorkItems();
    }

    private void AddSimLog(string message)
    {
        var ts = _simEngine?.State.Clock.ToString(@"hh\:mm\:ss\.fff") ?? "00:00:00.000";
        SimEventLog.Insert(0, $"[{ts}] {message}");
        if (SimEventLog.Count > 500) SimEventLog.RemoveAt(SimEventLog.Count - 1);
    }

    private void SetSimStatus(string statusText, string? logText = null)
    {
        _setStatusText(statusText);
        if (!string.IsNullOrWhiteSpace(logText))
            AddSimLog(logText);
    }

    private void CollectWarning(
        List<GraphWarningSection> sections,
        string title, WarningSeverity severity,
        IEnumerable<Tuple<Guid, string, string>> items,
        string? detail = null)
    {
        var itemList = items.ToList();
        foreach (var item in itemList)
            _warningGuids.Add(item.Item1);

        var lines = itemList
            .Select(static item => $"  - {item.Item2}.{item.Item3}")
            .ToList();
        if (lines.Count == 0)
            return;

        sections.Add(new GraphWarningSection(title, severity, lines, detail));
    }

    private static List<string> FormatGroupLines(
        IEnumerable<Tuple<string, Microsoft.FSharp.Collections.FSharpList<Tuple<Guid, string, string>>>> groups)
    {
        var lines = new List<string>();
        foreach (var group in groups)
        {
            var names = group.Item2.Select(m => m.Item3);
            lines.Add($"  - [{string.Join(", ", names)}]");
        }
        return lines;
    }

    private void CollectGroupIgnoreWarning(
        List<GraphWarningSection> sections,
        SimIndex index)
    {
        var groups = GraphValidator.findGroupWorksWithoutIgnore(index);
        if (!groups.Any()) return;

        foreach (var group in groups)
            foreach (var member in group.Item2)
                _warningGuids.Add(member.Item1);

        sections.Add(new GraphWarningSection(
            "Group Ignore 누락", WarningSeverity.Red, FormatGroupLines(groups),
            "(그룹 내 Work 중 1개를 제외한 나머지는 TokenRole.Ignore를 지정해야 합니다)"));
    }

    private void CollectDurationWarning(
        List<GraphWarningSection> sections,
        SimIndex index)
    {
        var lines = new List<string>();
        foreach (var workGuid in index.AllWorkGuids)
        {
            var workOpt = DsQuery.getWork(workGuid, index.Store);
            if (workOpt is null) continue;

            var periodOpt = workOpt.Value.Properties.Period;
            var userMs = periodOpt != null ? (int)periodOpt.Value.TotalMilliseconds : 0;

            var deviceOpt = DsQuery.tryGetDeviceDurationMs(workGuid, index.Store);
            if (deviceOpt is null) continue;
            var deviceMs = deviceOpt.Value;

            if (userMs > 0 && userMs < deviceMs)
            {
                var sysName = index.WorkSystemName.TryFind(workGuid)?.Value ?? "";
                var wName = index.WorkName.TryFind(workGuid)?.Value ?? "";
                _warningGuids.Add(workGuid);
                lines.Add($"  - {sysName}.{wName} (설정: {userMs}ms, Critical Path: {deviceMs}ms)");
            }
        }
        if (lines.Count == 0) return;

        sections.Add(new GraphWarningSection(
            "Work Duration < Critical Path", WarningSeverity.Yellow, lines,
            "(설정된 기간이 Device Critical Path보다 짧습니다. 실제 실행 시간은 Critical Path 기준으로 적용됩니다)"));
    }

    private void CollectTokenUnreachableWarning(
        List<GraphWarningSection> sections,
        SimIndex index)
    {
        var works = GraphValidator.findTokenUnreachableWorks(index).ToList();
        if (works.Count == 0) return;

        foreach (var w in works)
            _warningGuids.Add(w.Item1);

        sections.Add(new GraphWarningSection(
            "토큰 도달 불가", WarningSeverity.Red,
            works.Select(w => $"  - {w.Item2}.{w.Item3}").ToList(),
            "(모든 선행 Work가 Ignore 상태여서 토큰이 전달되지 않습니다)"));
    }

    /// <summary>시뮬레이션을 일시정지한 뒤 MessageBox를 표시하고, 닫으면 자동 재개합니다.</summary>
    private System.Windows.MessageBoxResult ShowPausedMessageBox(
        string message, string caption,
        System.Windows.MessageBoxButton buttons = System.Windows.MessageBoxButton.OK,
        string icon = "⚠",
        string? suppressKey = null)
    {
        if (suppressKey is not null && _suppressedWarnings.Contains(suppressKey))
            return buttons == System.Windows.MessageBoxButton.OK ? System.Windows.MessageBoxResult.OK
                 : System.Windows.MessageBoxResult.Yes;

        _simEngine?.Pause();
        GanttChart.IsRunning = false;
        var result = Dialogs.DialogHelpers.ShowThemedMessageBox(
            message, caption, buttons, icon,
            showDontShowAgain: suppressKey is not null, out var dontShowAgain);
        if (dontShowAgain && suppressKey is not null)
            _suppressedWarnings.Add(suppressKey);
        _simEngine?.Resume();
        GanttChart.IsRunning = true;
        return result;
    }

    private void DisposeSimEngine()
    {
        _simEngine?.Dispose();
        _simEngine = null;
        ClearSimStateFromCanvas();
        IsSimulating = false;
        IsSimPaused = false;
        _stateCache.Clear();
    }

    private void ApplySimulationResetUiState(bool clearCollections)
    {
        GanttChart.IsRunning = false;
        _stateChangeRecords.Clear();
        HasReportData = false;
        SimClock = "00:00:00.000";
        SelectedSimWork = null;
        IsSimulating = false;
        IsSimPaused = false;
        SimSpeed = 1.0;
        _stateCache.Clear();
        _suppressedWarnings.Clear();
        ClearSimStateFromCanvas();

        if (clearCollections)
        {
            SimNodes.Clear();
            SimEventLog.Clear();
            SimWorkItems.Clear();
            TokenSourceWorks.Clear();
            SelectedTokenSource = null;
            return;
        }

        SimEventLog.Clear();
        foreach (var row in SimNodes)
        {
            row.State = Status4.Ready;
            row.TokenDisplay = "";
        }
    }

}

/// <summary>Work 선택 ComboBox 항목입니다.</summary>
public record SimWorkItem(Guid Guid, string Name)
{
    /// <summary>Source Work 일괄 시작용 특수 항목</summary>
    public static readonly SimWorkItem AutoStart = new(Guid.Empty, "자동선택");
    public static readonly SimWorkItem SourceHeader = new(Guid.Empty, "── 시작노드 ──");
    public static readonly SimWorkItem NormalHeader = new(Guid.Empty, "── 일반노드 ──");
    public bool IsAutoStart => this == AutoStart;
    public override string ToString() => Name;
}

/// <summary>시뮬레이션 상태 모니터링 행 데이터입니다.</summary>
public partial class SimNodeRow : ObservableObject
{
    public Guid NodeGuid { get; init; }
    public string Name { get; init; } = "";
    public string NodeType { get; init; } = "";
    public string SystemName { get; init; } = "";

    [ObservableProperty] private Status4 _state;
    [ObservableProperty] private string _tokenDisplay = "";
}
