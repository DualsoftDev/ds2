// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Threading.Channels;
using Ds2.Backend.Common;

namespace DSPilot.Services;

/// <summary>
/// Hub 신호 처리의 순수 로직: source filter + channel + drop metric + retry + dead-letter.
/// HubSubscriberService 가 SignalR 의존성을 inject 하고, 본 processor 가 *플로우/카운트* 만 담당.
/// 테스트 시 SignalR 없이 직접 instantiate 가능.
///
/// spec 권위: <see href="../../../SIGNALR_FLOW.md"/> §3 (drop metric), §4 (retry/dead-letter).
/// </summary>
public sealed class HubSignalProcessor
{
    private readonly HashSet<string> _acceptedSources;
    // (address, value, source, wallClockMs, systemId) — systemId 는 "" 가능(구버전 송신자).
    private readonly Action<string, string, string, long, string?> _handleSignal;
    private readonly Action<string, long>? _onDrop;
    private readonly Action<string, Exception, int, int>? _onRetry;
    private readonly Action<string, string, string, Exception, long>? _onDeadLetter;
    private readonly Action? _onProcessed;
    private readonly int _maxRetries;
    private readonly Func<int, TimeSpan> _retryDelay;
    // 1024 → 8192 → 32768: Pi5 replay 버스트(터널 순단 후 수백~수천 건을 한 번에 몰아 전송) + resync
    // 스윕이 겹치면 순간 유입이 8192 를 넘겨 뭉텅이 drop 됐다(8/14 실측 오후 9만 drop). 32768 로 키워
    // 버스트를 흡수한다(항목당 수십 바이트 → 32768 이어도 수 MB, 메모리 무시 가능). 단 이는 "순간 초과"
    // 완충일 뿐 "정속 초과"의 근본은 소비자 가속(신호별 로그 제거)이며, 로그 backpressure 는 별도로
    // Program.cs 의 콘솔 로거 QueueFullMode=DropWrite 로 차단한다.
    private const int DefaultChannelCapacity = 32768;

    public Channel<HubSignal> SignalChannel { get; }
    public int ChannelCapacity  { get; }
    public long CurrentDepth    => Math.Max(0, Interlocked.Read(ref _currentDepth));
    public long DropCount       => Interlocked.Read(ref _dropCount);
    public long DeadLetterCount => Interlocked.Read(ref _deadLetterCount);

    private long _currentDepth;
    private long _intervalMaxDepth;
    private long _intervalEnqueuedCount;
    private long _intervalProcessedCount;
    private long _intervalDropCount;
    private long _intervalDeadLetterCount;
    private long _dropCount;
    private long _deadLetterCount;

