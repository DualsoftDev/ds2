// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Threading.Channels;
using Dapper;
using DSPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>
/// plcTagLog 의 INSERT 를 배치 처리.
/// 매 신호마다 connection open + INSERT + close 하던 패턴을 폐기하고,
/// 채널에 enqueue → 단일 컨슈머가 250ms 또는 100건마다 트랜잭션으로 한 번에 INSERT.
/// SQLite WAL 경합 차단 + 처리량 향상.
/// </summary>
public sealed class PlcTagLogWriterService : BackgroundService
{
    private const int FlushIntervalMs = 250;
    private const int FlushBatchSize = 100;

    private readonly Channel<PlcTagLogEntry> _channel = Channel.CreateUnbounded<PlcTagLogEntry>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly IDatabasePathResolver _pathResolver;
    private readonly ILogger<PlcTagLogWriterService> _logger;

    // 실패 누적 통계 — silent drop 으로 분석 페이지가 부정확해지는 케이스를 추적하기 위함.
    private long _totalDropped;
    private int _consecutiveFailures;

    public PlcTagLogWriterService(
        IDatabasePathResolver pathResolver,
        ILogger<PlcTagLogWriterService> logger)
    {
        _pathResolver = pathResolver;
        _logger = logger;
    }

    /// <summary>SimulationEngineService 가 Hub 신호 처리 시 enqueue.</summary>
    public bool TryWrite(int plcTagId, string value, DateTime timestamp)
        => _channel.Writer.TryWrite(new PlcTagLogEntry(plcTagId, value, timestamp));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PlcTagLogWriterService starting (batch={Size}, flush={Ms}ms)",
            FlushBatchSize, FlushIntervalMs);

        var buffer = new List<PlcTagLogEntry>(FlushBatchSize);
        var lastFlush = DateTime.UtcNow;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 채널에 데이터 도착할 때까지 대기 (최대 FlushIntervalMs)
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(FlushIntervalMs);
                bool hasMore;
                try
                {
                    hasMore = await _channel.Reader.WaitToReadAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    hasMore = true; // timeout — 버퍼 flush 시도
                }

                if (hasMore)
                {
                    while (_channel.Reader.TryRead(out var entry))
                    {
                        buffer.Add(entry);
                        if (buffer.Count >= FlushBatchSize) break;
                    }
                }

                var elapsed = (DateTime.UtcNow - lastFlush).TotalMilliseconds;
                if (buffer.Count >= FlushBatchSize || (buffer.Count > 0 && elapsed >= FlushIntervalMs))
                {
                    await FlushAsync(buffer);
                    buffer.Clear();
                    lastFlush = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        // 종료 시 잔여 버퍼 + 채널 drain
        while (_channel.Reader.TryRead(out var entry)) buffer.Add(entry);
        if (buffer.Count > 0) await FlushAsync(buffer);

        _logger.LogInformation("PlcTagLogWriterService stopped");
    }

    /// <summary>
    /// 문자열로 도착한 값을 저장에 맞는 형태로 바꾼다. 신호 표의 값 칸은 타입 친화도를 선언하지 않아
    /// 넣은 그대로 담긴다 — 비트는 1바이트 정수, 수치는 실수, 나머지는 문자열이다.
    /// <para>
    /// 비트를 "true"(4~5바이트 문자열) 대신 1/0 으로 넣는 것이 원시 로그 용량의 핵심이다.
    /// 조회 쪽 정규화는 <c>lower(trim(coalesce(value,'')))</c> 라 정수도 그대로 받아 준다.
    /// </para>
    /// </summary>
    internal static object NativeValue(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        switch (raw)
        {
            case "true": case "True": case "TRUE": case "on": case "On": case "1": return 1L;
            case "false": case "False": case "FALSE": case "off": case "Off": case "0": return 0L;
        }
        if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var i)) return i;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return raw;
    }

    private async Task FlushAsync(List<PlcTagLogEntry> entries)
    {
        try
        {
            var dbPath = _pathResolver.GetSharedDbPath();
            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            const string sql = @"
                INSERT INTO signal (tagId, atMs, value)
                VALUES (@TagId, @At, @Val)";

            await conn.ExecuteAsync(sql, entries.Select(e => new
            {
                TagId = e.PlcTagId,
                At = Kpi.KpiTime.ToMs(e.Timestamp),
                Val = NativeValue(e.Value),
            }), tx);

            tx.Commit();
            _logger.LogTrace("[signal] flushed {Count} entries", entries.Count);

            // 연속 실패 카운터 리셋 — 회복 시점을 명확히 로그.
            if (_consecutiveFailures > 0)
            {
                _logger.LogInformation(
                    "[signal] write recovered after {Failures} consecutive failures (total dropped so far={Dropped})",
                    _consecutiveFailures, _totalDropped);
                _consecutiveFailures = 0;
            }
        }
        catch (Exception ex)
        {
            // silent drop — 시계열 데이터는 영구 손실되므로 분석 페이지(Heatmap/CycleTimeAnalysis) 정확도에
            // 영향. 누적 dropped 카운트와 시각 범위를 함께 로그해 forensic 추적 가능하게.
            Interlocked.Add(ref _totalDropped, entries.Count);
            _consecutiveFailures++;
            var oldest = entries[0].Timestamp;
            var newest = entries[entries.Count - 1].Timestamp;
            _logger.LogError(ex,
                "[signal] flush failed: dropped={Count}, range={Oldest:HH:mm:ss.fff}~{Newest:HH:mm:ss.fff}, consecutive={Consec}, totalDropped={Total}",
                entries.Count, oldest, newest, _consecutiveFailures, _totalDropped);
        }
    }
}

public readonly record struct PlcTagLogEntry(int PlcTagId, string Value, DateTime Timestamp);
