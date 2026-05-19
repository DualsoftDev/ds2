using System;
using Promaker.Knowledge;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **Phase S7 D-S7-2c (s6-r32) — SseReconnectBackoff fact**.
/// <para/>
/// exponential 1→2→4→8→16→30 cap + 60s stable reset + log throttle 검증.
/// </summary>
public sealed class SseReconnectBackoffTests
{
    private static SseReconnectBackoff WithFixedTime(DateTime utc)
        => new(() => utc);

    private sealed class MutableClock
    {
        public DateTime Now { get; set; } = new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
    }

    [Fact]
    public void NextDelay_first_attempt_returns_1s_and_logs()
    {
        var b = WithFixedTime(new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Utc));
        var (delay, shouldLog) = b.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
        Assert.True(shouldLog);
        Assert.Equal(1, b.Attempt);
    }

    [Fact]
    public void NextDelay_exponential_sequence_caps_at_30s()
    {
        var clock = new MutableClock();
        var b = new SseReconnectBackoff(clock.Get);

        var expected = new[]
        {
            TimeSpan.FromSeconds(1),  // attempt 1
            TimeSpan.FromSeconds(2),  // attempt 2
            TimeSpan.FromSeconds(4),  // attempt 3
            TimeSpan.FromSeconds(8),  // attempt 4
            TimeSpan.FromSeconds(16), // attempt 5
            TimeSpan.FromSeconds(30), // attempt 6 (32 raw → cap 30)
            TimeSpan.FromSeconds(30), // attempt 7 (cap)
            TimeSpan.FromSeconds(30), // attempt 8 (cap)
        };

        for (int i = 0; i < expected.Length; i++)
        {
            // 매 attempt 사이에 짧은 시간 경과 — ResetThreshold (60s) 미만이라 attempt 누적.
            clock.Now = clock.Now.AddSeconds(1);
            var (delay, _) = b.NextDelay();
            Assert.Equal(expected[i], delay);
        }
        Assert.Equal(8, b.Attempt);
    }

    [Fact]
    public void NextDelay_should_log_throttle_skips_attempt_4()
    {
        var clock = new MutableClock();
        var b = new SseReconnectBackoff(clock.Get);

        // attempt 1, 2, 3 — Warn 출력 (≤3)
        for (int i = 1; i <= 3; i++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            var (_, shouldLog) = b.NextDelay();
            Assert.True(shouldLog, $"attempt {i} should log");
        }
        // attempt 4 — skip (4 > 3 + 4 % 5 != 0)
        clock.Now = clock.Now.AddSeconds(1);
        var (_, log4) = b.NextDelay();
        Assert.False(log4);

        // attempt 5 — Warn (5 % 5 == 0)
        clock.Now = clock.Now.AddSeconds(1);
        var (_, log5) = b.NextDelay();
        Assert.True(log5);

        // attempt 6,7,8,9 — skip
        for (int i = 6; i <= 9; i++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            var (_, sl) = b.NextDelay();
            Assert.False(sl, $"attempt {i} should skip");
        }

        // attempt 10 — Warn (10 % 5 == 0)
        clock.Now = clock.Now.AddSeconds(1);
        var (_, log10) = b.NextDelay();
        Assert.True(log10);
    }

    [Fact]
    public void NextDelay_resets_attempt_after_stable_threshold()
    {
        var clock = new MutableClock();
        var b = new SseReconnectBackoff(clock.Get);

        // 3 fail 누적 → attempt=3, 다음 delay 8s.
        for (int i = 0; i < 3; i++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            b.NextDelay();
        }
        Assert.Equal(3, b.Attempt);

        // 60s + 1s 경과 — stable.
        clock.Now = clock.Now.AddSeconds(61);
        var (delay, shouldLog) = b.NextDelay();
        // reset 후 attempt=1 → delay 1s.
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
        Assert.True(shouldLog);
        Assert.Equal(1, b.Attempt);
    }

    [Fact]
    public void NextDelay_no_reset_within_threshold()
    {
        var clock = new MutableClock();
        var b = new SseReconnectBackoff(clock.Get);

        for (int i = 0; i < 3; i++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            b.NextDelay();
        }
        // 59s 경과 — threshold 60s 미달이라 reset 안 됨.
        clock.Now = clock.Now.AddSeconds(59);
        var (delay, _) = b.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(8), delay);  // attempt 4 → 8s
        Assert.Equal(4, b.Attempt);
    }

    [Fact]
    public void Ctor_null_clock_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SseReconnectBackoff(null!));
    }
}
