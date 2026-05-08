using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Engine;
using Ds2.Runtime.IO;
using Ds2.Runtime.Model;
using Ds2.Runtime.Report;
using Ds2.Runtime.Report.Model;
using Ds2.Core.Store;
using Ds2.Editor;
using log4net;

namespace Promaker.ViewModels;

public enum LogSeverity { Info, Warn, Error, Timeout, Ready, Going, Finish, Homing, System }

public sealed class SimLogEntry(string message, LogSeverity severity = LogSeverity.Info)
{
    public string Message { get; } = message;
    public LogSeverity Severity { get; } = severity;
    public override string ToString() => Message;
}

/// <summary>시뮬레이션 패널과 툴바의 시뮬레이션 상태/명령을 담당합니다.</summary>
public partial class SimulationPanelState : ObservableObject
{
    private static readonly ILog SimLog = LogManager.GetLogger("Simulation");

    private readonly Func<DsStore> _storeProvider;
    private readonly Dispatcher _dispatcher;
    private readonly Func<IEnumerable<EntityNode>> _allCanvasNodes;
    private readonly Func<IEnumerable<EntityNode>> _allTreeNodes;
    private readonly Action<string> _setStatusText;
    private ISimulationEngine? _simEngine;
    internal ISimulationEngine? SimEngine => _simEngine;
    private DateTime _simStartTime = DateTime.Now;
    private readonly List<StateChangeRecord> _stateChangeRecords = [];
    private readonly StateCache _stateCache = new();
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
    [NotifyCanExecuteChangedFor(nameof(PauseSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepSimulationCommand))]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonHotEnabled))]
    [NotifyPropertyChangedFor(nameof(IsManualControlButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsManualControlButtonHotEnabled))]
    private RuntimeMode _selectedRuntimeMode = RuntimeMode.Simulation;
    [ObservableProperty] private string _hubAddress = "localhost:5050";
    [ObservableProperty] private bool _isHubHosting;

    /// <summary>Control 모드에서 실 PLC 와 연결할지 여부. 체크 해제면 BackendHost 가 PLC 게이트웨이 idle 로 동작.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsHomingButtonHotEnabled))]
    [NotifyPropertyChangedFor(nameof(IsManualControlButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsManualControlButtonHotEnabled))]
    private bool _isRealPlcConnected;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HubStatusText))]
    private bool _isHubConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HubStatusText))]
    private bool _isHubReconnecting;

    public bool NeedsHubConnection => SelectedRuntimeMode != RuntimeMode.Simulation;
    public bool IsHubHost => SelectedRuntimeMode == RuntimeMode.Control;
    public bool CanChangeMode => !IsSimulating && !IsHomingPhase;

    public string HubStatusText =>
        IsHubConnected ? "Hub 연결됨"
        : IsHubReconnecting ? "Hub 재연결 시도 중"
        : "Hub 끊김";

    private RuntimeMode _previousRuntimeMode = RuntimeMode.Simulation;
    private bool _suppressRuntimeModeChangeHandler;

    partial void OnSelectedRuntimeModeChanged(RuntimeMode value)
    {
        if (_suppressRuntimeModeChangeHandler) return;

        // Simulation 외 모드는 외부 Hub 신호 + I/O 매핑 필수.
        // I/O 미설정 상태에서 Control/VP/Monitoring 진입하면 시뮬 진행 불가 → 경고 + 이전 모드 revert.
        if (value != RuntimeMode.Simulation && !HasIOConfigured())
        {
            Dialogs.DialogHelpers.ShowThemedMessageBox(
                $"{value} 모드는 I/O 매핑 (ApiCall 의 OutTag/InTag 주소) 이 설정되어야 사용할 수 있습니다.\n\n" +
                "프로젝트에 I/O 를 먼저 설정한 후 다시 시도해 주세요.",
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
        OnPropertyChanged(nameof(IsHubHost));
        SetHubStatus(connected: false, reconnecting: false);
    }

    private bool HasIOConfigured()
    {
        var store = _storeProvider();
        var iomap = SignalIOMapModule.build(store);
        return iomap.Mappings.Length > 0;
    }

    /// <summary>현재 IO 매핑에서 dedup 된 PLC 주소 개수 — PLC 설정 다이얼로그 안내용.</summary>
    public int CountAutoImportablePlcAddresses()
    {
        var store = _storeProvider();
        var iomap = SignalIOMapModule.build(store);
        var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var k in iomap.OutAddressToMappings.Keys)
            if (!string.IsNullOrWhiteSpace(k)) set.Add(k);
        foreach (var k in iomap.InAddressToMappings.Keys)
            if (!string.IsNullOrWhiteSpace(k)) set.Add(k);
        return set.Count;
    }

    /// <summary>현재 IO 매핑 + UI 의 PlcSettings 로 PlcGatewayConfig 를 빌드.
    /// PLAY 시점 (TryStartHub) 에서 호출. 검증 실패 시 errors 채워 null 반환.</summary>
    public Ds2.Backend.Plc.PlcGatewayConfig? BuildPlcGatewayConfig(out System.Collections.Generic.List<string> errors)
    {
        var store = _storeProvider();
        var iomap = SignalIOMapModule.build(store);
        return PlcSettings.BuildGatewayConfig(iomap, out errors);
    }

    public bool CanChangeSpeed => !IsSimulating || IsSimPaused;

    [ObservableProperty] private double _simSpeed = 1.0;
    [ObservableProperty] private bool _simTimeIgnore;
    [ObservableProperty] private string _simClock = SimText.ClockZero;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureScenarioToProjectCommand))]
    private bool _hasReportData;

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
    public ObservableCollection<SimLogEntry> SimEventLog { get; } = [];
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
