// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Ds2.Backend.Common;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// HubSignalProcessor 단위 테스트 — ROADMAP ③ 5 통합 테스트의 *순수 로직* 부분.
/// SignalR client / SimulationEngineService 의존성 없이 *filter + channel + retry + drop + dead-letter*
/// 5 시나리오 검증. spec: SIGNALR_FLOW.md §3 (drop), §4 (retry/dead-letter).
/// </summary>
public class HubSignalProcessorTests
{
    private static HubSignalProcessor CreateProcessor(
        Action<string, string, string, long>? handle = null,
        IEnumerable<string>? accepted = null,
        int maxRetries = 3,
        int channelCapacity = 1024,
        Func<int, TimeSpan>? retryDelay = null,
        Action<string, long>? onDrop = null,
        Action<string, Exception, int, int>? onRetry = null,
        Action<string, string, string, Exception, long>? onDeadLetter = null) =>
        new(
            acceptedSources: accepted ?? HubSource.DefaultAcceptedSources,
            handleSignal: handle ?? ((_, _, _, _) => { }),
            maxRetries: maxRetries,
            channelCapacity: channelCapacity,
            retryDelay: retryDelay ?? (_ => TimeSpan.Zero), // 테스트 빠르게
            onDrop: onDrop,
            onRetry: onRetry,
            onDeadLetter: onDeadLetter);

    // ── 1. Source filter ────────────────────────────────────────────────

    [Fact]
    public void TryEnqueue_Accepted_source_returns_Accepted_and_enqueues()
    {
        var proc = CreateProcessor(accepted: new[] { HubSource.Plc });
        var result = proc.TryEnqueue("addr1", "true", HubSource.Plc);

        Assert.Equal(EnqueueResult.Accepted, result);
        Assert.True(proc.SignalChannel.Reader.TryRead(out var sig));
        Assert.Equal("addr1", sig.Address);
        Assert.Equal("true", sig.Value);
        Assert.Equal(HubSource.Plc, sig.Source);
        Assert.Equal(0, sig.RetryCount);
    }

    [Fact]
    public void TryEnqueue_Unaccepted_source_returns_Ignored_and_drop_count_zero()
    {
        long dropCount = 0;
        var proc = CreateProcessor(
            accepted: new[] { HubSource.Plc },
            onDrop: (_, total) => dropCount = total);

        var result = proc.TryEnqueue("addr1", "true", HubSource.Monitoring);

        Assert.Equal(EnqueueResult.Ignored, result);
        Assert.Equal(0L, proc.DropCount);
        Assert.Equal(0L, dropCount);
        Assert.False(proc.SignalChannel.Reader.TryRead(out _));
    }

    [Fact]
    public void TryEnqueue_Source_match_is_case_insensitive()
    {
        var proc = CreateProcessor(accepted: new[] { HubSource.Plc });
        Assert.Equal(EnqueueResult.Accepted, proc.TryEnqueue("addr1", "true", "PLC"));
        Assert.Equal(EnqueueResult.Accepted, proc.TryEnqueue("addr2", "true", "Plc"));
    }

    // ── 2. Drop rate metric ─────────────────────────────────────────────

    [Fact]
    public void TryEnqueue_After_channel_complete_returns_Dropped_and_increments_count()
    {
        var dropAddresses = new List<string>();
        var proc = CreateProcessor(
            accepted: new[] { HubSource.Plc },
            onDrop: (addr, _) => dropAddresses.Add(addr));

        // Channel writer 강제 종료 → 이후 TryWrite 실패
        proc.SignalChannel.Writer.Complete();

        for (var i = 0; i < 5; i++)
            Assert.Equal(EnqueueResult.Dropped, proc.TryEnqueue($"addr{i}", "true", HubSource.Plc));

        Assert.Equal(5L, proc.DropCount);
        Assert.Equal(5, dropAddresses.Count);
        Assert.Equal("addr0", dropAddresses[0]);
        Assert.Equal("addr4", dropAddresses[4]);
    }

    [Fact]
    public void TryEnqueue_When_channel_is_full_returns_Dropped_and_increments_count()
    {
        var dropped = new List<(string address, long total)>();
        var proc = CreateProcessor(
            accepted: new[] { HubSource.Plc },
            channelCapacity: 2,
            onDrop: (addr, total) => dropped.Add((addr, total)));

        Assert.Equal(EnqueueResult.Accepted, proc.TryEnqueue("addr1", "true", HubSource.Plc));
        Assert.Equal(EnqueueResult.Accepted, proc.TryEnqueue("addr2", "true", HubSource.Plc));
        Assert.Equal(EnqueueResult.Dropped, proc.TryEnqueue("addr3", "true", HubSource.Plc));

        Assert.Equal(1L, proc.DropCount);
        var item = Assert.Single(dropped);
        Assert.Equal("addr3", item.address);
        Assert.Equal(1L, item.total);
    }

