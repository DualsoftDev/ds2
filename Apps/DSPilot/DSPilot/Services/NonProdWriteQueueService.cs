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
/// 자가치유(doc/25 §4.1, 2026-07-16): 배치는 감지 목록과 함께 집계 창/스코프를 나른다.
/// writer 는 ① UPSERT(lastConfirmedAt 갱신·invalidatedAt 해제) 후 ② 같은 창에서 이번 패스가
/// 재확인하지 않은 행을 invalidatedAt 마킹 — 판정이 뒤집힌(고장/대기 재분류) 구간의 stale
/// 자동 비생산이 표시에 남지 않는다. 감지 0건 배치도 무효화를 위해 흘려보낸다.
///
/// 정확성 노트:
/// - UPSERT 는 (flowName, onsetAt, detectionReason) 자연키 멱등 — 중복 enqueue 무해.
/// - 무효화는 "이번 패스 미재확인"(lastConfirmedAt &lt; 패스 마크) 조건 — 같은 초의 재확인은 살아남고,
///   경합으로 남은 stale 은 다음 집계(사전계산 스윕 ≤20초)가 수렴시킨다.
/// - 큐 유실(프로세스 종료)도 같은 이유로 다음 집계가 재생성/재무효화 — 감지 로그는 파생 데이터다.
/// 정적 채널인 이유: 호출부가 OeeControllerBase(파생 4개) 라 생성자 리플을 피한다(OeeChangeSignal 과 동일 규약).
/// </summary>
public sealed class NonProdWriteQueueService : BackgroundService
{
    /// <summary>집계 1회분 — 감지 목록 + 자가치유용 창/스코프(doc/25 §4.1).</summary>
    public sealed record Batch(
        IReadOnlyList<OeeNonProdDetectionLog> Entries,
        DateTime FromUtc, DateTime ToUtc,
        IReadOnlyList<string> Flows,      // 이번 패스가 실제 처리한 flow(임계 보유) — 무효화 대상 한정
        bool IncludeLineScope);           // 라인 집계 패스만 true — 라인 스코프('') 행 정리 권한

    private static readonly Channel<Batch> Queue =
        Channel.CreateBounded<Batch>(
            new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NonProdWriteQueueService> _logger;

    public NonProdWriteQueueService(IServiceScopeFactory scopeFactory, ILogger<NonProdWriteQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>조회 경로에서 호출 — 블로킹/예외 없음(가득 차면 최신 우선 유지, 유실분은 다음 집계가 재생성).</summary>
    public static void Enqueue(Batch batch) => Queue.Writer.TryWrite(batch);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await Queue.Reader.WaitToReadAsync(stoppingToken))
            {
                // 1초 배치 — 같은 틱의 다발 집계(flow별 루프)를 한 번에 flush.
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) { break; }

                var batches = new List<Batch>();
                while (Queue.Reader.TryRead(out var b))
                    batches.Add(b);
                if (batches.Count == 0) continue;

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<IOeeRepository>();
                    // 패스 마크 = UPSERT 전 시각 — 이번 flush 가 재확인한 행(lastConfirmedAt ≥ mark)은 무효화에서 제외.
                    var mark = DateTime.UtcNow;
                    var merged = batches.SelectMany(b => b.Entries).ToList();
                    if (merged.Count > 0)
                        await repo.UpsertNonProdDetectionsAsync(merged, stoppingToken);
                    // 창/스코프별 자가치유 — 같은 (창, 스코프) 중복 배치는 1회만.
                    foreach (var g in batches.GroupBy(b => (b.FromUtc, b.ToUtc, b.IncludeLineScope,
                                 FlowsKey: string.Join("", b.Flows.OrderBy(f => f, StringComparer.Ordinal)))))
                    {
                        var b = g.First();
                        var n = await repo.InvalidateStaleNonProdDetectionsAsync(
                            b.FromUtc, b.ToUtc, b.Flows, b.IncludeLineScope, mark, stoppingToken);
                        if (n > 0)
                            _logger.LogInformation(
                                "[OEE-CLASSIFY] 자동 비생산 감지 {N}건 무효화(재판정과 불일치) — window={From:o}~{To:o} lineScope={Line}",
                                n, b.FromUtc, b.ToUtc, b.IncludeLineScope);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[OEE] 비생산 감지 로그 배치 flush 실패({Count}배치) — 다음 집계가 재생성", batches.Count);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }
}
