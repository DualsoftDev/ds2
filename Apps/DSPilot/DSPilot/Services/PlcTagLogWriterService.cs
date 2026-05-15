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

    private async Task FlushAsync(List<PlcTagLogEntry> entries)
    {
        try
        {
            var dbPath = _pathResolver.GetSharedDbPath();
            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            const string sql = @"
                INSERT INTO plcTagLog (plcTagId, dateTime, value)
                VALUES (@TagId, @Dt, @Val)";

            await conn.ExecuteAsync(sql, entries.Select(e => new
            {
                TagId = e.PlcTagId,
                Dt = SqliteDateTimeHelpers.ToSqliteUtcString(e.Timestamp),
                Val = e.Value,
            }), tx);

            tx.Commit();
            _logger.LogTrace("[plcTagLog] flushed {Count} entries", entries.Count);

            // 연속 실패 카운터 리셋 — 회복 시점을 명확히 로그.
            if (_consecutiveFailures > 0)
            {
                _logger.LogInformation(
                    "[plcTagLog] write recovered after {Failures} consecutive failures (total dropped so far={Dropped})",
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
                "[plcTagLog] flush failed: dropped={Count}, range={Oldest:HH:mm:ss.fff}~{Newest:HH:mm:ss.fff}, consecutive={Consec}, totalDropped={Total}",
                entries.Count, oldest, newest, _consecutiveFailures, _totalDropped);
        }
    }
}

public readonly record struct PlcTagLogEntry(int PlcTagId, string Value, DateTime Timestamp);
