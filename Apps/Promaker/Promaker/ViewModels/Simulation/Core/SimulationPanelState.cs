using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.IO;
using Ds2.Runtime.Model;
using Ds2.Runtime.Report;
using Ds2.Runtime.Report.Model;
using Ds2.Core.Store;
using Ds2.Editor;
using log4net;

namespace Promaker.ViewModels;

/// <summary>
/// 시뮬 이벤트 색상/카테고리 박제 — `AddSimLog` 호출 site 에서 사용. 통합 Log 탭 (AppLogState) 으로 routing 되며
/// Category 필드를 통해 AppLogView 의 DataTrigger 가 색상 분기.
/// </summary>
public enum LogSeverity { Info, Warn, Error, Timeout, Ready, Going, Finish, Homing, System }

/// <summary>시뮬레이션 패널과 툴바의 시뮬레이션 상태/명령을 담당합니다.</summary>
public partial class SimulationPanelState : ObservableObject
{
    private static readonly ILog SimLog = LogManager.GetLogger("Simulation");

    private readonly Func<DsStore> _storeProvider;
    // v12 자동 줄자 — Agent 가 push 한 학습 duration 누적(workGuid → ms). 정지 시 사용자 선택으로 모델 반영.
    private readonly System.Collections.Generic.Dictionary<Guid, (int avg, int min, int max)> _learnedDurations = new();
    /// <summary>모델을 dirty(미저장)로 표시 — MainViewModel 이 () => IsDirty=true 로 주입.</summary>
    public Action? MarkDirty { get; set; }
    private readonly Dispatcher _dispatcher;
    private readonly Func<IEnumerable<EntityNode>> _allCanvasNodes;
    private readonly Func<IEnumerable<EntityNode>> _allTreeNodes;
    private readonly Action<string> _setStatusText;
    private ISimulationEngine? _simEngine;
    internal ISimulationEngine? SimEngine => _simEngine;
    private DateTime _simStartTime = DateTime.Now;
    private TimeSpan? _passiveGanttClockAnchor;
    private DateTime _passiveGanttBaseWall = DateTime.Now;
    private TimeSpan _passiveGanttBaseElapsed = TimeSpan.Zero;
    private readonly object _ganttIoBaselineLock = new();
    private readonly HashSet<string> _ganttIoBaselineAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly StateCache _stateCache = new();

    /// <summary>시뮬 결과 누적/박제/내보내기 collaborator. XAML 바인딩 path 는 Report.Xxx 로 노출.</summary>
    public SimulationReportOrchestrator Report { get; }

    /// <summary>토큰별 traversal 시간 추적 collaborator. F# TokenTraversalSession 위임 + origin/specLabel 결정.</summary>
    public SimulationTokenTraversalTracker TokenTraversal { get; }

    /// <summary>SignalR Hub + PLC gateway lifecycle collaborator + XAML 바인딩 표면 (IsConnected/IsReconnecting/IsHosting/StatusText/HostingLabel/IsHubHost/EffectiveHubAddress) 통합 소유. XAML 은 Simulation.Hub.X 직접 바인딩.</summary>
    public SimulationHubBridge Hub { get; }

    /// <summary>연속 토큰 투입 controller. XAML 바인딩 path 는 ContinuousInjection.IsEnabled / IsAvailable.</summary>
    public SimulationContinuousInjectionController ContinuousInjection { get; }

    // ── Hub collaborator 주입용 helper (RuntimeSession/IOMap 접근 wrapping) ─────
    private bool HasRuntimeSession() => _runtimeSession is not null;

    private bool ShouldIgnoreHubSource(string address, string value, string source) =>
        _runtimeSession?.ShouldIgnoreHubSource(source) ?? false;

    private IEnumerable<Ds2.Runtime.Engine.Passive.RuntimeHubEffect> HandleHubTag(string address, string value, string source) =>
        _runtimeSession?.HandleHubTag(address, value, source)
            ?? System.Linq.Enumerable.Empty<Ds2.Runtime.Engine.Passive.RuntimeHubEffect>();

    private bool HasIoMap() => _simEngine?.IOMap is not null;

    private IEnumerable<string> TxOutAddresses()
    {
        var ioMap = _simEngine?.IOMap;
        if (ioMap is null) return System.Linq.Enumerable.Empty<string>();
        return ioMap.TxWorkToOutAddresses.SelectMany(kv => kv.Value);
    }
    private readonly HashSet<string> _suppressedWarnings = [];
    private readonly HashSet<Guid> _warningGuids = [];
    private bool _isStepMode;
    private long _simUiGeneration;
    private ISceneEventHandler? _sceneEventHandler;

