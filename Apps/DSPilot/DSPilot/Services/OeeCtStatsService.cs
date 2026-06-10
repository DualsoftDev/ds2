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
}