    public HubSignalProcessor(
        IEnumerable<string> acceptedSources,
        Action<string, string, string, long, string?> handleSignal,
        int maxRetries = 3,
        int channelCapacity = DefaultChannelCapacity,
        Func<int, TimeSpan>? retryDelay = null,
        Action<string, long>? onDrop = null,
        Action<string, Exception, int, int>? onRetry = null,
        Action<string, string, string, Exception, long>? onDeadLetter = null,
        Action? onProcessed = null)
    {
        if (channelCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(channelCapacity), "Channel capacity must be positive.");

        _acceptedSources = new HashSet<string>(acceptedSources, StringComparer.OrdinalIgnoreCase);
        _handleSignal    = handleSignal ?? throw new ArgumentNullException(nameof(handleSignal));
        _maxRetries      = maxRetries;
        _retryDelay      = retryDelay ?? (attempt => TimeSpan.FromMilliseconds(50 * attempt));
        _onDrop          = onDrop;
        _onRetry         = onRetry;
        _onDeadLetter    = onDeadLetter;
        _onProcessed     = onProcessed;
        ChannelCapacity  = channelCapacity;

        SignalChannel = Channel.CreateBounded<HubSignal>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>accepted source 면 channel 에 enqueue. unaccepted 는 *조용히 무시* (drop 아님 — spec §2).
    /// channel write 실패 시 drop count 증가 + 콜백.
    /// wallClockMs: 원천 관측 시각(UTC epoch ms, TagWrite.WallClockMs). 0 = 미제공(단건 OnTagChanged 등)
    /// → 소비측이 도착시각 폴백. replay 시 원래 시각 복원을 위해 반드시 관통 전달.</summary>
    public EnqueueResult TryEnqueue(
        string address, string value, string source, long wallClockMs = 0, string? systemId = null)
    {
        if (!_acceptedSources.Contains(source))
            return EnqueueResult.Ignored;

        // TryWrite 전에 depth 를 예약해야 writer 성공 직후 reader 가 먼저 dequeue 해도 음수가 되지 않는다.
        var depth = Interlocked.Increment(ref _currentDepth);
        if (SignalChannel.Writer.TryWrite(new HubSignal(address, value, source, 0, wallClockMs, systemId)))
        {
            Interlocked.Increment(ref _intervalEnqueuedCount);
            UpdateIntervalMaxDepth(depth);
            return EnqueueResult.Accepted;
        }

        Interlocked.Decrement(ref _currentDepth);

        var total = Interlocked.Increment(ref _dropCount);
        Interlocked.Increment(ref _intervalDropCount);
        _onDrop?.Invoke(address, total);
        return EnqueueResult.Dropped;
    }

    /// <summary>직전 호출 이후의 채널 처리량과 그 구간의 최대 적체를 원자적으로 가져온다.
    /// 학습/신호 상태와 무관한 운영 계측이며 호출 시 카운터 구간만 새로 시작한다.</summary>
    public HubSignalIntervalDiagnostics TakeIntervalDiagnostics()
    {
        var current = CurrentDepth;
        var maxDepth = Math.Max(current, Interlocked.Exchange(ref _intervalMaxDepth, current));
        return new HubSignalIntervalDiagnostics(
            Interlocked.Exchange(ref _intervalEnqueuedCount, 0),
            Interlocked.Exchange(ref _intervalProcessedCount, 0),
            Interlocked.Exchange(ref _intervalDropCount, 0),
            Interlocked.Exchange(ref _intervalDeadLetterCount, 0),
            current,
            maxDepth,
            ChannelCapacity);
    }

    private void UpdateIntervalMaxDepth(long depth)
    {
        var observed = Interlocked.Read(ref _intervalMaxDepth);
        while (depth > observed)
        {
            var previous = Interlocked.CompareExchange(ref _intervalMaxDepth, depth, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }

    /// <summary>signal 1개 처리. 예외 시 maxRetries 회 backoff 후 dead-letter.
    /// 호출자 (Consumer loop) 가 channel 에서 dequeue 한 sig 를 본 메서드로 위임.</summary>
    public async Task ProcessSignalAsync(HubSignal sig, CancellationToken ct)
    {
        try
        {
            var attempt = sig.RetryCount;
            while (true)
            {
                try
                {
                    _handleSignal(sig.Address, sig.Value, sig.Source, sig.WallClockMs, sig.SystemId);
                    return;
                }
                catch (Exception ex)
                {
                    attempt++;
                    if (attempt >= _maxRetries)
                    {
                        var dl = Interlocked.Increment(ref _deadLetterCount);
                        Interlocked.Increment(ref _intervalDeadLetterCount);
                        _onDeadLetter?.Invoke(sig.Address, sig.Value, sig.Source, ex, dl);
                        return;
                    }
                    _onRetry?.Invoke(sig.Address, ex, attempt, _maxRetries);
                    try { await Task.Delay(_retryDelay(attempt), ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        finally
        {
            Interlocked.Increment(ref _intervalProcessedCount);
            // 진단 콜백 실패가 유일한 Hub 소비 루프를 종료시키면 안 된다.
            try { _onProcessed?.Invoke(); } catch { /* diagnostics are best-effort */ }
        }
    }

    /// <summary>channel reader 를 drain 하며 ProcessSignalAsync 호출. shutdown 시 cancellation 으로 종료.</summary>
    public async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var sig in SignalChannel.Reader.ReadAllAsync(ct))
            {
                Interlocked.Decrement(ref _currentDepth);
                await ProcessSignalAsync(sig, ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }
}

/// <summary>한 진단 구간 동안의 Hub 신호 채널 집계.</summary>
public readonly record struct HubSignalIntervalDiagnostics(
    long Enqueued,
    long Processed,
    long Dropped,
    long DeadLetters,
    long CurrentDepth,
    long MaxDepth,
    int Capacity);

/// <summary>처리 큐 항목. RetryCount 는 재시도 추적용.
/// WallClockMs = 원천 관측 시각(UTC epoch ms, 0=미제공→도착시각 폴백).</summary>
/// <param name="SystemId">신호를 보유한 PLC 의 소유 System(Guid 문자열). "" / null = 미제공(구버전 송신자).</param>
public readonly record struct HubSignal(
    string Address, string Value, string Source, int RetryCount, long WallClockMs = 0, string? SystemId = null);

/// <summary>TryEnqueue 결과 — accepted(정상 enqueue), ignored(unaccepted source), dropped(채널 백압).</summary>
public enum EnqueueResult { Accepted, Ignored, Dropped }
