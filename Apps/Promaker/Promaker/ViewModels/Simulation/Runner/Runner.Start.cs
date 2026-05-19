using System;
using System.Linq;
using System.Threading.Tasks;
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
            var raceWarnings = GraphWarningProjection.findRaceConditionWarnings(index);
            if (raceWarnings.Length > 0)
            {
                hasPreStartWarnings = true;
                AddSimLog($"[WARN] Race Condition: 순서 없는 Call {raceWarnings.Length}쌍이 동일 Device ResetReset 관계 — 먼저 스케줄된 Call만 실행됩니다", LogSeverity.Warn);
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

    private bool CanStartSimulation() =>
        RuntimeCommandPolicy.canStartSimulation(IsSimulating, IsSimPaused, IsHomingPhase);
}
