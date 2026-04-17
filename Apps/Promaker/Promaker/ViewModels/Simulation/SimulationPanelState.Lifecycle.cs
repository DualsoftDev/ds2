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
        SimEventLog.Insert(0, new SimLogEntry($"[{ts}]{prefix} {message}", severity));
        if (SimEventLog.Count > 500)
            SimEventLog.RemoveAt(SimEventLog.Count - 1);
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
                // OutTag 수신 → Device Work Going → Duration 후 InTag 응답
                var ioMap = _simEngine.IOMap;
                var mappings = ioMap.GetByOutAddress(address);
                // 첫 번째 매핑으로 Device Work 처리 (Out 주소당 Device Work는 1개)
                var firstMapping = mappings.IsEmpty ? null : mappings.Head;
                if (firstMapping is { } mapping)
                {
                    var txWorkGuidOpt = mapping.TxWorkGuid;
                    if (txWorkGuidOpt != null && FSharpOption<Guid>.get_IsSome(txWorkGuidOpt))
                    {
                        var txWorkGuid = txWorkGuidOpt.Value;
                        var callGuid = mapping.CallGuid;
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
                                            // 자기 자신도 In OFF 판정 (상호 리셋)
                                            var resetInMappings = ioMap.GetByInAddress(resetInAddr);
                                            var doneReset = new HashSet<Guid>();
                                            foreach (var rim in resetInMappings)
                                                if (doneReset.Add(rim.ApiCallGuid))
                                                    PassiveStateInferByApiCall(rim.ApiCallGuid, resetInAddr, "false");
                                            _dispatcher.BeginInvoke(() =>
                                                AddSimLog($"[VP] 상호리셋: {resetInAddr}=false", LogSeverity.Homing));
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
                                Task.Run(async () =>
                                {
                                    await Task.Delay(duration);
                                    if (_simEngine is not null)
                                    {
                                        _simEngine.ForceWorkState(txWorkGuid, Status4.Finish);
                                        await hub.InvokeAsync(HubMethod.WriteTag, inAddress, "true", HubSource.VirtualPlant);
                                        // 자기 자신도 In ON 판정 (Hub 수신은 source 필터로 무시되므로)
                                        var inMappings = _simEngine.IOMap.GetByInAddress(inAddress);
                                        var done = new HashSet<Guid>();
                                        foreach (var im in inMappings)
                                            if (done.Add(im.ApiCallGuid))
                                                PassiveStateInferByApiCall(im.ApiCallGuid, inAddress, "true");
                                        _dispatcher.BeginInvoke(() =>
                                            AddSimLog($"[VP→] In ON: {inAddress} (after {duration}ms)", LogSeverity.Finish));
                                    }
                                });
                            }
                        }
                        else
                        {
                            // Out OFF → In OFF + Device Work H→R
                            var inAddress = mapping.InAddress;
                            var hub = _hubConnection;
                            if (hub is not null && !string.IsNullOrEmpty(inAddress))
                            {
                                var inAddr2 = inAddress;
                                Task.Run(async () =>
                                {
                                    await hub.InvokeAsync(HubMethod.WriteTag, inAddr2, "false", HubSource.VirtualPlant);
                                    // 자기 자신도 In OFF 판정
                                    if (_simEngine is not null)
                                    {
                                        var inMappings2 = _simEngine.IOMap.GetByInAddress(inAddr2);
                                        var done2 = new HashSet<Guid>();
                                        foreach (var im in inMappings2)
                                            if (done2.Add(im.ApiCallGuid))
                                                PassiveStateInferByApiCall(im.ApiCallGuid, inAddr2, "false");
                                    }
                                    _dispatcher.BeginInvoke(() =>
                                        AddSimLog($"[VP→] In OFF: {inAddr2}", LogSeverity.Ready));
                                });
                            }
                        }

                        // IO 에지 기반 상태 유추 — ApiCall 단위로 한 번만, 공유 Call 전부 적용
                        var processedApiCalls = new HashSet<Guid>();
                        foreach (var m in mappings)
                        {
                            if (processedApiCalls.Add(m.ApiCallGuid))
                                PassiveStateInferByApiCall(m.ApiCallGuid, address, value);
                        }
                    }
                }
                break;
            }

            case RuntimeMode.Monitoring:
            {
                var ioMap = _simEngine.IOMap;
                var outMappings = ioMap.GetByOutAddress(address);
                var inMappings = ioMap.GetByInAddress(address);
                var allMappings = outMappings.IsEmpty ? inMappings : outMappings;
                var processedApiCalls = new HashSet<Guid>();
                foreach (var m in allMappings)
                {
                    if (processedApiCalls.Add(m.ApiCallGuid))
                        PassiveStateInferByApiCall(m.ApiCallGuid, address, value);
                }

                _dispatcher.BeginInvoke(() =>
                    AddSimLog($"[Mon] {address}={value} (from {source})", LogSeverity.Info));
                break;
            }
        }
    }

    // ── Passive 상태 유추 (VirtualPlant / Monitoring) ──────────────────

    /// Call별 Out 선행 여부 추적 (이번 사이클에서 Out ON을 본 적 있는지)
    private readonly Dictionary<Guid, bool> _callOutSeen = [];
    /// Call별 F 래치 (Out OFF에 대해서만 래치, In OFF → H)
    private readonly HashSet<Guid> _callFinishLatched = [];

    /// ApiCall 단위로 상태 판정 → 공유하는 모든 Call에 적용
    private void PassiveStateInferByApiCall(Guid apiCallGuid, string address, string value)
    {
        if (_simEngine is null) return;

        // 이 ApiCall을 공유하는 모든 Call 찾기
        var sharingCalls = new List<Guid>();
        foreach (var call in _simEngine.Index.Store.Calls.Values)
            foreach (var ac in call.ApiCalls)
                if (ac.Id == apiCallGuid && !sharingCalls.Contains(call.Id))
                    sharingCalls.Add(call.Id);

        if (sharingCalls.Count == 0) return;

        // 첫 번째 Call 기준으로 판정
        var primaryCallGuid = sharingCalls[0];
        var newState = InferCallStateFromIO(primaryCallGuid, address, value);

        if (newState is { } ncs)
        {
            // 모든 공유 Call에 같은 상태 적용
            var affectedWorks = new HashSet<Guid>();
            foreach (var cg in sharingCalls)
            {
                _simEngine.ForceCallState(cg, ncs);
                if (ncs == Status4.Going)
                    _callOutSeen[cg] = true;
                if (ncs == Status4.Homing || ncs == Status4.Ready)
                    _callOutSeen[cg] = false;

                var callWorkMap = _simEngine.Index.CallWorkGuid;
                if (callWorkMap.ContainsKey(cg))
                    affectedWorks.Add(callWorkMap[cg]);
            }

            foreach (var wg in affectedWorks)
                InferWorkState(wg);
        }
    }

    /// IO 에지로 Call 상태 판정 (상태만 반환, 적용은 안 함)
    private Status4? InferCallStateFromIO(Guid callGuid, string address, string value)
    {
        if (_simEngine is null) return null;
        var ioMap = _simEngine.IOMap;

        var outOpt = ioMap.TryGetByOutAddress(address);
        var isOut = outOpt != null && FSharpOption<SignalMapping>.get_IsSome(outOpt);
        var cs = GetCallStateValue(callGuid);

        if (isOut && value == "true")
        {
            if (cs == Status4.Ready)
                return Status4.Going;
        }
        else if (isOut && value == "false")
        {
            if (cs == Status4.Going)
                return Status4.Homing;
        }
        else if (!isOut && value == "true")
        {
            if (cs == Status4.Going && _callOutSeen.GetValueOrDefault(callGuid))
                return Status4.Finish;
        }
        else if (!isOut && value == "false")
        {
            if (cs == Status4.Finish)
                return Status4.Homing;
        }

        return null;
    }

    private Status4 GetCallStateValue(Guid callGuid)
    {
        var opt = _simEngine?.GetCallState(callGuid);
        return opt != null && FSharpOption<Status4>.get_IsSome(opt) ? opt.Value : Status4.Ready;
    }

    private Status4 GetWorkStateValue(Guid workGuid)
    {
        var opt = _simEngine?.GetWorkState(workGuid);
        return opt != null && FSharpOption<Status4>.get_IsSome(opt) ? opt.Value : Status4.Ready;
    }

    /// Call 상태 합산 → Work 상태 판정 + H 탑다운 전파
    private void InferWorkState(Guid workGuid)
    {
        if (_simEngine is null) return;
        var workCallGuids = _simEngine.Index.WorkCallGuids;
        if (!workCallGuids.ContainsKey(workGuid)) return;
        var callGuids = workCallGuids[workGuid];

        var states = new List<Status4>();
        foreach (var cg in callGuids)
            states.Add(GetCallStateValue(cg));

        if (states.Count == 0) return;

        Status4 newWorkState;
        if (states.All(s => s == Status4.Ready))
            newWorkState = Status4.Ready;
        else if (states.Any(s => s == Status4.Going))
            newWorkState = Status4.Going;
        else if (states.All(s => s == Status4.Finish))
            newWorkState = Status4.Finish;
        else
            newWorkState = Status4.Homing;

        var currentWork = GetWorkStateValue(workGuid);
        if (currentWork == newWorkState) return;

        _simEngine.ForceWorkState(workGuid, newWorkState);

        // Work H → Call 탑다운 H + outSeen/래치 리셋
        if (newWorkState == Status4.Homing)
        {
            PropagateWorkHomingToCalls(workGuid, callGuids);
        }

        // Work F → 리셋 화살표 없으면 자동 H→R (비동기)
        if (newWorkState == Status4.Finish)
        {
            var resetPreds = _simEngine.Index.WorkResetPreds;
            var hasResetPred = resetPreds.ContainsKey(workGuid) && !resetPreds[workGuid].IsEmpty;
            if (!hasResetPred)
            {
                _simEngine.ForceWorkState(workGuid, Status4.Homing);
                PropagateWorkHomingToCalls(workGuid, callGuids);
                _simEngine.ForceWorkState(workGuid, Status4.Ready);
            }
            // 리셋 화살표 있으면 — 엔진의 evaluateWorkResets가 처리
        }
    }

    /// Work H → 소속 Call 전부 H (탑다운) + outSeen/래치 리셋
    private void PropagateWorkHomingToCalls(Guid workGuid, IEnumerable<Guid> callGuids)
    {
        foreach (var cg in callGuids)
        {
            _simEngine!.ForceCallState(cg, Status4.Homing);
            _simEngine!.ForceCallState(cg, Status4.Ready);
            _callOutSeen[cg] = false;
            _callFinishLatched.Remove(cg);
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
