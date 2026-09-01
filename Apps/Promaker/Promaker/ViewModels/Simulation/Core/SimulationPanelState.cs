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
    // 로컬 실측 duration 학습 — 비-Simulation 모드(Control/Monitoring/VP) PLAY 시 생성, 정지 시 _learnedDurations 에 병합.
    private CallDurationLearning? _durationLearning;
    // 건강 기준선 동결 + 드리프트 수명 추적 — 학습기의 정상 샘플 스트림(SampleRecorded)을 구독.
    private HealthBaselineTracker? _healthBaseline;
    // 자동 duration 정합 ON/OFF (hub 동기화). ON=실측 학습 기준 판정, OFF=모델 확정값 기준.
    // 정지 시 "AASX 반영" Yes → OFF 전환. hub 수신으로 set 할 땐 push 재발 방지(_suppress).
    [ObservableProperty] private bool _autoDurationCalibrate = true;
    private bool _suppressAutoCalibratePush;

    partial void OnAutoDurationCalibrateChanged(bool value)
    {
        // 영속 SSOT 동기화 — UI 토글이든 hub 수신이든 PlcSettings 에 반영해야 다음 Save/업로드 시
        // PlcConnection.json 에 기록된다(이게 빠져 '보정 안함' 이 파일에 안 담겨 Agent 가 ON 으로 복원하던 버그).
        PlcSettings.AutoDurationCalibrate = value;

        // OFF는 현재 세션의 로컬/원격 누적값까지 즉시 폐기한다. ON 전환 중 실행 중인 self-engine만
        // 로컬 학습기를 만들며, Agent proxy에서는 Agent가 유일한 학습 소유자다.
        if (!value)
        {
            StopLocalDurationLearning();
            _learnedDurations.Clear();
        }
        else if (IsSimulating
                 && ShouldUseLocalDurationLearning(SelectedRuntimeMode, UsesAgentProxy, value))
        {
            InitDurationLearning();
        }

        if (_suppressAutoCalibratePush) return;
        Hub.TrySetAutoCalibrate(value);   // 사용자 토글 → hub → 엔진 적용 + 전 인스턴스 broadcast
    }

    // 간트 표시 윈도우는 간트 차트 헤더 드롭다운이 소유 — GanttChartControl 이 GanttChartState.RenderWindowMinutes
    // 에 직접 반영하고 앱 설정(SettingsPaths.GanttWindowMinutes)에 영속화한다. (구) PLC 설정 다이얼로그에서 이사.
    /// <summary>모델을 dirty(미저장)로 표시 — MainViewModel 이 () => IsDirty=true 로 주입.</summary>
    public Action? MarkDirty { get; set; }
    private readonly Dispatcher _dispatcher;
    private readonly Func<IEnumerable<EntityNode>> _allCanvasNodes;
    private readonly Func<IEnumerable<EntityNode>> _allTreeNodes;
    private readonly Action<string> _setStatusText;
    private ISimulationEngine? _simEngine;
    internal ISimulationEngine? SimEngine => _simEngine;
    /// <summary>SimEngine 상태 → OPC UA Variable 로 값을 push 하는 브릿지. Start 후 attach, Stop/Dispose 시 detach.
    /// null 이면 UA 클라이언트가 read 시 <c>BadWaitingForInitialData</c> 를 받는다.</summary>
    private Promaker.Shared.SimEngineUaBridge? _uaBridge;
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

        // 간트 표시 윈도우 복원 — 앱 설정(ganttWindowMinutes.txt). 이후 변경은 간트 헤더 드롭다운이 담당.
        GanttChart.RenderWindowMinutes = Promaker.Presentation.AppSettingStore.LoadIntOrDefault(
            Promaker.Services.SettingsPaths.GanttWindowMinutes, 300);

        // 자동 정합 ON/OFF 복원 — 저장된 PLC 설정값으로. hub 미연결 상태이므로 push 는 막는다.
        _suppressAutoCalibratePush = true;
        try { AutoDurationCalibrate = PlcSettings.AutoDurationCalibrate; }
        finally { _suppressAutoCalibratePush = false; }

        _clockInterpolator = new SimulationClockInterpolator(
            engine:       () => _simEngine,
            simStart:     () => _simStartTime,
            isSimulating: () => IsSimulating,
            isSimPaused:  () => IsSimPaused,
            simSpeed:     () => SimSpeed);

        // #198: 리본 동작시간(SimClock, hh:mm:ss.fff) 을 간트 빨간선과 '같은' 보간 소스(_clockInterpolator)에
        // 연결해 ~30fps 로 부드럽게 흐르게 한다. 엔진/이벤트 cadence 는 불변 — 순수 표시(View) 보간 갱신.
        _simClockTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33)   // GanttChartControl 렌더 틱과 동일 간격(≈30fps)
        };
        _simClockTimer.Tick += (_, _) => TickSimClockInterpolated();

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
            applyRuntimeHubEffects:   ApplyRuntimeHubEffects,
            askAgentBusyChoice:       () => Promaker.Dialogs.AgentBusyDialog.Ask());

        // 자동 줄자 학습값 수신 → 누적(정지 시 사용자 선택으로 모델 반영).
        Hub.LearnedDurationReceived += OnLearnedDurationReceived;
        // 건강 기준선 수동 동결 — 어느 인스턴스(Promaker/DSPilot)의 버튼이든 hub 브로드캐스트로 동시 동결.
        Hub.HealthBaselineFreezeRequested += () => FreezeHealthBaselineNow("허브 동기화");
        // PLC 스캔 주기 동기화 — Agent/DSPilot 어느 쪽이 바꿔도 로컬 설정·슬라이더가 같은 값 유지.
        Hub.ScanIntervalChanged += ms =>
        {
            if (PlcSettings.ScanIntervalMs == ms) return;
            PlcSettings.ScanIntervalMs = ms;
            AddSimLog($"PLC 스캔 주기 동기화: {ms}ms", LogSeverity.System);
        };
        // 자동 duration 정합 ON/OFF 동기화 — 토글/정지반영(OFF)이 양쪽 체크박스에 반영.
        Hub.AutoCalibrateChanged += on =>
        {
            if (AutoDurationCalibrate == on) return;
            _suppressAutoCalibratePush = true;
            try { AutoDurationCalibrate = on; }
            finally { _suppressAutoCalibratePush = false; }
            AddSimLog($"자동 duration 정합: {(on ? "ON (실측 학습)" : "OFF (모델 확정값)")}", LogSeverity.System);
        };

        // 간트 I/O 줄 — Hub 의 실제 Tag(Out·In) 변화를 ApiCall I/O 행 막대로 반영.
        //   Plan(Call 수명=계획) 과 I/O(실제 송수신) 를 위아래로 대조 → 어디서 어긋나는지(abnormal) 가시화.
        Hub.TagBroadcast += (address, value, source) =>
        {
            // resync = PLC 재연결 직후의 전체 baseline 스냅샷 — 전이(edge)가 아니라 현재값이다.
            // UpdateIoState 는 동일 상태 재수신에도 열린 high 구간을 닫고 새로 열기 때문에,
            // 그리면 ON 유지 중이던 모든 막대에 재연결 시각의 가짜 이음새/거짓 전환이 생긴다 — 흡수.
            if (string.Equals(source, Ds2.Backend.Common.HubSource.Resync, StringComparison.OrdinalIgnoreCase))
                return;
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

        // 통신 blackout 감시 — PLC down/신호 무소식 시 actual 동결 + abnormal 억제 (coast 1단계).
        InitCommBlackoutWatch();
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


    /// <summary>실 PLC 모드(Agent 경유) 여부 — 런타임 모드의 파생값: Control/Monitoring = true, Sim/VP = false.
    /// (구) 런타임 세팅의 'PLC 읽기 방식' 라디오가 결정하던 값이었으나, 직접/위임 수집 구분은
    /// '업로드' 버튼(직접/위임 택1)이 session.json 에 박제하는 것으로 이사 — VM 상태에서 분리됨.</summary>
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

    /// <summary>'Edge 단말로 업로드(위임 수집)' 버튼 활성 여부 — Control 은 OUT 을 실 PLC 에 직접 써야 하므로
    /// 위임 수집 불가(직접만). 그 외 모드는 Monitoring 세션으로 업로드되므로 허용.</summary>
    public bool IsDelegatedUploadAvailable =>
        SelectedRuntimeMode != RuntimeMode.Control;

    /// <summary>Agent 가 engine 을 단일 호스팅하고 WPF 는 proxy(RemoteSimulationEngine)로만 붙는 모드 —
    /// Monitoring+실PLC(read-only) 또는 Control+실PLC(read-write). engine 호스팅/proxy 전환 판정 전용.
    /// UI 토글(Pause/Step 숨김 등)은 Monitoring 전용 의미라 <see cref="IsAgentDelegationMode"/> 를 따로 유지한다.</summary>
    public bool UsesAgentProxy =>
        IsRealPlcConnected
        && (SelectedRuntimeMode == RuntimeMode.Monitoring || SelectedRuntimeMode == RuntimeMode.Control)
        && !Hub.IsVirtualHubActive;   // 가상 Hub(새 포트 자체 호스팅) 모드면 self engine 으로 모델만 구동

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

    /// <summary>"통신 차단(테스트)" 토글 노출 — DEBUG 빌드 + 허브 모드 한정.
    /// 릴리즈 현장에서 켜면 수신이 통째로 멎는 테스트 전용 장치라 배포 빌드에선 숨긴다.
    /// (실 PLC coast/재합류 검증 기간에 임시로 릴리즈 노출했다가 검증 완료 후 복귀 — 2026-06-12.)</summary>
    public bool ShowCommBlockTestToggle =>
#if DEBUG
        NeedsHubConnection;
#else
        false;
#endif

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
        // IsRealPlcConnected = 모드 파생값 동기화 — Control/Monitoring 은 항상 실 PLC(Agent 경유).
        IsRealPlcConnected = value is RuntimeMode.Control or RuntimeMode.Monitoring;
        OnPropertyChanged(nameof(IsDelegatedUploadAvailable));
        OnPropertyChanged(nameof(NeedsHubConnection));
        OnPropertyChanged(nameof(ShowCommBlockTestToggle));
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
        // #198: 시뮬 실행 동안 SimClock 을 보간 갱신(부드럽게). 정지 시 멈추고, 직후 UpdateSimClock() 이 정확한 최종값 고정.
        if (value) _simClockTimer.Start();
        else       _simClockTimer.Stop();
    }

    // Pause 진입 시 base 가 그 시점 sim clock 으로 freeze. Resume 시 wall 새로 시작 — 누적 정지 시간을 보간에 더하지 않도록.
    partial void OnIsSimPausedChanged(bool value) => _clockInterpolator.ResetBase();

    private readonly SimulationClockInterpolator _clockInterpolator;
    private readonly DispatcherTimer _simClockTimer;

    /// <summary>
    /// #198: SimClock(리본 동작시간) 텍스트를 보간 소스로 매 프레임 갱신 — 이벤트 사이에도 부드럽게 흐른다.
    /// 간트 빨간선과 동일한 _clockInterpolator.EstimateNow 를 쓰며(같은 소스), 엔진/이벤트는 건드리지 않는다.
    /// EstimateNow 는 _simStartTime + 보간 clock 을 돌려주므로 경과 = EstimateNow - _simStartTime.
    /// </summary>
    private void TickSimClockInterpolated()
    {
        if (_simEngine is null) return;
        var elapsed = _clockInterpolator.EstimateNow() - _simStartTime;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        SimClock = elapsed.ToString(SimText.ClockFormat);
    }

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

        // plan overlay — 비-Simulation 모드(Control/Monitoring/VP)는 plan(BaseDurationMs) 배경 틀 + 얇은 actual 바.
        // Simulation 은 시뮬 자체가 plan 이므로 기존 단일 바 그대로.
        GanttChart.ShowPlanOverlay = SelectedRuntimeMode != RuntimeMode.Simulation;
        // 신호 유추 모드(VP/Monitoring)는 워밍업 후 사이클 중간 합류라 첫 Going 의 plan 틀이 침범 — 생략.
        GanttChart.SuppressFirstGoingPlanOverlay = UsesSignalDrivenGanttTimeline(SelectedRuntimeMode);
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
