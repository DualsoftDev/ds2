using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Ds2.Backend.Common;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.Engine.Passive;
using Ds2.Runtime.IO;
using Ds2.Runtime.Remote;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.FSharp.Core;
using Promaker.Shared;

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

        // Agent 위임 모드 + 이미 모니터링 중 — "Agent 전송" 재누름을 설정 갱신 명령으로 해석.
        // (CanStartSimulation 이 IsSimulating 중 이 경로를 막으므로 실제로는 도달하지 않지만 안전망으로 유지.)
        // 자체 엔진/Hub 를 끊지 않고: AASX 재발행 + active.flag 재기록 → Agent 가 새 설정으로 재시작.
        if (IsAgentDelegationMode && IsSimulating)
        {
            try
            {
                if (PublishAasxForHubMode is not null)
                {
                    try { PublishAasxForHubMode(); }
                    catch (Exception ex)
                    {
                        AddSimLog($"DSPilot 공유 AASX 재발행 실패 (갱신 계속): {ex.Message}", LogSeverity.Warn);
                    }
                }

                // PLC 설정 다이얼로그가 이미 저장하지만, 사용자가 우회로 PlcSettings 만 바꾼 케이스 보강.
                PlcSettings.Save();

                var session = AgentSession.ForCurrentDefaults(requestedBy: "promaker");
                if (session.TryWrite())
                    AddSimLog("Agent 모니터링 갱신 명령 전송 — 새 PLC/AASX 설정 적용 중", LogSeverity.System);
                else
                    AddSimLog("Agent 갱신 명령 기록 실패 — 공유 폴더 권한을 확인하세요.", LogSeverity.Error);
            }
            catch (Exception ex)
            {
                AddSimLog($"Agent 갱신 전송 중 예외: {ex.Message}", LogSeverity.Error);
            }
            return;
        }

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

            // v10 §12 — ApiDef/ApiCall V1~V6 invariant 점검. Error 면 시뮬 시작 중단, Warning 은 로그.
            var v10Issues = V10ValidationBatch.validateStore(Store);
            // Simulation 모드는 가상 시뮬레이션이라 실 I/O 신호가 불필요 — V1(Real⇒OutTag)/V2(Real⇒InTag)
            // invariant 를 면제해 I/O 미설정 모델도 시뮬 가능하게 연다. Control/Monitoring 은 실 I/O 가 진실원이라 그대로 강제.
            var v10Errors = v10Issues
                .Where(i => i.Severity.IsError)
                .Where(i => !(SelectedRuntimeMode == RuntimeMode.Simulation && (i.Rule == "V1" || i.Rule == "V2")))
                .ToList();
            var v10Warnings = v10Issues.Where(i => i.Severity.IsWarning).ToList();
            foreach (var w in v10Warnings)
                AddSimLog($"[v10 {w.Rule}] {w.Message}", LogSeverity.Warn);
            if (v10Errors.Count > 0)
            {
                foreach (var e in v10Errors)
                    AddSimLog($"[v10 {e.Rule}] {e.Message}", LogSeverity.Error);
                _setStatusText($"v10 모델 검증 실패: Error {v10Errors.Count}건 — 시뮬 시작 중단");
                return;
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

            // Agent 위임 모드 (Monitoring + 실 PLC) 진입 직전 — 현재 store 를 DSPilot 공유 AASX 경로에 자동 export.
            // Promaker.Agent (SYSTEM) 이 같은 파일을 IOMap 빌드용으로 읽고 DSPilot 이 dspFlow/dspCall 적재 →
            // "Agent 전송" 한 번에 두 소비자가 같은 모델을 보게 된다.
            // Control / VirtualPlant / Monitoring(가상 PLC) 에서는 BackendHost 가 없어 DSPilot 실시간 데이터가
            // 비어 있으므로 publish 의 실효 가치 없음 — 광역 publish 는 false 외부변경 알림과 디스크 IO 만 유발.
            // 실패해도 시뮬 시작은 막지 않음 — DSPilot 동기화는 부가 기능.
            if (IsAgentDelegationMode && PublishAasxForHubMode is not null)
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
            if (!Hub.TryStart())
                return;

            Action<string, string>? writeTagAction = null;
            if (Hub.Connection is not null && SelectedRuntimeMode == RuntimeMode.Control)
            {
                var hub = Hub.Connection;
                var hubGeneration = Hub.CurrentGeneration;
                var sender = Hub.BatchSender;
                writeTagAction = (address, value) =>
                {
                    if (!Hub.IsCurrentConnection(hubGeneration, hub))
                        return;

                    var state = hub.State;
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        if (Hub.IsCurrentConnection(hubGeneration, hub))
                            AddSimLog($"[Ctrl→] Out {address}={value} (hub={state})", LogSeverity.Going);
                    });

                    // Batch sender 가 짧은 윈도우 내 다른 WriteTag 들과 묶어 1개 SignalR 프레임으로 송신.
                    sender?.Enqueue(address, value, HubSource.Control);
                };
            }
            if (UsesAgentProxy && Hub.Connection is not null)
            {
                // Monitoring/Control + 실 PLC: Agent 가 engine 을 단일 호스팅한다 → WPF 는 self EventDrivenEngine 대신
                // 원격 proxy 를 _simEngine 에 둔다. 같은 PLC 를 WPF·Agent 가 각자 물어 abnormal/상태가 중복되던 문제 제거.
                // Control 도 Agent engine 이 writeTag→gateway 로 OUT 을 쓰므로 WPF 의 writeTagAction(self engine 용)은 미사용.
                // index/ioMap 은 로컬 build (SimIndex 가 Store 를 품어 직렬화 불가), 상태는 push-cache.
                // identity 의 SessionId/Generation 은 OnConnected snapshot 핸드셰이크에서 동기화되고,
                // ModelHash 는 Agent 와 같은 공유 AASX 파일로 계산해 stale guard 가 맞물린다.
                var proxyIoMap = SignalIOMapModule.build(Store);
                var proxyMode = SelectedRuntimeMode == RuntimeMode.Control ? "Control" : "Monitoring";
                var identity = new RuntimeSessionIdentity(
                    "", RuntimeModelHash.compute(SharedPaths.AasxFilePath), 0, proxyMode);
                _simEngine = new RemoteSimulationEngine(Hub.Connection, index, proxyIoMap, identity);
            }
            else
            {
                _simEngine = writeTagAction is not null
                    ? new EventDrivenEngine(index, SelectedRuntimeMode,
                        FSharpOption<FSharpFunc<string, FSharpFunc<string, Unit>>>.Some(
                            FuncConvert.FromAction<string, string>(writeTagAction)))
                    : new EventDrivenEngine(index, SelectedRuntimeMode);
            }
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

            // VP/Monitoring: Work별 고유 IO 주소 준비 + 학습 상태 리셋.
            // 단 Agent 위임(Monitoring/Control+실PLC proxy)에선 Agent engine 이 추론/구동을 수행하므로
            // WPF 측 추론은 끈다 — 켜두면 같은 IO 를 양쪽이 추론해 이중 Force 가 발생한다.
            if (!UsesAgentProxy && _runtimeSession?.RequiresPassiveInference == true)
            {
                PreparePassiveModeIoInference();
            }

            AdvanceSimUiGeneration();

            WireSimEvents();
            InitSimNodes();
            InitTokenSources();
            InitSceneEventHandler();

            // Passive 모드(VirtualPlant/Monitoring): Homing 없이 Start만, H 상태로 대기
            var isPassive = _runtimeSession?.StartsWithHomingPhase == false;

            _simStartTime = DateTime.Now;
            ResetPassiveGanttClockAnchor();
            ResetGanttIoBaseline();
            Report.Clear();
            _suppressedWarnings.Clear();
            _stepPrimingDone = false;
            SimEventLog.Clear();

            GanttChart.Reset(_simStartTime);
            InitGanttEntries();
            GanttChart.IsRunning = true;

            if (!hasPreStartWarnings)
                _warningGuids.Clear();

            var hasHoming = false;

            // Control 모드: Hub Tag 캐시에서 실제 IO 값 조회 → Device Work 초기 상태 싱크
            //   엔진 Start 전에 완료해야 executeApiCall 첫 호출 시 반영됨 → 동기 대기 (최대 5초)
            //   Hub 연결이 비동기라 Start 시점엔 Connecting 상태일 수 있음 → 내부에서 Connected 대기
            if (Hub.Connection is not null && _runtimeSession?.RequiresHubSnapshotSync == true)
            {
                var hub = Hub.Connection;
                var hubGeneration = Hub.CurrentGeneration;
                var runtimeSession = _runtimeSession;
                try
                {
                    AddSimLog($"[Ctrl] Hub 싱크 시작 (Hub 상태={hub.State})", LogSeverity.System);
                    var syncTask = Task.Run(async () =>
                    {
                        // Hub 연결 대기 (최대 3초)
                        var waitStart = DateTime.Now;
                        while (hub.State != HubConnectionState.Connected
                               && Hub.IsCurrentConnection(hubGeneration, hub)
                               && runtimeSession is not null
                               && (DateTime.Now - waitStart).TotalMilliseconds < runtimeSession.HubConnectionWaitTimeoutMs)
                        {
                            await Task.Delay(50);
                        }
                        if (hub.State != HubConnectionState.Connected || !Hub.IsCurrentConnection(hubGeneration, hub))
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

            // Monitoring + 실 PLC PLAY 성공 시 DSPilot 웹 대시보드를 기본 브라우저로 띄운다.
            // Agent (Windows Service) 는 사용자 세션이 없어 브라우저를 못 띄우므로 Promaker WPF 가 담당.
            // LaunchDspilotOnMonitoring (issue #154 "DsPilot으로 실행" 체크) 이 꺼져 있으면 띄우지 않는다.
            if (SelectedRuntimeMode == RuntimeMode.Monitoring && IsRealPlcConnected && LaunchDspilotOnMonitoring)
                Services.DspilotLauncher.Open();
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation start failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
        }
    }

    private bool CanStartSimulation() =>
        // Agent 위임 모드: 모니터링 미시작 시에만 활성 — 시작 후에는 같은 버튼이 "정지"(StopSimulationCommand) 로 토글.
        (IsAgentDelegationMode && !IsSimulating)
        || SimulationCommandFacade.IsAccepted(
            SimulationCommandFacade.DecideStart(IsSimulating, IsSimPaused, IsHomingPhase));
}
