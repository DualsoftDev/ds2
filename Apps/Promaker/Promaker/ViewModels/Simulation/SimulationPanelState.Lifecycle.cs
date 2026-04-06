using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private bool TryWithSimEngine(string operationName, Action<ISimulationEngine> action)
    {
        if (_simEngine is null)
            return false;

        try
        {
            action(_simEngine);
            return true;
        }
        catch (Exception ex)
        {
            SimLog.Error($"{operationName} failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
            return false;
        }
    }

    private bool TryDisposeCurrentEngine(string operationName)
    {
        if (_simEngine is null)
            return true;

        AdvanceSimUiGeneration();
        var engine = _simEngine;
        _simEngine = null;

        try
        {
            engine.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            SimLog.Error($"{operationName} failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartSimulation))]
    private void StartSimulation()
    {
        if (IsSimulating && IsSimPaused)
        {
            _simEngine?.SetAllFlowStates(FlowTag.Ready);
            _simEngine?.Resume();
            _isStepMode = false;
            SimStatusText = SimText.Running;
            ApplySimulationUiState(
                ganttRunning: true,
                isSimPaused: false,
                statusText: SimText.Resumed);
            return;
        }

        try
        {
            var index = SimIndexModule.build(Store, 10);

            // 토큰 역할이 설정되어 있으면 PLAY 전 자동 검증
            var hasPreStartWarnings = false;
            if (HasAnyTokenRole(index))
            {
                var sections = RunGraphValidation(index);
                if (sections.Count > 0)
                {
                    hasPreStartWarnings = true;
                    AddGraphWarningLogs(sections);
                    Dialogs.DialogHelpers.ShowGraphWarnings(sections);
                    _setStatusText($"모델 검증: {sections.Count}건의 경고 발견");
                }
            }

            if (!TryDisposeCurrentEngine("Simulation restart"))
                return;
            _simEngine = new EventDrivenEngine(index);
            AdvanceSimUiGeneration();

            WireSimEvents();
            InitSimNodes();
            InitTokenSources();
            InitSceneEventHandler();

            _simStartTime = DateTime.Now;
            _stateChangeRecords.Clear();
            _suppressedWarnings.Clear();
            HasReportData = false;
            SimEventLog.Clear();

            GanttChart.Reset(_simStartTime);
            InitGanttEntries();
            GanttChart.IsRunning = true;

            if (!hasPreStartWarnings)
                _warningGuids.Clear();

            // 자동 원위치 페이즈: Homing 대상이 있으면 페이즈 진행 후 정상 시뮬레이션
            var hasHoming = _simEngine.StartWithHomingPhase();
            if (hasHoming)
            {
                IsHomingPhase = true;
                _setStatusText("시뮬레이션 초기화 중...");
                SimStatusText = "시뮬레이션 초기화 중...";
                _simEngine.HomingPhaseCompleted += OnHomingPhaseCompleted;
            }

            ApplySimStateToCanvas();
            ApplyWarningsToCanvas();

            ApplySimulationUiState(
                ganttRunning: true,
                isSimulating: true,
                isSimPaused: false,
                statusText: hasHoming ? "시뮬레이션 초기화 중..." : SimText.Started,
                logText: hasHoming ? "시뮬레이션 자동 원위치 진행 중" : SimText.Started);
            if (!hasHoming)
                SimStatusText = SimText.Running;
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation start failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
        }
    }

    private bool CanStartSimulation() => (!IsSimulating || IsSimPaused) && !IsHomingPhase;

    [RelayCommand(CanExecute = nameof(CanPauseSimulation))]
    private void PauseSimulation()
    {
        _simEngine?.SetAllFlowStates(FlowTag.Pause);
        _isStepMode = true;
        SimStatusText = SimText.StepMode;
        ApplySimulationUiState(
            isSimPaused: true,
            statusText: SimText.Paused,
            logText: "단계 제어 모드 진입");
        RefreshSimulationProgressUi();
    }

    private bool CanPauseSimulation() => IsSimulating && !IsSimPaused && !IsHomingPhase;

    [RelayCommand(CanExecute = nameof(CanStopSimulation))]
    private void StopSimulation()
    {
        AdvanceSimUiGeneration();
        if (_simEngine is not null
            && !TryWithSimEngine("Simulation stop", engine => engine.Stop()))
            return;
        if (_simEngine is not null)
            _simEngine.HomingPhaseCompleted -= OnHomingPhaseCompleted;
        IsHomingPhase = false;
        ClearSimStateFromCanvas();
        ClearAllWarnings();
        HasWorkGoing = false;
        HasGoingCall = false;
        _isStepMode = false;

        SimStatusText = SimText.Stopped;
        _sceneEventHandler?.Reset();
        ApplySimulationUiState(
            ganttRunning: false,
            isSimulating: false,
            isSimPaused: false,
            statusText: SimText.Stopped,
            logText: SimText.Stopped);
    }

    private void InitSceneEventHandler()
    {
        _sceneEventHandler = new DeviceSceneEventHandler(ThreeD);
    }

    private void OnHomingPhaseCompleted(object? sender, EventArgs e)
    {
        if (_simEngine is not null)
            _simEngine.HomingPhaseCompleted -= OnHomingPhaseCompleted;
        _dispatcher.BeginInvoke(() =>
        {
            IsHomingPhase = false;
            SimStatusText = SimText.Running;
            _setStatusText(SimText.Started);
            AddSimLog("시뮬레이션 자동 원위치 완료");
        });
    }

    private bool CanStopSimulation() => IsSimulating;

    [RelayCommand(CanExecute = nameof(CanResetSimulation))]
    private void ResetSimulation()
    {
        AdvanceSimUiGeneration();
        if (_simEngine is not null
            && !TryWithSimEngine("Simulation reset", engine => engine.Reset()))
            return;
        _simStartTime = DateTime.Now;
        ApplySimulationResetUiState(clearCollections: false);
        GanttChart.Reset(_simStartTime);
        InitGanttEntries();
        HasWorkGoing = false;
        HasGoingCall = false;
        _isStepMode = false;
        SimStatusText = SimText.Reset;
        ApplySimulationUiState(
            statusText: SimText.Reset,
            logText: SimText.ResetLog);
    }

    private bool CanResetSimulation() => IsSimulating;

    [RelayCommand(CanExecute = nameof(CanStepSimulation))]
    private void StepSimulation()
    {
        if (_simEngine is null) return;
        SimStatusText = SimText.StepMode;

        var (selectedSourceGuid, autoStartSources) = GetStepAdvanceSelection();
        if (_simEngine.StepWithSourcePriming(selectedSourceGuid, autoStartSources))
            AddSimLog("STEP 실행");
        else
            AddSimLog("진행할 STEP 없음");

        RefreshSimulationProgressUi();
    }

    private bool CanStepSimulation() => IsSimulating
        && IsSimPaused
        && !IsHomingPhase
        && _simEngine is { } engine
        && engine.CanAdvanceStep(GetStepAdvanceSelection().SelectedSourceGuid, GetStepAdvanceSelection().AutoStartSources);

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

    public void NotifyStoreChanged()
    {
        if (!IsSimulating) return;
        const string msg = "모델이 변경되었습니다.\n시뮬레이션 초기화 버튼을 눌러야 반영됩니다.";
        ShowPausedMessageBox(msg, "모델 변경 감지",
            MessageBoxButton.OK, DialogHelpers.IconWarn, suppressKey: "StoreChanged");
    }

    public void NotifyConnectionsChanged()
    {
        if (!IsSimulating || _simEngine is null) return;

        if (HasWorkGoing || HasGoingCall)
        {
            const string msg =
                "시뮬레이션 중 연결을 변경하면 이미 Going 상태인 Work/Call은 현재 상태와 토큰을 유지합니다.\n" +
                "순수 Start 연결이 끊긴 항목은 진행이 일시 정지되고, StartReset 기반 항목은 계속 진행됩니다.";
            ShowPausedMessageBox(
                msg,
                "연결 변경 반영",
                MessageBoxButton.OK,
                DialogHelpers.IconWarn,
                suppressKey: "ConnectionsChangedInFlight");
        }

        try
        {
            _simEngine.ReloadConnections();
            SyncSimulationStateFromEngine();
            AddSimLog(IsSimPaused ? "연결 변경 반영" : "실행 중 연결 변경 반영");
            RefreshSimulationProgressUi();
        }
        catch (Exception ex)
        {
            SimLog.Error("Connection reload failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
        }
    }

    public void ResetForNewStore()
    {
        DisposeSimEngine();
        _simStartTime = DateTime.Now;
        ApplySimulationResetUiState(clearCollections: true);
        GanttChart.Reset(_simStartTime);
        PopulateWorkItems();
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

    private void RefreshSimulationProgressUi()
    {
        if (_simEngine is null) return;

        var anyGoingWork = _simEngine.State.WorkStates.Any(kv => kv.Value == Status4.Going);
        var anyGoingCall = _simEngine.State.CallStates.Any(kv => kv.Value == Status4.Going);

        HasWorkGoing = anyGoingWork || anyGoingCall;
        HasGoingCall = anyGoingCall;
        RefreshStepModeUi(anyGoingCall);
    }

    private void RefreshStepModeUi(bool anyGoingCall)
    {
        if (!_isStepMode || _simEngine is null)
            return;

        var hasActiveDuration = !anyGoingCall && _simEngine.HasActiveDuration;
        GanttChart.IsRunning = anyGoingCall || hasActiveDuration;
        StepSimulationCommand.NotifyCanExecuteChanged();

        if (!anyGoingCall && !hasActiveDuration)
            SimStatusText = SimText.Paused;
        else
            SimStatusText = SimText.StepMode;
    }

    private bool CanAdvanceStepCore() =>
        _simEngine is { } engine
        && !HasGoingCall
        && (engine.HasStartableWork || engine.HasActiveDuration);

    private void AddSimLog(string message)
    {
        var ts = _simEngine?.State.Clock.ToString(SimText.ClockFormat) ?? SimText.ClockZero;
        SimEventLog.Insert(0, $"[{ts}] {message}");
        if (SimEventLog.Count > 500)
            SimEventLog.RemoveAt(SimEventLog.Count - 1);
    }

    private void AddWarningLog(string severity, string message)
    {
        var ts = _simEngine?.State.Clock.ToString(SimText.ClockFormat) ?? SimText.ClockZero;
        SimEventLog.Insert(0, $"[{ts}] [{severity}] {message}");
        if (SimEventLog.Count > 500)
            SimEventLog.RemoveAt(SimEventLog.Count - 1);
    }

    private void AddGraphWarningLogs(List<GraphWarningSection> sections)
    {
        for (var i = sections.Count - 1; i >= 0; i--)
        {
            var section = sections[i];
            var severityTag = section.Severity == WarningSeverity.Red ? "ERROR" : "WARN";
            if (!string.IsNullOrWhiteSpace(section.Detail))
                AddWarningLog(severityTag, $"  {section.Detail}");
            for (var j = section.Lines.Count - 1; j >= 0; j--)
                AddWarningLog(severityTag, section.Lines[j].Trim());
            AddWarningLog(severityTag, $"[{section.Title}]");
        }
    }

    private void SetSimStatus(string statusText, string? logText = null)
    {
        _setStatusText(statusText);
        if (!string.IsNullOrWhiteSpace(logText))
            AddSimLog(logText);
    }

    private MessageBoxResult ShowPausedMessageBox(
        string message,
        string caption,
        MessageBoxButton buttons = MessageBoxButton.OK,
        string icon = DialogHelpers.IconWarn,
        string? suppressKey = null)
    {
        if (suppressKey is not null && _suppressedWarnings.Contains(suppressKey))
            return buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Yes;

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
        TryDisposeCurrentEngine("Simulation dispose");
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
        SimClock = SimText.ClockZero;
        SelectedSimWork = null;
        IsSimulating = false;
        IsSimPaused = false;
        _isStepMode = false;
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
