using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ds2.Aasx;
using Ds2.Backend;
using Ds2.Backend.Plc;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.Runtime.IO;
using log4net;
using Microsoft.AspNetCore.Builder;
using Promaker.Shared;

namespace Promaker.Agent;

/// <summary>
/// Agent 의 idle/active 상태머신.
/// - active.flag 생성 → Activate (BackendHost 시작)
/// - active.flag 삭제 → Deactivate (BackendHost 정지)
/// - session.json / PlcConnection.json 변경 → 활성 중이면 Restart (새 설정 적용)
/// (project.aasx 자동 재구독은 Phase 5 에서 추가)
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
    private WebApplication? _app;
    private FileSystemWatcher? _flagWatcher;
    private FileSystemWatcher? _sessionWatcher;
    private FileSystemWatcher? _plcWatcher;
    private FileSystemWatcher? _aasxWatcher;
    private Timer? _debounce;
    private DebounceReason _pendingReason;

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
        Log.Info("FileSystemWatchers started: flag, session, plc, aasx.");
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
        return w;
    }

    private void ScheduleDebounce(DebounceReason reason, string changeType, string fullPath)
    {
        // 여러 워처에서 동시에 들어온 이벤트는 [Flags] bitmask 로 OR 누적 — 한 debounce window 안에
        // FlagChanged 와 ConfigChanged 가 모두 발화하면 둘 다 처리해야 한다.
        // (예: Promaker WPF 가 "Agent 보내기" 시 PlcConnection.json 저장 + active.flag 재기록 →
        //  두 이벤트가 거의 동시에 들어옴. 덮어쓰면 새 PLC 설정이 적용 안 됨.)
        lock (_gate)
        {
            _pendingReason |= reason;
        }
        Log.Debug($"Watcher event: {changeType} on {fullPath} → debounce({reason}).");
        _debounce?.Dispose();
        _debounce = new Timer(_ => _ = OnDebounceFiredAsync(), null, DebounceMs, Timeout.Infinite);
    }

    private async Task OnDebounceFiredAsync()
    {
        DebounceReason reason;
        lock (_gate)
        {
            reason = _pendingReason;
            _pendingReason = DebounceReason.None;
        }
        if (reason == DebounceReason.None) return;

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
        Log.Info("Config changed while active → restart with new settings.");
        await DeactivateAsync().ConfigureAwait(false);
        await TryActivateAsync().ConfigureAwait(false);
    }

    private async Task TryActivateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is not null)
            {
                Log.Warn("Activate requested but BackendHost already running — skipping.");
                return;
            }

            // 1) session.json 로드 (없으면 기본 경로로 대체 — 첫 부팅 시나리오).
            var session = AgentSession.TryLoad() ?? AgentSession.ForCurrentDefaults(requestedBy: "agent");
            Log.Info($"Activating with session: aasx='{session.AasxPath}' plc='{session.PlcConnectionPath}' " +
                     $"requestedBy={session.RequestedBy} at={session.ActivatedAtUtc}");

            // 2) AASX 로드.
            if (string.IsNullOrWhiteSpace(session.AasxPath) || !File.Exists(session.AasxPath))
            {
                Log.Warn($"AASX not found at '{session.AasxPath}'. Cannot activate — remaining idle.");
                return;
            }
            var store = new DsStore();
            var importResult = AasxImporter.importIntoStoreWithError(store, session.AasxPath);
            if (importResult.IsError)
            {
                Log.Error($"AASX load failed: {importResult.ErrorValue}");
                return;
            }
            Log.Info($"AASX loaded. Systems={store.Systems.Count} Flows={store.Flows.Count}");

            // 3) IOMap + PlcGatewayConfig 빌드.
            var ioMap = SignalIOMapModule.build(store);
            Log.Info($"IOMap built. OUT={ioMap.OutAddressToMappings.Count} IN={ioMap.InAddressToMappings.Count}");

            // UserTag 전용 주소 — IOMap (Call In/Out) 에 안 들어가지만 모니터링/알림 대상이므로 PLC 구독에 포함.
            // 빠지면 DSPilot 의 UserTag 알림이 plcTagLog 행 부재로 fire 안 됨 (관찰된 증상).
            var userTagAddresses = store.GetAllUserTagsForProject()
                .Select(r => r.TagAddress)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();
            Log.Info($"UserTag addresses to subscribe: {userTagAddresses.Count} ({string.Join(", ", userTagAddresses.Take(5))})");

            var plcSettings = PlcConnectionSettings.LoadOrDefault(
                string.IsNullOrWhiteSpace(session.PlcConnectionPath)
                    ? SharedPaths.PlcConnectionFilePath
                    : session.PlcConnectionPath);
            var gatewayConfig = PlcGatewayConfigBuilder.TryBuild(plcSettings, ioMap, out var errors, userTagAddresses);
            if (gatewayConfig is null)
            {
                Log.Error($"Gateway config build failed: {string.Join(" / ", errors)}");
                return;
            }

            // 4) BackendHost 시작.
            // UseWindowsService 는 외부 Generic Host (Program.cs) 가 담당 — 여기서는 추가 안 함.
            // BackendHost 의 WebApplication 은 ASP.NET Core Kestrel 만 호스팅하고 SCM 신호와 무관.
            Log.Info($"Starting BackendHost on port {Port} (read-only / Monitoring)...");
            _app = BackendHost.startWithPlcConfigReadOnly(Port, gatewayConfig);
            Log.Info($"Hub active: {BackendHost.getHubUrl(Port)}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DeactivateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is null) return;
            Log.Info("Stopping BackendHost (deactivate)...");
            try { BackendHost.stop(_app); }
            catch (Exception ex) { Log.Warn("Exception during BackendHost.stop — ignoring.", ex); }
            _app = null;
            Log.Info("BackendHost stopped → idle.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _debounce?.Dispose();
        try { _flagWatcher?.Dispose(); } catch { }
        try { _sessionWatcher?.Dispose(); } catch { }
        try { _plcWatcher?.Dispose(); } catch { }
        try { _aasxWatcher?.Dispose(); } catch { }
        await DeactivateAsync().ConfigureAwait(false);
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
