using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Promaker.LlmAgent;

namespace Promaker.Knowledge;

/// <summary>
/// **D-S7-3b (s6-r30) — multi-instance LightHouseClient host** (todo-lighthouse-kb-server.md §3.16.7).
/// <para/>
/// 단일 process 가 N 개 active LightHouse service endpoint 에 동시 접속. 각 ServiceId 별로 별 <see cref="LightHouseClient"/>
/// + 별 SSE subscription + 별 session token 셋. 이전 (D-S7-3a 까지) 의 단일 `_instance` static field 는
/// <see cref="Current"/> backward-compat 만 유지 ([Obsolete]).
///
/// <para/>
/// **lifetime**: process. App.OnStartup 이후 어디서든 <see cref="EnsureCreated"/> 호출 → 모든 active service 의
/// client 가 보장됨. <see cref="ApplicationSettingsDialog"/> 가 service config 변경 시 <see cref="Invalidate"/>
/// 호출 → 모든 entry dispose, 다음 EnsureCreated 에서 재생성.
///
/// <para/>
/// **session 추적 (per-service)**: <see cref="LlmChatViewModel"/> 가 InitializeAsync 에서 각 active service 별로
/// session 발급 시 <see cref="RegisterSession(string, string)"/>. 명시 해제 시 <see cref="UnregisterSession(string, string)"/>.
/// App.OnExit 가 <see cref="DisposeAllAsync"/> 호출 → 모든 service 의 모든 token best-effort DELETE.
///
/// <para/>
/// **thread safety**: dictionary 자체는 ConcurrentDictionary. mutation (entry add/remove + 변경 감지 후 dispose/recreate)
/// 은 `object _mutationLock` 으로 직렬화 — 사용자가 Settings dialog 의 service 추가를 빠르게 반복하는 race 차단.
///
/// <para/>
/// **SSE event 의 ServiceId tagging (D-S7-3b)**: 각 service 별 SSE callback 안에서 evt.ServiceId 를 stream owner
/// 의 ServiceId 로 박제 후 <see cref="EventReceived"/> publish. caller (KbManagerDialog) 가 어느 service tab 을 갱신할지 결정.
/// </summary>
public static class LightHouseClientHolder
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LightHouseClientHolder));
    private static readonly object _mutationLock = new();
    private static readonly ConcurrentDictionary<string, ServiceClientEntry> _clients = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _liveSessions = new();

    /// <summary>
    /// SSE event aggregator (D-S7-2b s6-r28 + D-S7-3b s6-r30 ServiceId tagging). caller (KbManagerDialog) 가
    /// subscribe → 모든 active service 의 server event 가 본 event 로 fan-out. evt.ServiceId 로 source 식별.
    /// handler 안의 UI thread marshalling 은 caller 책임 (`Dispatcher.Invoke`).
    /// </summary>
    public static event Action<ServerEventDto>? EventReceived;

    /// <summary>
    /// **D-S7-3b (s6-r30) backward-compat** — 단일 시절의 단일 client 단축 접근자. 첫 active service 의 client
    /// 반환 (또는 null). multi-service path 진입 후 caller 는 <see cref="GetClient"/> + serviceId 명시 사용 권장.
    /// </summary>
    [Obsolete("D-S7-3b 이후 GetClient(serviceId) 또는 EnsureCreated(config) 의 IReadOnlyList<LightHouseClient> 사용. 본 property 는 backward-compat 만 유지.")]
    public static LightHouseClient? Current
    {
        get
        {
            var entry = _clients.Values.FirstOrDefault();
            return entry?.Client;
        }
    }

    /// <summary>
    /// **D-S7-3b (s6-r30)** — 지정 ServiceId 의 client 반환. 미생성 / 비활성 시 null.
    /// caller 가 EnsureCreated 후 직접 조회. multi-service N 개 client 중 1개 선택.
    /// </summary>
    public static LightHouseClient? GetClient(string serviceId)
    {
        if (string.IsNullOrEmpty(serviceId)) return null;
        return _clients.TryGetValue(serviceId, out var entry) ? entry.Client : null;
    }

    /// <summary>
    /// **D-S7-3b (s6-r30)** — config 의 모든 active service 에 대해 client 보장. 변경된 entry (BaseUrl/PSK 변동)
    /// 만 dispose 후 재생성 — 변경 없는 entry 는 그대로 유지 (효율 + SSE 단절 회피). config 에서 사라진 ServiceId
    /// 는 entry dispose + 제거.
    /// <para/>
    /// 반환 = 본 호출 후 살아있는 active service client 목록 (순서 = config.LightHouseServices 의 순서, active=false 제외).
    /// </summary>
    public static IReadOnlyList<LightHouseClient> EnsureCreated(LlmConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        lock (_mutationLock)
        {
            var activeServices = config.LightHouseServices.Where(s => s.Active).ToList();
            var activeIds = new HashSet<string>(activeServices.Select(s => s.ServiceId));

            // config 에서 사라진 (또는 inactive 로 바뀐) entry 정리.
            foreach (var staleId in _clients.Keys.Where(id => !activeIds.Contains(id)).ToList())
            {
                if (_clients.TryRemove(staleId, out var staleEntry))
                {
                    DisposeEntry(staleEntry);
                }
            }

            var result = new List<LightHouseClient>(activeServices.Count);
            foreach (var svc in activeServices)
            {
                if (string.IsNullOrEmpty(svc.BaseUrl)) continue;
                var psk = config.GetLightHousePsk(svc.ServiceId);
                if (string.IsNullOrEmpty(psk)) continue;

                var pskHash = ComputeHash(psk);

                // 기존 entry 가 있고 BaseUrl/PSK 동일 = 재사용.
                if (_clients.TryGetValue(svc.ServiceId, out var existing)
                    && existing.BaseUrl == svc.BaseUrl
                    && existing.PskHash == pskHash)
                {
                    result.Add(existing.Client);
                    continue;
                }

                // 변경 entry — 기존 dispose 후 재생성.
                if (existing is not null)
                {
                    _clients.TryRemove(svc.ServiceId, out _);
                    DisposeEntry(existing);
                }

                var capturedServiceId = svc.ServiceId;
                LightHouseClient client;
                try
                {
                    client = new LightHouseClient(
                        svc.BaseUrl,
                        () => config.GetLightHousePsk(capturedServiceId),
                        Environment.UserName,
                        () => config.KbCollections
                            .Where(k => k.Active && k.ServiceId == capturedServiceId)
                            .Select(k => k.CollectionId)
                            .ToList());
                }
                catch (ArgumentException ex)
                {
                    Log.Warn($"LightHouseClient 생성 실패 (serviceId={svc.ServiceId}) — {ex.Message}");
                    continue;
                }

                var entry = new ServiceClientEntry(svc.ServiceId, svc.BaseUrl, pskHash, client);
                StartSseLoopLocked(entry);
                _clients[svc.ServiceId] = entry;
                Log.Info($"LightHouseClientHolder created — {svc.BaseUrl} (serviceId={svc.ServiceId})");
                result.Add(client);
            }
            return result;
        }
    }

    /// <summary>
    /// Settings dialog 의 service config 갱신 후 호출. 모든 entry dispose + 다음 EnsureCreated 에서 재생성.
    /// 진행 중 session token 은 추적 dict 에서 자연 stale — server idle TTL 만료 후 사라짐 (L2-3).
    /// </summary>
    public static void Invalidate()
    {
        lock (_mutationLock)
        {
            foreach (var entry in _clients.Values.ToList())
            {
                DisposeEntry(entry);
            }
            _clients.Clear();
        }
    }

    /// <summary>
    /// **D-S7-3b (s6-r30)** — service 별 session 추적. caller (LlmChatViewModel) 가 CreateSessionAsync 응답
    /// 직후 호출. 동일 (serviceId, token) 멱등 — 중복 RegisterSession 무영향.
    /// </summary>
    public static void RegisterSession(string serviceId, string token)
    {
        if (string.IsNullOrEmpty(serviceId) || string.IsNullOrEmpty(token)) return;
        var bag = _liveSessions.GetOrAdd(serviceId, _ => new ConcurrentDictionary<string, byte>());
        bag[token] = 0;
    }

    /// <summary>**D-S7-3b (s6-r30)** — 명시 session 해제 추적에서 제거. 실제 DELETE 는 caller 책임.</summary>
    public static void UnregisterSession(string serviceId, string token)
    {
        if (string.IsNullOrEmpty(serviceId) || string.IsNullOrEmpty(token)) return;
        if (_liveSessions.TryGetValue(serviceId, out var bag)) bag.TryRemove(token, out _);
    }

    /// <summary>**D-S7-3b (s6-r30)** — service 별 live session snapshot (App.OnExit 등 진단 / 일괄 DELETE 용).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> LiveSessions =>
        _liveSessions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyCollection<string>)kv.Value.Keys.ToArray());

    /// <summary>
    /// App.OnExit 진입점 — 모든 service 의 모든 token 일괄 DELETE (L2-2, §3.8). Best-effort: 실패해도 server-side
    /// idle TTL 이 backstop. 호출 후 모든 entry dispose.
    /// <para/>
    /// **self-driven invariant (s6-r30 자가 검열 M-1)**: 본 메서드는 **App.OnExit 단일 caller** 만 호출. 진입 후
    /// 다른 thread 가 <see cref="EnsureCreated"/> 호출 시 race window 가능 — snapshot 시점 ~ DELETE await ~ final
    /// lock 사이에서 같은 ServiceId 의 client 가 외부에 노출 후 dispose 됨. App.OnExit 가 process 종료 직전이라
    /// 현실 risk 미미하지만 별 caller 추가 시 invariant 위반 주의.
    /// </summary>
    public static async Task DisposeAllAsync()
    {
        // snapshot — lock 밖에서 await 안전.
        List<(ServiceClientEntry entry, string[] tokens)> snapshot;
        lock (_mutationLock)
        {
            snapshot = _clients.Values
                .Select(e =>
                {
                    var tokens = _liveSessions.TryGetValue(e.ServiceId, out var bag)
                        ? bag.Keys.ToArray()
                        : Array.Empty<string>();
                    return (e, tokens);
                })
                .ToList();
        }

        foreach (var (entry, tokens) in snapshot)
        {
            foreach (var t in tokens)
            {
                try { await entry.Client.DeleteSessionAsync(t).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Warn($"DisposeAllAsync — service {entry.ServiceId} session {t} DELETE 실패 (best-effort): {ex.Message}");
                }
            }
        }

        lock (_mutationLock)
        {
            foreach (var entry in _clients.Values.ToList()) DisposeEntry(entry);
            _clients.Clear();
            _liveSessions.Clear();
        }
    }

    private static void DisposeEntry(ServiceClientEntry entry)
    {
        StopSseLoop(entry);
        try { entry.Client.Dispose(); }
        catch (Exception ex) { Log.Warn($"LightHouseClient.Dispose 실패 (serviceId={entry.ServiceId}): {ex.Message}"); }
    }

    /// <summary>
    /// **D-S7-3b (s6-r30) → D-S7-2c (s6-r32)** — entry 별 SSE subscription Task 시작. 호출은 _mutationLock 안에서.
    /// SSE callback 안에서 evt.ServiceId = entry.ServiceId 박제 후 publish.
    /// <para/>
    /// **reconnect 정책 (s6-r32)**: <see cref="SseReconnectBackoff"/> SSOT — exponential 1→2→4→8→16→30 cap +
    /// 60s stable reset + log throttle (≤3 또는 매 5회). 이전 (s6-r28~s6-r31) fixed 5s 정책은 폐기.
    /// </summary>
    private static void StartSseLoopLocked(ServiceClientEntry entry)
    {
        var cts = new CancellationTokenSource();
        entry.SseCts = cts;
        var ct = cts.Token;
        var client = entry.Client;
        var ownerServiceId = entry.ServiceId;
        var backoff = new SseReconnectBackoff();
        entry.SseTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await client.OpenEventsStreamAsync(evt =>
                    {
                        evt.ServiceId = ownerServiceId;  // D-S7-3b — client-side tagging
                        var handler = EventReceived;
                        if (handler is not null)
                        {
                            try { handler(evt); }
                            catch (Exception ex) { Log.Warn($"EventReceived handler 결함 (serviceId={ownerServiceId}, skip): {ex.Message}"); }
                        }
                        return Task.CompletedTask;
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (
                    ex is System.Net.Http.HttpRequestException
                    || ex is System.IO.IOException
                    || ex is TaskCanceledException
                    || ex is LightHouseAuthException
                    || ex is LightHouseProtocolException)
                {
                    var (delay, shouldLog) = backoff.NextDelay();
                    if (shouldLog)
                    {
                        Log.Warn($"SSE stream 끊김 (serviceId={ownerServiceId}, attempt={backoff.Attempt}) — "
                            + $"{ex.GetType().Name}: {ex.Message}. {delay.TotalSeconds:0.#}초 후 재연결.");
                    }
                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }, ct);
    }

    /// <summary>
    /// SSE loop 종료 + cleanup. <para/>
    /// **timeout 정책 (s6-r32, s6-r30 review Minor-1)**: Wait 3s — 이전 1s 는 SSE catch 안의 Task.Delay (cap 30s)
    /// 와 cancellation token signal 처리 사이 race 에 짧음. Cancel() 직후 Task.Delay 의 OCE catch 진입 + return 까지
    /// 의 여유 박제. 3s 미초과 시 caller (Invalidate / EnsureCreated) 가 다음 step 진입.
    /// </summary>
    private static void StopSseLoop(ServiceClientEntry entry)
    {
        if (entry.SseCts is null) return;
        try { entry.SseCts.Cancel(); } catch (ObjectDisposedException) { }
        try { entry.SseTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        try { entry.SseCts.Dispose(); } catch (ObjectDisposedException) { }
        entry.SseCts = null;
        entry.SseTask = null;
    }

    private static string ComputeHash(string plain)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(plain));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// **D-S7-3b (s6-r30)** — per-service client + lifecycle bundle. _clients dict 의 value.
    /// SseCts/SseTask 는 entry 별 lifetime — Dispose 시 함께 cancel.
    /// </summary>
    private sealed class ServiceClientEntry
    {
        public string ServiceId { get; }
        public string BaseUrl { get; }
        public string PskHash { get; }
        public LightHouseClient Client { get; }
        public CancellationTokenSource? SseCts { get; set; }
        public Task? SseTask { get; set; }

        public ServiceClientEntry(string serviceId, string baseUrl, string pskHash, LightHouseClient client)
        {
            ServiceId = serviceId;
            BaseUrl = baseUrl;
            PskHash = pskHash;
            Client = client;
        }
    }
}
