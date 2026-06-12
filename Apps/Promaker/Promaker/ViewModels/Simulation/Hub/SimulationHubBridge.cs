using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Backend.Common;
using Ds2.Backend.Plc;
using Ds2.Core;
using Ds2.Runtime.Engine.Passive;
using log4net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;

namespace Promaker.ViewModels;

/// <summary>
/// SignalR Hub + PLC gateway lifecycle collaborator. SimulationPanelState 의 partial 에서 분리.
/// 보유 상태: hub host / connection / batch sender / generation token / reconnect stabilization cts +
/// IsConnected / IsReconnecting / IsHosting (ObservableProperty) + StatusText / HostingLabel /
/// IsHubHost / EffectiveHubAddress 계산 속성. XAML 은 Simulation.Hub.X 직접 바인딩.
/// 본체(이 파일) = 상태/표면/Tag routing. Lifecycle (TryStart/Stop/ConnectAsync) 은 partial Lifecycle.cs.
/// </summary>
public sealed partial class SimulationHubBridge : ObservableObject
{
    private static readonly ILog SimLog = LogManager.GetLogger("Simulation");

    private WebApplication?           _hubHost;
    private HubConnection?            _hubConnection;
    private HubTagBatchSender?        _hubBatchSender;
    private CancellationTokenSource?  _hubConnectionCts;
    private CancellationTokenSource?  _reconnectStabilizationCts;
    private int                       _hubGeneration;
    /// SignalR 자동 재연결 시도 카운트. OnReconnecting 마다 ++, OnReconnected 시 0, 새 generation 시 0.
    /// ETA 라벨용 + UI 노출용.
    private int                       _reconnectAttempt;
    /// Monitoring+RealPlc 경로에서 Promaker.Agent (Windows Service) 가 5051 Hub 호스팅을 위임받았는지.
    /// TryStartHost 가 자체 BackendHost.start 대신 active.flag 를 쓴 경우 true → Stop 에서 TryDeactivate.
    private bool                      _delegatedToAgent;

    private const string AgentServiceName = "PromakerAgentService";

