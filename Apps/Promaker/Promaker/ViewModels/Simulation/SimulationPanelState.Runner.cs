using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Backend.Common;
using Ds2.Core;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.Engine.Passive;
using Ds2.Runtime.IO;
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
        ClearContinuousSourceCycle();

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

        // Monitoring + 실 PLC 시 트레이 전환 동의 확인 — 사용자가 취소하면 시작 중단.
        // 동의 시 TrayTransitionPending=true 로 두고, 시작 흐름 끝에 FireTrayTransitionIfPending() 호출.
        if (!TryAcquireTrayConsent())
            return;

        try
        {
            var index = SimIndexModule.build(Store, 10);

            // 토큰 역할이 설정되어 있으면 PLAY 전 자동 검증.
            // 단 원위치(BeginHoming) 세션은 deadman switch 라 모달 다이얼로그와 양립 불가 —
            // ShowGraphWarnings 의 모달이 mouse capture 를 가로채면 LostMouseCapture →
            // EndHoming 이 발화해 _homingOnlyMode 가 꺼진 상태로 StartSimulation 이 그대로 완주,
            // 결과적으로 원위치 버튼이 일반 PLAY 와 동일하게 동작한다. 검증은 PLAY 경로 전용.
            var hasPreStartWarnings = false;
            if (!_homingOnlyMode && HasAnyTokenRole(index))
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

            // Hub 모드 진입 직전 — 현재 store 를 DSPilot 공유 AASX 경로에 자동 export.
            // DSPilot 은 같은 파일을 읽어 dspFlow/dspCall 을 생성하므로, 사용자가 별도로 "공유 위치에 저장"
            // 을 누르지 않아도 모니터링 시작과 동시에 두 앱이 같은 모델을 보게 된다.
            // 실패해도 시뮬 시작은 막지 않음 — DSPilot 동기화는 부가 기능.
            if (SelectedRuntimeMode != RuntimeMode.Simulation && PublishAasxForHubMode is not null)
            {
                try
                {
                    var published = PublishAasxForHubMode();
                    if (!published)
                        AddSimLog("DSPilot 공유 경로 AASX 자동 저장 건너뜀 (프로젝트 없음/저장 실패)", LogSeverity.Warn);
                }
                catch (Exception ex)
                {
                    AddSimLog($"DSPilot 공유 경로 AASX 자동 저장 실패: {ex.Message}", LogSeverity.Warn);
                }
            }

            // Hub 시작/연결 (Simulation 모드 이외)
            if (!TryStartHub())
                return;

            Action<string, string>? writeTagAction = null;
            if (_hubConnection is not null && SelectedRuntimeMode == RuntimeMode.Control)
            {
                var hub = _hubConnection;
                var hubGeneration = CurrentHubGeneration;
                var sender = _hubBatchSender;
                writeTagAction = (address, value) =>
                {
                    if (!IsCurrentHubConnection(hubGeneration, hub))
                        return;

                    var state = hub.State;
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        if (IsCurrentHubConnection(hubGeneration, hub))
                            AddSimLog($"[Ctrl→] Out {address}={value} (hub={state})", LogSeverity.Going);
                    });

                    // Batch sender 가 짧은 윈도우 내 다른 WriteTag 들과 묶어 1개 SignalR 프레임으로 송신.
                    sender?.Enqueue(address, value, HubSource.Control);
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
            if (SimSpeed <= 0)
                SimSpeed = 1.0;
            SimTimeIgnore = false;
            _simEngine.SpeedMultiplier = SimSpeed;
            _simEngine.TimeIgnore = false;

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
            _stepPrimingDone = false;
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
                var hubGeneration = CurrentHubGeneration;
                var runtimeSession = _runtimeSession;
                try
                {
                    AddSimLog($"[Ctrl] Hub 싱크 시작 (Hub 상태={hub.State})", LogSeverity.System);
                    var syncTask = Task.Run(async () =>
                    {
                        // Hub 연결 대기 (최대 3초)
                        var waitStart = DateTime.Now;
                        while (hub.State != HubConnectionState.Connected
                               && IsCurrentHubConnection(hubGeneration, hub)
                               && runtimeSession is not null
                               && (DateTime.Now - waitStart).TotalMilliseconds < runtimeSession.HubConnectionWaitTimeoutMs)
                        {
                            await Task.Delay(50);
                        }
                        if (hub.State != HubConnectionState.Connected || !IsCurrentHubConnection(hubGeneration, hub))
                            return false;
                        if (runtimeSession is null)
                            return false;
                        await SyncRuntimeBootstrapStateFromHub(hub, runtimeSession, hubGeneration);
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
                _simEngine.HomingPhaseCompleted += OnHomingPhaseCompleted;
                hasHoming = _simEngine.StartWithHomingPhase();
                if (hasHoming)
                {
                    IsHomingPhase = true;
                    _setStatusText("시뮬레이션 초기화 중...");
                    SimStatusText = "시뮬레이션 초기화 중...";
                }
                else
                    _simEngine.HomingPhaseCompleted -= OnHomingPhaseCompleted;
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

            // 시작 정상 종료 — Monitoring + RealPLC 동의가 있었으면 트레이로 전환.
            FireTrayTransitionIfPending();
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation start failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
            // 시작 실패 시 트레이 보류 플래그 해제 — 다음 시도 때 다시 confirm.
            TrayTransitionPending = false;
        }
    }

    private bool CanStartSimulation() => (!IsSimulating || IsSimPaused) && !IsHomingPhase;

    [RelayCommand(CanExecute = nameof(CanPauseSimulation))]
    private void PauseSimulation()
    {
        // Pause = 시간 정지:
        // 1) SetAllFlowStates(Pause) — work transition 막기 + SyncCurrentTime 으로 sim 시계 elapsed 한 번 반영
        // 2) engine.Pause() — status=Paused, simulation thread 종료 → RuntimeClock 멈춤
        // 3) GanttChart.IsRunning=false — 간트차트의 real-time AdjustedNow 도 정지 (안 그러면 sim 시계는
        //    멈췄어도 간트차트 자체 시계가 real-time 따라 흘러서 시각적으로 시간 흐르는 듯이 보임)
        _simEngine?.SetAllFlowStates(FlowTag.Pause);
        _simEngine?.Pause();
        GanttChart.IsRunning = false;
        // STEP 모드는 Simulation 모드 전용. Control/VP/Monitoring 은 단순 일시 정지.
        var isStepEligible = SelectedRuntimeMode == RuntimeMode.Simulation;
        _isStepMode = isStepEligible;
        SimStatusText = isStepEligible ? SimText.StepMode : SimText.Paused;
        ApplySimulationUiState(
            isSimPaused: true,
            statusText: SimText.Paused,
            logText: isStepEligible ? "단계 제어 모드 진입" : "시뮬레이션 일시 정지");
        RefreshSimulationProgressUi();
    }

    private bool CanPauseSimulation() =>
        IsSimulating
        && !IsSimPaused
        && !IsHomingPhase
        && SelectedRuntimeMode is not (RuntimeMode.VirtualPlant or RuntimeMode.Monitoring)
        && !(SelectedRuntimeMode == RuntimeMode.Control && IsRealPlcConnected);
        // VP/Monitoring 은 외부 Hub 신호로 진행되어 일시정지 자체가 의미 없음 → 버튼 비활성.
        // Control + 실 PLC 연결 시에도 Pause 비활성 — Pause 는 엔진만 freeze 하고
        // 이미 송출된 OUT 코일은 그대로 유지되므로 PLC 측 액추에이터 모션이 멈추지 않음.
        // 사용자가 "Pause = 라인 멈춤" 으로 오해하는 안전 위험 차단. 실 라인 정지는 STOP 사용
        // (BroadcastClearOwnOutputsAsync 로 모든 OUT 을 false 송출 → 솔레노이드 OFF).

    [RelayCommand(CanExecute = nameof(CanStopSimulation))]
    private void StopSimulation()
    {
        AdvanceSimUiGeneration();
        // homing-only 세션 도중 사용자가 STOP 으로 빠져나오는 경우에도 플래그 리셋.
        _homingOnlyMode = false;
        if (_simEngine is not null
            && !TryWithSimEngine("Simulation stop", engine => engine.Stop()))
            return;
        if (_simEngine is not null)
            _simEngine.HomingPhaseCompleted -= OnHomingPhaseCompleted;
        IsHomingPhase = false;
        StopHub();
        ClearSimStateFromCanvas();
        ClearAllWarnings();
        ClearContinuousSourceCycle();
        HasWorkGoing = false;
        HasGoingCall = false;
        _isStepMode = false;
        _stepPrimingDone = false;

        SimStatusText = SimText.Stopped;
        _sceneEventHandler?.Reset();
        ApplySimulationUiState(
            ganttRunning: false,
            isSimulating: false,
            isSimPaused: false,
            statusText: SimText.Stopped,
            logText: SimText.Stopped);

        // 시뮬 종료 시 결과 시나리오 자동 박제 (TechnicalData.SimulationResults).
        // CapturedRuns 에 누적되어 "시뮬레이션 결과 보기" 다이얼로그에 표시된다.
        // 자동 박제는 Simulation 모드 한정 — VP/Control 은 외부 신호 기반이라 의도된 "Run" 경계가 없고
        // scenario 객체가 무거워(_stateChangeRecords 전체 + KPI + traversals) 누적 시 메모리 폭증.
        try
        {
            // 활성 traversal 들을 finalize → KPI 집계가 모든 토큰을 본다.
            // (분기 도중 stuck 된 branch 까지 포함; 완주 branch 가 있으면 그 max 시각으로 기록.)
            FinalizePendingTraversals();
            if (SelectedRuntimeMode == RuntimeMode.Simulation)
                TryCaptureScenario($"Run_{DateTime.Now:yyyyMMdd_HHmmss}");
        }
        catch { /* best-effort */ }

        // 토큰 traversal 누적 초기화 — 다음 Run 이 이전 완주 카운트/이력 위에 누적되지 않도록.
        // (Capture 가 _completedTraversals 를 사용하므로 반드시 capture 이후에 reset.)
        ResetTraversalTracking();

        // 트레이 모드였다면 윈도우 복원 + 아이콘 제거. 평소 모드면 no-op.
        FireTrayRestore();
    }

    private void InitSceneEventHandler()
    {
        _sceneEventHandler = new DeviceSceneEventHandler(ThreeD);
    }

    private bool _pendingFirstStepAfterStart;
    private bool _stepPrimingDone;
    /// <summary>HomingCommand 가 시작한 1회성 원위치 세션. true 이면 OnHomingPhaseCompleted 가
    /// 정상 흐름 대신 자동 StopSimulation 으로 빠진다.</summary>
    private bool _homingOnlyMode;

    /// <summary>원위치 버튼이 "눌린" 상태 — XAML 의 시각 피드백(누름 색)과 양방향 바인딩.
    /// IsHomingButtonHotEnabled 가 자기 세션 도중 enabled 유지되도록 함께 알림.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonHotEnabled))]
    private bool _isHomingPressed;

    /// <summary>원위치 push-button 의 "누름" 진입 핸들러 (deadman switch 시작).
    /// 누른 시점에 hub+PLC 게이트웨이를 띄우고 자동 원위치 페이즈를 시작한다.
    /// 이 호출 후 사용자는 버튼을 *놓을 때까지* 누르고 있어야 한다 — 떼는 순간 EndHoming 이 STOP 한다.
    /// Control + 실 PLC 연결 + 정지 상태에서만 의미 있음 (XAML 가시성/Enabled 가 강제).</summary>
    public void BeginHoming()
    {
        if (!CanBeginHoming()) return;

        AddSimLog("원위치 시작 (버튼 누름) — 실 PLC 라인 home 복귀 진행", LogSeverity.System);
        _homingOnlyMode = true;
        IsHomingPressed = true;

        StartSimulation();

        if (_simEngine is null)
        {
            // Hub/PLC 시작 실패 — StartSimulation 내부에서 이미 로그/오류 기록됨.
            AddSimLog("원위치 중단 — 엔진 초기화 실패", LogSeverity.Error);
            _homingOnlyMode = false;
            IsHomingPressed = false;
            return;
        }

        // 진단용 — 엔진이 본 OUT 주소 카운트, homing 진입 여부.
        var outCount = _simEngine.IOMap.OutAddressToMappings.Count;
        var inCount = _simEngine.IOMap.InAddressToMappings.Count;
        AddSimLog(
            $"엔진 시작 OK — IO map: OUT={outCount}, IN={inCount}, homingPhase={IsHomingPhase}",
            LogSeverity.System);

        // 진단 — engine 의 home 관련 정보가 모델에서 얼마나 채워졌는지 dump.
        // (모델 구조가 적절하면 자동 plan 동작; 비어있으면 길 B 로 가도 식별 정보 없음)
        try
        {
            var idx = _simEngine.Index;
            var iom = _simEngine.IOMap;
            var totalWorks = idx.AllWorkGuids.Length;
            var totalCalls = idx.AllCallGuids.Length;

            // findInitialFlagRxWorkGuids — "fresh-play 시 Finish 상태여야 하는" Work
            var finishedFlag = SimIndexModule.findInitialFlagRxWorkGuids(idx);

            // computeAutoHomingPlan — (Finish 대상, Ready 대상) Work 집합. ADV/RET 컨벤션 + Start arrow 분석.
            var plan = SimIndexModule.computeAutoHomingPlan(idx);
            var planFinish = plan.Item1.Count;
            var planReady = plan.Item2.Count;

            // WorkResetPreds — 각 Work 의 reset partner 매핑 (이 Work 를 home 으로 되돌릴 partner Work).
            var resetPredsCount = idx.WorkResetPreds.Count(kv => kv.Value.Length > 0);

            // IO map size
            var outAddrs = iom.OutAddressToMappings.Count;
            var inAddrs = iom.InAddressToMappings.Count;
            var txWorks = iom.TxWorkToOutAddresses.Count;
            var rxWorks = iom.RxWorkToInAddresses.Count;

            // ApiName 컨벤션 확인 (ADV/RET 등)
            var apiNames = _storeProvider().Calls.Values
                .Select(c => c.ApiName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .Take(10)
                .ToArray();
            var apiNameSample = apiNames.Length > 0 ? string.Join(",", apiNames) : "(없음)";

            AddSimLog(
                $"[원위치 진단:1] AllWorks={totalWorks} AllCalls={totalCalls} " +
                $"OUT주소={outAddrs} IN주소={inAddrs} TxWorks={txWorks} RxWorks={rxWorks}",
                LogSeverity.Info);
            AddSimLog(
                $"[원위치 진단:2] computeAutoHomingPlan=(Finish:{planFinish}, Ready:{planReady})  " +
                $"isFinishedFlagged={finishedFlag.Count}  WorkResetPreds(non-empty)={resetPredsCount}",
                (planFinish + planReady + finishedFlag.Count + resetPredsCount) == 0 ? LogSeverity.Warn : LogSeverity.Info);
            AddSimLog(
                $"[원위치 진단:3] ApiName 샘플=[{apiNameSample}]",
                LogSeverity.Info);

            // 해석 가이드 — 사용자에게 무엇이 부족한지 명확히.
            if (planFinish == 0 && planReady == 0 && finishedFlag.Count == 0 && resetPredsCount == 0)
            {
                AddSimLog(
                    "[원위치 진단:결론] 엔진이 모델에서 home 관련 메타를 0개 인식. " +
                    "→ AASX 모델에 (a) Active Work 안에 같은 Device 의 ADV/RET Call + Start 화살표, 또는 " +
                    "(b) WorkProperties.IsFinished 수동 플래그, 또는 (c) Reset 화살표(ResetReset)가 있어야 " +
                    "engine 또는 길 B 가 home OUT 을 식별 가능합니다.",
                    LogSeverity.Warn);
            }
        }
        catch (Exception ex)
        {
            AddSimLog($"[원위치 진단] failed: {ex.Message}", LogSeverity.Warn);
        }

        if (!IsHomingPhase)
        {
            // 엔진의 정적 homing plan 이 비었지만 (이미 모델 설계대로면 모든 Work 가 home 상태라고 결론),
            // 사용자가 매뉴얼로 디바이스를 움직여 *현재 PLC 상태와 모델 예상이 어긋난* 케이스가 있을 수 있음.
            // 엔진 우회 — engine-aware runtime homing: 엔진의 정적 plan(finishTargets/readyTargets) +
            // WorkResetPreds + 현재 PLC IN 상태를 결합해 어긋난 Work 들의 home OUT 을 직접 송출.
            // PauseSimulation 으로 엔진 cycle 차단된 상태에서 동작 → DS 규칙 위반 없음 (manual control 과 동일 모델).
            // PLC 가 OUT 신호를 받아 액추에이터 구동 → IN echo 가 돌아오면 home 도달.
            // 사용자 release 시 BroadcastClearOwn 이 모든 OUT off → 안전 종료.
            _ = DriveEngineAwareHomingAsync();
        }
        else
        {
            AddSimLog("원위치 페이즈 진입 — 엔진이 OUT 송출 중. PLC 응답 대기.", LogSeverity.Info);
        }
    }

    /// <summary>
    /// engine-aware push-button homing: 엔진의 정적 home 메타(<c>computeAutoHomingPlan</c> +
    /// <c>WorkResetPreds</c>)와 현재 PLC IN 상태를 비교해, 어긋난 Work 의 home OUT 을 직접 송출.
    /// 엔진의 <c>StartWithHomingPhase</c> 가 빈 plan 으로 빠지는 케이스(=정적 model 분석으로는
    /// 이미 home 이라 결론)에 대한 보강. PauseSimulation 으로 cycle 차단된 paused engine 위에서 동작.
    /// </summary>
    private async Task DriveEngineAwareHomingAsync()
    {
        if (_simEngine is null) return;

        var idx = _simEngine.Index;
        var iom = _simEngine.IOMap;
        var plan = SimIndexModule.computeAutoHomingPlan(idx);
        var finishTargets = plan.Item1;   // Set<Guid> — home 시 Finish 상태여야 할 Work
        var readyTargets = plan.Item2;    // Set<Guid> — home 시 Ready 상태여야 할 Work

        // 모든 IN 주소를 hub cache 에서 일괄 조회 — initial scan 으로 이미 채워져 있음.
        var inValues = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var allInAddrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in iom.RxWorkToInAddresses)
            foreach (var addr in kv.Value)
                if (!string.IsNullOrWhiteSpace(addr)) allInAddrs.Add(addr);
        foreach (var addr in allInAddrs)
        {
            var v = await QueryTagFromManualAsync(addr);
            inValues[addr] = v == "true";
        }

        // 의도된 (work → 송출할 OUT 후보 + 어느 Call 이 source 인지) 매핑을 먼저 모은다.
        // 다음 단계에서 Call 의 ComAux condition 을 평가해 *통과한 Call 의 OUT 만* 실 송출.
        var candidates = new List<(string outAddr, Guid callGuid, string workName, string reason)>();

        foreach (var workGuid in idx.AllWorkGuids)
        {
            // Work 의 IO addresses
            string[] outAddrs = iom.TxWorkToOutAddresses.TryGetValue(workGuid, out var outs)
                ? outs.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToArray()
                : Array.Empty<string>();
            string[] inAddrs = iom.RxWorkToInAddresses.TryGetValue(workGuid, out var ins)
                ? ins.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToArray()
                : Array.Empty<string>();
            if (outAddrs.Length == 0 && inAddrs.Length == 0) continue;

            var workName = idx.WorkName.TryGetValue(workGuid, out var n) ? n : workGuid.ToString();
            var inOn = inAddrs.Any(a => inValues.TryGetValue(a, out var b) && b);

            if (finishTargets.Contains(workGuid) && !inOn)
            {
                // 목표 Finish 인데 IN=false → 이 Work 의 OUT 을 firing 해서 home(Finish)으로 보냄.
                foreach (var oa in outAddrs)
                    foreach (var (cg, _) in CallsForOutAddress(iom, oa))
                        candidates.Add((oa, cg, workName, "FIRE 목표=Finish, 현재 IN=false"));
            }
            else if (readyTargets.Contains(workGuid) && inOn)
            {
                // 목표 Ready 인데 IN=true → reset partner 의 OUT 을 firing 해서 reset.
                if (idx.WorkResetPreds.TryGetValue(workGuid, out var partners) && partners.Any())
                {
                    foreach (var partnerGuid in partners)
                    {
                        if (iom.TxWorkToOutAddresses.TryGetValue(partnerGuid, out var pOuts))
                        {
                            foreach (var po in pOuts.Where(a => !string.IsNullOrWhiteSpace(a)))
                                foreach (var (cg, _) in CallsForOutAddress(iom, po))
                                    candidates.Add((po, cg, workName, "RESET via partner 목표=Ready, 현재 IN=true"));
                        }
                    }
                }
                else
                {
                    AddSimLog($"  · ⚠ {workName}: 목표=Ready, IN=true 이지만 WorkResetPreds 없음 → reset 불가", LogSeverity.Warn);
                }
            }
        }

        if (candidates.Count == 0)
        {
            AddSimLog("[engine-aware homing] 어긋난 Work 없음 — 모든 디바이스가 이미 home 위치", LogSeverity.System);
            return;
        }

        // ── B1 안전 게이트: 각 candidate 의 Call 에 대해 ComAux 조건 평가 ────────────────
        // ComAux = "공통 보조 조건" = 비상정지/안전문/시스템 인터록 같이 *모든 모드에서 항상 충족돼야 하는*
        // 안전 floor. 모델러가 정의한 그대로 평가 — 미충족 Call 의 OUT 은 firing 차단.
        // 엔진의 정상 cycle 에서 canStartCall 이 적용하는 것과 동일한 evaluator (DS 규칙 일관성).
        var state = _simEngine.State;
        var blockedByComAux = new List<string>();
        var passedCandidates = new List<(string outAddr, Guid callGuid, string workName, string reason)>();
        var blockedCallNames = new HashSet<string>();
        var seenCallGuids = new HashSet<Guid>();

        foreach (var c in candidates)
        {
            bool comAuxOk = true;
            if (idx.CallComAuxConditions.TryGetValue(c.callGuid, out var expr))
            {
                comAuxOk = WorkConditionChecker.evaluateConditionExpression(state, expr);
            }
            if (comAuxOk)
            {
                passedCandidates.Add(c);
            }
            else if (seenCallGuids.Add(c.callGuid))
            {
                var callName = _storeProvider().Calls.TryGetValue(c.callGuid, out var call)
                    ? call.Name : c.callGuid.ToString();
                blockedByComAux.Add($"{callName} ({c.workName})");
                blockedCallNames.Add(callName);
            }
        }

        if (blockedByComAux.Count > 0)
        {
            AddSimLog(
                $"⛔ [B1 안전 차단] ComAux 조건 미충족 Call {blockedByComAux.Count}건 — 해당 OUT 송출 차단됨. " +
                $"비상정지·안전문·인터록 등 공통 안전 조건이 미충족 상태입니다.",
                LogSeverity.Error);
            foreach (var bn in blockedByComAux.Take(8))
                AddSimLog($"  ⛔ {bn}", LogSeverity.Error);
            if (blockedByComAux.Count > 8)
                AddSimLog($"  ⛔ ... 외 {blockedByComAux.Count - 8}건", LogSeverity.Error);
        }

        // OUT 주소 dedup (여러 Call 이 같은 OUT 공유 시 중복 송출 방지).
        // 단 한 Call 이라도 ComAux 통과한 경우만 OUT 송출 (= OR 정책).
        // 보수적 정책 원하면 ALL pass 요구로 바꿀 수 있음 — 현재는 일관 firing 보장 위해 OR.
        var outsToFire = new HashSet<string>(passedCandidates.Select(c => c.outAddr), StringComparer.OrdinalIgnoreCase);

        if (outsToFire.Count == 0)
        {
            AddSimLog(
                "[engine-aware homing] 통과한 OUT 0개 — 모든 후보가 ComAux 차단됨. " +
                "안전 조건 확인 후 다시 시도하세요.",
                LogSeverity.Warn);
            return;
        }

        AddSimLog(
            $"[engine-aware homing] {candidates.Count}개 후보 → ComAux 통과 {outsToFire.Count}개 OUT firing " +
            $"(차단 {blockedByComAux.Count}건)",
            LogSeverity.System);

        // 진단 — 통과한 케이스 일부 표시.
        var passedSample = passedCandidates.GroupBy(c => c.workName).Take(8);
        foreach (var grp in passedSample)
            AddSimLog($"  · {grp.First().reason}: {grp.Key}", LogSeverity.Info);

        // 송출. 모든 OUT 을 source="control" 로. PLC 가 OUT=true 를 받아 액추에이터 구동.
        // 사용자 release 시 StopHub 의 BroadcastClearOwnOutputsAsync 가 모든 OUT 을 false 로 → 안전 정지.
        foreach (var addr in outsToFire)
        {
            if (!IsHomingPressed) break;   // release 시 즉시 중단
            var ok = await WriteTagFromManualAsync(addr, "true");
            if (!ok)
                AddSimLog($"[engine-aware homing] 송신 실패: {addr}", LogSeverity.Warn);
        }
    }

    /// <summary>OUT 주소가 속한 Call 들 — 같은 OUT 을 여러 Call 이 공유 가능 (OR group 등).</summary>
    private static IEnumerable<(Guid CallGuid, Guid ApiCallGuid)> CallsForOutAddress(SignalIOMap iom, string outAddress)
    {
        if (iom.OutAddressToMappings.TryGetValue(outAddress, out var mappings))
            foreach (var m in mappings)
                yield return (m.CallGuid, m.ApiCallGuid);
    }

    /// <summary>원위치 push-button 의 "놓음" 핸들러 (deadman switch 종료).
    /// homing 도중이든 완료 후이든, 버튼이 떼지면 즉시 StopSimulation 으로 깨끗이 정리.
    /// StopSimulation 의 BroadcastClearOwnOutputsAsync 가 자기 OUT 들을 모두 false 로 broadcast 해
    /// PLC 측 실 액추에이터 모션도 안전하게 중단된다.</summary>
    public void EndHoming()
    {
        if (!IsHomingPressed) return;
        IsHomingPressed = false;
        _homingOnlyMode = false;

        if (IsSimulating)
        {
            AddSimLog("원위치 종료 (버튼 놓음) — 시뮬·Hub·PLC 게이트웨이 정리", LogSeverity.System);
            StopSimulation();
        }
    }

    /// <summary>BeginHoming 진입 가능 조건 — XAML 측 Visibility/Enabled 와 별개로 백엔드 가드.</summary>
    public bool CanBeginHoming() =>
        SelectedRuntimeMode == RuntimeMode.Control
        && IsRealPlcConnected
        && !IsSimulating
        && !IsHomingPhase
        && !IsHomingPressed;

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

            // Push-button 원위치 모드: 엔진이 home 도달을 알려도 STOP 하지 않고 사용자 release 까지 유지.
            // 단 그대로 Run 상태로 두면 token source 가 발화해 의도치 않은 cycle 시작이 가능 — 즉시 Pause 로 잠금.
            // 버튼 놓으면 EndHoming → StopSimulation 으로 정리.
            if (_homingOnlyMode)
            {
                if (CanPauseSimulation())
                {
                    PauseSimulation();
                }
                AddSimLog("원위치 완료 — 엔진 정지. 버튼 놓으면 세션 종료.", LogSeverity.System);
                SimStatusText = "원위치 완료 — 버튼 놓으면 종료";
                return;
            }

            // STEP 으로 시뮬을 시작한 경우: Homing 완료 후 자동 PauseSimulation + STEP 재진입.
            if (_pendingFirstStepAfterStart)
            {
                _pendingFirstStepAfterStart = false;
                PauseSimulation();
                _ = StepSimulationAsync();
            }
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
        _stepPrimingDone = false;
        SimStatusText = SimText.Reset;
        ApplySimulationUiState(
            statusText: SimText.Reset,
            logText: SimText.ResetLog);
    }

    private bool CanResetSimulation() => IsSimulating;

    [RelayCommand(CanExecute = nameof(CanStepSimulation))]
    private async Task StepSimulationAsync()
    {
        // 시뮬 시작 안 한 상태면 STEP 으로 Start.
        if (!IsSimulating)
        {
            // Homing 페이즈가 있는 모델이면 OnHomingPhaseCompleted 가 끝난 후 자동으로
            // PauseSimulation + STEP 재호출 하도록 flag set. (polling/Sleep 안 씀)
            _pendingFirstStepAfterStart = true;
            StartSimulation();
            if (_simEngine is null)
            {
                _pendingFirstStepAfterStart = false;
                return;
            }
            if (IsHomingPhase)
                return;  // OnHomingPhaseCompleted 가 이어서 처리

            _pendingFirstStepAfterStart = false;
            PauseSimulation();
        }

        if (_simEngine is null) return;
        var engine = _simEngine;
        SimStatusText = SimText.StepMode;

        var (selectedSourceGuid, autoStartSources) = GetStepAdvanceSelection();

        // Source priming — *첫 STEP* 에만 호출. 이후 STEP 에선 자연 cascade/cycle 따라감.
        // 매 STEP 호출 시 NewFlow.NewWork 가 cycle 끝나서 Ready 로 돌아오는 시점마다 다시
        // ForceWorkState(Going) 호출되면 NewFlow1 안 끝났는데 NewFlow 가 또 시작하는 race 발생.
        if (!_stepPrimingDone)
        {
            if (autoStartSources)
            {
                foreach (var sg in engine.Index.TokenSourceGuids)
                {
                    if (engine.GetWorkToken(sg) is null)
                        engine.StartSourceWork(sg);
                }
            }
            else if (selectedSourceGuid != Guid.Empty)
            {
                if (SimIndexModule.isTokenSource(engine.Index, selectedSourceGuid))
                {
                    if (engine.GetWorkToken(selectedSourceGuid) is null)
                        engine.StartSourceWork(selectedSourceGuid);
                }
                else
                {
                    var st = engine.GetWorkState(selectedSourceGuid);
                    if (st is not null && FSharpOption<Status4>.get_IsSome(st) && st.Value == Status4.Ready)
                        engine.ForceWorkState(selectedSourceGuid, Status4.Going);
                }
            }
            _stepPrimingDone = true;
        }

        Guid[] batch;

        try
        {
            batch = engine.BeginStepBatch(selectedSourceGuid, autoStartSources);
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation step failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
            return;
        }

        var progressed = false;

        // batch 의 unit 모두 finish 까지 단계적 advance + wait.
        // 각 nextEventTime 까지 real-time wait 후 그 시점에 AdvanceSimulationTo 호출 →
        // 이벤트들이 그 시점에 발화 → GanttChart 가 시간선 따라 표시 (점프 X).
        GanttChart.IsRunning = true;
        try
        {
            var guard = 0;
            while (engine.IsStepBatchActive(batch) && guard < 256)
            {
                guard++;
                var nextEventTime = engine.NextEventTimeMs;
                if (nextEventTime is null) break;

                var simDelta = nextEventTime.Value - engine.CurrentTimeMs;
                if (simDelta > 0)
                {
                    var speed = Math.Max(0.000001, engine.SpeedMultiplier);
                    var realWaitMs = (int)Math.Ceiling(simDelta / speed);
                    if (realWaitMs > 0)
                        await Task.Delay(realWaitMs);
                }
                engine.AdvanceSimulationTo(nextEventTime.Value);
                progressed = true;
            }
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation step advance failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
        }
        finally
        {
            engine.EndStep();
            GanttChart.IsRunning = false;
            GanttChart.CurrentTime = ToGanttTimestamp(engine.State.Clock);
        }

        AddSimLog(progressed ? "STEP 실행" : "STEP 진행 없음", LogSeverity.System);
        RefreshSimulationProgressUi();
        SimStatusText = SimText.Paused;
    }

    private bool CanStepSimulation() =>
        (!IsSimulating || IsSimPaused)
        && !IsHomingPhase
        && SelectedRuntimeMode == RuntimeMode.Simulation;
        // STEP 은 Simulation 모드 전용. Control 은 외부 Hub 신호로 진행되어 단계적 advance 의미 없음,
        // VP/Monitoring 도 외부 신호 owner 라 STEP 부적절.
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
        SimTimeIgnore = false;
        SimStatusText = SimText.Stopped;
        _stateCache.Clear();
        _suppressedWarnings.Clear();
        ClearContinuousSourceCycle();
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
