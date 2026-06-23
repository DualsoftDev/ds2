// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;
using DSPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>Flow 1개의 CT 통계(이상치 제외 클린사이클 기준, ms 단위).</summary>
public readonly record struct OeeCtStat(int SampleCount, int Min, int Median, int Avg, int Recommended);

/// <summary>
/// Flow별 실측 CT 통계(이상치 제외 = IsIdle 0, ct&gt;0) 단일 소스. OeeController 의 표준CT 추천 테이블
/// (/api/oee/ideal-cycle/table)과 <see cref="OeeIdealCycleAutoFillService"/>(자동 1회 기입)가 같은 공식을
/// 공유하도록 컨트롤러에서 추출했다 — 추천값과 자동기입값이 항상 일치한다.
/// Recommended = percentile 분위수(오름차순 → 작을수록 빠름 = best-demonstrated). 평균이 아니라
/// "가장 빠른 반복가능 CT"를 기준으로 삼아 Performance 가 속도손실을 정직하게 잡도록 한다(순환정의 방지).
/// </summary>
public sealed class OeeCtStatsService
{
    /// <summary>
    /// CT이상치(표준CT)가 "통계적으로 믿을 만한" 클린샘플 수의 신뢰선(doc/22 §2). 산출 자체는 ≥1 샘플이면
    /// 하되(잠정값), 이 값 미만이면 호출측이 "샘플 부족" 표시를 띄운다 — 샘플이 쌓이면 자동으로 정상화.
    /// UI 의 "≥5" 안내 문구와 단일 소스.
    /// </summary>
    public const int ConfidentMinCleanCycles = 5;

    private readonly IDatabasePathResolver _pathResolver;
    private readonly ILogger<OeeCtStatsService> _logger;

    public OeeCtStatsService(IDatabasePathResolver pathResolver, ILogger<OeeCtStatsService> logger)
    {
        _pathResolver = pathResolver;
        _logger = logger;
    }

    private sealed class CtRowRaw
    {
        public string? FlowName { get; set; }
        public long Ct { get; set; }
    }

    /// <summary>
    /// Flow별 CT 통계. flow별 최근 <paramref name="sampleLimit"/> 사이클 기준.
    /// dspFlow 의 전체 flow 를 0-샘플 항목으로라도 포함(사이클 없는 flow 도 테이블에 노출). 키는 flowName.
    /// 실패/테이블 부재 시 빈(또는 부분) 맵 반환 — 호출측은 결측을 "데이터 없음"으로 취급한다.
    /// </summary>
    public async Task<Dictionary<string, OeeCtStat>> ComputeAsync(int sampleLimit, double percentile)
    {
        var result = new Dictionary<string, OeeCtStat>(StringComparer.OrdinalIgnoreCase);
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return result;
        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();

            // 전체 flow 목록 — 사이클이 없어도 행을 노출(미설정 표준CT 식별).
            var dspFlowExists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlow'");
            if (dspFlowExists > 0)
            {
                var names = await conn.QueryAsync<string>(
                    "SELECT flowName FROM dspFlow WHERE flowName IS NOT NULL AND flowName <> ''");
                foreach (var n in names) result[n] = new OeeCtStat(0, 0, 0, 0, 0);
            }

            var histExists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (histExists == 0) return result;

            // flow별 최근 N 사이클 ct (이상치 제외). 윈도우 함수로 flow마다 최신 sampleLimit 행만.
            const string sql = @"
                SELECT flowName AS FlowName, ct AS Ct FROM (
                    SELECT flowName, ct,
                           ROW_NUMBER() OVER (PARTITION BY flowName ORDER BY recordedAt DESC) AS rn
                    FROM dspFlowHistory
                    WHERE COALESCE(IsIdle, 0) = 0 AND ct IS NOT NULL AND ct > 0
                ) WHERE rn <= @Limit";
            var raw = await conn.QueryAsync<CtRowRaw>(sql, new { Limit = sampleLimit });

            var grouped = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in raw)
            {
                if (string.IsNullOrEmpty(r.FlowName)) continue;
                if (!grouped.TryGetValue(r.FlowName, out var list)) { list = new List<int>(); grouped[r.FlowName] = list; }
                list.Add((int)r.Ct);
            }

