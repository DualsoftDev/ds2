using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Ds2.Core;
using log4net.Core;
using Promaker.Dialogs;
using Promaker.ViewModels.Logging;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{

    partial void OnSimSpeedChanged(double value)
    {
        if (value <= 0)
        {
            SimTimeIgnore = false;
            if (_simEngine is { } engine)
            {
                engine.TimeIgnore = false;
                engine.SpeedMultiplier = 1.0;
            }
            if (SimSpeed != 1.0)
                SimSpeed = 1.0;
            return;
        }

        SimTimeIgnore = false;
        if (_simEngine is { } activeEngine)
        {
            activeEngine.TimeIgnore = false;
            activeEngine.SpeedMultiplier = value;
        }
        // #198: 여기서 ResetBase() 하면 표시가 직전 이벤트의 엔진 clock 으로 되돌아가(뒤로 점프),
        // 속도 변경 시마다 값이 늘었다 줄었다 했다. 보간 연속성은 EstimateNow 가 '직전 속도로 적립' 방식으로
        // 직접 처리하므로(과거 구간 재계산/되감기 없음) 여기서는 base 를 건드리지 않는다.
    }

    partial void OnSimTimeIgnoreChanged(bool value)
    {
        if (_simEngine is { } engine) engine.TimeIgnore = false;
        if (value)
            SimTimeIgnore = false;
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
            AddSimLog(IsSimPaused ? "연결 변경 반영" : "실행 중 연결 변경 반영", LogSeverity.System);
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
        ResetPassiveGanttClockAnchor();
        ApplySimulationResetUiState(clearCollections: true);
        ClearAllWarnings();
        GanttChart.Reset(_simStartTime);
        PopulateWorkItems();

        // 새 모델로 바뀌면 이전 RuntimeMode 가 새 모델에 부적합 (I/O 미설정 등) 가능성 →
        // Simulation 으로 리셋. _suppressRuntimeModeChangeHandler 로 I/O 검사 우회.
        if (SelectedRuntimeMode != RuntimeMode.Simulation)
        {
            _suppressRuntimeModeChangeHandler = true;
            try
            {
                SelectedRuntimeMode = RuntimeMode.Simulation;
                _previousRuntimeMode = RuntimeMode.Simulation;
                OnPropertyChanged(nameof(NeedsHubConnection));
                Hub.RaiseHostingDependentsChanged();
                Hub.SetStatus(connected: false, reconnecting: false);
            }
            finally { _suppressRuntimeModeChangeHandler = false; }
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

        // GanttChart.IsRunning 은 PauseSimulation/StepSimulationAsync 가 직접 관리.
        // 여기서 덮어쓰면 STEP wait 도중 events handler 발화로 IsRunning=false 되어 progress bar 정지.
        var hasActiveDuration = !anyGoingCall && _simEngine.HasActiveDuration;
        StepSimulationCommand.NotifyCanExecuteChanged();

        if (!anyGoingCall && !hasActiveDuration)
            SimStatusText = SimText.Paused;
        else
            SimStatusText = SimText.StepMode;
    }

    /// <summary>
    /// 시뮬레이션 이벤트 로그 진입점 — 통합 AppLogState 로 routing.
    /// 엔진 클럭 prefix 는 메시지에 박제 (wall clock 은 AppLogEntry.Timestamp 가 별도 표시).
    /// Severity 9단 → log4net Level 5단 매핑 + Category 박제로 색상 분기 유지.
    /// 디스크 백업은 log4net RollingFileAppender 가 담당 (구 per-mode `ds2_eventlog_*.txt` 폐기).
    /// </summary>
    private void AddSimLog(string message, LogSeverity severity = LogSeverity.Info)
    {
        var ts = _simEngine?.State.Clock.ToString(SimText.ClockFormat) ?? SimText.ClockZero;
        var line = $"[{ts}] {message}";
        AppLogState.Instance.Enqueue(MapLevel(severity), "Simulation", line, severity.ToString());
    }

    private static Level MapLevel(LogSeverity s) => s switch
    {
        LogSeverity.Error or LogSeverity.Timeout => Level.Error,
        LogSeverity.Warn                          => Level.Warn,
        _                                         => Level.Info,
    };

    private void AddWarningLog(string severity, string message)
    {
        var sev = severity switch
        {
            "ERROR" => LogSeverity.Error,
            "TIMEOUT" => LogSeverity.Timeout,
            _ => LogSeverity.Warn
        };
        AddSimLog($"[{severity}] {message}", sev);
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

    private void SetSimStatus(string statusText, string? logText = null, LogSeverity severity = LogSeverity.System)
    {
        _setStatusText(statusText);
        if (!string.IsNullOrWhiteSpace(logText))
            AddSimLog(logText, severity);
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

        AddSimLog($"[{caption}] {message.Replace("\n", " ")}", LogSeverity.Warn);
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

}
