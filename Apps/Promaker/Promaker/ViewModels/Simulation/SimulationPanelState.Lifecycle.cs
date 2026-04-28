using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Ds2.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{

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
        ApplySimulationResetUiState(clearCollections: true);
        ClearAllWarnings();
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

    private void AddSimLog(string message, LogSeverity severity = LogSeverity.Info)
    {
        var ts = _simEngine?.State.Clock.ToString(SimText.ClockFormat) ?? SimText.ClockZero;
        var prefix = severity == LogSeverity.Info ? "" : $" [{severity.ToString().ToUpper()}]";
        var line = $"[{ts}]{prefix} {message}";
        SimEventLog.Insert(0, new SimLogEntry(line, severity));
        if (SimEventLog.Count > 500)
            SimEventLog.RemoveAt(SimEventLog.Count - 1);
        // 진단 파일 append — 모드별로 분리
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"ds2_eventlog_{SelectedRuntimeMode}.txt");
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch { }
    }

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
