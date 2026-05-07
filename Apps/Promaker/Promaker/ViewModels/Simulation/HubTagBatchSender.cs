using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Ds2.Backend.Common;
using Microsoft.AspNetCore.SignalR.Client;

namespace Promaker.ViewModels;

/// <summary>
/// PLC 스캔 사이클의 다수 tag 변경을 짧은 윈도우(<see cref="MaxWindowMs"/>) 동안 모아
/// <see cref="HubMethod.WriteTags"/> 1개 호출로 송신해, SignalR 프레임 수와 JSON
/// 직렬화 비용을 줄인다. 주문 보존(FIFO) — channel 단일 reader 가 enqueue 순서 그대로 flush.
/// </summary>
internal sealed class HubTagBatchSender : IAsyncDisposable
{
    /// <summary>한 batch 에 묶이는 최대 tag 수. 초과분은 즉시 다음 batch 로.</summary>
    private const int MaxBatchSize = 200;
    /// <summary>첫 enqueue 후 추가 tag 를 기다리는 최대 시간(ms). 짧을수록 latency↓ batch 효율↓.</summary>
    private const int MaxWindowMs = 25;

    private readonly HubConnection _hub;
    private readonly int _generation;
    private readonly Func<int, HubConnection, bool> _isCurrent;
    private readonly Action<string, Exception?> _logError;
    private readonly Channel<Item> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drainTask;

    public HubTagBatchSender(
        HubConnection hub,
        int generation,
        Func<int, HubConnection, bool> isCurrent,
        Action<string, Exception?> logError)
    {
        _hub = hub;
        _generation = generation;
        _isCurrent = isCurrent;
        _logError = logError;
        _channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _drainTask = Task.Run(() => DrainLoopAsync(_cts.Token));
    }

    /// <summary>비동기 enqueue — 즉시 반환. drain 루프가 batch 로 묶어 송신.</summary>
    public bool Enqueue(string address, string value, string source)
    {
        if (string.IsNullOrEmpty(address)) return false;
        return _channel.Writer.TryWrite(new Item(new TagWrite(address, value, source), FlushTcs: null));
    }

    /// <summary>현재 channel 에 쌓인 enqueue 분이 모두 송신될 때까지 대기.</summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new Item(null, tcs)))
            tcs.TrySetResult(false);
        return tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { await _drainTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        var batch = new List<TagWrite>(MaxBatchSize);
        var pendingFlushes = new List<TaskCompletionSource<bool>>();

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                DrainAvailable(reader, batch, pendingFlushes);

                if (batch.Count > 0 && batch.Count < MaxBatchSize)
                {
                    try { await Task.Delay(MaxWindowMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    DrainAvailable(reader, batch, pendingFlushes);
                }

                if (batch.Count > 0)
                    await SendBatchAsync(batch, ct).ConfigureAwait(false);

                batch.Clear();
                foreach (var tcs in pendingFlushes) tcs.TrySetResult(true);
                pendingFlushes.Clear();
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            // 잔여 flush 대기자 해제 — Dispose 시 hang 방지.
            foreach (var tcs in pendingFlushes) tcs.TrySetResult(false);
        }
    }

    private static void DrainAvailable(
        ChannelReader<Item> reader,
        List<TagWrite> batch,
        List<TaskCompletionSource<bool>> pendingFlushes)
    {
        while (batch.Count < MaxBatchSize && reader.TryRead(out var item))
        {
            if (item.FlushTcs is not null)
                pendingFlushes.Add(item.FlushTcs);
            else if (item.Tag is not null)
                batch.Add(item.Tag);
        }
    }

    private async Task SendBatchAsync(List<TagWrite> batch, CancellationToken ct)
    {
        if (!_isCurrent(_generation, _hub))
            return;
        if (_hub.State != HubConnectionState.Connected)
        {
            _logError($"WriteTags 송신 skip — Hub not connected (drop {batch.Count} tags)", null);
            return;
        }

        try
        {
            await _hub.InvokeAsync(HubMethod.WriteTags, batch.ToArray(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logError($"WriteTags 실패 (batch={batch.Count}): {ex.Message}", ex);
        }
    }

    private readonly record struct Item(TagWrite? Tag, TaskCompletionSource<bool>? FlushTcs);
}