            foreach (var (flowName, list) in grouped)
            {
                if (list.Count == 0) continue;
                list.Sort();
                var min = list[0];
                var median = list[list.Count / 2];
                var avg = (int)Math.Round(list.Average());
                var idx = Math.Clamp((int)Math.Floor(percentile / 100.0 * (list.Count - 1)), 0, list.Count - 1);
                result[flowName] = new OeeCtStat(list.Count, min, median, avg, list[idx]);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] CT stats compute failed");
            return result;
        }
    }

    /// <summary>
    /// CT이상치(표준 CT) = 최근 <paramref name="windowDays"/>일(기본 14) 클린사이클(IsIdle=0, ct&gt;0) CT의
    /// flow별 통계 — doc/22 §2. <b>AvgMs</b>=평균(비가동 판정·가용성 공용 임계, 정상상태 P≈100% 수렴),
    /// <b>P10Ms</b>=p10 분위수(=best-demonstrated 최속, 성능 P 의 선택적 기준 — "잘 돌 때 대비 속도손실"을 잡음).
    /// 둘 다 같은 14일 클린 윈도우에서 산출(apples-to-apples). 드리프트 방지 위해 RAM 산출(DB 미기입).
    /// 표본 &lt; <paramref name="minCleanCycles"/>(기본 1) 인 flow 만 맵에서 제외 = 클린샘플 0(=진짜 데이터 없음)일 때만
    /// 산출 불가. 1개라도 있으면 잠정값을 내보내고, 신뢰선(<see cref="ConfidentMinCleanCycles"/>) 미만인지는
    /// 반환 튜플의 <c>Sample</c> 로 호출측이 판단해 "샘플 부족"을 표시한다(샘플이 쌓이면 자동 정상화).
    /// p10 분위 공식은 추천 테이블/자동기입(<see cref="ComputeAsync"/>, 기본 percentile=10)과 동일하다.
    /// </summary>
    public async Task<Dictionary<string, (double AvgMs, double P10Ms, int Sample)>> ComputeCtThresholdAsync(
        int windowDays = 14, int minCleanCycles = 1)
    {
        const double p10Percentile = 10.0; // best-demonstrated 분위수 (ComputeAsync 기본값과 동일)
        var result = new Dictionary<string, (double AvgMs, double P10Ms, int Sample)>(StringComparer.OrdinalIgnoreCase);
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return result;
        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();

            var histExists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (histExists == 0) return result;

            // recordedAt 은 UTC(Z 없는 DATETIME) 문자열 — 동일 포맷 since 문자열로 비교.
            var since = DateTime.UtcNow.AddDays(-Math.Max(1, windowDays))
                .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            // avg 와 p10 을 같은 모집단에서 뽑으려면 원시 ct 가 필요(SQLite 에 percentile 내장 없음) → C# 집계.
            const string sql = @"
                SELECT flowName AS FlowName, ct AS Ct
                FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0 AND ct IS NOT NULL AND ct > 0
                  AND recordedAt >= @Since";
            var raw = await conn.QueryAsync<CtRowRaw>(sql, new { Since = since });

            var grouped = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in raw)
            {
                if (string.IsNullOrEmpty(r.FlowName)) continue;
                if (!grouped.TryGetValue(r.FlowName, out var list)) { list = new List<int>(); grouped[r.FlowName] = list; }
                list.Add((int)r.Ct);
            }

            foreach (var (flow, list) in grouped)
            {
                if (list.Count < Math.Max(1, minCleanCycles)) continue; // 클린샘플 0(또는 minClean 미만) → 산출 불가(제외)
                list.Sort();
                double avg = list.Average();
                if (avg <= 0) continue;
                var idx = Math.Clamp((int)Math.Floor(p10Percentile / 100.0 * (list.Count - 1)), 0, list.Count - 1);
                result[flow] = (avg, list[idx], list.Count);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] CT threshold (14d avg/p10) compute failed");
            return result;
        }
    }
}
