using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;
using Ds2.Runtime.Sim.Model;
using Ds2.Runtime.Sim.Report;
using Ds2.Runtime.Sim.Report.Exporters;
using Ds2.Runtime.Sim.Report.Model;
using Ds2.UI.Core;
using log4net;
using Microsoft.Win32;
using Promaker.Presentation;

namespace Promaker.ViewModels;

/// <summary>시뮬레이션 패널과 툴바의 시뮬레이션 상태/명령을 담당합니다.</summary>
public partial class SimulationPanelState : ObservableObject
{
    private static readonly ILog SimLog = LogManager.GetLogger("Simulation");

    private readonly Func<DsStore> _storeProvider;
    private readonly Dispatcher _dispatcher;
    private readonly ObservableCollection<EntityNode> _canvasNodes;
    private readonly Action<string> _setStatusText;
    private ISimulationEngine? _simEngine;
    private DateTime _simStartTime = DateTime.Now;
    private readonly List<StateChangeRecord> _stateChangeRecords = [];
    private readonly StateCache _stateCache = new();

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
        ObservableCollection<EntityNode> canvasNodes,
        Action<string> setStatusText)
    {
        _storeProvider = storeProvider;
        _dispatcher = dispatcher;
        _canvasNodes = canvasNodes;
        _setStatusText = setStatusText;
    }

    private DsStore Store => _storeProvider();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    private bool _isSimulating;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    private bool _isSimPaused;

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

    [RelayCommand(CanExecute = nameof(CanStartSimulation))]
    private void StartSimulation()
    {
        if (IsSimulating && IsSimPaused)
        {
            _simEngine?.Resume();
            GanttChart.IsRunning = true;
            IsSimPaused = false;
            _setStatusText(SimText.Resumed);
            return;
        }

        try
        {
            var index = SimIndexModule.build(Store, 10);
            _simEngine?.Dispose();
            _simEngine = new EventDrivenEngine(index);

            WireSimEvents();
            InitSimNodes();

            _simStartTime = DateTime.Now;
            _stateChangeRecords.Clear();
            HasReportData = false;
            SimEventLog.Clear();

            GanttChart.Reset(_simStartTime);
            InitGanttEntries();
            GanttChart.IsRunning = true;

            _simEngine.ApplyInitialStates();
            _simEngine.Start();

            ApplySimStateToCanvas();
            IsSimulating = true;
            IsSimPaused = false;
            SetSimStatus(SimText.Started, SimText.Started);
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
        GanttChart.IsRunning = false;
        IsSimPaused = true;
        SetSimStatus(SimText.Paused, SimText.Paused);
    }

    private bool CanPauseSimulation() => IsSimulating && !IsSimPaused;

    [RelayCommand(CanExecute = nameof(CanStopSimulation))]
    private void StopSimulation()
    {
        _simEngine?.Stop();
        GanttChart.IsRunning = false;
        ClearSimStateFromCanvas();
        IsSimulating = false;
        IsSimPaused = false;
        SetSimStatus(SimText.Stopped, SimText.Stopped);
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
        SetSimStatus(SimText.Reset, SimText.ResetLog);
    }

    private bool CanResetSimulation() => IsSimulating;

    [RelayCommand(CanExecute = nameof(CanForceWork))]
    private void ForceWorkStart()
    {
        if (!TryGetSelectedSimWork(out var engine, out var selectedWork)) return;

        var guid = selectedWork.Guid;
        var currentState = _stateCache.GetOrDefault(guid, Status4.Ready);
        if (currentState == Status4.Going) return;

        engine.ForceWorkState(guid, Status4.Going);
        AddSimLog(SimText.ManualWorkStarted(selectedWork.Name));
    }

    [RelayCommand(CanExecute = nameof(CanForceWork))]
    private void ForceWorkReset()
    {
        if (!TryGetSelectedSimWork(out var engine, out var selectedWork)) return;

        engine.ForceWorkState(selectedWork.Guid, Status4.Ready);
        AddSimLog(SimText.ManualWorkReset(selectedWork.Name));
    }

    private bool CanForceWork() => IsSimulating && !IsSimPaused && SelectedSimWork is not null;

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

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReportCsv() => ExportReportAs(ExportFormat.Csv);

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReportXlsx() => ExportReportAs(ExportFormat.Excel);

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReportHtml() => ExportReportAs(ExportFormat.Html);

    private bool CanExportReport() => HasReportData;

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

    private bool TryGetSelectedSimWork(
        [NotNullWhen(true)] out ISimulationEngine? engine,
        [NotNullWhen(true)] out SimWorkItem? selectedWork)
    {
        engine = _simEngine;
        selectedWork = SelectedSimWork;
        return engine is not null && selectedWork is not null;
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
        ClearSimStateFromCanvas();

        if (clearCollections)
        {
            SimNodes.Clear();
            SimEventLog.Clear();
            SimWorkItems.Clear();
            return;
        }

        SimEventLog.Clear();
        foreach (var row in SimNodes)
            row.State = Status4.Ready;
    }

    private DateTime CurrentSimulationTimestamp()
    {
        var clock = _simEngine?.State.Clock ?? TimeSpan.Zero;
        return _simStartTime + clock;
    }

    private void ExportReportAs(ExportFormat format)
    {
        var report = BuildReport();
        if (report.Entries.IsEmpty)
        {
            _setStatusText(SimText.ReportEmpty);
            return;
        }

        var filter = ExportHelper.getFilter(format);
        var ext = ExportHelper.getExtension(format);

        var dlg = new SaveFileDialog
        {
            Title = SimText.ReportDialogTitle,
            Filter = filter,
            DefaultExt = ext,
            FileName = $"SimReport_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var options = new ExportOptions
            {
                Format = format,
                FilePath = dlg.FileName,
                IncludeGanttChart = true,
                IncludeSummary = true,
                IncludeDetails = true,
                PixelsPerSecond = 10.0
            };
            var result = ReportService.export(report, options);

            if (result.IsSuccess)
                _setStatusText(SimText.ReportSaved(dlg.FileName));
            else if (result.IsError)
                _setStatusText(SimText.ReportSaveFailed(((ExportResult.Error)result).message));
        }
        catch (Exception ex)
        {
            SimLog.Error("Report export failed", ex);
            _setStatusText(SimText.ReportError(ex.Message));
        }
    }

    private void RecordStateChange(string nodeId, string nodeName, string nodeType, string systemId, Status4 state)
    {
        var stateString = SimText.StateCode(state);
        var timestamp = CurrentSimulationTimestamp();
        _stateChangeRecords.Add(
            new StateChangeRecord(nodeId, nodeName, nodeType, systemId, stateString, timestamp));
        HasReportData = _stateChangeRecords.Count > 0;
    }

    private SimulationReport BuildReport()
    {
        if (_stateChangeRecords.Count == 0) return ReportService.empty();

        var currentTime = CurrentSimulationTimestamp();
        var lastRecordTime = _stateChangeRecords[^1].Timestamp;
        var endTime = currentTime >= lastRecordTime ? currentTime : lastRecordTime;
        return ReportService.fromStateChanges(_simStartTime, endTime, _stateChangeRecords);
    }
}

/// <summary>Work 선택 ComboBox 항목입니다.</summary>
public record SimWorkItem(Guid Guid, string Name)
{
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
}
