using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;
using Ds2.Runtime.Sim.Model;
using Ds2.Runtime.Sim.Report;
using Ds2.Runtime.Sim.Report.Exporters;
using Ds2.Runtime.Sim.Report.Model;
using log4net;
using Microsoft.Win32;

namespace Promaker.ViewModels;

/// <summary>????? ??? ?? ??? ?????.</summary>
public partial class MainViewModel
{
    private static readonly ILog SimLog = LogManager.GetLogger("Simulation");

    private static class SimText
    {
        public const string Resumed = "????? ??";
        public const string Started = "????? ??";
        public const string Paused = "????? ????";
        public const string Stopped = "????? ??";
        public const string Completed = "????? ??";
        public const string Reset = "????? ??";
        public const string ResetLog = "????? ?? (F5/??)";
        public const string ReportEmpty = "??? ????? ???? ????.";
        public const string ReportDialogTitle = "????? ??? ????";

        public static string SimulationError(string message) => $"????? ??: {message}";
        public static string ManualWorkStarted(string name) => $"Work ?? ??: {name}";
        public static string ManualWorkReset(string name) => $"Work ?? ??: {name}";
        public static string ReportSaved(string path) => $"??? ?? ??: {path}";
        public static string ReportSaveFailed(string message) => $"??? ?? ??: {message}";
        public static string ReportError(string message) => $"??? ??: {message}";
        public static string StateCode(Status4 state) => state.ToString()[..1];
    }

    private ISimulationEngine? _simEngine;
    private DateTime _simStartTime;
    private readonly List<StateChangeRecord> _stateChangeRecords = [];
    private readonly StateCache _stateCache = new();

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
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    private SimWorkItem? _selectedSimWork;

    public ObservableCollection<SimNodeRow> SimNodes { get; } = [];
    public ObservableCollection<string> SimEventLog { get; } = [];
    public ObservableCollection<SimWorkItem> SimWorkItems { get; } = [];

    [RelayCommand(CanExecute = nameof(CanStartSimulation))]
    private void StartSimulation()
    {
        if (IsSimulating && IsSimPaused)
        {
            _simEngine?.Resume();
            IsSimPaused = false;
            StatusText = SimText.Resumed;
            return;
        }

        try
        {
            var index = SimIndexModule.build(_store, 10);
            _simEngine?.Dispose();
            _simEngine = new EventDrivenEngine(index);

            WireSimEvents();
            InitSimNodes();

            _simStartTime = DateTime.Now;
            _stateChangeRecords.Clear();
            SimEventLog.Clear();

            _simEngine.ApplyInitialStates();
            _simEngine.Start();

            ApplySimStateToCanvas();
            IsSimulating = true;
            IsSimPaused = false;
            SetSimStatus(SimText.Started, SimText.Started);
        }
        catch (Exception ex)
        {
            SimLog.Error("????? ?? ??", ex);
            StatusText = SimText.SimulationError(ex.Message);
        }
    }

    private bool CanStartSimulation() => !IsSimulating || IsSimPaused;

    [RelayCommand(CanExecute = nameof(CanPauseSimulation))]
    private void PauseSimulation()
    {
        _simEngine?.Pause();
        IsSimPaused = true;
        SetSimStatus(SimText.Paused, SimText.Paused);
    }

    private bool CanPauseSimulation() => IsSimulating && !IsSimPaused;

    [RelayCommand(CanExecute = nameof(CanStopSimulation))]
    private void StopSimulation()
    {
        _simEngine?.Stop();
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
        _simEngine?.SetSpeedMultiplier(value);
    }

    partial void OnSimTimeIgnoreChanged(bool value)
    {
        _simEngine?.SetTimeIgnore(value);
    }

    private void AddSimLog(string message)
    {
        var ts = _simEngine?.State.Clock.ToString(@"hh\:mm\:ss\.fff") ?? "00:00:00.000";
        SimEventLog.Insert(0, $"[{ts}] {message}");
        if (SimEventLog.Count > 500) SimEventLog.RemoveAt(SimEventLog.Count - 1);
    }

    private void SetSimStatus(string statusText, string? logText = null)
    {
        StatusText = statusText;
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

    [RelayCommand]
    private void ExportReportCsv() => ExportReportAs(ExportFormat.Csv);

    [RelayCommand]
    private void ExportReportXlsx() => ExportReportAs(ExportFormat.Excel);

    [RelayCommand]
    private void ExportReportHtml() => ExportReportAs(ExportFormat.Html);

    private void ExportReportAs(ExportFormat format)
    {
        var report = BuildReport();
        if (report.Entries.IsEmpty)
        {
            StatusText = SimText.ReportEmpty;
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
                StatusText = SimText.ReportSaved(dlg.FileName);
            else if (result.IsError)
                StatusText = SimText.ReportSaveFailed(((ExportResult.Error)result).message);
        }
        catch (Exception ex)
        {
            SimLog.Error("??? ???? ??", ex);
            StatusText = SimText.ReportError(ex.Message);
        }
    }
}

/// <summary>Work ?? ???? ?????.</summary>
public record SimWorkItem(Guid Guid, string Name)
{
    public override string ToString() => Name;
}

/// <summary>????? ?? ?? ??? ????.</summary>
public partial class SimNodeRow : ObservableObject
{
    public Guid NodeGuid { get; init; }
    public string Name { get; init; } = "";
    public string NodeType { get; init; } = "";
    public string SystemName { get; init; } = "";

    [ObservableProperty] private Status4 _state;
}
