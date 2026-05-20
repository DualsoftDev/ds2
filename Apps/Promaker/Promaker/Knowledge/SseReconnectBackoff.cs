using System;

namespace Promaker.Knowledge;

/// <summary>
/// **Phase S7 D-S7-2c (s6-r32)** — SSE reconnect backoff 정책 SSOT.
/// <para/>
/// 이전 (s6-r28 ~ s6-r31) 의 fixed 5s 가 단점:
/// (a) flapping server (예: rolling restart) 와의 적합 X — 매 5s thundering herd
/// (b) network glitch 한 번 후 즉시 회복해도 5s 무조건 wait — UX 손실
/// <para/>
/// 정책: **exponential 1 → 2 → 4 → 8 → 16 → 30 (cap), 60s stable 후 reset + ±20% jitter (R8-M2)**.
/// <list type="bullet">
///   <item>delay = <c>1s * 2^min(attempt-1, 5)</c>, cap 30s</item>
///   <item>stable threshold 60s: 마지막 failure 로부터 60s 가 경과한 시점에 다음 failure 가 발생하면 attempt reset
///   (server restart 한 번 / 일시적 net glitch 와 chronic outage 구분)</item>
///   <item>log throttle: attempt ≤ 3 또는 5 의 배수만 Warn — 무한 outage 시 log spam 방지</item>
///   <item><b>R8-M2 (s6-r76)</b> — production constructor 는 base delay 에 [-20%, +20%) random jitter
///   적용 (thundering herd 회피). 다중 Promaker instance 동시 server-side outage 후 동기 reconnect race 차단.
///   test 친화 internal ctor (`Func&lt;DateTime&gt;` 단일 인자) 는 jitter=0 deterministic 박제.</item>
/// </list>
/// <para/>
/// thread-safety: <see cref="LightHouseClientHolder"/> 의 SSE loop 가 single Task 안에서 호출 → instance 별 단일 thread.
/// 동일 instance 를 다중 thread 가 호출하는 경우는 박제 안 함 (현재 caller 패턴 한정).
/// </summary>
public sealed class SseReconnectBackoff
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);

    /// <summary>이 시간 이상 안정 후 다음 fail 발생 시 attempt 를 reset (새 incident 로 카운트).</summary>
    private static readonly TimeSpan ResetThreshold = TimeSpan.FromSeconds(60);

    private readonly Func<DateTime> _utcNow;
    private readonly Func<double> _jitterFraction;
    private int _attempt;
    private DateTime _lastFailureUtc = DateTime.MinValue;

    /// <summary>production constructor — <see cref="DateTime.UtcNow"/> + <see cref="Random.Shared"/> 기반 ±20% jitter.</summary>
    public SseReconnectBackoff()
        : this(static () => DateTime.UtcNow, static () => Random.Shared.NextDouble() * 0.4 - 0.2) { }

    /// <summary>test 친화 constructor — 시각 mock. jitter=0 deterministic 박제 (기존 fact 회귀 0).</summary>
    internal SseReconnectBackoff(Func<DateTime> utcNow)
        : this(utcNow, static () => 0.0) { }

    /// <summary>test 친화 constructor — 시각 + jitter mock (R8-M2 회귀 차단 fact 용).</summary>
    internal SseReconnectBackoff(Func<DateTime> utcNow, Func<double> jitterFraction)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _jitterFraction = jitterFraction ?? throw new ArgumentNullException(nameof(jitterFraction));
    }

    /// <summary>현재 누적 attempt (debug / log 진단용).</summary>
    public int Attempt => _attempt;

    /// <summary>
    /// catch 진입 시 호출 — stable 후 진입이면 attempt reset, 그렇지 않으면 attempt 증가 + delay 산출.
    /// <para/>
    /// 반환 = (이번 reconnect 시도 전 wait 할 delay, 이번 시도를 Warn 으로 log 할지 여부).
    /// </summary>
    public (TimeSpan Delay, bool ShouldLog) NextDelay()
    {
        var now = _utcNow();
        if (_attempt > 0 && now - _lastFailureUtc > ResetThreshold)
            _attempt = 0;
        _attempt++;
        _lastFailureUtc = now;

        var exp = Math.Min(_attempt - 1, 5);
        var raw = TimeSpan.FromMilliseconds(Initial.TotalMilliseconds * Math.Pow(2, exp));
        var basis = raw > Cap ? Cap : raw;
        // R8-M2 — ±20% jitter. production = Random [-0.2, +0.2). test = 0 (deterministic).
        var ratio = _jitterFraction();
        var jittered = basis.TotalMilliseconds * (1.0 + ratio);
        var delay = TimeSpan.FromMilliseconds(Math.Max(0.0, jittered));
        var shouldLog = _attempt <= 3 || _attempt % 5 == 0;
        return (delay, shouldLog);
    }
}
