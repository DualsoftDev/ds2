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

/// <summary>시뮬레이션 패널과 툴바의 시뮬레이션 상태/명령을 담당합니다.
/// Promaker 는 로컬 엔진으로 시뮬레이션만 한다 — 실 PLC·Hub·Agent 경로는 없다.</summary>
public partial class SimulationPanelState : ObservableObject
{
    private static readonly ILog SimLog = LogManager.GetLogger("Simulation");

    private readonly Func<DsStore> _storeProvider;

    // 간트 표시 윈도우는 간트 차트 헤더 드롭다운이 소유 — GanttChartControl 이 GanttChartState.RenderWindowMinutes
    // 에 직접 반영하고 앱 설정(SettingsPaths.GanttWindowMinutes)에 영속화한다.
    /// <summary>모델을 dirty(미저장)로 표시 — MainViewModel 이 () => IsDirty=true 로 주입.</summary>
    public Action? MarkDirty { get; set; }
    private readonly Dispatcher _dispatcher;
    private readonly Func<IEnumerable<EntityNode>> _allCanvasNodes;
    private readonly Func<IEnumerable<EntityNode>> _allTreeNodes;
    private readonly Action<string> _setStatusText;
    private ISimulationEngine? _simEngine;
    internal ISimulationEngine? SimEngine => _simEngine;
    private DateTime _simStartTime = DateTime.Now;
    private readonly StateCache _stateCache = new();

    /// <summary>시뮬 결과 누적/박제/내보내기 collaborator. XAML 바인딩 path 는 Report.Xxx 로 노출.</summary>
    public SimulationReportOrchestrator Report { get; }

    /// <summary>토큰별 traversal 시간 추적 collaborator. F# TokenTraversalSession 위임 + origin/specLabel 결정.</summary>
    public SimulationTokenTraversalTracker TokenTraversal { get; }

    /// <summary>연속 토큰 투입 controller. XAML 바인딩 path 는 ContinuousInjection.IsEnabled / IsAvailable.</summary>
    public SimulationContinuousInjectionController ContinuousInjection { get; }

    private readonly HashSet<string> _suppressedWarnings = [];
    private readonly HashSet<Guid> _warningGuids = [];
    private bool _isStepMode;
    private long _simUiGeneration;
    /// <summary>
    /// 씬 뷰 이벤트 fan-out 허브. 예전에는 단일 <see cref="ISceneEventHandler"/> 필드였으나
    /// 3D 배치 뷰 외의 뷰(그래픽 정보뷰)가 공존해야 해서 Composite 으로 승격했다.
    /// 3D 배치 뷰용 <see cref="DeviceSceneEventHandler"/> 는 InitSceneEventHandler() 에서
    /// ReplaceSingleton 으로 "타입당 1개" 를 유지하므로 기존 동작은 변하지 않는다.
    /// </summary>
    private readonly CompositeSceneEventHandler _sceneEventHandler = new();

    /// <summary>씬 뷰(그래픽 정보뷰 등)가 시뮬레이션 상태 이벤트를 구독한다.</summary>
    public void RegisterSceneEventHandler(ISceneEventHandler handler) => _sceneEventHandler.Add(handler);

    /// <summary>창이 닫힐 때 구독 해제. 누락되면 죽은 WebView 로 push 를 계속 시도하게 된다.</summary>
    public void UnregisterSceneEventHandler(ISceneEventHandler handler) => _sceneEventHandler.Remove(handler);

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

    // 직전 스냅샷과 그 원본 map. NotifyRuntimeIoChanged 는 Work/Call 상태변화 1건마다 불리지만
    // IOValues 가 바뀌는 것은 Call 전이뿐이라, 그 사이 이벤트에서는 같은 내용을 다시 복사하게 된다.
    private object? _ioSnapshotSourceMap;
    private IReadOnlyDictionary<Guid, string>? _ioSnapshotCache;

    /// <summary>현재 시뮬 엔진의 IOValues 를 C# Dictionary 로 스냅샷. 미실행이면 null.
    ///
    /// <para>F# Map 은 불변이고 엔진은 상태를 통째로 교체하므로, map 참조가 그대로면 내용도
    /// 그대로다 — 그 경우 직전 스냅샷을 재사용한다. 반환된 사전은 읽기 전용으로만 쓰인다
    /// (PropertyPanel 이 _lastIoSnapshot 으로 들고 있다가 조건 항목 로드 시 다시 적용).</para></summary>
    public IReadOnlyDictionary<Guid, string>? GetIoValuesSnapshot()
    {
        var engine = _simEngine;
        if (engine is null) return null;
        var map = engine.State.IOValues;
        if (_ioSnapshotCache is not null && ReferenceEquals(map, _ioSnapshotSourceMap))
            return _ioSnapshotCache;

        var dict = new Dictionary<Guid, string>(capacity: map.Count);
        foreach (var kv in map)
            dict[kv.Key] = kv.Value;
        _ioSnapshotSourceMap = map;
        _ioSnapshotCache = dict;
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
            isSimulating:         () => IsSimulating,
            isSimPaused:          () => IsSimPaused,
            isHomingPhase:        () => IsHomingPhase,
            engineProvider:       () => _simEngine,
            storeProvider:        storeProvider,
            addSimLog:            AddSimLog);
    }

    private DsStore Store => _storeProvider();
    internal DsStore StoreReadOnly => Store;
    private long AdvanceSimUiGeneration() => Interlocked.Increment(ref _simUiGeneration);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSpeed))]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeedTokenCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepSimulationCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceWorkResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeedTokenCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepSimulationCommand))]
    private bool _isHomingPhase;

    [ObservableProperty]
    private bool _hasWorkGoing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepSimulationCommand))]
    private bool _hasGoingCall;

    [ObservableProperty] private string _simStatusText = SimText.Stopped;

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
    /// Gantt 빨간선의 시간 source — 시뮬 실행 중이면 sim clock 기반 보간 provider, 아니면 wall clock(null).
    /// 노드 막대 timestamp 도 동일 source 라 빨간선과 일치 — 배속 시 막대가 빨간선 추월하던 mismatch 해결.
    /// </summary>
    private void RefreshGanttTimeSource()
    {
        GanttChart.NowOverride = IsSimulating ? _clockInterpolator.EstimateNow : null;
        // 시뮬 자체가 plan 이므로 plan overlay 없이 단일 바.
        GanttChart.ShowPlanOverlay = false;
        GanttChart.SuppressFirstGoingPlanOverlay = false;
    }

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

    /// <summary>NodeGuid → SimNodes 행. 상태/토큰 갱신이 엔진 이벤트 1건마다 행을 찾는데,
    /// 선형 탐색이면 "이벤트 수 × 노드 수" 가 UI 스레드에서 돈다. SimNodes 와 같은 자리에서만
    /// 갱신한다(InitSimNodes / AddSimNode / ResetSimulationState).
    /// 중복 NodeGuid 는 TryAdd 로 첫 행 유지 — 종전 FirstOrDefault 와 같은 결과.</summary>
    private readonly Dictionary<Guid, SimNodeRow> _simNodeByGuid = [];

    internal SimNodeRow? TryFindSimNode(Guid nodeGuid) =>
        _simNodeByGuid.TryGetValue(nodeGuid, out var row) ? row : null;

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
