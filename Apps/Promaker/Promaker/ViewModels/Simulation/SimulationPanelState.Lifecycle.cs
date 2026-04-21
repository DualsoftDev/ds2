using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Backend;
using Ds2.Backend.Common;
using Ds2.Runtime.Engine;
using Ds2.Runtime.IO;
using Microsoft.FSharp.Core;
using Ds2.Runtime.Engine.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private WebApplication? _hubHost;
    private HubConnection? _hubConnection;
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

            // Race Condition 경고: 순서 없는 Call이 같은 Device의 ResetReset 관계 Work를 참조
            if (index.CallRaceExclusions.Count > 0)
            {
                var raceWarnings = new List<string>();
                var reported = new HashSet<string>();
                foreach (var kv in index.CallRaceExclusions)
                {
                    var callGuid = kv.Key;
                    var callName = Store.Calls.TryGetValue(callGuid, out var c) ? c.Name : "?";
                    var workGuid = index.CallWorkGuid.TryGetValue(callGuid, out var wg) ? wg : Guid.Empty;
                    var workName = index.WorkName.TryGetValue(workGuid, out var wn) ? wn : "?";
                    foreach (var exGuid in kv.Value)
                    {
                        var exName = Store.Calls.TryGetValue(exGuid, out var ec) ? ec.Name : "?";
                        var pairKey = string.Join(",", new[] { callGuid.ToString(), exGuid.ToString() }.OrderBy(x => x));
                        if (reported.Add(pairKey))
                            raceWarnings.Add($"  {workName}: {callName} ↔ {exName}");
                    }
                }
                if (raceWarnings.Count > 0)
                {
                    hasPreStartWarnings = true;
                    AddSimLog($"[WARN] Race Condition: 순서 없는 Call {raceWarnings.Count}쌍이 동일 Device ResetReset 관계 — 먼저 스케줄된 Call만 실행됩니다", LogSeverity.Warn);
                }
            }

            if (!TryDisposeCurrentEngine("Simulation restart"))
                return;

            // Hub 시작/연결 (Simulation 모드 이외)
            if (!TryStartHub())
                return;

            Action<string, string>? writeTagAction = null;
            if (_hubConnection is not null && SelectedRuntimeMode == RuntimeMode.Control)
            {
                var hub = _hubConnection;
                writeTagAction = (address, value) =>
                {
                    var state = hub.State;
                    _dispatcher.BeginInvoke(() =>
                        AddSimLog($"[Ctrl→] Out {address}={value} (hub={state})", LogSeverity.Going));
                    if (state != HubConnectionState.Connected)
                        return;
                    Task.Run(async () =>
                    {
                        try
                        {
                            await hub.InvokeAsync(HubMethod.WriteTag, address, value, HubSource.Control);
                            _dispatcher.BeginInvoke(() =>
                                AddSimLog($"[Ctrl→] Hub 전송 완료: {address}={value}", LogSeverity.System));
                        }
                        catch (Exception ex)
                        {
                            _dispatcher.BeginInvoke(() =>
                                AddSimLog($"[Ctrl→] WriteTag 실패: {ex.Message}", LogSeverity.Error));
                        }
                    });
                };
            }
            _simEngine = writeTagAction is not null
                ? new EventDrivenEngine(index, SelectedRuntimeMode,
                    FSharpOption<FSharpFunc<string, FSharpFunc<string, Unit>>>.Some(
                        FuncConvert.FromAction<string, string>(writeTagAction)))
                : new EventDrivenEngine(index, SelectedRuntimeMode);
            _simEngine.SpeedMultiplier = SimSpeed;
            _simEngine.TimeIgnore = SimTimeIgnore;

            // SignalIOMap 덤프: Out/In 주소 매핑 전체 목록을 파일로 저장 (진단용)
            try
            {
                var outKeys = _simEngine.IOMap.OutAddressToMappings.Keys
                    .Cast<string>().OrderBy(k => k).ToList();
                var inKeys = _simEngine.IOMap.InAddressToMappings.Keys
                    .Cast<string>().OrderBy(k => k).ToList();
                var dumpPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    $"ds2_iomap_{SelectedRuntimeMode}.txt");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Mode: {SelectedRuntimeMode}");
                sb.AppendLine($"Out addresses ({outKeys.Count}):");
                foreach (var k in outKeys) sb.AppendLine($"  {k}");
                sb.AppendLine($"In addresses ({inKeys.Count}):");
                foreach (var k in inKeys) sb.AppendLine($"  {k}");
                sb.AppendLine();
                sb.AppendLine("TxWorkToOutAddresses:");
                foreach (var kv in _simEngine.IOMap.TxWorkToOutAddresses)
                    sb.AppendLine($"  {kv.Key} → {string.Join(",", kv.Value)}");
                sb.AppendLine();
                sb.AppendLine("Mappings detail:");
                foreach (var m in _simEngine.IOMap.Mappings)
                    sb.AppendLine($"  ApiCall={m.ApiCallGuid} Call={m.CallGuid} " +
                                  $"Tx={(m.TxWorkGuid != null && FSharpOption<Guid>.get_IsSome(m.TxWorkGuid) ? m.TxWorkGuid.Value.ToString() : "-")} " +
                                  $"Out={m.OutAddress} In={m.InAddress}");
                System.IO.File.WriteAllText(dumpPath, sb.ToString());
                AddSimLog($"[IOMap] 덤프 저장: {dumpPath} (Out={outKeys.Count}, In={inKeys.Count})", LogSeverity.System);
            }
            catch (Exception ex)
            {
                AddSimLog($"[IOMap] 덤프 실패: {ex.Message}", LogSeverity.Error);
            }

            // VP/Monitoring: Work별 고유 IO 주소 준비 + 학습 상태 리셋
            if (SelectedRuntimeMode is RuntimeMode.VirtualPlant or RuntimeMode.Monitoring)
            {
                PreparePassiveModeIoInference();
            }

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

            // Passive 모드(VirtualPlant/Monitoring): Homing 없이 Start만, H 상태로 대기
            var isPassive = SelectedRuntimeMode is RuntimeMode.VirtualPlant or RuntimeMode.Monitoring;
            var hasHoming = false;

            // Control 모드: Hub Tag 캐시에서 실제 IO 값 조회 → Device Work 초기 상태 싱크
            //   엔진 Start 전에 완료해야 executeApiCall 첫 호출 시 반영됨 → 동기 대기 (최대 5초)
            //   Hub 연결이 비동기라 Start 시점엔 Connecting 상태일 수 있음 → 내부에서 Connected 대기
            if (_hubConnection is not null && SelectedRuntimeMode == RuntimeMode.Control)
            {
                var hub = _hubConnection;
                try
                {
                    AddSimLog($"[Ctrl] Hub 싱크 시작 (Hub 상태={hub.State})", LogSeverity.System);
                    var syncTask = Task.Run(async () =>
                    {
                        // Hub 연결 대기 (최대 3초)
                        var waitStart = DateTime.Now;
                        while (hub.State != HubConnectionState.Connected
                               && (DateTime.Now - waitStart).TotalMilliseconds < 3000)
                        {
                            await Task.Delay(50);
                        }
                        if (hub.State != HubConnectionState.Connected)
                            return false;
                        await SyncDeviceWorkStatesFromHub(hub);
                        return true;
                    });
                    if (!syncTask.Wait(5000))
                        AddSimLog("[Ctrl] 싱크 타임아웃 (5초)", LogSeverity.Warn);
                    else if (!syncTask.Result)
                        AddSimLog("[Ctrl] Hub 연결 대기 실패 — 싱크 건너뜀", LogSeverity.Warn);
                }
                catch (Exception ex)
                {
                    AddSimLog($"[Ctrl] 싱크 실패: {ex.Message}", LogSeverity.Warn);
                }
            }

            if (isPassive)
            {
                _simEngine.Start();
            }
            else
            {
                hasHoming = _simEngine.StartWithHomingPhase();
                if (hasHoming)
                {
                    IsHomingPhase = true;
                    _setStatusText("시뮬레이션 초기화 중...");
                    SimStatusText = "시뮬레이션 초기화 중...";
                    _simEngine.HomingPhaseCompleted += OnHomingPhaseCompleted;
                }
            }

            ApplySimStateToCanvas();
            ApplyWarningsToCanvas();

            ApplySimulationUiState(
                ganttRunning: true,
                isSimulating: true,
                isSimPaused: false,
                statusText: hasHoming ? "시뮬레이션 초기화 중..."
                    : isPassive ? "Hub 신호 대기 중..." : SimText.Started,
                logText: hasHoming ? "시뮬레이션 자동 원위치 진행 중"
                    : isPassive ? $"{SelectedRuntimeMode} 모드 — Hub 신호 대기" : SimText.Started);
            if (!hasHoming)
                SimStatusText = isPassive ? "Hub 신호 대기 중..." : SimText.Running;
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
        StopHub();
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
            AddSimLog("시뮬레이션 자동 원위치 완료", LogSeverity.System);
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
            AddSimLog("STEP 실행", LogSeverity.System);
        else
            AddSimLog("진행할 STEP 없음", LogSeverity.Warn);

        RefreshSimulationProgressUi();
    }

    private bool CanStepSimulation() => IsSimulating
        && IsSimPaused
        && !IsHomingPhase
        && SelectedRuntimeMode is not (RuntimeMode.VirtualPlant or RuntimeMode.Monitoring)
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

    private int ParseHubPort() =>
        HubAddress.Split(':') is { Length: >= 2 } parts && int.TryParse(parts[^1], out var p) ? p : 5050;

    private string BuildHubUrl() => IsHubHost
        ? BackendHost.getHubUrl(ParseHubPort())
        : $"http://{HubAddress}/hub/signal";

    private bool TryStartHub()
    {
        if (SelectedRuntimeMode == RuntimeMode.Simulation)
            return true;

        try
        {
            if (IsHubHost)
            {
                _hubHost = BackendHost.start(ParseHubPort());
                IsHubHosting = true;
                AddSimLog($"SignalR Hub 호스팅 시작 (port={ParseHubPort()})", LogSeverity.System);
            }

            var hubUrl = BuildHubUrl();
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(hubUrl)
                .WithAutomaticReconnect()
                .Build();

            _hubConnection.On<string, string, string>(HubMethod.OnTagChanged, OnHubTagChanged);
            _hubConnection.Closed += OnHubDisconnected;
            _hubConnection.Reconnecting += _ => { _dispatcher.BeginInvoke(() => SimStatusText = "Hub 재연결 중..."); return Task.CompletedTask; };
            _hubConnection.Reconnected += _ => { _dispatcher.BeginInvoke(() => { SimStatusText = IsSimulating ? "Hub 재연결 완료" : SimText.Stopped; AddSimLog("Hub 재연결 완료", LogSeverity.System); }); return Task.CompletedTask; };

            // 비동기 연결 시도 (UI 안 막음)
            _ = ConnectHubAsync(hubUrl);
            return true;
        }
        catch (Exception ex)
        {
            SimLog.Error("Hub start failed", ex);
            _setStatusText($"Hub 시작 실패: {ex.Message}");
            StopHub();
            return false;
        }
    }

    private async Task ConnectHubAsync(string hubUrl)
    {
        var retryDelayMs = 1000;
        const int maxDelayMs = 10000;

        while (_hubConnection is { State: HubConnectionState.Disconnected } conn)
        {
            try
            {
                _dispatcher.BeginInvoke(() =>
                {
                    SimStatusText = "Hub 연결 시도 중...";
                    _setStatusText($"Hub 연결 시도 중... ({hubUrl})");
                });

                await conn.StartAsync();

                _dispatcher.BeginInvoke(() =>
                {
                    AddSimLog($"SignalR Hub 연결 완료 ({hubUrl})", LogSeverity.System);
                    var isPassive = SelectedRuntimeMode is RuntimeMode.VirtualPlant or RuntimeMode.Monitoring;
                    var statusMsg = isPassive ? "Hub 신호 대기 중..." : $"{SelectedRuntimeMode} 동작 중";
                    SimStatusText = statusMsg;
                    _setStatusText(statusMsg);
                });
                return;
            }
            catch
            {
                _dispatcher.BeginInvoke(() =>
                    AddSimLog($"Hub 연결 실패 — {retryDelayMs / 1000.0:F1}초 후 재시도", LogSeverity.Warn));

                await Task.Delay(retryDelayMs);
                retryDelayMs = Math.Min(retryDelayMs * 2, maxDelayMs);
            }
        }
    }

    private Task OnHubDisconnected(Exception? ex)
    {
        if (!IsSimulating) return Task.CompletedTask;
        _dispatcher.BeginInvoke(() =>
        {
            AddSimLog($"Hub 연결 끊김{(ex is not null ? $": {ex.Message}" : "")}", LogSeverity.Warn);
            SimStatusText = "Hub 연결 끊김";
        });
        return Task.CompletedTask;
    }

    private void StopHub()
    {
        if (_hubConnection is not null)
        {
            var conn = _hubConnection;
            _hubConnection = null;
            conn.Closed -= OnHubDisconnected;
            // 비동기 정리 — UI 안 막음
            _ = Task.Run(async () =>
            {
                try { await conn.StopAsync(); } catch { /* ignore */ }
                try { await conn.DisposeAsync(); } catch { /* ignore */ }
            });
        }

        if (_hubHost is not null)
        {
            var host = _hubHost;
            _hubHost = null;
            IsHubHosting = false;
            _ = Task.Run(() =>
            {
                try { BackendHost.stop(host); } catch { /* ignore */ }
            });
        }
    }

    private void OnHubTagChanged(string address, string value, string source)
    {
        _dispatcher.BeginInvoke(() =>
            AddSimLog($"[Hub수신] {address}={value} from={source}", LogSeverity.Info));

        if (_simEngine is null)
            return;
        // 자기 모드의 source는 무시 (순환 방지)
        var mySource = SelectedRuntimeMode switch
        {
            RuntimeMode.Control => HubSource.Control,
            RuntimeMode.VirtualPlant => HubSource.VirtualPlant,
            RuntimeMode.Monitoring => HubSource.Monitoring,
            _ => ""
        };
        if (!string.IsNullOrEmpty(mySource) && source == mySource)
            return;

        switch (SelectedRuntimeMode)
        {
            case RuntimeMode.Control:
            {
                // InTag 수신 → IO 값 주입 + RxWork Finish
                var ioMap = _simEngine.IOMap;
                var inMappings = ioMap.GetByInAddress(address);
                if (!inMappings.IsEmpty)
                {
                    _simEngine.InjectIOValueByAddress(address, value);

                    // 모든 매핑의 RxWork를 Finish
                    foreach (var mapping in inMappings)
                    {
                        var rxOpt = mapping.RxWorkGuid;
                        if (rxOpt != null && FSharpOption<Guid>.get_IsSome(rxOpt))
                        {
                            if (value == "true")
                                _simEngine.ForceWorkState(rxOpt.Value, Status4.Finish);
                        }
                    }

                    _dispatcher.BeginInvoke(() =>
                        AddSimLog($"[Ctrl←] In {address}={value} (from {source})", LogSeverity.Finish));
                }
                else
                {
                    _dispatcher.BeginInvoke(() =>
                        AddSimLog($"[Ctrl←] {address}={value} [unmapped]", LogSeverity.Warn));
                }
                break;
            }

            case RuntimeMode.VirtualPlant:
            {
                // VP의 두 레이어:
                //  (1) Device 역할 (상시 작동): Out ON → Device Work Going → Duration → Finish + In ON 응답
                //  (2) Active 상태 유추 (사이클 확정 후에만): IO 패턴으로 Call/Work 상태 역추적
                var ioMap = _simEngine.IOMap;
                var mappings = ioMap.GetByOutAddress(address);
                var firstMapping = mappings.IsEmpty ? null : mappings.Head;
                if (firstMapping is { } mapping)
                {
                    var txWorkGuidOpt = mapping.TxWorkGuid;
                    if (txWorkGuidOpt != null && FSharpOption<Guid>.get_IsSome(txWorkGuidOpt))
                    {
                        var txWorkGuid = txWorkGuidOpt.Value;
                        if (value == "true")
                        {
                            // Out ON → Device Work Going + 상호 리셋 + Duration 후 Finish + In Write
                            _simEngine.ForceWorkState(txWorkGuid, Status4.Going);

                            // 상호 리셋: 이 Work의 ResetPreds(상대 Device Work)의 In OFF
                            var resetPreds = _simEngine.Index.WorkResetPreds;
                            if (resetPreds.ContainsKey(txWorkGuid))
                            {
                                foreach (var predGuid in resetPreds[txWorkGuid])
                                {
                                    var rxInMap = ioMap.RxWorkToInAddresses;
                                    if (rxInMap.ContainsKey(predGuid))
                                    {
                                        var hubConn = _hubConnection;
                                        foreach (var resetInAddr in rxInMap[predGuid])
                                        {
                                            if (hubConn is not null)
                                            {
                                                var ria = resetInAddr;
                                                Task.Run(() => hubConn.InvokeAsync(HubMethod.WriteTag, ria, "false", HubSource.VirtualPlant));
                                            }
                                            var riaCapture = resetInAddr;
                                            _dispatcher.BeginInvoke(() =>
                                            {
                                                AddSimLog($"[VP] 상호리셋: {riaCapture}=false", LogSeverity.Homing);
                                                ObserveAndInferPassiveState(riaCapture, "false");
                                            });
                                        }
                                    }
                                }
                            }

                            _dispatcher.BeginInvoke(() =>
                                AddSimLog($"[VP] Out ON: {address} → Device Going", LogSeverity.Going));

                            var inAddress = mapping.InAddress;
                            var durationMap = _simEngine.Index.WorkDuration;
                            var duration = durationMap.ContainsKey(txWorkGuid) ? (int)durationMap[txWorkGuid] : 500;
                            var hub = _hubConnection;
                            if (hub is not null && !string.IsNullOrEmpty(inAddress))
                            {
                                var inAddrCapture = inAddress;
                                Task.Run(async () =>
                                {
                                    await Task.Delay(duration);
                                    if (_simEngine is not null)
                                    {
                                        _simEngine.ForceWorkState(txWorkGuid, Status4.Finish);
                                        await hub.InvokeAsync(HubMethod.WriteTag, inAddrCapture, "true", HubSource.VirtualPlant);
                                        _dispatcher.BeginInvoke(() =>
                                        {
                                            AddSimLog($"[VP→] In ON: {inAddrCapture} (after {duration}ms)", LogSeverity.Finish);
                                            // VP는 자기 In을 source 필터로 무시하므로 self-observe
                                            ObserveAndInferPassiveState(inAddrCapture, "true");
                                        });
                                    }
                                });
                            }
                        }
                        else
                        {
                            // Out OFF → In OFF 쓰기
                            var inAddress = mapping.InAddress;
                            var hub = _hubConnection;
                            if (hub is not null && !string.IsNullOrEmpty(inAddress))
                            {
                                var inAddr2 = inAddress;
                                Task.Run(async () =>
                                {
                                    await hub.InvokeAsync(HubMethod.WriteTag, inAddr2, "false", HubSource.VirtualPlant);
                                    _dispatcher.BeginInvoke(() =>
                                    {
                                        AddSimLog($"[VP→] In OFF: {inAddr2}", LogSeverity.Ready);
                                        ObserveAndInferPassiveState(inAddr2, "false");
                                    });
                                });
                            }
                        }
                    }
                }

                // Active 상태 유추 — UI dispatcher로 직렬화 (SignalR 스레드 동시성 레이스 방지)
                _dispatcher.BeginInvoke(() => ObserveAndInferPassiveState(address, value));
                break;
            }

            case RuntimeMode.Monitoring:
            {
                // Monitoring은 IO 읽기 전용, Device 역할 없음. 상태 유추는 동일.
                _dispatcher.BeginInvoke(() =>
                {
                    AddSimLog($"[Mon] {address}={value} (from {source})", LogSeverity.Info);
                    ObserveAndInferPassiveState(address, value);
                });
                break;
            }
        }
    }

    // ── Passive 상태 유추: 시퀀스 매칭 기반 사이클 확정 (A안) ──────────────
    //
    // 1) 첫 사이클 관찰: 모든 IO 이벤트를 순서대로 기록
    //    주소별 상태 머신 None → OnSeen → Cycled로 "1회 완주" 감지
    //    모든 매핑 주소가 Cycled 되면 첫 사이클 완료로 간주 → 기록된 시퀀스를 패턴으로 저장
    //
    // 2) 두 번째 사이클 매칭: 이후 이벤트가 첫 사이클 시퀀스와 순서+값 일치하는지 실시간 비교
    //    일치 계속 → 매칭 인덱스 증가
    //    불일치 → 처음부터 다시 매칭 시도
    //    첫 사이클 길이만큼 매칭 성공 → 확정 (_cycleSynced = true)
    //
    // 3) Synced 단계: IO 수신 즉시 Call/Work 상태 유추 (가장 specific Call 선택)

    /// Work learning state.
    private sealed class WorkLearning
    {
        public List<string> Sequence = new();
        public List<(string Dir, string Val)> GroupKeys = new();
        public (string Dir, string Val)? LearningCurrentKey;
        public int? DetectedPeriod;
        public int? WorkFinishGroupIdx;
        public int? WorkGoingStartGroupIdx;
        public bool Synced;
        public int NextExpectedGroupIdx;
        public List<string> CycleSequence = new();
        public List<(string Dir, string Val)> CycleGroupKeys = new();
        public (string Dir, string Val)? LiveCurrentKey;
        public HashSet<string> LiveCurrentTokens = new(StringComparer.Ordinal);
    }
    private readonly Dictionary<Guid, WorkLearning> _workLearning = [];
    /// Work별 고유 IO 주소 (다른 어떤 Work와도 공유 안 되는 주소만). StartSimulation에서 계산.
    private readonly Dictionary<Guid, HashSet<string>> _workUniqueAddresses = [];
    /// Work별 positive phase family token map: ("Out|addr" / "In|addr") -> family token.
    private readonly Dictionary<Guid, Dictionary<string, string>> _workPositiveFamilyTokens = [];
    /// Reset predecessor -> passive target Work list.
    private readonly Dictionary<Guid, List<Guid>> _workResetTargetsByPred = [];
    private readonly Dictionary<Guid, HashSet<string>> _callOutAddresses = [];
    /// Pseudo Call state — 학습 중 엔진을 건드리지 않고 Call 상태 추적용 (Synced 전)
    private readonly Dictionary<Guid, HashSet<string>> _callInAddresses = [];
    private readonly Dictionary<Guid, HashSet<string>> _callOutHighAddresses = [];
    /// Device Work pseudo state — 엔진 scheduler는 비동기라 Force 직후 GetWorkState 읽으면 이전 값.
    private readonly Dictionary<Guid, HashSet<string>> _callInHighAddresses = [];
    /// Work별 진행도 (theta) — 고유 이벤트 수신 수. theta == DetectedPeriod에서 Work.Finish.
    /// 중복 필터: 직전 수신한 (address → value). 같은 값 연속이면 무시
    private readonly Dictionary<string, string> _lastObservedValue = [];

    private void PreparePassiveModeIoInference()
    {
        ComputeWorkUniqueAddresses();
        ComputeWorkPositiveFamilyTokens();
        BuildPassiveResetTargetsByPred();
        BuildPassiveCallAddressSets();

        _workLearning.Clear();
        _callOutHighAddresses.Clear();
        _callInHighAddresses.Clear();
        _lastObservedValue.Clear();
    }

    private void ComputeWorkPositiveFamilyTokens()
    {
        if (_simEngine is null) return;

        _workPositiveFamilyTokens.Clear();

        foreach (var (workGuid, uniqueAddresses) in _workUniqueAddresses)
        {
            var workCalls = _simEngine.Index.WorkCallGuids.TryFind(workGuid);
            if (workCalls == null)
                continue;

            var canonicalOrder = new Dictionary<Guid, int>();
            var nextOrdinal = 0;
            foreach (var callGuid in workCalls.Value)
            {
                var canonicalCallGuid = ResolveCanonicalCallGuid(callGuid);
                if (!canonicalOrder.ContainsKey(canonicalCallGuid))
                    canonicalOrder[canonicalCallGuid] = nextOrdinal++;
            }

            var tokenMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var workMappings = _simEngine.IOMap.Mappings
                .Where(mapping =>
                {
                    var wgOpt = _simEngine.Index.CallWorkGuid.TryFind(mapping.CallGuid);
                    return wgOpt != null && FSharpOption<Guid>.get_IsSome(wgOpt) && wgOpt.Value == workGuid;
                })
                .ToArray();

            foreach (var address in uniqueAddresses)
            {
                SetPositiveFamilyToken(tokenMap, "Out", address,
                    workMappings
                        .Where(mapping => mapping.OutAddress == address)
                        .Select(mapping => ResolveCanonicalCallGuid(mapping.CallGuid))
                        .Where(canonicalOrder.ContainsKey)
                        .Select(canonicalGuid => canonicalOrder[canonicalGuid]));

                SetPositiveFamilyToken(tokenMap, "In", address,
                    workMappings
                        .Where(mapping => mapping.InAddress == address)
                        .Select(mapping => ResolveCanonicalCallGuid(mapping.CallGuid))
                        .Where(canonicalOrder.ContainsKey)
                        .Select(canonicalGuid => canonicalOrder[canonicalGuid]));
            }

            if (tokenMap.Count > 0)
                _workPositiveFamilyTokens[workGuid] = tokenMap;
        }
    }

    private void BuildPassiveResetTargetsByPred()
    {
        if (_simEngine is null) return;

        _workResetTargetsByPred.Clear();
        foreach (var entry in _simEngine.Index.WorkResetPreds)
        {
            var targetGuid = entry.Key;
            foreach (var predGuid in entry.Value.Distinct())
            {
                if (!_workResetTargetsByPred.TryGetValue(predGuid, out var targets))
                {
                    targets = [];
                    _workResetTargetsByPred[predGuid] = targets;
                }

                if (!targets.Contains(targetGuid))
                    targets.Add(targetGuid);
            }
        }
    }

    private void BuildPassiveCallAddressSets()
    {
        if (_simEngine is null) return;

        _callOutAddresses.Clear();
        _callInAddresses.Clear();

        foreach (var callGuid in _simEngine.Index.AllCallGuids)
        {
            if (!_simEngine.Index.Store.Calls.TryGetValue(callGuid, out var call))
                continue;

            var outSet = new HashSet<string>(StringComparer.Ordinal);
            var inSet = new HashSet<string>(StringComparer.Ordinal);

            foreach (var apiCall in call.ApiCalls)
            {
                var outAddress = apiCall.OutTag != null && FSharpOption<IOTag>.get_IsSome(apiCall.OutTag)
                    ? apiCall.OutTag.Value.Address
                    : null;
                var inAddress = apiCall.InTag != null && FSharpOption<IOTag>.get_IsSome(apiCall.InTag)
                    ? apiCall.InTag.Value.Address
                    : null;

                if (!string.IsNullOrWhiteSpace(outAddress))
                    outSet.Add(outAddress);
                if (!string.IsNullOrWhiteSpace(inAddress))
                    inSet.Add(inAddress);
            }

            _callOutAddresses[callGuid] = outSet;
            _callInAddresses[callGuid] = inSet;
        }
    }

    private void ObserveAndInferPassiveState(string address, string value)
    {
        if (_simEngine is null) return;
        if (_lastObservedValue.TryGetValue(address, out var prev) && prev == value) return;

        _lastObservedValue[address] = value;

        var ioMap = _simEngine.IOMap;
        var outMappings = ioMap.GetByOutAddress(address);
        var inMappings = ioMap.GetByInAddress(address);
        if (outMappings.IsEmpty && inMappings.IsEmpty)
            return;

        if (!outMappings.IsEmpty)
            ObservePassiveSignalDirection(address, value, isOut: true, outMappings);

        if (!inMappings.IsEmpty)
            ObservePassiveSignalDirection(address, value, isOut: false, inMappings);
    }

    private void ObservePassiveSignalDirection(
        string address,
        string value,
        bool isOut,
        IEnumerable<SignalMapping> mappings)
    {
        if (_simEngine is null) return;

        var isOn = value == "true";
        foreach (var callGuid in mappings.Select(m => m.CallGuid).Distinct())
            ObservePassiveCallSignal(callGuid, address, isOut, isOn);

        if (!isOn)
            return;

        var dirVal = (isOut ? "Out" : "In", value);

        foreach (var (workGuid, uniqueAddresses) in _workUniqueAddresses)
        {
            if (!uniqueAddresses.Contains(address))
                continue;

            var token = ResolveWorkPositiveFamilyToken(workGuid, address, isOut) ?? address;
            var wl = GetOrCreateWorkLearning(workGuid);
            if (!wl.Synced)
            {
                AppendToWorkSequence(wl, dirVal, token);
                DetectWorkPeriod(workGuid);
            }
            else
            {
                ObserveSyncedWorkGroup(workGuid, wl, dirVal, token);
            }
        }
    }

    private void ObservePassiveCallSignal(Guid callGuid, string address, bool isOut, bool isOn)
    {
        if (_simEngine is null) return;

        var highMap = isOut ? _callOutHighAddresses : _callInHighAddresses;
        var highSet = GetOrAddSignalSet(highMap, callGuid);

        if (isOn)
            highSet.Add(address);
        else
            highSet.Remove(address);

        if (HasAllObservedSignals(_callInAddresses, _callInHighAddresses, callGuid))
        {
            if (GetCallStateSafe(callGuid) != Status4.Finish)
                _simEngine.ForceCallState(callGuid, Status4.Finish);
            return;
        }

        if (HasAllObservedSignals(_callOutAddresses, _callOutHighAddresses, callGuid)
            && GetCallStateSafe(callGuid) != Status4.Going)
        {
            _simEngine.ForceCallState(callGuid, Status4.Going);
        }
    }

    private static void AppendToWorkSequence(WorkLearning wl, (string Dir, string Val) dirVal, string token)
    {
        if (wl.LearningCurrentKey == dirVal && wl.Sequence.Count > 0)
        {
            var items = wl.Sequence[^1]
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            items.Add(token);
            wl.Sequence[^1] = string.Join("|", items.OrderBy(static x => x, StringComparer.Ordinal));
            return;
        }

        wl.Sequence.Add(token);
        wl.GroupKeys.Add(dirVal);
        wl.LearningCurrentKey = dirVal;
    }

    /// Work별 고유 IO 주소 계산: Work의 모든 Call의 모든 ApiCall Out+In 주소 중
    /// 다른 어떤 Work와도 공유 안 되는 것만.
    private void ComputeWorkUniqueAddresses()
    {
        if (_simEngine is null) return;
        _workUniqueAddresses.Clear();

        // Work별 전체 IO 주소 집합
        var workAllAddrs = new Dictionary<Guid, HashSet<string>>();
        foreach (var m in _simEngine.IOMap.Mappings)
        {
            var wgOpt = _simEngine.Index.CallWorkGuid.TryFind(m.CallGuid);
            if (wgOpt == null || !FSharpOption<Guid>.get_IsSome(wgOpt)) continue;
            var wg = wgOpt.Value;
            if (!workAllAddrs.TryGetValue(wg, out var set))
            {
                set = new HashSet<string>();
                workAllAddrs[wg] = set;
            }
            if (!string.IsNullOrEmpty(m.OutAddress)) set.Add(m.OutAddress);
            if (!string.IsNullOrEmpty(m.InAddress)) set.Add(m.InAddress);
        }

        // 각 Work 고유 주소 = 자기 주소 - 다른 Work 주소 합집합
        foreach (var (wg, addrs) in workAllAddrs)
        {
            var otherAddrs = new HashSet<string>();
            foreach (var (otherWg, otherSet) in workAllAddrs)
            {
                if (otherWg == wg) continue;
                otherAddrs.UnionWith(otherSet);
            }
            var unique = new HashSet<string>(addrs);
            unique.ExceptWith(otherAddrs);
            _workUniqueAddresses[wg] = unique;

            var wname = ResolveWorkName(wg);
            AddSimLog($"[VP 학습] {wname} 고유 주소 {unique.Count}개 (전체 {addrs.Count}개 중)", LogSeverity.System);
            if (unique.Count == 0)
                AddSimLog($"[VP 학습] {wname} 고유 주소 없음 — 사이클 감지 불가 (완전 공유 케이스)", LogSeverity.Warn);
        }
    }

    /// 시퀀스 그룹에서 엄격 순서 일치 주기 검출. 매칭되는 최소 k를 주기로 확정.
    /// Passive 모드별로 필요한 반복 수만큼(예: VP 2회, Monitoring 3회) 관찰 후 확정.
    /// 단 현재 진행 중인 그룹(마지막 요소)은 아직 미완성일 수 있어 비교 대상에서 제외 (L-1까지만).
    private void DetectWorkPeriod(Guid workGuid)
    {
        if (!_workLearning.TryGetValue(workGuid, out var wl)) return;
        if (wl.Synced) return;
        var seq = wl.Sequence;
        var completedCount = seq.Count;
        if (completedCount < 2) return;
        var requiredMatches = GetRequiredPassiveCycleMatchCount();

        for (var period = 1; period <= completedCount / requiredMatches; period++)
        {
            for (var start = 0; start + (period * requiredMatches) <= completedCount; start++)
            {
                var match = true;
                for (var repeat = 1; repeat < requiredMatches && match; repeat++)
                {
                    for (var i = 0; i < period; i++)
                    {
                        if (seq[start + i] != seq[start + (repeat * period) + i])
                        {
                            match = false;
                            break;
                        }
                    }
                }

                if (!match)
                    continue;

                wl.DetectedPeriod = period;
                wl.CycleSequence = seq.Skip(start).Take(period).ToList();
                wl.CycleGroupKeys = wl.GroupKeys.Skip(start).Take(period).ToList();

                var workGoingStartIdx = -1;
                for (var i = 0; i < wl.CycleGroupKeys.Count; i++)
                {
                    var gk = wl.CycleGroupKeys[i];
                    if (workGoingStartIdx < 0 && gk.Dir == "Out" && gk.Val == "true")
                        workGoingStartIdx = i;
                }

                var workFinishIdx = -1;
                for (var i = wl.CycleGroupKeys.Count - 1; i >= 0; i--)
                {
                    var gk = wl.CycleGroupKeys[i];
                    if (gk.Dir == "In" && gk.Val == "true")
                    {
                        workFinishIdx = i;
                        break;
                    }
                }

                wl.WorkFinishGroupIdx = workFinishIdx >= 0 ? workFinishIdx : (int?)null;
                wl.WorkGoingStartGroupIdx = workGoingStartIdx >= 0 ? workGoingStartIdx : (int?)null;
                wl.Synced = true;
                wl.NextExpectedGroupIdx = (completedCount - start) % period;
                wl.LiveCurrentKey = null;
                wl.LiveCurrentTokens.Clear();

                var wname = ResolveWorkName(workGuid);
                var seqStr = string.Join(" | ", wl.CycleSequence);
                var msg = $"[{ResolvePassiveLearningLogPrefix()}] {wname} cycle fixed groups={period}, matches={requiredMatches}, GoingStart={wl.WorkGoingStartGroupIdx?.ToString() ?? "none"}, Finish={wl.WorkFinishGroupIdx?.ToString() ?? "none"} / Seq[0..{period - 1}]={seqStr}";
                _dispatcher.BeginInvoke(() => AddSimLog(msg, LogSeverity.System));
                return;
            }
        }
    }

    private string ResolveWorkName(Guid workGuid)
    {
        var wname = _simEngine?.Index.WorkName.TryFind(workGuid);
        return (wname != null && FSharpOption<string>.get_IsSome(wname)) ? wname.Value : workGuid.ToString();
    }

    private int GetRequiredPassiveCycleMatchCount() => SelectedRuntimeMode == RuntimeMode.Monitoring ? 3 : 2;

    private string ResolvePassiveLearningLogPrefix() => SelectedRuntimeMode switch
    {
        RuntimeMode.Monitoring => "Mon",
        RuntimeMode.VirtualPlant => "VP",
        _ => "Passive"
    };

    private void ObserveSyncedWorkGroup(
        Guid workGuid,
        WorkLearning wl,
        (string Dir, string Val) dirVal,
        string token)
    {
        if (wl.LiveCurrentKey == dirVal)
        {
            wl.LiveCurrentTokens.Add(token);
            return;
        }

        if (wl.LiveCurrentKey is not null)
            FinalizeObservedWorkGroup(wl);

        wl.LiveCurrentKey = dirVal;
        wl.LiveCurrentTokens.Clear();
        wl.LiveCurrentTokens.Add(token);
        ApplyWorkStateForExpectedGroup(workGuid, wl);
    }

    private void FinalizeObservedWorkGroup(WorkLearning wl)
    {
        if (wl.DetectedPeriod is not int period || period <= 0 || wl.LiveCurrentTokens.Count == 0)
            return;

        var actual = string.Join("|", wl.LiveCurrentTokens.OrderBy(static x => x, StringComparer.Ordinal));

        if (wl.NextExpectedGroupIdx < period && wl.CycleSequence[wl.NextExpectedGroupIdx] == actual)
        {
            wl.NextExpectedGroupIdx = (wl.NextExpectedGroupIdx + 1) % period;
            return;
        }

        var matchedIdx = -1;
        for (var i = 0; i < period; i++)
        {
            if (wl.CycleSequence[i] != actual)
                continue;

            matchedIdx = i;
            break;
        }

        wl.NextExpectedGroupIdx = matchedIdx >= 0
            ? (matchedIdx + 1) % period
            : 0;
    }

    private void ApplyWorkStateForExpectedGroup(Guid workGuid, WorkLearning wl, bool isBootstrap = false)
    {
        if (_simEngine is null || wl.DetectedPeriod is not int period || period <= 0)
            return;

        var groupIdx = wl.NextExpectedGroupIdx;
        if (groupIdx < 0 || groupIdx >= period)
            return;

        var shouldFinish = ShouldHoldFinishForGroup(wl, groupIdx, period);
        var currentState = GetWorkStateSafe(workGuid);
        if (isBootstrap && shouldFinish && currentState != Status4.Finish)
            return;

        var nextState = shouldFinish ? Status4.Finish : Status4.Going;
        if (currentState != nextState)
        {
            _simEngine.ForceWorkState(workGuid, nextState);
            if (nextState == Status4.Going)
                ApplyPassiveResetTargets(workGuid);
        }
    }

    private static bool ShouldHoldFinishForGroup(WorkLearning wl, int groupIdx, int period)
    {
        if (wl.WorkFinishGroupIdx is not int finishIdx)
            return false;

        if (wl.WorkGoingStartGroupIdx is not int goingStartIdx)
            return groupIdx >= finishIdx;

        var finishTailEnd = (goingStartIdx - 1 + period) % period;
        return IsCircularInclusive(groupIdx, finishIdx, finishTailEnd, period);
    }

    private static bool IsCircularInclusive(int value, int start, int end, int period)
    {
        if (period <= 0)
            return false;

        if (start <= end)
            return value >= start && value <= end;

        return value >= start || value <= end;
    }

    private Status4 GetWorkStateSafe(Guid workGuid)
    {
        if (_simEngine is null) return Status4.Ready;
        var opt = _simEngine.GetWorkState(workGuid);
        return (opt != null && FSharpOption<Status4>.get_IsSome(opt)) ? opt.Value : Status4.Ready;
    }

    /// Call 상태 전이 유추. Going→Finish 전이가 실제로 일어났으면 true 반환 (theta 증가 트리거용).
    /// Multi Call (ApiCall > 1)은 소속 모든 Device Work 상태가 일치할 때만 전이.
    private Status4 GetCallStateSafe(Guid callGuid)
    {
        if (_simEngine is null) return Status4.Ready;
        var opt = _simEngine.GetCallState(callGuid);
        return (opt != null && FSharpOption<Status4>.get_IsSome(opt)) ? opt.Value : Status4.Ready;
    }

    private WorkLearning GetOrCreateWorkLearning(Guid workGuid)
    {
        if (_workLearning.TryGetValue(workGuid, out var wl))
            return wl;

        wl = new WorkLearning();
        _workLearning[workGuid] = wl;
        return wl;
    }

    private Guid ResolveCanonicalCallGuid(Guid callGuid)
    {
        if (_simEngine is null)
            return callGuid;

        var canonical = _simEngine.Index.CallCanonicalGuids.TryFind(callGuid);
        return (canonical != null && FSharpOption<Guid>.get_IsSome(canonical))
            ? canonical.Value
            : callGuid;
    }

    private string? ResolveWorkPositiveFamilyToken(Guid workGuid, string address, bool isOut)
    {
        if (!_workPositiveFamilyTokens.TryGetValue(workGuid, out var map))
            return null;

        return map.TryGetValue(FamilyAddressKey(isOut ? "Out" : "In", address), out var token)
            ? token
            : null;
    }

    private void ApplyPassiveResetTargets(Guid predWorkGuid)
    {
        if (_simEngine is null)
            return;

        if (!_workResetTargetsByPred.TryGetValue(predWorkGuid, out var targets))
            return;

        foreach (var targetWorkGuid in targets)
        {
            if (targetWorkGuid == predWorkGuid)
                continue;

            if (GetWorkStateSafe(targetWorkGuid) != Status4.Finish)
                continue;

            _simEngine.ForceWorkState(targetWorkGuid, Status4.Ready);

            var workCalls = _simEngine.Index.WorkCallGuids.TryFind(targetWorkGuid);
            if (workCalls == null)
                continue;

            foreach (var callGuid in workCalls.Value)
            {
                if (_callOutHighAddresses.TryGetValue(callGuid, out var outHigh))
                    outHigh.Clear();
                if (_callInHighAddresses.TryGetValue(callGuid, out var inHigh))
                    inHigh.Clear();
                if (GetCallStateSafe(callGuid) != Status4.Ready)
                    _simEngine.ForceCallState(callGuid, Status4.Ready);
            }
        }
    }

    private static void SetPositiveFamilyToken(
        Dictionary<string, string> tokenMap,
        string dir,
        string address,
        IEnumerable<int> ownerOrdinals)
    {
        var ordinals = ownerOrdinals.Distinct().OrderBy(static ordinal => ordinal).ToArray();
        if (ordinals.Length == 0)
            return;

        tokenMap[FamilyAddressKey(dir, address)] = $"{dir}#{string.Join(",", ordinals)}";
    }

    private static string FamilyAddressKey(string dir, string address) => $"{dir}|{address}";

    private static HashSet<string> GetOrAddSignalSet(Dictionary<Guid, HashSet<string>> map, Guid key)
    {
        if (map.TryGetValue(key, out var set))
            return set;

        set = new HashSet<string>(StringComparer.Ordinal);
        map[key] = set;
        return set;
    }

    private static bool HasAllObservedSignals(
        Dictionary<Guid, HashSet<string>> expectedMap,
        Dictionary<Guid, HashSet<string>> highMap,
        Guid key)
    {
        if (!expectedMap.TryGetValue(key, out var expected) || expected.Count == 0)
            return false;

        return highMap.TryGetValue(key, out var high) && high.IsSupersetOf(expected);
    }

    private async Task SyncDeviceWorkStatesFromHub(HubConnection hub)
    {
        if (_simEngine is null) return;
        try
        {
            var ioMap = _simEngine.IOMap;
            // TxWork → (out 주소, in 주소) 매핑 구성
            var deviceWorkIo = new Dictionary<Guid, (string outAddr, string inAddr)>();
            foreach (var m in ioMap.Mappings)
            {
                if (m.TxWorkGuid is null || !FSharpOption<Guid>.get_IsSome(m.TxWorkGuid)) continue;
                var g = m.TxWorkGuid.Value;
                if (!deviceWorkIo.ContainsKey(g))
                    deviceWorkIo[g] = (m.OutAddress ?? "", m.InAddress ?? "");
            }

            int synced = 0;
            foreach (var (workGuid, (outAddr, inAddr)) in deviceWorkIo)
            {
                string outVal = "", inVal = "";
                if (!string.IsNullOrEmpty(outAddr))
                    outVal = await hub.InvokeAsync<string>(HubMethod.QueryTag, outAddr);
                if (!string.IsNullOrEmpty(inAddr))
                    inVal = await hub.InvokeAsync<string>(HubMethod.QueryTag, inAddr);

                var outOn = outVal == "true";
                var inOn = inVal == "true";
                Status4 inferred = (outOn, inOn) switch
                {
                    (false, false) => Status4.Ready,
                    (true,  false) => Status4.Going,
                    (true,  true)  => Status4.Finish,
                    (false, true)  => Status4.Finish,  // In 래치 상태
                };

                _simEngine.ForceWorkState(workGuid, inferred);
                synced++;
            }

            _dispatcher.BeginInvoke(() =>
                AddSimLog($"[Ctrl] Device {synced}개 상태 Hub 싱크 완료", LogSeverity.System));
        }
        catch (Exception ex)
        {
            _dispatcher.BeginInvoke(() =>
                AddSimLog($"[Ctrl] Device 상태 싱크 실패: {ex.Message}", LogSeverity.Warn));
        }
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
        SimStatusText = SimText.Stopped;
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
