using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Backend.Common;
using Ds2.Core;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.Engine.Passive;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.FSharp.Core;

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
        _runtimeSession = null;
        _passiveInference = null;

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
            _runtimeSession = SelectedRuntimeMode == RuntimeMode.Simulation
                ? null
                : new RuntimeModeSession(_simEngine.Index, _simEngine.IOMap, SelectedRuntimeMode);
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
            if (_runtimeSession?.RequiresPassiveInference == true)
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
            var isPassive = _runtimeSession?.StartsWithHomingPhase == false;
            var hasHoming = false;

            // Control 모드: Hub Tag 캐시에서 실제 IO 값 조회 → Device Work 초기 상태 싱크
            //   엔진 Start 전에 완료해야 executeApiCall 첫 호출 시 반영됨 → 동기 대기 (최대 5초)
            //   Hub 연결이 비동기라 Start 시점엔 Connecting 상태일 수 있음 → 내부에서 Connected 대기
            if (_hubConnection is not null && _runtimeSession?.RequiresHubSnapshotSync == true)
            {
                var hub = _hubConnection;
                var runtimeSession = _runtimeSession;
                try
                {
                    AddSimLog($"[Ctrl] Hub 싱크 시작 (Hub 상태={hub.State})", LogSeverity.System);
                    var syncTask = Task.Run(async () =>
                    {
                        // Hub 연결 대기 (최대 3초)
                        var waitStart = DateTime.Now;
                        while (hub.State != HubConnectionState.Connected
                               && runtimeSession is not null
                               && (DateTime.Now - waitStart).TotalMilliseconds < runtimeSession.HubConnectionWaitTimeoutMs)
                        {
                            await Task.Delay(50);
                        }
                        if (hub.State != HubConnectionState.Connected)
                            return false;
                        if (runtimeSession is null)
                            return false;
                        await SyncRuntimeBootstrapStateFromHub(hub, runtimeSession);
                        return true;
                    });
                    if (!syncTask.Wait(runtimeSession?.HubSnapshotSyncTimeoutMs ?? 5000))
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
