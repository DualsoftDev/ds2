using System;
using System.Collections.Generic;
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

/// <summary>BuildPlcGatewayConfig 의 out 인자 시그니처 호환 delegate (Func 는 out 미지원).</summary>
public delegate PlcGatewayConfig? PlcGatewayConfigBuilder(out List<string> errors);

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

    // 본체에서 주입되는 read 의존
    private readonly Func<RuntimeMode>      _runtimeMode;
    private readonly Func<bool>             _isRealPlcConnected;
    private readonly Func<bool>             _isSimulating;
    private readonly Func<string>           _hubAddress;
    private readonly Func<string>           _monitoringHubAddress;
    private readonly Func<PlcSettings>      _plcSettings;
    private readonly PlcGatewayConfigBuilder _buildPlcGatewayConfig;
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
    /// Monitoring 은 실 PLC 연결 시에만 self-host (5051, read-only) — PLC 미연결이면
    /// 기존 동작대로 외부 Control hub (5050) 에 client 로 붙는다.
    /// VirtualPlant 는 항상 외부 Hub client.</summary>
    public bool IsHubHost =>
        _runtimeMode() == RuntimeMode.Control
        || (_runtimeMode() == RuntimeMode.Monitoring && _isRealPlcConnected());

    /// <summary>툴바에 표시할 hosting 상태 — self-host 인 모드일 때만 의미. Monitoring 은 [RO] 표시.</summary>
    public string HostingLabel =>
        !IsHubHost ? ""
        : _runtimeMode() == RuntimeMode.Monitoring ? "Self-Hosted [읽기전용]"
        : "Self-Hosted";

    /// <summary>Monitoring + 실 PLC 체크 — Promaker 가 자체 Hub(5051) 를 띄우고 PLC 게이트웨이를 직접 돌린다.</summary>
    private bool IsMonitoringSelfHost =>
        _runtimeMode() == RuntimeMode.Monitoring && _isRealPlcConnected();

    /// <summary>현재 모드가 편집/노출하는 Hub 주소. Monitoring + 실 PLC self-host 만 MonitoringHubAddress,
    /// 그 외(Monitoring PLC 미연결 포함)는 HubAddress. TextBox 가 mode 별 올바른 backing field 를 편집하도록 dispatch.</summary>
    public string EffectiveHubAddress
    {
        get => IsMonitoringSelfHost ? _monitoringHubAddress() : _hubAddress();
        set
        {
            if (IsMonitoringSelfHost) _setMonitoringHubAddress(value);
            else _setHubAddress(value);
        }
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
        Func<PlcSettings>      plcSettings,
        PlcGatewayConfigBuilder buildPlcGatewayConfig,
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
        _plcSettings            = plcSettings;
        _buildPlcGatewayConfig  = buildPlcGatewayConfig;
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

    // ── Tag routing ──────────────────────────────────────────────

    private void OnHubTagChanged(int generation, string address, string value, string source)
    {
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