    // ── 3. Retry → success ──────────────────────────────────────────────

    [Fact]
    public async Task ProcessSignalAsync_Transient_failure_retries_until_success()
    {
        var attempts = 0;
        var retryCalls = new List<int>();
        var proc = CreateProcessor(
            handle: (_, _, _, _) =>
            {
                attempts++;
                if (attempts < 3) throw new InvalidOperationException("transient");
                // attempt 3: 성공
            },
            maxRetries: 5,
            onRetry: (_, _, attempt, _) => retryCalls.Add(attempt));

        await proc.ProcessSignalAsync(new HubSignal("addr1", "true", HubSource.Plc, 0), CancellationToken.None);

        Assert.Equal(3, attempts);              // 2회 실패 + 1회 성공
        Assert.Equal(new[] { 1, 2 }, retryCalls);
        Assert.Equal(0L, proc.DeadLetterCount);
    }

    // ── 4. Dead-letter ──────────────────────────────────────────────────

    [Fact]
    public async Task ProcessSignalAsync_Permanent_failure_reaches_dead_letter()
    {
        var attempts = 0;
        var deadLetters = new List<(string addr, string val, string src, long count)>();
        var proc = CreateProcessor(
            handle: (_, _, _, _) =>
            {
                attempts++;
                throw new InvalidOperationException("permanent");
            },
            maxRetries: 3,
            onDeadLetter: (addr, val, src, _, count) => deadLetters.Add((addr, val, src, count)));

        await proc.ProcessSignalAsync(new HubSignal("addr1", "true", HubSource.Plc, 0), CancellationToken.None);

        Assert.Equal(3, attempts);              // maxRetries = 3 → 3회 모두 실패
        Assert.Equal(1L, proc.DeadLetterCount);
        var dl = Assert.Single(deadLetters);
        Assert.Equal("addr1", dl.addr);
        Assert.Equal("true", dl.val);
        Assert.Equal(HubSource.Plc, dl.src);
        Assert.Equal(1L, dl.count);
    }

    [Fact]
    public async Task ProcessSignalAsync_Multiple_dead_letters_accumulate_count()
    {
        var proc = CreateProcessor(
            handle: (_, _, _, _) => throw new InvalidOperationException("permanent"),
            maxRetries: 1);

        await proc.ProcessSignalAsync(new HubSignal("a1", "v", HubSource.Plc, 0), CancellationToken.None);
        await proc.ProcessSignalAsync(new HubSignal("a2", "v", HubSource.Plc, 0), CancellationToken.None);
        await proc.ProcessSignalAsync(new HubSignal("a3", "v", HubSource.Plc, 0), CancellationToken.None);

        Assert.Equal(3L, proc.DeadLetterCount);
    }

    // ── 5. End-to-end consume loop ──────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_drains_channel_and_invokes_handler_per_signal()
    {
        var processed = new List<(string addr, string val, string src)>();
        var proc = CreateProcessor(
            handle: (addr, val, src, _) =>
            {
                lock (processed) processed.Add((addr, val, src));
            });

        proc.TryEnqueue("addr1", "true",  HubSource.Plc);
        proc.TryEnqueue("addr2", "false", HubSource.Plc);
        proc.TryEnqueue("addr3", "1",     HubSource.Control);
        proc.SignalChannel.Writer.Complete();

        await proc.ConsumeAsync(CancellationToken.None);

        Assert.Equal(3, processed.Count);
        Assert.Equal("addr1", processed[0].addr);
        Assert.Equal("addr2", processed[1].addr);
        Assert.Equal("addr3", processed[2].addr);
    }

    [Fact]
    public async Task ConsumeAsync_does_not_propagate_handler_exceptions()
    {
        var processed = new List<string>();
        var proc = CreateProcessor(
            handle: (addr, _, _, _) =>
            {
                if (addr == "fail") throw new InvalidOperationException("boom");
                lock (processed) processed.Add(addr);
            },
            maxRetries: 1);

        proc.TryEnqueue("ok1",  "true", HubSource.Plc);
        proc.TryEnqueue("fail", "true", HubSource.Plc);
        proc.TryEnqueue("ok2",  "true", HubSource.Plc);
        proc.SignalChannel.Writer.Complete();

        await proc.ConsumeAsync(CancellationToken.None);

        // ok1, ok2 처리 성공. fail 은 dead-letter.
        Assert.Equal(new[] { "ok1", "ok2" }, processed);
        Assert.Equal(1L, proc.DeadLetterCount);
    }
}
