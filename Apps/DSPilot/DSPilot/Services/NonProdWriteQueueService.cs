// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Threading.Channels;
using DSPilot.Models.Oee;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// 비생산 감지 로그(oeeNonProdDetectionLog) 쓰기 큐 — P2-3 읽기경로 쓰기 분리.
///
/// 기존에는 OEE 집계(GET)가 당일 10×CT 감지 결과를 조회 도중 UPSERT 해서, 사용자 응답이
/// 파일 쓰기(WAL 커밋)를 기다렸다. 이제 조회 경로는 여기에 enqueue 만 하고 즉시 응답하며,
/// 백그라운드 단일 writer 가 1초 배치로 flush 한다.
///
/// 정확성 노트:
/// - UPSERT 는 (flowName, onsetAt, detectionReason) 자연키 멱등 — 중복 enqueue 무해.
/// - '비가동으로 보내기'(reclassify)의 겹침 삭제와 큐 잔량이 경합하면 stale 감지가 잠깐 되살아날 수
///   있으나, 다음 집계(사전계산 스윕 ≤20초)가 현재 규칙으로 다시 materialize 하므로 수렴한다.
/// - 큐 유실(프로세스 종료)도 같은 이유로 다음 집계가 재생성 — 감지 로그는 파생 데이터다.
/// 정적 채널인 이유: 호출부가 OeeControllerBase(파생 4개) 라 생성자 리플을 피한다(OeeChangeSignal 과 동일 규약).
/// </summary>
public sealed class NonProdWriteQueueService : BackgroundService
{
    private static readonly Channel<IReadOnlyList<OeeNonProdDetectionLog>> Queue =
        Channel.CreateBounded<IReadOnlyList<OeeNonProdDetectionLog>>(
            new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NonProdWriteQueueService> _logger;

    public NonProdWriteQueueService(IServiceScopeFactory scopeFactory, ILogger<NonProdWriteQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>조회 경로에서 호출 — 블로킹/예외 없음(가득 차면 최신 우선 유지, 유실분은 다음 집계가 재생성).</summary>
    public static void Enqueue(IReadOnlyList<OeeNonProdDetectionLog> entries)
    {
        if (entries.Count == 0) return;
        Queue.Writer.TryWrite(entries);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await Queue.Reader.WaitToReadAsync(stoppingToken))
            {
                // 1초 배치 — 같은 틱의 다발 집계(flow별 루프)를 한 번에 flush.
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) { break; }

                var merged = new List<OeeNonProdDetectionLog>();
                while (Queue.Reader.TryRead(out var batch))
                    merged.AddRange(batch);
                if (merged.Count == 0) continue;

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<IOeeRepository>();
                    await repo.UpsertNonProdDetectionsAsync(merged, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[OEE] 비생산 감지 로그 배치 flush 실패({Count}건) — 다음 집계가 재생성", merged.Count);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }
}
