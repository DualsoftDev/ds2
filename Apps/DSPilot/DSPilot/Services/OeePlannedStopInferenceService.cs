// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using Dapper;
using DSPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>
/// 계획정지 시간대 <b>자동 감지</b> — 사용자가 계획정지 시간대를 직접 설정하지 않았을 때(폴백), DSPilot 이 이미 수집하는
/// 사이클 시계열(dspFlowHistory)에서 <b>최근 5일 패턴</b>을 학습해 "매일 같은 시각에 규칙적으로 멈추는 구간"(점심·교대 등
/// 계획정지)을 추정한다. OEE 사이클 모델(doc/22)에서 이 시간대의 비가동은 계획정지로 분류되어 가용성(A) 분모에서 제외된다.
///
/// 자동 ▸ 수동 전환: 사용자가 <see cref="Models.OeeManualSettings.PlannedStops"/> 를 1개라도 설정하면 그 값이 권위적이며
/// 본 자동 추정은 무시된다(컨트롤러가 판단). 본 서비스는 "설정 안 했을 때"의 자동값과 "설정 화면 미리보기"만 제공한다.
///
/// 알고리즘(라인 전체, 30분 슬롯 48칸): 일자별 활동 슬롯의 <b>운영 envelope</b>(그날 첫~마지막 사이클 슬롯)를 잡고,
/// envelope <b>안</b>인데 사이클이 0인 슬롯 = "운영 중 규칙적 공백" 후보. 여러 날(coverDays≥3) envelope 에 들고
/// 그중 60% 이상 날에서 공백이면 계획정지 슬롯으로 확정 → 인접 슬롯 병합해 시간대로 만든다. envelope <b>밖</b>(시업 전·
/// 종업 후 off-hours)은 계획정지로 보지 않는다 — 그건 비가동 사이클 자체가 안 생기는 비가동시간이라 별개.
///
/// 패턴: RAM-only 싱글톤 + HostedService 동일 인스턴스. DB 영구기입 금지(doc/21 §10 — 추정값을 사실처럼 박제하지 않음).
/// recordedAt 은 UTC·Z없는 "yyyy-MM-dd HH:mm:ss(.fffffff)" → substr(...,1,19)+'localtime' 로 로컬 변환(소수 가드).
/// </summary>
public sealed class OeePlannedStopInferenceService : BackgroundService
{
    private const int LookbackDays = 5;
    private const int SlotsPerDay = 48;          // 30분 슬롯
    private const int SlotMinutes = 30;
    private const int MinCoverDays = 3;          // envelope 안에 든 날이 이만큼은 돼야 패턴 인정
    private const double IdleDayRatio = 0.6;     // envelope 든 날 중 이 비율 이상에서 공백이면 계획정지

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan RecomputeInterval = TimeSpan.FromMinutes(30);

    private readonly IDatabasePathResolver _pathResolver;
    private readonly ILogger<OeePlannedStopInferenceService> _logger;

    private volatile OeePlannedStopInference _cache = OeePlannedStopInference.Empty;

    public OeePlannedStopInferenceService(IDatabasePathResolver pathResolver, ILogger<OeePlannedStopInferenceService> logger)
    {
        _pathResolver = pathResolver;
        _logger = logger;
    }

    /// <summary>최신 자동 추정(라인 전체). 미산출/표본부족이면 Available=false.</summary>
    public OeePlannedStopInference Get() => _cache;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OeePlannedStopInferenceService starting (lookback={Days}d, recompute={Min}m, RAM-only)",
            LookbackDays, RecomputeInterval.TotalMinutes);
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RecomputeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "[OEE-plannedstop] recompute failed"); }
                try { await Task.Delay(RecomputeInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private sealed class SlotRow { public string? D { get; set; } public int Slot { get; set; } public long Cnt { get; set; } }

    private async Task RecomputeAsync()
    {
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return;

        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
        await conn.OpenAsync();
        var exists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
        if (exists == 0) { _cache = OeePlannedStopInference.Empty; return; }

        var fromStr = DateTime.UtcNow.AddDays(-LookbackDays).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        const string sql = @"
            SELECT strftime('%Y-%m-%d', substr(recordedAt,1,19), 'localtime') AS D,
                   (CAST(strftime('%H', substr(recordedAt,1,19), 'localtime') AS INTEGER) * 2
                    + CAST(strftime('%M', substr(recordedAt,1,19), 'localtime') AS INTEGER) / 30) AS Slot,
                   COUNT(*) AS Cnt
            FROM dspFlowHistory
            WHERE COALESCE(IsIdle,0) = 0 AND recordedAt >= @From
            GROUP BY D, Slot";
        var rows = (await conn.QueryAsync<SlotRow>(sql, new { From = fromStr })).ToList();

        // 일자별 활동 슬롯 집합.
        var byDay = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.D) || r.Slot is < 0 or >= SlotsPerDay) continue;
            if (!byDay.TryGetValue(r.D!, out var set)) { set = new HashSet<int>(); byDay[r.D!] = set; }
            if (r.Cnt > 0) set.Add(r.Slot);
        }

        var sampleDays = byDay.Count;
        if (sampleDays < MinCoverDays) { _cache = OeePlannedStopInference.Empty with { SampleDays = sampleDays }; return; }

        // 슬롯별: envelope 든 날 수(coverDays) / 그중 공백인 날 수(idleDays).
        var cover = new int[SlotsPerDay];
        var idle = new int[SlotsPerDay];
        foreach (var (_, active) in byDay)
        {
            if (active.Count == 0) continue;
            int min = SlotsPerDay, max = -1;
            foreach (var s in active) { if (s < min) min = s; if (s > max) max = s; }
            for (int s = min; s <= max; s++)
            {
                cover[s]++;
                if (!active.Contains(s)) idle[s]++;
            }
        }

        // 계획정지 슬롯 → 인접 병합 → 시간대.
        var planned = new bool[SlotsPerDay];
        for (int s = 0; s < SlotsPerDay; s++)
            planned[s] = cover[s] >= MinCoverDays && idle[s] * 1.0 >= IdleDayRatio * cover[s] && idle[s] >= 2;

        var windows = new List<PlannedStopRange>();
        int run = -1;
        for (int s = 0; s <= SlotsPerDay; s++)
        {
            var on = s < SlotsPerDay && planned[s];
            if (on && run < 0) run = s;
            else if (!on && run >= 0)
            {
                windows.Add(new PlannedStopRange(run * SlotMinutes, s * SlotMinutes));
                run = -1;
            }
        }

        _cache = new OeePlannedStopInference(windows.Count > 0, windows, sampleDays);
        _logger.LogInformation("[OEE-plannedstop] 계획정지 자동감지 갱신: 표본 {Days}일, 시간대 {N}개", sampleDays, windows.Count);
    }
}

/// <summary>계획정지 시간대 한 칸(로컬 자정 기준 분, [Start, End)).</summary>
public readonly record struct PlannedStopRange(int StartMinutes, int EndMinutes);

/// <summary>계획정지 5일 자동감지 결과(라인 전체, RAM only).</summary>
public sealed record OeePlannedStopInference(bool Available, IReadOnlyList<PlannedStopRange> Windows, int SampleDays)
{
    public static readonly OeePlannedStopInference Empty = new(false, new List<PlannedStopRange>(), 0);
}
