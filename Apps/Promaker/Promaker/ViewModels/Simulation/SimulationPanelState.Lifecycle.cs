using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using Ds2.Core;
using Promaker.Dialogs;

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
                OnPropertyChanged(nameof(IsHubHost));
                SetHubStatus(connected: false, reconnecting: false);
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

    private void AddSimLog(string message, LogSeverity severity = LogSeverity.Info)
    {
        var ts = _simEngine?.State.Clock.ToString(SimText.ClockFormat) ?? SimText.ClockZero;
        var prefix = severity == LogSeverity.Info ? "" : $" [{severity.ToString().ToUpper()}]";
        var line = $"[{ts}]{prefix} {message}";
        SimEventLog.Insert(0, new SimLogEntry(line, severity));
        if (SimEventLog.Count > 500)
            SimEventLog.RemoveAt(SimEventLog.Count - 1);
        // 진단 파일 append — 모드별로 분리. UI 스레드를 막지 않도록 background writer 로 위임.
        // (Control/VP 모드의 빈번한 Hub 신호와 결합하면 매 호출의 동기 디스크 쓰기가 dispatcher 큐를 블로킹.)
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"ds2_eventlog_{SelectedRuntimeMode}.txt");
        DiagnosticLogWriter.Enqueue(path, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
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

/// <summary>
/// 진단 로그 파일을 background 에서 batch 로 append 하는 single-writer queue.
/// UI 스레드의 매 AddSimLog 호출이 동기 디스크 쓰기로 dispatcher 큐를 블로킹하던 패턴 분리.
/// 같은 path 의 여러 line 은 한 번의 AppendAllTextAsync 로 모아 쓴다.
/// rotation: 파일이 10MB 초과 시 .old 로 회전 (단일 백업).
/// </summary>
internal static class DiagnosticLogWriter
{
    private const long MaxBytes = 10 * 1024 * 1024;

    private static readonly Channel<(string Path, string Line)> _channel =
        Channel.CreateBounded<(string Path, string Line)>(
            new BoundedChannelOptions(10_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

    static DiagnosticLogWriter() => _ = Task.Run(WorkerAsync);

    public static void Enqueue(string path, string line)
        => _channel.Writer.TryWrite((path, line));

    private static async Task WorkerAsync()
    {
        var reader = _channel.Reader;
        var byPath = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            byPath.Clear();
            while (reader.TryRead(out var item))
            {
                if (!byPath.TryGetValue(item.Path, out var sb))
                    byPath[item.Path] = sb = new StringBuilder();
                sb.Append(item.Line);
            }
            foreach (var kv in byPath)
            {
                try
                {
                    RotateIfNeeded(kv.Key);
                    await File.AppendAllTextAsync(kv.Key, kv.Value.ToString()).ConfigureAwait(false);
                }
                catch
                {
                    // 진단 로그는 best-effort.
                }
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var bak = path + ".old";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(path, bak);
        }
        catch
        {
        }
    }
}
