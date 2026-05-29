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
    // _pendingReason / _inflight 보호용 monitor lock — SemaphoreSlim 객체에 lock 거는 antipattern 회피.
    private readonly object _pendingLock = new();
    private WebApplication? _app;
    private FileSystemWatcher? _flagWatcher;
    private FileSystemWatcher? _sessionWatcher;
    private FileSystemWatcher? _plcWatcher;
    private FileSystemWatcher? _aasxWatcher;
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
        Log.Info("Config changed while active → restart with new settings.");
        // 이전 구현: Deactivate → TryActivate 를 두 번의 gate scope 로 나눠 호출.
        // gate 사이 race 로 다른 task 가 BackendHost 를 다시 띄우면 TryActivate 의 skip 분기가
        // silent drop 됐던 사례 (2026-05-28 17:12). TryActivate 한 번 호출로 통합 — 내부에서
        // _app != null 이면 stop 한 뒤 새 설정으로 재시작 (한 gate scope 안 atomic).
        await TryActivateAsync().ConfigureAwait(false);
    }

    private async Task TryActivateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is not null)
            {
                // 이전엔 silent skip — race 시 새 설정이 누락된 사례 확인됨.
                // 이미 떠 있는 host 는 옛 설정을 들고 있으므로 in-place stop 한 뒤 아래 흐름으로 재시작.
                Log.Info("Activate requested while BackendHost running → stopping in-place for restart.");
                try { BackendHost.stop(_app); }
                catch (Exception ex) { Log.Warn("Exception during BackendHost.stop — ignoring.", ex); }
                _app = null;
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
