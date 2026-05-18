using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>현재 살아있는 client (또는 null). 외부 caller 는 <see cref="EnsureCreated"/> 사용 권장.</summary>
    public static LightHouseClient? Current
    {
        get { lock (_lock) return _instance; }
    }

    /// <summary>
    /// LlmConfig 의 LightHouseService 가 설정되어 있으면 client 보장. BaseUrl/PSK 변경 감지 시 재생성.
    /// 미설정 시 null 반환 (caller 가 "LightHouse 비활성" 분기).
    /// </summary>
    public static LightHouseClient? EnsureCreated(LlmConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        var url = config.LightHouseService?.BaseUrl ?? "";
        var psk = config.GetLightHousePsk() ?? "";
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
                    () => config.GetLightHousePsk(),
                    Environment.UserName,
                    () => config.KbCollections.Where(k => k.Active).Select(k => k.CollectionId).ToList());
                _lastBaseUrl = url;
                _lastPskHash = pskHash;
                Log.Info($"LightHouseClientHolder created — {url}");
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
        try { _instance.Dispose(); }
        catch (Exception ex) { Log.Warn($"LightHouseClient.Dispose 실패: {ex.Message}"); }
        _instance = null;
    }

    private static string ComputeHash(string plain)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(plain));
        return Convert.ToHexString(bytes);
    }
}