    /// <summary>
    /// 시뮬 IO 값이 갱신될 가능성이 있는 시점 (Work/Call 상태 전이) 에 호출되는 후크.
    /// MainViewModel 에서 PropertyPanel.RefreshConditionRuntime 으로 wiring.
    /// 인자: 현재 IO 스냅샷 (시뮬 미실행이면 null).
    /// </summary>
    public Action<IReadOnlyDictionary<Guid, string>?>? RuntimeIoChanged { get; set; }

    /// <summary>
    /// Hub 모드(Control/VirtualPlant/Monitoring) 시뮬레이션이 시작되기 직전에 호출되는 후크.
    /// MainViewModel 이 현재 store 를 DSPilot 공유 AASX 경로에 export 해 두 앱이 동일 모델로 동기화되도록 함.
    /// 반환값: 성공 시 true, 프로젝트 미보유/실패 시 false. (실패해도 시뮬 시작은 계속.)
    /// </summary>
    public Func<bool>? PublishAasxForHubMode { get; set; }

    private void NotifyRuntimeIoChanged()
    {
        if (RuntimeIoChanged is null) return;
        var snapshot = GetIoValuesSnapshot();
        RuntimeIoChanged(snapshot);
    }

    /// <summary>현재 시뮬 엔진의 IOValues 를 C# Dictionary 로 스냅샷. 미실행이면 null.</summary>
    public IReadOnlyDictionary<Guid, string>? GetIoValuesSnapshot()
    {
        var engine = _simEngine;
        if (engine is null) return null;
        var map = engine.State.IOValues;
        var dict = new Dictionary<Guid, string>(capacity: 16);
        foreach (var kv in map)
            dict[kv.Key] = kv.Value;
        return dict;
    }

    private static class SimText
    {
        public const string Running = "시뮬레이션 동작 중";
        public const string StepMode = "시뮬레이션 단계 제어 중";
        public const string Resumed = "시뮬레이션 재개";
        public const string Started = "시뮬레이션 시작";
        public const string Paused = "시뮬레이션 일시정지";
        public const string Stopped = "시뮬레이션 정지 됨";
        public const string Completed = "시뮬레이션 완료";
        public const string Reset = "시뮬레이션 리셋";
        public const string ResetLog = "시뮬레이션 리셋 (F5/정지)";
        public const string ReportEmpty = "내보낼 시뮬레이션 데이터가 없습니다.";
        public const string ReportDialogTitle = "시뮬레이션 리포트 내보내기";

        public static string SimulationError(string message) => $"시뮬레이션 오류: {message}";
        public static string ManualWorkStarted(string name) => $"Work 수동 시작: {name}";
        public static string ManualWorkReset(string name) => $"Work 수동 리셋: {name}";
        public static string ReportSaved(string path) => $"리포트 저장 완료: {path}";
        public static string ReportSaveFailed(string message) => $"리포트 저장 실패: {message}";
        public static string ReportError(string message) => $"리포트 오류: {message}";
        public static string ScenarioCaptured(string name) => $"시뮬 시나리오 저장됨: {name}";
        public const string ScenarioCaptureFailed = "시뮬 시나리오 저장 실패: 데이터가 없거나 프로젝트를 찾을 수 없습니다.";
        public static string StateCode(Status4 state) => Presentation.Status4Visuals.ShortCode(state);

        public const string ClockFormat = @"hh\:mm\:ss\.fff";
        public const string ClockZero   = "00:00:00.000";
    }

