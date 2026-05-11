using System;
using System.Threading.Tasks;
using Ds2.Core.Store;
using Ds2.LlmAgent;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// `LlmTurnContext.DecrementMutationCount` 경계 + `IsQuotaExceeded` 자동 reset 회귀 검증.
/// M-A (path-symmetric revert) / M-B (over-revert 회귀) 회귀 방어 — 사전 charge 후 throw 시
/// quota counter 정확히 원복되는지, fast-fail 가드로 가산 없이 throw 한 경우 over-revert 없는지.
/// </summary>
public sealed class LlmTurnContextRevertTests
{
    private static LlmTurnContext NewContext(int quota = 200)
    {
        var store = new DsStore();
        return new LlmTurnContext(store, new SyncDispatcher()) { MutationQuota = quota };
    }

    [Fact]
    public void DecrementMutationCount_delta_below_1_is_silent_noop()
    {
        var ctx = NewContext();
        ctx.IncrementMutationCount(5);
        ctx.DecrementMutationCount(0);
        Assert.Equal(5, ctx.MutationCallCount);
        ctx.DecrementMutationCount(-3);
        Assert.Equal(5, ctx.MutationCallCount);
    }

    [Fact]
    public void DecrementMutationCount_clamps_to_zero_no_underflow()
    {
        var ctx = NewContext();
        ctx.IncrementMutationCount(3);
        ctx.DecrementMutationCount(10);
        Assert.Equal(0, ctx.MutationCallCount);
    }

    [Fact]
    public void DecrementMutationCount_resets_IsQuotaExceeded_when_below_cap()
    {
        var ctx = NewContext(quota: 5);
        ctx.IncrementMutationCount(3);
        Assert.Throws<QuotaExceededException>(() => ctx.IncrementMutationCount(10));
        Assert.True(ctx.IsQuotaExceeded);
        ctx.DecrementMutationCount(10);
        Assert.False(ctx.IsQuotaExceeded);
        Assert.Equal(3, ctx.MutationCallCount);
    }

    [Fact]
    public void DecrementMutationCount_keeps_IsQuotaExceeded_when_still_above_cap()
    {
        var ctx = NewContext(quota: 5);
        ctx.IncrementMutationCount(3);
        Assert.Throws<QuotaExceededException>(() => ctx.IncrementMutationCount(20));
        Assert.True(ctx.IsQuotaExceeded);
        ctx.DecrementMutationCount(5);
        Assert.True(ctx.IsQuotaExceeded);
        Assert.Equal(18, ctx.MutationCallCount);
    }

    [Fact]
    public void IncrementMutationCount_fast_fail_guard_no_count_change()
    {
        var ctx = NewContext(quota: 5);
        Assert.Throws<QuotaExceededException>(() => ctx.IncrementMutationCount(20));
        Assert.True(ctx.IsQuotaExceeded);
        int countAfterFirstThrow = ctx.MutationCallCount;
        Assert.Throws<QuotaExceededException>(() => ctx.IncrementMutationCount(1));
        Assert.Equal(countAfterFirstThrow, ctx.MutationCallCount);
    }

    /// <summary>
    /// M-B 회귀: fast-fail 가드 throw 후 호출자가 over-revert 하면 counter 0 reset + IsQuotaExceeded auto-reset →
    /// DoS 표면. 호출자는 IncrementMutationCount 성공 직후에만 누적해야 함 — ApplyOperations / RunWithChargedQuota 패턴.
    /// 본 테스트는 over-revert 가 발생하면 어떻게 망가지는지 명시적으로 기록 (회귀 발견 시 본 테스트 fail 보장 없음 —
    /// 호출자 측 패턴 위반은 별도 grep 으로 검증).
    /// </summary>
    [Fact]
    public void OverRevert_causes_IsQuotaExceeded_auto_reset_pathological_case()
    {
        var ctx = NewContext(quota: 5);
        ctx.IncrementMutationCount(3);
        Assert.Throws<QuotaExceededException>(() => ctx.IncrementMutationCount(10));
        Assert.True(ctx.IsQuotaExceeded);
        ctx.DecrementMutationCount(10);
        Assert.False(ctx.IsQuotaExceeded);
    }

    private sealed class SyncDispatcher : IUiDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }
}
