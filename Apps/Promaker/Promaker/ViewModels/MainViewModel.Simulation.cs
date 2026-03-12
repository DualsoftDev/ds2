using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;
using Ds2.Runtime.Sim.Model;
using Ds2.Runtime.Sim.Report;
using log4net;

namespace Promaker.ViewModels;

/// <summary>
/// 시뮬레이션 제어 (Start/Pause/Stop/Reset) + 상태 모니터 + 이벤트 로그
/// </summary>
public partial class MainViewModel
{
    private static readonly ILog SimLog = LogManager.GetLogger("Simulation");

    private ISimulationEngine? _simEngine;
    private DateTime _simStartTime;
    private readonly List<StateChangeRecord> _stateChangeRecords = [];
    private readonly StateCache _stateCache = new();

    // ── Observable Properties ──────────────────────────────────────────

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

    // ── Collections ────────────────────────────────────────────────────

    public ObservableCollection<SimNodeRow> SimNodes { get; } = [];
    public ObservableCollection<string> SimEventLog { get; } = [];
    public ObservableCollection<SimWorkItem> SimWorkItems { get; } = [];

    // ── Commands ───────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStartSimulation))]
    private void StartSimulation()
    {
        if (IsSimulating && IsSimPaused)
        {
            _simEngine?.Resume();
            IsSimPaused = false;
            StatusText = "시뮬레이션 재개";
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
            StatusText = "시뮬레이션 시작";
            AddSimLog("시뮬레이션 시작");
        }
        catch (Exception ex)
        {
            SimLog.Error("시뮬레이션 시작 실패", ex);
            StatusText = $"시뮬레이션 오류: {ex.Message}";
        }
    }

    private bool CanStartSimulation() => !IsSimulating || IsSimPaused;

    [RelayCommand(CanExecute = nameof(CanPauseSimulation))]
    private void PauseSimulation()
    {
        _simEngine?.Pause();
        IsSimPaused = true;
        StatusText = "시뮬레이션 일시정지";
        AddSimLog("시뮬레이션 일시정지");
    }

    private bool CanPauseSimulation() => IsSimulating && !IsSimPaused;

    [RelayCommand(CanExecute = nameof(CanStopSimulation))]
    private void StopSimulation()
    {
        _simEngine?.Stop();
        ClearSimStateFromCanvas();
        IsSimulating = false;
        IsSimPaused = false;
        StatusText = "시뮬레이션 중지";
        AddSimLog("시뮬레이션 중지");
    }

    private bool CanStopSimulation() => IsSimulating;

    [RelayCommand(CanExecute = nameof(CanResetSimulation))]
    private void ResetSimulation()
    {
        _simEngine?.Reset();
        StatusText = "시뮬레이션 리셋";
        AddSimLog("시뮬레이션 리셋 (F→H→R)");
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
        AddSimLog($"Work 수동 시작: {selectedWork.Name}");
    }

    [RelayCommand(CanExecute = nameof(CanForceWork))]
    private void ForceWorkReset()
    {
        if (!TryGetSelectedSimWork(out var engine, out var selectedWork)) return;

        engine.ForceWorkState(selectedWork.Guid, Status4.Ready);
        AddSimLog($"Work 수동 리셋: {selectedWork.Name}");
    }

    private bool CanForceWork() => IsSimulating && !IsSimPaused && SelectedSimWork is not null;

    // ── Speed / TimeIgnore ─────────────────────────────────────────────

    partial void OnSimSpeedChanged(double value)
    {
        _simEngine?.SetSpeedMultiplier(value);
    }

    partial void OnSimTimeIgnoreChanged(bool value)
    {
        _simEngine?.SetTimeIgnore(value);
    }

    // ── Engine Events → UI ─────────────────────────────────────────────

    // ── SimNodes 초기화/갱신 ───────────────────────────────────────────

    // ── Report Data 수집 ───────────────────────────────────────────────

    // ── 헬퍼 ───────────────────────────────────────────────────────────

    private void AddSimLog(string message)
    {
        var ts = _simEngine?.State.Clock.ToString(@"hh\:mm\:ss\.fff") ?? "00:00:00.000";
        SimEventLog.Insert(0, $"[{ts}] {message}");
        if (SimEventLog.Count > 500) SimEventLog.RemoveAt(SimEventLog.Count - 1);
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
}

/// <summary>Work 선택 ComboBox 항목</summary>
public record SimWorkItem(Guid Guid, string Name)
{
    public override string ToString() => Name;
}

/// <summary>시뮬레이션 상태 모니터 행</summary>
public partial class SimNodeRow : ObservableObject
{
    public Guid NodeGuid { get; init; }
    public string Name { get; init; } = "";
    public string NodeType { get; init; } = "";
    public string SystemName { get; init; } = "";

    [ObservableProperty] private Status4 _state;
}
