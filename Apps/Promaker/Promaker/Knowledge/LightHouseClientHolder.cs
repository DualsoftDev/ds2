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
/// Process 단일 <see cref="LightHouseClient"/> + 진행 중 session token 추적기
/// (todo-lighthouse-kb-server.md §3.8 L2-2 process exit hook / s5b 잔여 우려 1 통일 결정).
///
/// **lifetime**: process. App.OnStartup 이후 어디서든 <see cref="EnsureCreated"/> / <see cref="Current"/> 접근.
/// <see cref="ApplicationSettingsDialog"/> 가 BaseUrl/PSK 변경 시 <see cref="Invalidate"/> 호출 → 다음 접근에 재생성.
///
/// **session 추적**: <see cref="LlmChatViewModel"/> 가 InitializeAsync 에서 session 발급 시 <see cref="RegisterSession"/>,
/// 명시 해제 시 <see cref="UnregisterSession"/>. App.OnExit 가 <see cref="DisposeAllAsync"/> 호출 →
/// 살아있는 모든 token 을 best-effort DELETE 후 client Dispose (L2-2 §3.8).
///
/// **thread safety**: holder 자체 mutation 은 `object lock`. session set 은 ConcurrentBag (add/remove 빈번 + 정확한
/// 중복 검사 불요 — 같은 token 두번 들어가도 DELETE 멱등).
/// </summary>
public static class LightHouseClientHolder
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LightHouseClientHolder));
    private static readonly object _lock = new();
    private static LightHouseClient? _instance;
    private static string _lastBaseUrl = "";
    private static string _lastPskHash = "";  // DPAPI ciphertext 의 해시는 의미 없음 → 평문 PSK 의 해시 비교
    private static readonly ConcurrentDictionary<string, byte> _liveSessions = new();

    // D-S7-2b (s6-r28) — SSE subscription lifecycle. EnsureCreated 가 새 client 생성 시 _sseTask 시작 +
    // _sseCts 보유. Invalidate / DisposeAllAsync 가 cancel. 단절 시 5초 backoff + 자동 재연결.
    private static CancellationTokenSource? _sseCts;
    private static Task? _sseTask;

    /// <summary>
    /// SSE event aggregator (D-S7-2b s6-r28). caller (예: KbManagerDialog) 가 subscribe → 모든 active client
    /// 의 server event 가 본 이벤트로 fan-out. handler 안의 UI thread marshalling 은 caller 책임 (`Dispatcher.Invoke`).
    /// <para/>
    /// **lifetime**: process-wide. handler 가 unsubscribe 안 하면 dead reference 유지 → caller 의 panel
    /// close 시점에 명시 unsubscribe 의무.
    /// </summary>
    public static event Action<ServerEventDto>? EventReceived;

    /// <summary>현재 살아있는 client (또는 null). 외부 caller 는 <see cref="EnsureCreated"/> 사용 권장.</summary>
    public static LightHouseClient? Current
    {
        get { lock (_lock) return _instance; }
    }

    /// <summary>
    /// LlmConfig 의 active LightHouse service (D-S7-3a: <c>LightHouseServices.FirstOrDefault(s => s.Active)</c>)
    /// 가 설정되어 있으면 client 보장. BaseUrl/PSK 변경 감지 시 재생성. 미설정 시 null 반환.
    /// <para/>
    /// **D-S7-3a (s6-r29) 임시 path** — 단일 client 만 유지 (active service 1개 한정 가정). D-S7-3b 에서 multi-instance
    /// dictionary 로 재설계 예정. active service 가 *여러* 개인 경우 본 path 는 **첫 entry 만** 사용 — 다른 active
    /// service 는 무시됨 (UI 가 active=true 1개 보장 의무).
    /// </summary>
    public static LightHouseClient? EnsureCreated(LlmConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        var activeService = config.LightHouseServices.FirstOrDefault(s => s.Active);
        var url = activeService?.BaseUrl ?? "";
        var serviceId = activeService?.ServiceId ?? "";
        var psk = string.IsNullOrEmpty(serviceId) ? "" : (config.GetLightHousePsk(serviceId) ?? "");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(psk)) return null;

        var pskHash = ComputeHash(psk);

        lock (_lock)
        {
            if (_instance is not null && _lastBaseUrl == url && _lastPskHash == pskHash) return _instance;

            DisposeInstanceLocked();
            try
            {
                _instance = new LightHouseClient(
                    url,
                    () => config.GetLightHousePsk(serviceId),
                    Environment.UserName,
                    () => config.KbCollections.Where(k => k.Active).Select(k => k.CollectionId).ToList());
                _lastBaseUrl = url;
                _lastPskHash = pskHash;
                Log.Info($"LightHouseClientHolder created — {url} (serviceId={serviceId})");
                StartSseLoopLocked(_instance);  // D-S7-2b (s6-r28)
                return _instance;
            }
            catch (ArgumentException ex)
            {
                Log.Warn($"LightHouseClient 생성 실패 — {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Settings dialog 의 LightHouseService 갱신 후 호출. 현 instance dispose + 다음 EnsureCreated 에서 재생성.
    /// 진행 중 session token 은 invalidate 후 서버에서 자연 idle TTL 만료 → 새 client 가 받으면 L3 회복 (재발급).
    /// </summary>
    public static void Invalidate()
    {
        lock (_lock)
        {
            DisposeInstanceLocked();
            _lastBaseUrl = "";
            _lastPskHash = "";
        }
    }

    /// <summary>새 session 발급 시 caller (LlmChatViewModel) 가 호출. token = `CreateSessionAsync` 응답의 token.</summary>
    public static void RegisterSession(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        _liveSessions[token] = 0;
    }

    /// <summary>명시 해제 (panel close / Dispose) — token 추적에서 제거. 실제 DELETE 는 caller 책임.</summary>
    public static void UnregisterSession(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        _liveSessions.TryRemove(token, out _);
    }

    /// <summary>현재 추적 중인 token 의 스냅샷 (App.OnExit 등 진단 / 일괄 DELETE).</summary>
    public static IReadOnlyCollection<string> LiveSessions => _liveSessions.Keys.ToArray();

    /// <summary>
    /// App.OnExit 진입점 — 살아있는 token 일괄 DELETE (L2-2, §3.8). Best-effort: 실패해도 server-side idle TTL 이 backstop.
    /// 호출 후 client Dispose.
    /// </summary>
    public static async Task DisposeAllAsync()
    {
        LightHouseClient? client;
        string[] tokens;
        lock (_lock)
        {
            client = _instance;
            tokens = _liveSessions.Keys.ToArray();
        }
        if (client is null)
        {
            _liveSessions.Clear();
            return;
        }

        foreach (var t in tokens)
        {
            try { await client.DeleteSessionAsync(t).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"DisposeAllAsync — session {t} DELETE 실패 (best-effort): {ex.Message}"); }
        }
        _liveSessions.Clear();

        lock (_lock) { DisposeInstanceLocked(); }
    }

    private static void DisposeInstanceLocked()
    {
        if (_instance is null) return;
        StopSseLoopLocked();  // D-S7-2b — instance dispose 전 SSE cancel.
        try { _instance.Dispose(); }
        catch (Exception ex) { Log.Warn($"LightHouseClient.Dispose 실패: {ex.Message}"); }
        _instance = null;
    }

    /// <summary>
    /// D-S7-2b (s6-r28) — SSE subscription Task 시작. lock 안에서 호출 (`EnsureCreated`).
    /// 단절 (server restart / network blip) 시 5초 backoff + 자동 재연결 무한 loop. CancellationToken 으로 종료.
    /// </summary>
    private static void StartSseLoopLocked(LightHouseClient client)
    {
        StopSseLoopLocked();  // 안전: 이전 task 잔존 시 cleanup
        _sseCts = new CancellationTokenSource();
        var ct = _sseCts.Token;
        _sseTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await client.OpenEventsStreamAsync(evt =>
                    {
                        var handler = EventReceived;
                        if (handler is not null)
                        {
                            try { handler(evt); }
                            catch (Exception ex) { Log.Warn($"EventReceived handler 결함 (skip): {ex.Message}"); }
                        }
                        return Task.CompletedTask;
                    }, ct).ConfigureAwait(false);
                    // OpenEventsStreamAsync 가 정상 반환 = server-side 정상 close. 다음 loop 가 즉시 재연결.
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (
                    ex is System.Net.Http.HttpRequestException
                    || ex is System.IO.IOException
                    || ex is TaskCanceledException
                    || ex is LightHouseAuthException
                    || ex is LightHouseProtocolException)
                {
                    // M2 (s6-r28 자가 검열 review) — network / protocol 결함만 swallow + reconnect.
                    // OutOfMemoryException / StackOverflowException 등 process-fatal 은 propagate.
                    Log.Warn($"SSE stream 끊김 — {ex.GetType().Name}: {ex.Message}. 5초 후 재연결 시도.");
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }, ct);
    }

    /// <summary>SSE Task cancel + 종료 대기 (best-effort 1s). lock 안에서 호출.</summary>
    private static void StopSseLoopLocked()
    {
        if (_sseCts is null) return;
        try { _sseCts.Cancel(); } catch (ObjectDisposedException) { }
        // task 가 cancellation 까지 완전히 정리되는 데 최대 1초 대기. lock 안에서 호출이므로 짧게.
        try { _sseTask?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        try { _sseCts.Dispose(); } catch (ObjectDisposedException) { }
        _sseCts = null;
        _sseTask = null;
    }

    private static string ComputeHash(string plain)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(plain));
        return Convert.ToHexString(bytes);
    }
}
