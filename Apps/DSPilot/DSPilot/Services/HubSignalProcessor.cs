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
    private readonly Action<string, string, string> _handleSignal;
    private readonly Action<string, long>? _onDrop;
    private readonly Action<string, Exception, int, int>? _onRetry;
    private readonly Action<string, string, string, Exception, long>? _onDeadLetter;
    private readonly int _maxRetries;
    private readonly Func<int, TimeSpan> _retryDelay;
    private const int DefaultChannelCapacity = 1024;

    public Channel<HubSignal> SignalChannel { get; }
    public long DropCount       => Interlocked.Read(ref _dropCount);
    public long DeadLetterCount => Interlocked.Read(ref _deadLetterCount);

    private long _dropCount;
    private long _deadLetterCount;

    public HubSignalProcessor(
        IEnumerable<string> acceptedSources,
        Action<string, string, string> handleSignal,
        int maxRetries = 3,
        int channelCapacity = DefaultChannelCapacity,
        Func<int, TimeSpan>? retryDelay = null,
        Action<string, long>? onDrop = null,
        Action<string, Exception, int, int>? onRetry = null,
        Action<string, string, string, Exception, long>? onDeadLetter = null)
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

        SignalChannel = Channel.CreateBounded<HubSignal>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>accepted source 면 channel 에 enqueue. unaccepted 는 *조용히 무시* (drop 아님 — spec §2).
    /// channel write 실패 시 drop count 증가 + 콜백.</summary>
    public EnqueueResult TryEnqueue(string address, string value, string source)
    {
        if (!_acceptedSources.Contains(source))
            return EnqueueResult.Ignored;

        if (SignalChannel.Writer.TryWrite(new HubSignal(address, value, source, 0)))
            return EnqueueResult.Accepted;

        var total = Interlocked.Increment(ref _dropCount);
        _onDrop?.Invoke(address, total);
        return EnqueueResult.Dropped;
    }

    /// <summary>signal 1개 처리. 예외 시 maxRetries 회 backoff 후 dead-letter.
    /// 호출자 (Consumer loop) 가 channel 에서 dequeue 한 sig 를 본 메서드로 위임.</summary>
    public async Task ProcessSignalAsync(HubSignal sig, CancellationToken ct)
    {
        var attempt = sig.RetryCount;
        while (true)
        {
            try
            {
                _handleSignal(sig.Address, sig.Value, sig.Source);
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                if (attempt >= _maxRetries)
                {
                    var dl = Interlocked.Increment(ref _deadLetterCount);
                    _onDeadLetter?.Invoke(sig.Address, sig.Value, sig.Source, ex, dl);
                    return;
                }
                _onRetry?.Invoke(sig.Address, ex, attempt, _maxRetries);
                try { await Task.Delay(_retryDelay(attempt), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>channel reader 를 drain 하며 ProcessSignalAsync 호출. shutdown 시 cancellation 으로 종료.</summary>
    public async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var sig in SignalChannel.Reader.ReadAllAsync(ct))
                await ProcessSignalAsync(sig, ct);
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }
}

/// <summary>처리 큐 항목. RetryCount 는 재시도 추적용.</summary>
public readonly record struct HubSignal(string Address, string Value, string Source, int RetryCount);

/// <summary>TryEnqueue 결과 — accepted(정상 enqueue), ignored(unaccepted source), dropped(채널 백압).</summary>
public enum EnqueueResult { Accepted, Ignored, Dropped }