    /// <summary>설치된 Promaker.Agent.exe 의 경로 (있으면). 없으면 null — 자체 BackendHost 호스팅으로 fallback.
    /// 설치 스크립트는 {app}\Agent\Promaker.Agent.exe 로 번들한다. 개발 환경에서는 publish 디렉터리 직접 가리키도록
    /// PROMAKER_AGENT_EXE 환경변수 override 지원.</summary>
    private static readonly Lazy<string?> AgentExePath = new(() =>
    {
        var env = Environment.GetEnvironmentVariable("PROMAKER_AGENT_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(asmDir)) return null;
        var candidate = Path.Combine(asmDir, "Agent", "Promaker.Agent.exe");
        return File.Exists(candidate) ? candidate : null;
    });

    /// <summary>PromakerAgentService 가 SCM 에 등록되어 있고 <b>현재 Running</b> 상태인지.
    /// 등록만 되고 Stopped 면 false. 매 호출 fresh check (Lazy 하지 않음) — 사용자가 sc start/stop 을 런타임에 할 수 있어서.</summary>
    private static bool IsAgentServiceRunning()
    {
        try
        {
            using var sc = new ServiceController(AgentServiceName);
            // Status 접근이 실제 SCM 조회 트리거. 미등록이면 여기서 throw → false.
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Promaker.Agent.exe 프로세스가 현재 실행 중인지 (서비스 모드 / 콘솔 모드 모두 포착).
    /// 개발 시 `dotnet run --project Promaker.Agent` 로 띄운 콘솔 모드 Agent 는 서비스가 아니라서
    /// IsAgentServiceRunning() 는 false 인데, 이 체크가 true 로 잡아준다.</summary>
    private static bool IsAgentProcessRunning()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("Promaker.Agent");
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Promaker.Agent 가 가용한 상태인지 — 서비스 Running 또는 콘솔 모드 프로세스 발견.
    /// 둘 다 false 면 PLAY (Monitoring + 실 PLC) 가 차단된다 (Agent 가 모니터링 전담).
    /// AgentExePath 는 더 이상 게이팅하지 않음: 콘솔/개발 모드는 설치 위치에 exe 가 없을 수 있어서.</summary>
    public static bool IsAgentAvailable =>
        IsAgentServiceRunning() || IsAgentProcessRunning();

    // 본체에서 주입되는 read 의존
    private readonly Func<RuntimeMode>      _runtimeMode;
    private readonly Func<bool>             _isRealPlcConnected;
    private readonly Func<bool>             _isSimulating;
    private readonly Func<string>           _hubAddress;
    private readonly Func<string>           _monitoringHubAddress;
    private readonly Func<bool>             _hasRuntimeSession;
    private readonly Func<string, string, string, bool> _shouldIgnoreHubSource;
    private readonly Func<string, string, string, IEnumerable<RuntimeHubEffect>> _handleHubTag;
    private readonly Func<string>           _resolveRuntimeHubSource;
    private readonly Func<bool>             _hasIoMap;
    private readonly Func<IEnumerable<string>> _txOutAddresses;
    private readonly Dispatcher             _dispatcher;

    // 본체에서 주입되는 write 의존 (콜백)
    private readonly Action<string, LogSeverity> _addSimLog;
    private readonly Action<string>         _setStatusText;
    private readonly Action<string>         _setSimStatusText;
    private readonly Action<IEnumerable<RuntimeHubEffect>> _applyRuntimeHubEffects;

    // 본체 HubAddress / MonitoringHubAddress 의 setter — EffectiveHubAddress 의 set 처리용.
    private readonly Action<string> _setHubAddress;
    private readonly Action<string> _setMonitoringHubAddress;

    // ── XAML 바인딩 표면 (Simulation.Hub.X 로 직접 노출) ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isReconnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHubHost))]
    [NotifyPropertyChangedFor(nameof(EffectiveHubAddress))]
    [NotifyPropertyChangedFor(nameof(HostingLabel))]
    private bool _isHosting;

    public string StatusText =>
        IsConnected ? "Hub 연결됨"
        : IsReconnecting ? "Hub 재연결 시도 중"
        : "Hub 끊김";

    /// <summary>Control 은 항상 Promaker 자체가 Hub 호스트.
    /// Monitoring + 실 PLC 는 host 모드(5051) 지만 실제 호스팅은 Promaker.Agent (Windows Service) 가 전담 —
    /// Promaker 본체는 active.flag 만 쓰고 5051 의 클라이언트로 붙는다.
    /// Monitoring PLC 미연결이면 외부 Control hub (5050) 에 client 로 붙는다.
    /// VirtualPlant 는 항상 외부 Hub client.</summary>
    public bool IsHubHost =>
        _runtimeMode() == RuntimeMode.Control
        || (_runtimeMode() == RuntimeMode.Monitoring && _isRealPlcConnected());

    /// <summary>툴바에 표시할 hosting 상태. Monitoring + 실 PLC 는 항상 Agent 위임이라 "Agent [읽기전용]".
    /// Control 은 자체 호스팅.</summary>
    public string HostingLabel =>
        !IsHubHost ? ""
        : _runtimeMode() == RuntimeMode.Monitoring
            ? "Agent [읽기전용]"
            : "Self-Hosted";

    /// <summary>편집/노출 Hub 주소. Agent 가 5051 단일 호스팅이라 모든 모드(Control/Monitoring/VP)가
    /// 같은 주소(_monitoringHubAddress, 기본 localhost:5051)를 공유한다.</summary>
    public string EffectiveHubAddress
    {
        get => _monitoringHubAddress();
        set => _setMonitoringHubAddress(value);
    }

    /// <summary>본체 RuntimeMode/PLC 토글 시 호출 — IsHubHost / EffectiveHubAddress / HostingLabel 의 PropertyChanged 발화.</summary>
    internal void RaiseHostingDependentsChanged()
    {
        OnPropertyChanged(nameof(IsHubHost));
        OnPropertyChanged(nameof(EffectiveHubAddress));
        OnPropertyChanged(nameof(HostingLabel));
    }

    /// <summary>본체 HubAddress / MonitoringHubAddress ObservableProperty 변경 시 호출 — EffectiveHubAddress 갱신.</summary>
    internal void RaiseEffectiveAddressChanged() => OnPropertyChanged(nameof(EffectiveHubAddress));

    /// <summary>Hub 연결 3-state 을 두 bool 한 쌍으로 set. Reconnecting 먼저 → Connected 나중 순서를
    /// 헬퍼 안에 고정해 두 bool 동시 true 모순 시점이 호출자에 의존하지 않도록 보장.</summary>
    public void SetStatus(bool connected, bool reconnecting)
    {
        IsReconnecting = reconnecting;
        IsConnected = connected;
    }

    public SimulationHubBridge(
        Func<RuntimeMode>      runtimeMode,
        Func<bool>             isRealPlcConnected,
        Func<bool>             isSimulating,
        Func<string>           hubAddress,
        Func<string>           monitoringHubAddress,
        Action<string>         setHubAddress,
        Action<string>         setMonitoringHubAddress,
        Func<bool>             hasRuntimeSession,
        Func<string, string, string, bool> shouldIgnoreHubSource,
        Func<string, string, string, IEnumerable<RuntimeHubEffect>> handleHubTag,
        Func<string>           resolveRuntimeHubSource,
        Func<bool>             hasIoMap,
        Func<IEnumerable<string>> txOutAddresses,
        Dispatcher             dispatcher,
        Action<string, LogSeverity> addSimLog,
        Action<string>         setStatusText,
        Action<string>         setSimStatusText,
        Action<IEnumerable<RuntimeHubEffect>> applyRuntimeHubEffects)
    {
        _runtimeMode            = runtimeMode;
        _isRealPlcConnected     = isRealPlcConnected;
        _isSimulating           = isSimulating;
        _hubAddress             = hubAddress;
        _monitoringHubAddress   = monitoringHubAddress;
        _setHubAddress          = setHubAddress;
        _setMonitoringHubAddress= setMonitoringHubAddress;
        _hasRuntimeSession      = hasRuntimeSession;
        _shouldIgnoreHubSource  = shouldIgnoreHubSource;
        _handleHubTag           = handleHubTag;
        _resolveRuntimeHubSource= resolveRuntimeHubSource;
        _hasIoMap               = hasIoMap;
        _txOutAddresses         = txOutAddresses;
        _dispatcher             = dispatcher;
        _addSimLog              = addSimLog;
        _setStatusText          = setStatusText;
        _setSimStatusText       = setSimStatusText;
        _applyRuntimeHubEffects = applyRuntimeHubEffects;
    }

    // ── 노출 상태 ────────────────────────────────────────────────

    /// <summary>현재 generation 의 batch sender — 없으면 null. WriteTag 송신은 모두 이 sender 경유.
    /// HubTagBatchSender 가 internal 이라 exposed property 도 internal.</summary>
    internal HubTagBatchSender? BatchSender => _hubBatchSender;
    public HubConnection?       Connection  => _hubConnection;
    public int                  CurrentGeneration => Volatile.Read(ref _hubGeneration);
    public bool IsCurrentGeneration(int generation) =>
        Volatile.Read(ref _hubGeneration) == generation;
    public bool IsCurrentConnection(int generation, HubConnection hub) =>
        IsCurrentGeneration(generation) && ReferenceEquals(_hubConnection, hub);

    /// <summary>외부 UI(수동 컨트롤러 다이얼로그) 가 hub 의 OnTagChanged 를 구독하기 위한 이벤트.
    /// (address, value, source) — engine/runtime session 과 무관히 hub 가 받는 모든 변화를 그대로 흘림.</summary>
    public event Action<string, string, string>? TagBroadcast;

    /// <summary>v12 자동 줄자 — Agent 의 OnLearnedDuration(학습된 device duration) 수신 시 발화.
    /// SimulationPanelState 가 구독해 누적했다가 정지 시 사용자 선택으로 모델에 반영(dirty).</summary>
    public event Action<Ds2.Backend.Common.LearnedDurationPayload>? LearnedDurationReceived;

    /// <summary>PLC 스캔 주기 동기화 — 연결 직후 GetScanIntervalMs pull + OnScanIntervalChanged push 수신 시 발화.
    /// SimulationPanelState 가 구독해 PlcSettings.ScanIntervalMs 를 갱신 — Promaker/DSPilot 어느 쪽이
    /// 바꿔도 양쪽 슬라이더가 같은 값을 본다.</summary>
    public event Action<int>? ScanIntervalChanged;

    /// <summary>Promaker.Agent 가 호스팅하는 SignalHub 로부터 어댑터별 PLC 연결 상태 변화를 받았을 때 발화.
    /// 툴바/상태바가 구독해 "PLC 통신 실패" 라벨/툴팁을 갱신할 수 있다.
    /// 첫 PLAY 직후 OnConnectedAsync snapshot 으로 모든 어댑터의 초기 상태가 한 번씩 전달된다.</summary>
    public event Action<Ds2.Backend.Common.PlcConnectionStatus>? PlcConnectionStatusChanged;

    /// <summary>건강 기준선 수동 동결 — hub 의 OnHealthBaselineFreeze 브로드캐스트 수신 시 발화.
    /// SimulationPanelState 가 구독해 자기 추적기의 미동결 기준선을 동결한다.
    /// (발신은 DSPilot 설정 페이지 → hub FreezeHealthBaseline — Promaker 리본 버튼은 제거됨.)</summary>
    public event Action? HealthBaselineFreezeRequested;

    /// <summary>테스트 전용 — 수신 신호 차단(통신 두절 시뮬레이션).
    /// SignalR 연결은 유지한 채 수신 태그만 무시해 "장비(신호원)는 계속 도는데 나만 안 보이는"
    /// PLC 단선 시나리오를 로컬에서 재현한다. 토글 해제 시 진행된 위치의 신호부터 다시 보임
    /// (스킵 구간 발생) — coast/재합류 검증용.</summary>
    public bool TestSignalBlocked { get; set; }

    // ── Tag routing ──────────────────────────────────────────────

    private void OnHubTagChanged(int generation, string address, string value, string source)
    {
        if (TestSignalBlocked)
            return;
        if (!IsCurrentGeneration(generation))
            return;

        _dispatcher.BeginInvoke(() =>
        {
            if (IsCurrentGeneration(generation))
                _addSimLog($"[Hub수신] {address}={value} from={source}", LogSeverity.Info);
        });

        // 외부 구독자에게 broadcast — engine·session 상태와 무관히 항상 발화.
        try { TagBroadcast?.Invoke(address, value, source); }
        catch (Exception ex) { SimLog.Error("TagBroadcast subscriber threw", ex); }

        if (!_hasRuntimeSession())
            return;
        // 자기 모드의 source 는 무시 (순환 방지)
        if (_shouldIgnoreHubSource(address, value, source))
            return;

        var effects = _handleHubTag(address, value, source);
        _applyRuntimeHubEffects(effects);
    }

    /// <summary>수동 컨트롤러 측에서 OUT 태그를 hub 로 쓰기 위한 진입점.
    /// 내부적으로 Control source 로 InvokeAsync — SignalHub 가 PLC 게이트웨이로 forward.
    /// hub 미연결이면 false 반환.</summary>
    public async Task<bool> WriteTagFromManualAsync(string address, string value)
    {
        var conn = _hubConnection;
        if (conn is null || conn.State != HubConnectionState.Connected)
            return false;
        try
        {
            await conn.InvokeAsync(HubMethod.WriteTag, address, value, HubSource.Control);
            return true;
        }
        catch (Exception ex)
        {
            SimLog.Error($"WriteTagFromManual failed {address}={value}", ex);
            return false;
        }
    }

    /// <summary>수동 컨트롤러 다이얼로그 초기 로드 시 hub 캐시에서 현재 값 한 번 조회.</summary>
    public async Task<string> QueryTagFromManualAsync(string address)
    {
        var conn = _hubConnection;
        if (conn is null || conn.State != HubConnectionState.Connected) return "";
        try { return await conn.InvokeAsync<string>(HubMethod.QueryTag, address); }
        catch { return ""; }
    }
}
