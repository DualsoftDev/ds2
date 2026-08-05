using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ds2.Aasx;
using Ds2.Backend;
using Ds2.Backend.Common;
using Ds2.Backend.Plc;
using Ds2.Backend.Runtime;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.IO;
using log4net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using Promaker.Shared;

namespace Promaker.Agent;

/// <summary>
/// Agent 의 idle/active 상태머신.
/// - active.flag 생성 → Activate (BackendHost 시작)
/// - active.flag 삭제 → Deactivate (BackendHost 정지)
    /// - session.json / PlcConnection.json / project.aasx / OPC UA 설정 변경 → 활성 중이면 Restart
///
/// 모든 상태 전이는 <see cref="_gate"/> SemaphoreSlim 로 직렬화 — race 방지.
/// FileSystemWatcher 의 잘림/중복 이벤트는 <see cref="_debounce"/> Timer 로 1초 quiet 대기 후 1회 처리.
/// </summary>
public sealed class MonitoringSupervisor : IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MonitoringSupervisor));
    private const int Port = 5051;
    private const int DebounceMs = 1000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    // _pendingReason / _inflight 보호용 monitor lock — SemaphoreSlim 객체에 lock 거는 antipattern 회피.
    private readonly object _pendingLock = new();
    private WebApplication? _app;
    // Agent 단일 호스팅: Monitoring engine 을 Agent 가 직접 보유. PLC IN → engine → OnRuntime* push.
    // _app 과 lifecycle 동일 — TryActivate 에서 생성, Deactivate/restart 에서 정리.
    private ISimulationEngine? _engine;
    // Agent가 OPC UA 서버의 정식 소유자다. WPF 데모 인스턴스와 데이터 루트를 분리한다.
    private readonly OpcUaServerHost _opcUaHost = new(SharedPaths.AgentOpcUaDataDirectory);
    private SimEngineUaBridge? _uaBridge;
    private AidSouthboundRuntime? _aidSouthbound;
    private AidHttpWebhookRouter? _aidWebhookRouter;
    // 성공적으로 구동된 마지막 계획. 새 candidate가 stop 이후 실패하면 같은 in-memory 모델로 즉시 복구한다.
    private ActivationPlan? _activePlan;
    private FileSystemWatcher? _flagWatcher;
    private FileSystemWatcher? _sessionWatcher;
    private FileSystemWatcher? _plcWatcher;
    private FileSystemWatcher? _aasxWatcher;
    private FileSystemWatcher? _opcUaSettingsWatcher;
    private Timer? _debounce;
    private DebounceReason _pendingReason;
    // OnDebounceFiredAsync 가 실행 중인 동안 ScheduleDebounce 가 새 Timer 를 만들지 않도록 가드.
    // 처리 중 들어온 이벤트는 _pendingReason 에 누적하고, 마무리 시점에 self-reschedule.
    private bool _inflight;

    /// <summary>부팅 시 한 번 호출 — 디렉터리 준비, 워처 시작, 현재 flag 상태 기준으로 초기 전이.</summary>
    public async Task StartAsync()
    {
        Directory.CreateDirectory(SharedPaths.AgentDirectory);
        StartWatchers();

        if (AgentSession.IsActive())
        {
            Log.Info("active.flag detected at boot → activating.");
            await TryActivateAsync().ConfigureAwait(false);
        }
        else
        {
            Log.Info("Idle on boot (no active.flag). Waiting for Promaker WPF PLAY...");
        }
    }

    private void StartWatchers()
    {
        _flagWatcher = MakeWatcher(SharedPaths.AgentDirectory, Path.GetFileName(SharedPaths.AgentActiveFlagPath),
            (s, e) => ScheduleDebounce(DebounceReason.FlagChanged, e.ChangeType.ToString(), e.FullPath));
        _sessionWatcher = MakeWatcher(SharedPaths.AgentDirectory, Path.GetFileName(SharedPaths.AgentSessionJsonPath),
            (s, e) => ScheduleDebounce(DebounceReason.ConfigChanged, e.ChangeType.ToString(), e.FullPath));
        _plcWatcher = MakeWatcher(SharedPaths.SharedDirectory, Path.GetFileName(SharedPaths.PlcConnectionFilePath),
            (s, e) => ScheduleDebounce(DebounceReason.ConfigChanged, e.ChangeType.ToString(), e.FullPath));
        // 공유 AASX 변경 감지 — Promaker WPF 가 모델 수정 후 "DSPilot 공유 위치에 저장" 또는 Hub 진입 시
        // 자동 publish 한다. 활성 상태에서 변경되면 새 IOMap 으로 재구독.
        _aasxWatcher = MakeWatcher(SharedPaths.SharedDirectory, Path.GetFileName(SharedPaths.AasxFilePath),
            (s, e) => ScheduleDebounce(DebounceReason.ConfigChanged, e.ChangeType.ToString(), e.FullPath));
        _opcUaSettingsWatcher = MakeWatcher(SharedPaths.AgentDirectory, Path.GetFileName(SharedPaths.AgentOpcUaSettingsPath),
            (s, e) => ScheduleDebounce(DebounceReason.ConfigChanged, e.ChangeType.ToString(), e.FullPath));
        Log.Info("FileSystemWatchers started: flag, session, plc, aasx, opcua.");
    }

    private static FileSystemWatcher MakeWatcher(string dir, string fileName, FileSystemEventHandler onChange)
    {
        Directory.CreateDirectory(dir);
        var w = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        w.Created += onChange;
        w.Changed += onChange;
        w.Deleted += onChange;
        w.Renamed += (s, e) => onChange(s, new FileSystemEventArgs(WatcherChangeTypes.Renamed, dir, fileName));
        w.Error += (_, e) =>
        {
            Log.Error($"FileSystemWatcher overflow/error for '{fileName}'; scheduling a full configuration recheck.",
                e.GetException());
            onChange(w, new FileSystemEventArgs(WatcherChangeTypes.All, dir, fileName));
        };
        return w;
    }

    private void ScheduleDebounce(DebounceReason reason, string changeType, string fullPath)
    {
        // 여러 워처에서 동시에 들어온 이벤트는 [Flags] bitmask 로 OR 누적 — 한 debounce window 안에
        // FlagChanged 와 ConfigChanged 가 모두 발화하면 둘 다 처리해야 한다.
        // (예: Promaker WPF 가 "Agent 보내기" 시 PlcConnection.json 저장 + active.flag 재기록 →
        //  두 이벤트가 거의 동시에 들어옴. 덮어쓰면 새 PLC 설정이 적용 안 됨.)
        bool startTimer;
        lock (_pendingLock)
        {
            _pendingReason |= reason;
            // 처리 중이면 reason 만 누적하고 종료 — OnDebounceFiredAsync 가 마무리 시점에
            // _pendingReason 을 다시 보고 self-reschedule 한다.
            // (BackendHost.stop 이 수 초 걸리는 동안 새 이벤트가 들어오면 두 번째 Timer 가
            //  병렬 OnDebounceFiredAsync 를 만들어 gate 큐잉 race 가 났던 사례 회피.)
            if (_inflight)
            {
                Log.Debug($"Watcher event during in-flight: {changeType} on {fullPath} → reason+={reason}.");
                return;
            }
            startTimer = true;
        }
        if (startTimer)
        {
            Log.Debug($"Watcher event: {changeType} on {fullPath} → debounce({reason}).");
            _debounce?.Dispose();
            _debounce = new Timer(_ => _ = OnDebounceFiredAsync(), null, DebounceMs, Timeout.Infinite);
        }
    }

    private async Task OnDebounceFiredAsync()
    {
        // While 루프 — 한 iteration 처리 도중 새 이벤트가 누적되면 즉시 한 번 더 처리.
        // _inflight 가 true 인 동안 ScheduleDebounce 는 Timer 를 만들지 않으므로 여기서 drain.
        while (true)
        {
            DebounceReason reason;
            lock (_pendingLock)
            {
                reason = _pendingReason;
                _pendingReason = DebounceReason.None;
                if (reason == DebounceReason.None)
                {
                    _inflight = false;
                    return;
                }
                _inflight = true;
            }

            try
            {
                // FlagChanged 와 ConfigChanged 가 함께 들어왔으면 둘 다 처리.
                // 순서: Flag 먼저(idle↔active 전이) → Config (활성 상태 restart).
                // active 상태에서 둘 다 set 되면 Flag 는 no-op + Config 가 restart 를 수행 → 새 IP 적용.
                if ((reason & DebounceReason.FlagChanged) != 0)
                    await HandleFlagChangedAsync().ConfigureAwait(false);
                if ((reason & DebounceReason.ConfigChanged) != 0)
                    await HandleConfigChangedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Unhandled exception in debounce handler.", ex);
            }
        }
    }

    private async Task HandleFlagChangedAsync()
    {
        var nowActive = AgentSession.IsActive();
        var wasActive = _app is not null;
        if (nowActive && !wasActive)
        {
            Log.Info("Flag transition: idle → active.");
            await TryActivateAsync().ConfigureAwait(false);
        }
        else if (!nowActive && wasActive)
        {
            Log.Info("Flag transition: active → idle.");
            await DeactivateAsync().ConfigureAwait(false);
        }
        // else: flag 상태 = 현재 BackendHost 상태 일치 — 아무것도 안 함.
    }

    private async Task HandleConfigChangedAsync()
    {
        if (_app is null)
        {
            Log.Debug("Config changed while idle — will be picked up on next activation.");
            return;
        }

        // 스캔 주기만 바뀐 변경은 재시작 없이 라이브 적용 — Promaker/DSPilot 슬라이더가
        // hub SetScanIntervalMs 로 영속화한 self-write, 또는 파일 직접 편집 모두 이 경로.
        var (fingerprint, scanMs) = ComputeConfigFingerprint();
        if (fingerprint == _appliedConfigFingerprint)
        {
            if (scanMs != _appliedScanIntervalMs)
            {
                ApplyScanIntervalLive(scanMs, broadcast: true);
                return;
            }
            Log.Debug("Config change event with no effective difference — ignoring.");
            return;
        }

        Log.Info("Config changed while active → restart with new settings.");
        // 이전 구현: Deactivate → TryActivate 를 두 번의 gate scope 로 나눠 호출.
        // gate 사이 race 로 다른 task 가 BackendHost 를 다시 띄우면 TryActivate 의 skip 분기가
        // silent drop 됐던 사례 (2026-05-28 17:12). TryActivate 한 번 호출로 통합 — 내부에서
        // _app != null 이면 stop 한 뒤 새 설정으로 재시작 (한 gate scope 안 atomic).
        await TryActivateAsync().ConfigureAwait(false);
    }

    // ── 스캔 주기 라이브 적용 (재시작 없음) ─────────────────────────
    // 활성 시점의 설정 지문(스캔 주기 제외 정규화). ConfigChanged 가 지문 동일 + 스캔만 다르면
    // BackendHost 재시작 대신 게이트웨이 override 로 즉시 반영하고 전 클라이언트에 동기화한다.
    private string _appliedConfigFingerprint = "";
    private int _appliedScanIntervalMs;

    private static (string Fingerprint, int ScanMs) ComputeConfigFingerprint(
        AgentSession? selectedSession = null,
        OpcUaServerSettings? selectedUaSettings = null)
    {
        var sessionState = "valid";
        AgentSession session;
        if (selectedSession is not null)
            session = selectedSession;
        else if (File.Exists(SharedPaths.AgentSessionJsonPath))
        {
            if (!AgentSession.TryLoadExact(
                    SharedPaths.AgentSessionJsonPath, out var loadedSession, out var sessionError)
                || loadedSession is null)
            {
                session = AgentSession.ForCurrentDefaults(requestedBy: "agent");
                sessionState = $"invalid:{sessionError}:{FingerprintFile(SharedPaths.AgentSessionJsonPath)}";
            }
            else session = loadedSession;
        }
        else session = AgentSession.ForCurrentDefaults(requestedBy: "agent");
        var plcPath = string.IsNullOrWhiteSpace(session.PlcConnectionPath)
            ? SharedPaths.PlcConnectionFilePath
            : session.PlcConnectionPath;
        PlcConnectionSettings plc;
        var plcState = "valid";
        if (File.Exists(plcPath)
            && (!PlcConnectionSettings.TryLoadExact(plcPath, out var loadedPlc, out var plcError)
                || loadedPlc is null))
        {
            plc = new PlcConnectionSettings();
            plcState = $"invalid:{plcError}:{FingerprintFile(plcPath)}";
        }
        else plc = PlcConnectionSettings.LoadOrDefault(plcPath);
        var scanMs = plc.ScanIntervalMs;

        // 스캔 주기를 0 으로 밀어 정규화 — 나머지 필드/프로파일이 같으면 "scan-only 변경" 판정.
        plc.ScanIntervalMs = 0;
        foreach (var profile in plc.Profiles.Values)
            profile.ScanIntervalMs = 0;
        var plcNormalized = System.Text.Json.JsonSerializer.Serialize(plc);

        var aasxStamp = FingerprintFile(session.AasxPath);
        var sessionStamp = $"{session.AasxPath}|{session.PlcConnectionPath}|{session.RuntimeMode}|{session.IsRealPlcConnected}";
        string opcUaNormalized;
        var uaState = "valid";
        if (selectedUaSettings is not null)
            opcUaNormalized = System.Text.Json.JsonSerializer.Serialize(selectedUaSettings);
        else if (File.Exists(SharedPaths.AgentOpcUaSettingsPath)
                 && (!OpcUaServerSettings.TryLoadExact(
                         SharedPaths.AgentOpcUaSettingsPath, out var loadedUa, out var uaError)
                     || loadedUa is null))
        {
            opcUaNormalized = "";
            uaState = $"invalid:{uaError}:{FingerprintFile(SharedPaths.AgentOpcUaSettingsPath)}";
        }
        else
            opcUaNormalized = System.Text.Json.JsonSerializer.Serialize(
                OpcUaServerSettings.LoadAgentOrDefault(SharedPaths.AgentOpcUaSettingsPath));
        return ($"{sessionState}\n{sessionStamp}\n{aasxStamp}\n{plcState}\n{plcNormalized}\n{uaState}\n{opcUaNormalized}", scanMs);
    }

    private static string FingerprintFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "none";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch (Exception ex)
        {
            return $"unreadable:{ex.GetType().Name}";
        }
    }

    private void ApplyScanIntervalLive(int ms, bool broadcast)
    {
        var app = _app;
        if (app is null) return;
        var clamped = Math.Clamp(ms, 10, 500);   // SignalHub.SetScanIntervalMs 와 동일 clamp
        var gateway = app.Services.GetService<IPlcGateway>();
        if (gateway is null)
        {
            Log.Warn("ApplyScanIntervalLive: IPlcGateway not resolvable — skipped.");
            return;
        }
        gateway.ScanIntervalOverrideMs = FSharpOption<int>.Some(clamped);
        _appliedScanIntervalMs = ms;
        Log.Info($"Scan interval live-applied: {clamped}ms (no restart).");
        if (broadcast)
        {
            var hubCtx = app.Services.GetService<IHubContext<SignalHub>>();
            hubCtx?.Clients.All.SendAsync(HubMethod.OnScanIntervalChanged, clamped);
        }
    }

    /// <summary>SignalHub.SetScanIntervalMs 의 영속화 훅 — PlcConnection.json 의 플랫 + 활성 벤더
    /// 프로파일 스캔 주기를 갱신 저장. _appliedScanIntervalMs 를 먼저 맞춰 self-write 로 발화하는
    /// ConfigChanged 가 no-op 이 되게 한다 (이중 적용/브로드캐스트 방지).</summary>
    private void PersistScanInterval(int ms)
    {
        try
        {
            var session = _activePlan?.Session ?? AgentSession.TryLoad();
            var plcPath = string.IsNullOrWhiteSpace(session?.PlcConnectionPath)
                ? SharedPaths.PlcConnectionFilePath
                : session!.PlcConnectionPath;
            var settings = PlcConnectionSettings.LoadOrDefault(plcPath);
            settings.ScanIntervalMs = ms;
            if (settings.Profiles.TryGetValue(settings.Vendor, out var profile))
                profile.ScanIntervalMs = ms;
            _appliedScanIntervalMs = ms;
            if (!settings.TrySave(plcPath))
                Log.Warn($"PersistScanInterval: TrySave failed ({plcPath}) — live value applied, file not updated.");
        }
        catch (Exception ex)
        {
            Log.Warn("PersistScanInterval threw — live value applied, file not updated.", ex);
        }
    }

    /// <summary>SignalHub.SetAutoCalibrate 의 영속화 훅 — PlcConnection.json 에 자동정합 ON/OFF 저장.
    /// 스캔주기와 동형. OFF 상태가 재시작 후에도 유지되게 한다.</summary>
    private void PersistAutoCalibrate(bool on)
    {
        try
        {
            var session = _activePlan?.Session ?? AgentSession.TryLoad();
            var plcPath = string.IsNullOrWhiteSpace(session?.PlcConnectionPath)
                ? SharedPaths.PlcConnectionFilePath
                : session!.PlcConnectionPath;
            var settings = PlcConnectionSettings.LoadOrDefault(plcPath);
            settings.AutoDurationCalibrate = on;
            if (!settings.TrySave(plcPath))
                Log.Warn($"PersistAutoCalibrate: TrySave failed ({plcPath}) — live value applied, file not updated.");
        }
        catch (Exception ex)
        {
            Log.Warn("PersistAutoCalibrate threw — live value applied, file not updated.", ex);
        }
    }

    private static void WireDeviceCredentialValidator(IReadOnlySet<string>? allowedDeviceIds)
    {
        SignalHub.ValidateDeviceCredential = null;
        if (allowedDeviceIds is null)
        {
            Log.Info("Device credentials file absent or unreadable — Hub device whitelist disabled.");
            return;
        }

        SignalHub.ValidateDeviceCredential = deviceId =>
            !string.IsNullOrWhiteSpace(deviceId) && allowedDeviceIds.Contains(deviceId);
        Log.Info($"Device whitelist wired — {allowedDeviceIds.Count} registered device(s).");
    }

    private static bool TryLoadDeviceCredentialPolicy(
        AgentSession session,
        out IReadOnlySet<string>? policy,
        out string error)
    {
        policy = null;
        error = "";
        var delegated = !string.Equals(session.RuntimeMode, "Control", StringComparison.OrdinalIgnoreCase)
                        && !session.IsRealPlcConnected;
        if (!delegated) return true;

        var hubScheme = Environment.GetEnvironmentVariable("DS2_AGENT_HUB_SCHEME")?.Trim();
        var secureTransport = string.Equals(hubScheme, "https", StringComparison.OrdinalIgnoreCase);
        var privateHttpOptIn = bool.TryParse(
            Environment.GetEnvironmentVariable("DS2_AGENT_HUB_ALLOW_PRIVATE_HTTP"), out var allowPrivateHttp)
            && allowPrivateHttp;
        if (!secureTransport && !privateHttpOptIn)
        {
            error = "Delegated Hub requires HTTPS, or explicit DS2_AGENT_HUB_ALLOW_PRIVATE_HTTP=true on a private network.";
            return false;
        }

        var path = Environment.GetEnvironmentVariable("DS2_AGENT_DEVICE_CREDENTIALS_PATH");
        if (string.IsNullOrWhiteSpace(path)) path = DeviceCredentialsPath;
        path = Path.GetFullPath(path);

        if (!File.Exists(path))
        {
            Log.Info($"Device credentials file absent ({path}) — Hub device whitelist disabled.");
            return true;
        }

        try
        {
            var document = JsonSerializer.Deserialize<DeviceCredentialsFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var deviceId in document?.DeviceIds ?? Array.Empty<string>())
            {
                var id = deviceId?.Trim();
                if (!string.IsNullOrWhiteSpace(id)) allowed.Add(id);
            }
            policy = allowed;
        }
        catch (Exception ex)
        {
            // 기존 CloudWorks 계약과 동일하게 가용성 우선: 파일 파싱 실패는 Agent 활성화를 막지 않는다.
            Log.Warn($"Device credential load failed ({path}) — Hub device whitelist disabled.", ex);
        }
        return true;
    }

    /// <summary>cloudinit device-credentials.json 계약 경로 — 쓰는 쪽(cloudinit.py DEVICE_CREDENTIALS_PATH)과 일치.</summary>
    private const string DeviceCredentialsPath = "/etc/agent/device-credentials.json";

    private sealed record DeviceCredentialsFile
    {
        public int Version { get; init; }
        public string[]? DeviceIds { get; init; }
    }

    /// <summary>실행 중인 runtime을 내리기 전에 완성하는 side-effect-free 활성화 계획.</summary>
    private sealed record ActivationPlan(
        AgentSession Session,
        DsStore Store,
        PlcGatewayConfig GatewayConfig,
        AidXgtConfigResult? AidXgtPlan,
        AidSouthboundConfigResult? AidSouthboundPlan,
        OpcUaServerSettings UaSettings,
        IReadOnlySet<string>? DeviceCredentials,
        SimIndex Index,
        string ModelHash);

    /// <summary>
    /// AASX import, AID/PLC 바인딩, runtime index 생성을 모두 선행 검증한다.
    /// false면 현재 runtime을 그대로 유지할 수 있어 잘못된 AASX가 정상 UA/Backend를 내리지 않는다.
    /// </summary>
    private static bool TryBuildActivationPlan(
        AgentSession session,
        out ActivationPlan? plan,
        out string error,
        OpcUaServerSettings? uaSettingsOverride = null)
    {
        plan = null;
        error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(session.AasxPath) || !File.Exists(session.AasxPath))
            {
                error = $"AASX not found at '{session.AasxPath}'.";
                return false;
            }
            if (!AasxPackageSafety.TryValidate(session.AasxPath, out var packageError))
            {
                error = packageError;
                return false;
            }

            var store = new DsStore();
            var importResult = AasxImporter.importIntoStoreWithError(store, session.AasxPath);
            if (importResult.IsError)
            {
                error = $"AASX load failed: {importResult.ErrorValue}";
                return false;
            }

            AidXgtConfigResult? aidXgtPlan = null;
            AidSouthboundConfigResult? aidSouthboundPlan = null;
            var project = store.Projects.Values.FirstOrDefault();
            if (project?.AssetInterfaces is { } aid)
            {
                aidXgtPlan = AidXgtGatewayConfig.buildForProject(store, project, aid.Value);
                aidSouthboundPlan = AidSouthboundConfig.buildForProject(store, project, aid.Value);
                if (aidSouthboundPlan is { HasBinding: true, Success: false })
                {
                    error = $"AID southbound config build failed: {string.Join(" / ", aidSouthboundPlan.Errors)}";
                    return false;
                }
                if (aidSouthboundPlan is { HasBinding: true, Success: true }
                    && !AidSouthboundRuntime.TryValidatePlan(aidSouthboundPlan, out var adapterErrors))
                {
                    error = $"AID southbound adapter validation failed: {string.Join(" / ", adapterErrors)}";
                    return false;
                }

                var duplicateSignalIds = (aidXgtPlan?.Signals.Select(signal => signal.SignalId)
                        ?? Enumerable.Empty<string>())
                    .Concat(aidSouthboundPlan?.Endpoints.SelectMany(endpoint =>
                        endpoint.Signals.Select(signal => signal.SignalId)
                            .Concat(endpoint.Events.Select(eventBinding => eventBinding.SignalId)))
                        ?? Enumerable.Empty<string>())
                    .GroupBy(signalId => signalId, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .OrderBy(signalId => signalId, StringComparer.Ordinal)
                    .ToArray();
                if (duplicateSignalIds.Length > 0)
                {
                    error = $"AID signalId must be unique across every binding: {string.Join(", ", duplicateSignalIds)}";
                    return false;
                }
            }

            PlcGatewayConfig gatewayConfig;
            if (aidXgtPlan is { HasBinding: true })
            {
                if (!aidXgtPlan.Success)
                {
                    error = $"AID InterfaceXGT config build failed: {string.Join(" / ", aidXgtPlan.Errors)}";
                    return false;
                }
                gatewayConfig = aidXgtPlan.Config;
            }
            else if (aidSouthboundPlan is { HasBinding: true })
            {
                gatewayConfig = new PlcGatewayConfig(
                    Microsoft.FSharp.Collections.ListModule.OfSeq(Array.Empty<PlcConnectionConfig>()));
                Log.Info("Standard AID southbound adapters own acquisition; XGT gateway is empty.");
            }
            else
            {
                error = "AID collection binding is required. Define InterfaceXGT, OPC UA, Modbus, MQTT, or HTTP in AssetInterfacesDescription.";
                return false;
            }

            OpcUaServerSettings uaSettings;
            if (uaSettingsOverride is not null)
            {
                uaSettings = uaSettingsOverride;
            }
            else if (File.Exists(SharedPaths.AgentOpcUaSettingsPath))
            {
                if (!OpcUaServerSettings.TryLoadExact(
                        SharedPaths.AgentOpcUaSettingsPath, out var loadedUaSettings, out var loadError)
                    || loadedUaSettings is null)
                {
                    error = $"Agent OPC UA settings rejected: {loadError}";
                    return false;
                }
                uaSettings = loadedUaSettings;
            }
            else
            {
                uaSettings = OpcUaServerSettings.LoadAgentOrDefault(SharedPaths.AgentOpcUaSettingsPath);
            }
            if (!uaSettings.TryValidateForAgent(out var uaSettingsError))
            {
                error = $"Agent OPC UA settings rejected: {uaSettingsError}";
                return false;
            }
            if (!uaSettings.Enabled
                && (aidXgtPlan is { HasBinding: true } || aidSouthboundPlan is { HasBinding: true }))
            {
                error = "AID bindings require the Agent OPC UA server, but it is disabled.";
                return false;
            }
            if (!TryLoadDeviceCredentialPolicy(session, out var deviceCredentials, out var deviceCredentialError))
            {
                error = deviceCredentialError;
                return false;
            }

            var index = SimIndexModule.build(store, 10);
            plan = new ActivationPlan(
                session,
                store,
                gatewayConfig,
                aidXgtPlan,
                aidSouthboundPlan,
                uaSettings,
                deviceCredentials,
                index,
                RuntimeModelHash.compute(session.AasxPath));
            return true;
        }
        catch (Exception ex)
        {
            error = $"Activation preflight failed: {ex.Message}";
            Log.Error(error, ex);
            return false;
        }
    }

    private static bool TryBuildPersistedRecoveryPlan(out ActivationPlan? plan, out string error)
    {
        plan = null;
        if (!AgentLastKnownGoodStore.TryLoad(out var snapshot, out error) || snapshot is null)
            return false;
        if (!TryBuildActivationPlan(snapshot.Session, out plan, out error, snapshot.UaSettings)
            || plan is null)
            return false;
        if (!string.Equals(plan.ModelHash, snapshot.ModelHash, StringComparison.OrdinalIgnoreCase))
        {
            plan = null;
            error = "The recovered activation plan does not match the saved AASX integrity hash.";
            return false;
        }
        return true;
    }

    /// <summary>업로드 수신기가 live 파일 교체 전에 동일한 활성화 검증을 재사용한다.</summary>
    internal static bool TryPreflightCandidate(AgentSession session, out string error) =>
        TryBuildActivationPlan(session, out _, out error);

    private Task TryActivateAsync() => TryActivateAsync(forcedPlan: null);

    private async Task TryActivateAsync(ActivationPlan? forcedPlan)
    {
        ActivationPlan? rollbackPlan = null;
        var persistentRecovery = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        var previousPlan = _activePlan;
        try
        {
            ActivationPlan? candidate = forcedPlan;
            if (candidate is null)
            {
                // Candidate를 import/config/index까지 먼저 완성한다. 실패하면 현재 runtime은 건드리지 않는다.
                AgentSession? requestedSession = null;
                var preflightError = "";
                if (File.Exists(SharedPaths.AgentSessionJsonPath)
                    && (!AgentSession.TryLoadExact(
                            SharedPaths.AgentSessionJsonPath, out requestedSession, out preflightError)
                        || requestedSession is null))
                {
                    candidate = null;
                }
                else
                {
                    requestedSession ??= AgentSession.ForCurrentDefaults(requestedBy: "agent");
                    Log.Info($"Preflighting session: aasx='{requestedSession.AasxPath}' plc='{requestedSession.PlcConnectionPath}' " +
                             $"requestedBy={requestedSession.RequestedBy} at={requestedSession.ActivatedAtUtc}");
                    if (TryBuildActivationPlan(requestedSession, out candidate, out preflightError)
                        && candidate is not null)
                        preflightError = "";
                }

                if (candidate is null)
                {
                    if (_app is not null)
                    {
                        Log.Error($"Candidate rejected; current Agent runtime remains active. {preflightError}");
                        return;
                    }

                    Log.Error($"Activation candidate rejected at boot/idle. {preflightError}");
                    if (!TryBuildPersistedRecoveryPlan(out candidate, out var recoveryError) || candidate is null)
                    {
                        Log.Error($"Last-known-good recovery unavailable; Agent remains idle. {recoveryError}");
                        return;
                    }
                    persistentRecovery = true;
                    Log.Warn($"Recovering persisted last-known-good activation: aasx='{candidate.Session.AasxPath}'.");
                }
            }
            else
                Log.Warn("Restoring last-known-good in-memory activation plan after candidate failure.");

            if (_app is not null)
            {
                // 이전엔 silent skip — race 시 새 설정이 누락된 사례 확인됨.
                // 이미 떠 있는 host 는 옛 설정을 들고 있으므로 in-place stop 한 뒤 아래 흐름으로 재시작.
                Log.Info("Activate requested while BackendHost running → stopping in-place for restart.");
                try { BackendHost.stop(_app); }
                catch (Exception ex) { Log.Warn("Exception during BackendHost.stop — ignoring.", ex); }
                _app = null;
            }
            await StopOpcUaAsync().ConfigureAwait(false);
            DisposeEngine();

            var session = candidate.Session;
            var store = candidate.Store;
            var gatewayConfig = candidate.GatewayConfig;
            var aidXgtPlan = candidate.AidXgtPlan;
            var aidSouthboundPlan = candidate.AidSouthboundPlan;
            var uaSettings = candidate.UaSettings;
            var deviceCredentials = candidate.DeviceCredentials;
            var index = candidate.Index;
            Log.Info($"Activating with session: aasx='{session.AasxPath}' plc='{session.PlcConnectionPath}' " +
                     $"requestedBy={session.RequestedBy} at={session.ActivatedAtUtc}");
            Log.Info($"Candidate accepted. Systems={store.Systems.Count} Flows={store.Flows.Count} " +
                     $"connections={gatewayConfig.Connections.Length} " +
                     $"aidXgtSignals={(aidXgtPlan is { Success: true } ? aidXgtPlan.Signals.Length : 0)} " +
                     $"aidStandardSignals={(aidSouthboundPlan is { Success: true } ? aidSouthboundPlan.SignalCount : 0)} " +
                     $"aidEvents={(aidSouthboundPlan is { Success: true } ? aidSouthboundPlan.EventCount : 0)}");

            // 4) engine 생성 — Agent 단일 호스팅. session.RuntimeMode 로 Control(read-write)/Monitoring(read-only) 분기.
            //    호스팅·proxy·발행 경로는 mode 무관 단일 — RuntimeMode 는 engine 생성 파라미터일 뿐이다.
            //    PLC IN → SignalHubBroadcaster → engine.InjectIOValueByAddress → OnRuntime* push (양 모드 공통).
            var isControl = string.Equals(session.RuntimeMode, "Control", StringComparison.OrdinalIgnoreCase);
            var runtimeMode = isControl ? RuntimeMode.Control : RuntimeMode.Monitoring;
            var readOnly = !isControl;
            // 위임 스캔(§10.10 ①): Monitoring 이고 "실제 PLC 연결" 미체크(=위임)면 Agent 는 PLC 에 직접 접속하지 않고
            // 분리된 Pi5 수집기가 WriteTags 로 IN 을 공급한다 → PlcScanService off. Control 은 OUT 을 실 PLC 에 써야
            // 하므로 항상 직접. (구 session.json 은 IsRealPlcConnected 기본 true → 직접 = 올인원 회귀 0.)
            var delegatedScan = !isControl && !session.IsRealPlcConnected;

            // Control 은 OUT 태그를 실제 PLC 로 쓴다. engine 은 BackendHost 시작 전에 생성되므로 DI 가 만드는
            // gateway 를 기다릴 수 없다 → gateway 인스턴스를 여기서 직접 만들어 engine writeTag 콜백과
            // BackendHost DI(아래 configureBuilder) 가 동일 인스턴스를 공유한다. Monitoring 은 writeTag 없음(None).
            IPlcGateway? sharedGateway = isControl ? new PlcGateway(gatewayConfig) : null;
            // Control OUT 은 실 PLC(gateway) + SignalR broadcast 둘 다 — VP(가상 설비)·관찰 client 가 OUT 을 보고
            // 반응(echo)해야 한다. broadcast 핸들은 BackendHost 시작 후 IHubContext 확보 시 채운다(아래).
            Action<string, string>? broadcastOut = null;
            var writeTag = sharedGateway is not null
                ? FSharpOption<FSharpFunc<string, FSharpFunc<string, Unit>>>.Some(
                    FuncConvert.FromAction<string, string>((address, value) =>
                    {
                        _ = sharedGateway.WriteAsync(address, value);
                        broadcastOut?.Invoke(address, value);
                    }))
                : FSharpOption<FSharpFunc<string, FSharpFunc<string, Unit>>>.None;
            var engine = (ISimulationEngine)new EventDrivenEngine(index, runtimeMode, writeTag);

            // Fail closed before Kestrel begins accepting delegated connections.
            WireDeviceCredentialValidator(deviceCredentials);

            // session identity — client 의 stale guard 가 맞춰 보낼 기준값. ModelHash 는 AASX 내용 해시(결정적).
            var identity = new RuntimeSessionIdentity(
                Guid.NewGuid().ToString("N"),
                candidate.ModelHash,
                1,
                isControl ? "Control" : "Monitoring");

            // 5) BackendHost 시작 — configureBuilder 가 bootstrap 의 TryAddSingleton 들보다 먼저 실행 →
            //    IPlcGateway(Control 공유 인스턴스)·IRuntimeHubSession 을 우선 등록. readOnly=false(Control) 면
            //    SignalHub.SetReadOnly(false) 로 client write 허용 + PlcScanService 초기 동기 스캔 활성.
            //    UseWindowsService 는 외부 Generic Host (Program.cs) 담당 — 여기서는 추가 안 함.
            // ActionUnder/ActionOver 게이트 — calibration-state 사이드카의 실측 확정값을 현재 모델 duration 과 대조해 구성.
            // 어댑터(F#)/engine watchdog 이 이 Func 로 미확정 Work 의 판정을 거른다.
            // raw AASX 해시 대신 Work 별 duration 값으로 stale 판정: usertag·이름 등 duration 무관 편집엔 게이트 유지,
            // duration 이 실제로 바뀐 Work 만 재확정 요구(GUID 승계). index.WorkDurationRange = 엔진 판정과 동일 SSOT.
            var calibState = CalibrationState.Load();
            var durById = new Dictionary<Guid, (int Min, int Max)>();
            foreach (var kv in index.WorkDurationRange)
                durById[kv.Key] = (kv.Value.MinMs, kv.Value.MaxMs);
            Func<Guid, bool> isMinMeasured = g => durById.TryGetValue(g, out var r) && calibState.IsMinMeasured(g, r.Min);
            Func<Guid, bool> isMaxMeasured = g => durById.TryGetValue(g, out var r) && calibState.IsMaxMeasured(g, r.Max);
            // 게이트 주입.
            //  - ActionOver(Max): Control(adapter.OnTick)·Monitoring(engine device-watchdog) 양쪽 경로 → engine 에 항상 주입.
            //  - ActionUnder(Min): Control 은 engine adapter, Monitoring 은 아래 HubSession(MonitoringAbnormalAdapter) 이 주입.
            if (engine is EventDrivenEngine edEngine)
            {
                edEngine.SetMaxMeasured(isMaxMeasured);
                if (isControl)
                    edEngine.SetMinMeasured(isMinMeasured);
            }

            // [Gate] ActionOver 게이트 스냅샷 — 활성화 시 1회, Max 있는 device work 중 게이트 닫힌 항목을 값과 함께 덤프.
            // 게이트 닫힘(엔진/어댑터 발행 침묵)의 하위원인(사이드카 미기록 / ms 불일치 / 재활성화 race)을
            // 로그만으로 판별한다(IsMaxMeasured 는 모델 Max 와 사이드카 MaxMs 의 int 완전일치 비교).
            {
                var closedCount = 0;
                foreach (var kv in durById)
                {
                    if (kv.Value.Max <= 0 || isMaxMeasured(kv.Key)) continue;
                    var sysName = index.WorkSystemName.TryFind(kv.Key);
                    if (sysName is null || index.ActiveSystemNames.Contains(sysName.Value)) continue; // device work 만
                    closedCount++;
                    var workName = index.WorkName.TryFind(kv.Key) is { } n ? n.Value : kv.Key.ToString("N")[..8];
                    var sidecar = calibState.Works.TryGetValue(kv.Key.ToString("D"), out var w)
                        ? $"MaxMeasured={w.MaxMeasured} MaxMs={w.MaxMs}"
                        : "absent";
                    Log.Warn($"[Gate] MaxMeasured=false work={workName} modelMax={kv.Value.Max} sidecar={{{sidecar}}}");
                }
                if (closedCount > 0)
                    Log.Warn($"[Gate] ActionOver 게이트 닫힌 device work {closedCount}건 — 위 목록의 Work 는 시간초과여도 자동 ActionOver 미발행");
            }

            AidUaValueBridge? aidUaValueBridge = null;
            AidHttpWebhookRouter? webhookRouter = null;
            if (aidSouthboundPlan is { HasBinding: true, Success: true } webhookPlan)
            {
                webhookRouter = new AidHttpWebhookRouter(webhookPlan);
                _aidWebhookRouter = webhookRouter;
            }
            Log.Info($"Starting BackendHost on port {Port} " +
                     $"({(readOnly ? "read-only / Monitoring" : "read-write / Control")}, " +
                     $"scan={(delegatedScan ? "위임(Pi5) — PlcScanService off" : "직접(Agent)")}) " +
                     $"with engine session {identity.SessionId}...");
            _app = BackendHost.startWithBuilderAndAppConfig(Port, gatewayConfig, readOnly, delegatedScan, builder =>
            {
                if (sharedGateway is not null)
                    builder.Services.AddSingleton<IPlcGateway>(sharedGateway);
                // scanPeriodMs — abnormal 어댑터의 폴링 양자화 마진(±스캔) 산정용. 현재 적용 스캔주기,
                // 미상이면 100. (라이브 스캔주기 변경 시 정밀 동기는 재학습으로 수렴)
                var scanForMargin = _appliedScanIntervalMs > 0 ? _appliedScanIntervalMs : 100;
                builder.Services.AddSingleton<IRuntimeHubSession>(sp =>
                {
                    var runtimeSession = new EventDrivenEngineRuntimeHubSession(
                        engine,
                        sp.GetRequiredService<IHubContext<SignalHub>>(),
                        identity,
                        scanForMargin,
                        isMinMeasured,
                        isMaxMeasured);
                    runtimeSession.SetAddressBatchObserver(items => aidUaValueBridge?.Observe(items));
                    runtimeSession.SetPlcConnectionObserver(status => aidUaValueBridge?.ObserveConnection(status));
                    return runtimeSession;
                });
            }, app => webhookRouter?.Map(app));
            _engine = engine;

            // Agent가 UA 서버를 소유한다. AASX import가 복원한 AID와 현재 DsStore를 주소공간으로 만들고,
            // 같은 engine을 bridge에 붙인다. AASX watcher 재시작 시 이 전체 구성이 원자적으로 교체된다.
            var uaResult = await _opcUaHost.StartAsync(uaSettings, store).ConfigureAwait(false);
            if (!uaResult.Success)
            {
                if (aidXgtPlan is { HasBinding: true } || aidSouthboundPlan is { HasBinding: true })
                    throw new InvalidOperationException($"AID requires the Agent OPC UA server: {uaResult.Message}");
                Log.Warn($"Agent OPC UA server unavailable: {uaResult.Message}");
            }
            else if (_opcUaHost.Server is { IsRunning: true } uaServer)
            {
                _uaBridge = new SimEngineUaBridge(engine, uaServer);
                if (aidXgtPlan is { Success: true } xgt)
                {
                    aidUaValueBridge = new AidUaValueBridge(uaServer, xgt.Signals);
                    var gateway = _app.Services.GetService<IPlcGateway>();
                    if (gateway is not null)
                    {
                        foreach (var status in gateway.GetConnectionStatuses())
                            aidUaValueBridge.ObserveConnection(status);
                    }
                    Log.Info($"AID XGT → OPC UA value bridge active: addresses={aidUaValueBridge.AddressCount}");
                }
                if (aidSouthboundPlan is { HasBinding: true, Success: true } southbound)
                {
                    webhookRouter?.Attach(uaServer);
                    _aidSouthbound = new AidSouthboundRuntime(
                        southbound,
                        uaServer,
                        Path.Combine(SharedPaths.AgentDirectory, "aid-southbound"));
                    await _aidSouthbound.StartAsync().ConfigureAwait(false);
                    Log.Info($"AID standard southbound active: endpoints={_aidSouthbound.EndpointCount}");
                }
                Log.Info($"Agent OPC UA active: endpoint={uaResult.EndpointUrl} assets={uaResult.AssetCount}");
            }
            else
            {
                if (aidXgtPlan is { HasBinding: true } || aidSouthboundPlan is { HasBinding: true })
                    throw new InvalidOperationException("AID bindings require OPC UA, but the Agent OPC UA server is disabled.");
                Log.Info("Agent OPC UA disabled by settings.");
            }

            // Control: engine OUT(writeTag) → 모든 client(OnTagChanged, source="control") broadcast.
            // VP(가상 설비)가 이 OUT 을 받아 가상 IN echo 를 만들고, 그 IN 은 SignalHub.WriteTag→engine forward 로 돌아온다.
            if (sharedGateway is not null)
            {
                var hubCtx = _app.Services.GetRequiredService<IHubContext<SignalHub>>();
                broadcastOut = (address, value) =>
                    hubCtx.Clients.All.SendAsync(HubMethod.OnTagChanged, address, value, HubSource.Control);
            }

            // 6) engine 기동 — Monitoring 은 passive(조건평가 OFF)지만 IO 주입 처리 루프를 위해, Control 은 능동 구동을 위해 Start.
            engine.Start();

            // 7) 스캔 주기 라이브 동기화 배선 — hub SetScanIntervalMs 영속화 훅 + 활성 설정 지문 박제.
            //    이후 ConfigChanged 가 "스캔 주기만 변경" 이면 재시작 없이 ApplyScanIntervalLive 경로.
            SignalHub.PersistScanIntervalMs = PersistScanInterval;
            SignalHub.PersistAutoCalibrate = PersistAutoCalibrate;

            // 분리 아키텍처: 조립한 PlcGatewayConfig 를 수집기(Pi5)에 push(server→client) + 캐시.
            //   올인원(Agent 직접 스캔)은 이 push 를 무시하는 게 정상 — 추가 채널이지 기존 스캔 대체 아님(회귀 0).
            //   Agent 가 서버 자신이라 IHubContext 로 직접 fan-out(heartbeat 올인원 발행부와 동형).
            //   신규 Pi5 가 나중에 붙어도 SignalHub.OnConnectedAsync 가 캐시본을 caller 로 보낸다.
            try
            {
                var collectorPayload = CollectorConfig.fromGateway(gatewayConfig);
                SignalHub.UpdateCollectorConfigCache(collectorPayload);
                var cfgHubCtx = _app.Services.GetRequiredService<IHubContext<SignalHub>>();
                // fire-and-forget — 신규 Pi5 는 OnConnectedAsync 캐시본으로도 받으므로 여기 대기 불필요.
                _ = cfgHubCtx.Clients.All.SendAsync(HubMethod.OnCollectorConfig, collectorPayload);
                Log.Info($"CollectorConfig pushed — connections={collectorPayload.Connections.Length}");
            }
            catch (Exception ex) { Log.Warn("CollectorConfig push failed — Pi5 는 재연결 시 캐시본 수신.", ex); }

            (_appliedConfigFingerprint, _appliedScanIntervalMs) = ComputeConfigFingerprint(session, uaSettings);

            // 저장된 자동정합 상태로 엔진 초기화 — OFF 영속 복원(정지 시 AASX 반영→OFF 결과 유지).
            try
            {
                var plcP = string.IsNullOrWhiteSpace(session.PlcConnectionPath)
                    ? SharedPaths.PlcConnectionFilePath : session.PlcConnectionPath;
                var savedAuto = PlcConnectionSettings.LoadOrDefault(plcP).AutoDurationCalibrate;
                _app.Services.GetRequiredService<IRuntimeHubSession>().SetAutoCalibrate(savedAuto);
                SignalHub.InitAutoCalibrate(savedAuto);   // hub 캐시도 — DSPilot 연결 직후 pull 정합
                Log.Info($"AutoCalibrate restored from settings: {savedAuto}");
            }
            catch (Exception ex) { Log.Warn("AutoCalibrate restore failed — default ON.", ex); }

            _activePlan = candidate;
            if (forcedPlan is null && !persistentRecovery)
            {
                if (AgentLastKnownGoodStore.TrySave(session, uaSettings, candidate.ModelHash, out var snapshotError))
                    Log.Info("Persisted last-known-good Agent activation snapshot.");
                else
                    Log.Error($"Agent is active, but its last-known-good snapshot was not updated. {snapshotError}");
            }
            Log.Info($"Hub active: {BackendHost.getHubUrl(Port)} — mode={runtimeMode} engine status={engine.Status}");
        }
        catch (Exception ex)
        {
            Log.Error(forcedPlan is null
                ? "Candidate activation failed after preflight. Cleaning partial runtime before rollback."
                : "Last-known-good rollback activation failed.", ex);
            if (_app is not null)
            {
                try { BackendHost.stop(_app); }
                catch (Exception stopEx) { Log.Warn("Partial BackendHost cleanup failed.", stopEx); }
                _app = null;
            }
            await StopOpcUaAsync().ConfigureAwait(false);
            DisposeEngine();
            _activePlan = null;
            if (forcedPlan is null && previousPlan is not null)
            {
                rollbackPlan = previousPlan;
            }
            else if (forcedPlan is null && !persistentRecovery)
            {
                if (TryBuildPersistedRecoveryPlan(out var persistedPlan, out var recoveryError)
                    && persistedPlan is not null)
                {
                    rollbackPlan = persistedPlan;
                    Log.Warn("Activation failed after preflight; scheduling persisted last-known-good recovery.");
                }
                else
                {
                    Log.Error($"Post-preflight recovery unavailable; Agent remains idle. {recoveryError}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (rollbackPlan is not null)
            await TryActivateAsync(rollbackPlan).ConfigureAwait(false);
    }

    private async Task DeactivateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            SignalHub.ValidateDeviceCredential = null;
            Log.Info("Stopping Agent runtime (deactivate)...");
            SignalHub.PersistScanIntervalMs = null;
            if (_app is not null)
            {
                try { BackendHost.stop(_app); }
                catch (Exception ex) { Log.Warn("Exception during BackendHost.stop — ignoring.", ex); }
                _app = null;
            }
            await StopOpcUaAsync().ConfigureAwait(false);
            DisposeEngine();
            _activePlan = null;
            Log.Info("Agent runtime stopped → idle.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// engine 정리 — host stop 이후 호출. Stop → Dispose 순서로 thread/wakeSignal 해제.
    private void DisposeEngine()
    {
        if (_engine is null) return;
        try { _engine.Stop(); } catch (Exception ex) { Log.Warn("engine.Stop threw — ignoring.", ex); }
        try { _engine.Dispose(); } catch (Exception ex) { Log.Warn("engine.Dispose threw — ignoring.", ex); }
        _engine = null;
    }

    private async Task StopOpcUaAsync()
    {
        var webhookRouter = _aidWebhookRouter;
        _aidWebhookRouter = null;
        if (webhookRouter is not null)
        {
            try { await webhookRouter.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("AidHttpWebhookRouter.DisposeAsync threw — ignoring.", ex); }
        }

        var southbound = _aidSouthbound;
        _aidSouthbound = null;
        if (southbound is not null)
        {
            try { await southbound.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("AidSouthboundRuntime.DisposeAsync threw — ignoring.", ex); }
        }

        var bridge = _uaBridge;
        _uaBridge = null;
        try { bridge?.Dispose(); }
        catch (Exception ex) { Log.Warn("SimEngineUaBridge.Dispose threw — ignoring.", ex); }

        try { await _opcUaHost.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("OpcUaServerHost.StopAsync threw — ignoring.", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        _debounce?.Dispose();
        try { _flagWatcher?.Dispose(); } catch { }
        try { _sessionWatcher?.Dispose(); } catch { }
        try { _plcWatcher?.Dispose(); } catch { }
        try { _aasxWatcher?.Dispose(); } catch { }
        try { _opcUaSettingsWatcher?.Dispose(); } catch { }
        await DeactivateAsync().ConfigureAwait(false);
        await _opcUaHost.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    [Flags]
    private enum DebounceReason
    {
        None          = 0,
        FlagChanged   = 1,
        ConfigChanged = 2,
    }
}