    public SimulationPanelState(
        Func<DsStore> storeProvider,
        Dispatcher dispatcher,
        Func<IEnumerable<EntityNode>> allCanvasNodes,
        Func<IEnumerable<EntityNode>> allTreeNodes,
        Action<string> setStatusText)
    {
        _storeProvider = storeProvider;
        _dispatcher = dispatcher;
        _allCanvasNodes = allCanvasNodes;
        _allTreeNodes = allTreeNodes;
        _setStatusText = setStatusText;

        _clockInterpolator = new SimulationClockInterpolator(
            engine:       () => _simEngine,
            simStart:     () => _simStartTime,
            isSimulating: () => IsSimulating,
            isSimPaused:  () => IsSimPaused,
            simSpeed:     () => SimSpeed);

        TokenTraversal = new SimulationTokenTraversalTracker(
            storeProvider:         storeProvider,
            engineProvider:        () => _simEngine,
            simStartTimeProvider:  () => _simStartTime);

        Report = new SimulationReportOrchestrator(
            engineProvider:        () => _simEngine,
            simStartTimeProvider:  () => _simStartTime,
            storeProvider:         storeProvider,
            setStatusText:         setStatusText,
            traversalsProvider:    () => TokenTraversal.Snapshot());

        ContinuousInjection = new SimulationContinuousInjectionController(
            runtimeMode:          () => SelectedRuntimeMode,
            isRealPlcConnected:   () => IsRealPlcConnected,
            isSimulating:         () => IsSimulating,
            isSimPaused:          () => IsSimPaused,
            isHomingPhase:        () => IsHomingPhase,
            engineProvider:       () => _simEngine,
            storeProvider:        storeProvider,
            addSimLog:            AddSimLog);

        // RuntimeMode/PLC 토글 시 ContinuousInjection.IsAvailable 갱신 (RuntimeCommandPolicy 입력 변화).
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedRuntimeMode) || e.PropertyName == nameof(IsRealPlcConnected))
                ContinuousInjection.RaiseIsAvailableChanged();
        };

        Hub = new SimulationHubBridge(
            runtimeMode:              () => SelectedRuntimeMode,
            isRealPlcConnected:       () => IsRealPlcConnected,
            isSimulating:             () => IsSimulating,
            hubAddress:               () => HubAddress,
            monitoringHubAddress:     () => MonitoringHubAddress,
            setHubAddress:            v => HubAddress = v,
            setMonitoringHubAddress:  v => MonitoringHubAddress = v,
            plcSettings:              () => PlcSettings,
            buildPlcGatewayConfig:    BuildPlcGatewayConfig,
            hasRuntimeSession:        HasRuntimeSession,
            shouldIgnoreHubSource:    ShouldIgnoreHubSource,
            handleHubTag:             HandleHubTag,
            resolveRuntimeHubSource:  ResolveRuntimeHubSource,
            hasIoMap:                 HasIoMap,
            txOutAddresses:           TxOutAddresses,
            dispatcher:               dispatcher,
            addSimLog:                AddSimLog,
            setStatusText:            setStatusText,
            setSimStatusText:         v => SimStatusText = v,
            applyRuntimeHubEffects:   ApplyRuntimeHubEffects);

        // 자동 줄자 학습값 수신 → 누적(정지 시 사용자 선택으로 모델 반영).
        Hub.LearnedDurationReceived += OnLearnedDurationReceived;

        // 간트 I/O 줄 — Hub 의 실제 Tag(Out·In) 변화를 ApiCall I/O 행 막대로 반영.
        //   Plan(Call 수명=계획) 과 I/O(실제 송수신) 를 위아래로 대조 → 어디서 어긋나는지(abnormal) 가시화.
        Hub.TagBroadcast += (address, value, source) =>
        {
            bool on = !string.IsNullOrEmpty(value)
                      && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                      && value != "0";
            if (ShouldAbsorbInitialMonitoringPlcIo(address, source))
                return;
            // I/O 막대는 PLN(Call 상태)과 같은 engine-clock 축 + 같은 anchor 로 찍어야 위아래가 정렬된다.
            //   - 발생 시점(hub 스레드)의 engine clock 을 먼저 캡처한다. dispatch 시점에 읽으면 burst dispatch
            //     중 같은 신호가 한 점으로 몰려 0너비로 붕괴한다.
            //   - proxy(RemoteSimulationEngine) 의 State.Clock 은 CurrentTimeMs 기준으로 진행한다(원격 push).
            //     예전엔 0 고정이라 실PLC Control 의 I/O 가 모두 _simStartTime 한 점에 찍혀 안 보였다.
            //   - anchor 보정(ResolveEventTimestamp)은 PLN 과 같은 dispatcher 스레드에서 적용해
            //     _passiveGanttClockAnchor 갱신을 단일 스레드로 유지(race 방지)하고, signal-driven 모드에서
            //     I/O 가 PLN 과 동일하게 첫 신호 기준으로 정렬되도록 한다.
            var clock = _simEngine?.State.Clock;
            dispatcher.BeginInvoke(new Action(() =>
            {
                var ts = clock is { } c ? ResolveEventTimestamp(c) : GanttChart.AdjustedNow;
                GanttChart.UpdateIoState(address, on, ts);
            }));
        };
    }

    private bool ShouldAbsorbInitialMonitoringPlcIo(string address, string source)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;
        if (SelectedRuntimeMode != RuntimeMode.Monitoring || !IsRealPlcConnected)
            return false;
        if (!string.Equals(source, Ds2.Backend.Common.HubSource.Plc, StringComparison.OrdinalIgnoreCase))
            return false;

        lock (_ganttIoBaselineLock)
            return _ganttIoBaselineAddresses.Add(address);
    }

    private void ResetGanttIoBaseline()
    {
        lock (_ganttIoBaselineLock)
            _ganttIoBaselineAddresses.Clear();
    }

    private DsStore Store => _storeProvider();
    internal DsStore StoreReadOnly => Store;
    private long AdvanceSimUiGeneration() => Interlocked.Increment(ref _simUiGeneration);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeMode))]
    [NotifyPropertyChangedFor(nameof(CanChangeSpeed))]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeedTokenCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepSimulationCommand))]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonHotEnabled))]
    [NotifyPropertyChangedFor(nameof(IsManualControlButtonHotEnabled))]
    private bool _isSimulating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSpeed))]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeedTokenCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepSimulationCommand))]
    private bool _isSimPaused;

    /// 자동 원위치 페이즈 진행 중 — PLAY/PAUSE/ForceWork/ForceReset/SeedToken/Step 비활성화
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeMode))]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeedTokenCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepSimulationCommand))]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonHotEnabled))]
    private bool _isHomingPhase;

    [ObservableProperty]
    private bool _hasWorkGoing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepSimulationCommand))]
    private bool _hasGoingCall;

    [ObservableProperty] private string _simStatusText = SimText.Stopped;

    // ── Runtime Mode + Hub ───────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepSimulationCommand))]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonHotEnabled))]
    [NotifyPropertyChangedFor(nameof(IsManualControlButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsManualControlButtonHotEnabled))]
    [NotifyPropertyChangedFor(nameof(IsAgentDelegationMode))]
    private RuntimeMode _selectedRuntimeMode = RuntimeMode.Simulation;
    [ObservableProperty] private string _hubAddress = "localhost:5050";

    /// <summary>Monitoring 모드가 self-host 할 때 사용할 주소. Control(5050) 과 별도 포트로 두 Promaker 가
    /// 같은 머신에서 Control + Monitoring 으로 동시 운용될 수 있도록 분리.</summary>
    [ObservableProperty] private string _monitoringHubAddress = "localhost:5051";


    /// <summary>Control 모드에서 실 PLC 와 연결할지 여부. 체크 해제면 BackendHost 가 PLC 게이트웨이 idle 로 동작.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonHotEnabled))]
    [NotifyPropertyChangedFor(nameof(IsManualControlButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsManualControlButtonHotEnabled))]
    [NotifyPropertyChangedFor(nameof(IsAgentDelegationMode))]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    private bool _isRealPlcConnected;

    /// <summary>Monitoring + 실 PLC 모드 — Promaker.Agent (Windows Service) 가 BackendHost+PLC 를 전담한다.
    /// "Agent 전송" 을 누르면 active.flag 를 기록해 Agent 에 모니터링을 위임하고 DSPilot 대시보드도 띄운다.
    /// 시작 후 같은 버튼은 "정지"(StopSimulationCommand) 로 토글 — "정지" 는 Promaker 화면만 끄고
    /// active.flag 유지 → Agent 는 sticky monitoring (Promaker 종료/재부팅과 무관하게 계속 동작).</summary>
    public bool IsAgentDelegationMode =>
        SelectedRuntimeMode == RuntimeMode.Monitoring && IsRealPlcConnected;

    /// <summary>Agent 가 engine 을 단일 호스팅하고 WPF 는 proxy(RemoteSimulationEngine)로만 붙는 모드 —
    /// Monitoring+실PLC(read-only) 또는 Control+실PLC(read-write). engine 호스팅/proxy 전환 판정 전용.
    /// UI 토글(Pause/Step 숨김 등)은 Monitoring 전용 의미라 <see cref="IsAgentDelegationMode"/> 를 따로 유지한다.</summary>
    public bool UsesAgentProxy =>
        IsRealPlcConnected
        && (SelectedRuntimeMode == RuntimeMode.Monitoring || SelectedRuntimeMode == RuntimeMode.Control);

    /// <summary>Monitoring + 실 PLC(Agent 전송) 시작 시 DSPilot 웹 대시보드를 자동으로 띄울지 여부
    /// (issue #154 "모니터링 > DsPilot으로 실행 체크"). 기본 켜짐(기존 동작 보존) — 체크 해제 시
    /// 모니터링만 시작하고 DSPilot 브라우저는 띄우지 않는다. (세션 단위 토글, 영구 저장 안 함)</summary>
    [ObservableProperty] private bool _launchDspilotOnMonitoring = true;

    /// <summary>실 라인 owner 일 때만 원위치 버튼 노출 — Sim 모드는 PLAY 가 곧 자동 원위치라 별도 버튼 불필요,
    /// VP/Monitoring 은 외부 컨트롤러가 owner 라 부적절.</summary>
    public bool IsHomingButtonVisible =>
        SelectedRuntimeMode == RuntimeMode.Control && IsRealPlcConnected;

    /// <summary>원위치 버튼 IsEnabled — 다른 시뮬이 돌고 있지 않을 때만 새 누름을 받지만,
    /// 자신의 push-session 도중에는 enabled 유지해 release 이벤트가 안전하게 도달하도록.</summary>
    public bool IsHomingButtonHotEnabled =>
        IsHomingButtonVisible && (!IsSimulating || IsHomingPressed);

    /// <summary>수동 컨트롤러 버튼 가시성 — 원위치와 동일 조건 (Control + 실 PLC 연결).</summary>
    public bool IsManualControlButtonVisible =>
        SelectedRuntimeMode == RuntimeMode.Control && IsRealPlcConnected;

    /// <summary>수동 컨트롤러 버튼 활성 — 다이얼로그 열려 있는 동안엔 enabled (자기 세션) 유지.</summary>
    public bool IsManualControlButtonHotEnabled =>
        IsManualControlButtonVisible && (!IsSimulating || IsManualControlActive);

    /// <summary>수동 컨트롤러 다이얼로그가 열려 있는 동안 true. UI 상태 표시·재진입 차단에 사용.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualControlButtonHotEnabled))]
    private bool _isManualControlActive;
    /// <summary>PLC 연결 정보 — 사용자가 RuntimeSettingDialog 에서 "PLC 설정" 으로 편집.
    /// 마지막 입력값은 AppData 의 PlcConnection.json 에 저장돼 다음 실행 시 자동 로드.</summary>
    public PlcSettings PlcSettings { get; } = PlcSettings.LoadOrDefault();

    public bool NeedsHubConnection => SelectedRuntimeMode != RuntimeMode.Simulation;

    public bool CanChangeMode => !IsSimulating && !IsHomingPhase;

    private RuntimeMode _previousRuntimeMode = RuntimeMode.Simulation;
    private bool _suppressRuntimeModeChangeHandler;

    partial void OnSelectedRuntimeModeChanged(RuntimeMode value)
    {
        if (_suppressRuntimeModeChangeHandler) return;

        // 결정 (I/O 미설정 차단 + 메시지 + cleanup 플래그) 은 F# 에 위임.
        var decision = RuntimeModeTransition.evaluate(
            value,
            HasIOConfigured(),
            IsRealPlcConnected,
            ContinuousInjection.IsEnabled);

        if (!decision.Accepted)
        {
            var msg = decision.RejectionMessage?.Value ?? "";
            Dialogs.DialogHelpers.ShowThemedMessageBox(
                msg,
                "I/O 미설정",
                System.Windows.MessageBoxButton.OK,
                Dialogs.DialogHelpers.IconWarn);

            _suppressRuntimeModeChangeHandler = true;
            try { SelectedRuntimeMode = _previousRuntimeMode; }
            finally { _suppressRuntimeModeChangeHandler = false; }
            return;
        }

        _previousRuntimeMode = value;
        OnPropertyChanged(nameof(NeedsHubConnection));
        Hub.RaiseHostingDependentsChanged();
        Hub.SetStatus(connected: false, reconnecting: false);
        RefreshGanttTimeSource();

        if (decision.ShouldDisableContinuousInjection)
            ContinuousInjection.IsEnabled = false;
    }

    partial void OnIsRealPlcConnectedChanged(bool value)
    {
        // Control + 실 PLC 진입 시점에 연속투입 토글이 켜져 있으면 해제 (PLC owner 와 충돌 방지).
        if (ContinuousInjection.IsEnabled && !ContinuousInjection.IsAvailable)
            ContinuousInjection.IsEnabled = false;
        // IsHubHost / EffectiveHubAddress / HostingLabel 은 IsRealPlcConnected 의존 → collaborator 재발화.
        Hub.RaiseHostingDependentsChanged();
    }

    partial void OnHubAddressChanged(string value) =>
        Hub.RaiseEffectiveAddressChanged();

    partial void OnMonitoringHubAddressChanged(string value) =>
        Hub.RaiseEffectiveAddressChanged();

    partial void OnIsSimulatingChanged(bool value)
    {
        _clockInterpolator.ResetBase();
        RefreshGanttTimeSource();
    }

    // Pause 진입 시 base 가 그 시점 sim clock 으로 freeze. Resume 시 wall 새로 시작 — 누적 정지 시간을 보간에 더하지 않도록.
    partial void OnIsSimPausedChanged(bool value) => _clockInterpolator.ResetBase();

    private readonly SimulationClockInterpolator _clockInterpolator;

    /// <summary>
    /// Gantt 빨간선의 시간 source 를 현재 모드/시뮬 상태에 맞게 갱신.
    /// Simulation 모드 + 시뮬 실행 중 → sim clock 기반 보간 provider 주입.
    /// VP/Monitoring → 첫 외부 신호 전에는 start 에 고정, 이후 engine clock-anchor 기준 provider 주입.
    /// 그 외 (Control 또는 시뮬 미실행) → null (wall clock default).
    /// 노드 막대 timestamp 도 동일 source 라 빨간선과 일치 — 배속 시 막대가 빨간선 추월하던 mismatch 해결.
    /// </summary>
    private void RefreshGanttTimeSource()
    {
        if (IsSimulating && SelectedRuntimeMode == RuntimeMode.Simulation)
            GanttChart.NowOverride = _clockInterpolator.EstimateNow;
        else if (IsSimulating && IsSignalDrivenGanttTimeline)
            GanttChart.NowOverride = ResolveSignalDrivenGanttNow;
        else
            GanttChart.NowOverride = null;
    }

    // PLC IO 헬퍼는 SimulationPanelState.PlcConfig.cs partial 참조.

    public bool CanChangeSpeed => !IsSimulating || IsSimPaused;

    [ObservableProperty] private double _simSpeed = 1.0;
    [ObservableProperty] private bool _simTimeIgnore;
    [ObservableProperty] private string _simClock = SimText.ClockZero;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepSimulationCommand))]
    private SimWorkItem? _selectedSimWork;

    partial void OnSelectedSimWorkChanged(SimWorkItem? value)
    {
        if (value is not null)
            _lastSelectedWorkId = value.Guid;
    }

    public ObservableCollection<SimNodeRow> SimNodes { get; } = [];
    public ObservableCollection<SimWorkItem> SimWorkItems { get; } = [];
    public GanttChartState GanttChart { get; } = new();

    public ThreeDViewState ThreeD { get; } = new();

    public void SyncCanvasSelection(IReadOnlyList<SelectionKey> orderedSelection)
    {
        if (!IsSimulating) return;
        foreach (var key in orderedSelection)
        {
            if (key.EntityKind != EntityKind.Work) continue;
            var match = SimWorkItems.FirstOrDefault(item => item.Guid == key.Id);
            if (match is not null)
            {
                SelectedSimWork = match;
                return;
            }
        }
    }
}

/// <summary>Work 선택 ComboBox 항목입니다.</summary>
public record SimWorkItem(Guid Guid, string Name)
{
    public static readonly SimWorkItem AutoStart = new(Guid.Empty, "자동선택");
    public static readonly SimWorkItem SourceHeader = new(Guid.Empty, "── 시작노드 ──");
    public static readonly SimWorkItem NormalHeader = new(Guid.Empty, "── 일반노드 ──");
    public bool IsAutoStart => this == AutoStart;
    public override string ToString() => Name;
}

/// <summary>시뮬레이션 상태 모니터링 행 데이터입니다.</summary>
public partial class SimNodeRow : ObservableObject
{
    public Guid NodeGuid { get; init; }
    public string Name { get; init; } = "";
    public string NodeType { get; init; } = "";
    public string SystemName { get; init; } = "";

    [ObservableProperty] private Status4 _state;
    [ObservableProperty] private string _tokenDisplay = "";
}
