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
    private readonly HistoryMirrorService _mirror;
    private readonly DsProjectService _project;
    private readonly AppSettingsService _settings;
    private readonly ILogger<OeeCtStatsService> _logger;

    // ── TTL 캐시 + single-flight ────────────────────────────────────────────
    // 세 통계 모두 dspFlowHistory 창 스캔이라 요청 단가가 테이블 크기에 비례하고, OEE 엔드포인트
    // 5종 × 10초 폴링 × 동접 탭 수만큼 동일 계산이 반복된다. 14일 통계라 30초 staleness 는 무해
    // — 동시/연속 호출을 계산 1회로 코얼레싱한다. 호출측이 반환 딕셔너리를 mutate 하므로
    // (ResolveCtThresholdsAsync 의 TryAdd/인덱서 덮어쓰기) 캐시 원본이 아닌 복사본을 반환할 것.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ExpiresUtc, object Value)> _cache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<object>>> _inflight = new();

    public OeeCtStatsService(IDatabasePathResolver pathResolver, HistoryMirrorService mirror,
        DsProjectService project, AppSettingsService settings, ILogger<OeeCtStatsService> logger)
    {
        _pathResolver = pathResolver;
        _mirror = mirror;
        _project = project;
        _settings = settings;
        _logger = logger;
    }

    private async Task<Dictionary<string, T>> GetOrComputeCachedAsync<T>(
        string key, Func<Task<Dictionary<string, T>>> factory)
    {
        if (_cache.TryGetValue(key, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
            return new Dictionary<string, T>((Dictionary<string, T>)hit.Value, StringComparer.OrdinalIgnoreCase);

        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<object>>(async () =>
        {
            var value = await factory().ConfigureAwait(false);
            _cache[key] = (DateTime.UtcNow.Add(CacheTtl), value);
            // 키 공간은 호출 파라미터 조합(수 개)뿐이지만 excludeUntil 이 날짜 경계로 바뀌므로 만료분만 정리.
            if (_cache.Count > 64)
                foreach (var stale in _cache.Where(e => e.Value.ExpiresUtc <= DateTime.UtcNow).ToList())
                    _cache.TryRemove(stale.Key, out _);
            return value;
        }));

        try
        {
            var computed = (Dictionary<string, T>)await lazy.Value.ConfigureAwait(false);
            return new Dictionary<string, T>(computed, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private sealed class CtRowRaw
    {
        public string? FlowName { get; set; }
        public string? BranchName { get; set; } // 사이클 분기 라벨 — branchView 임계에서만 소비
        public long Ct { get; set; }
        public double AgeDays { get; set; } // julianday('now') - julianday(recordedAt), 가중 감쇠 산출용
    }

    /// <summary>
    /// Flow별 CT 통계. flow별 최근 <paramref name="sampleLimit"/> 사이클 기준.
    /// dspFlow 의 전체 flow 를 0-샘플 항목으로라도 포함(사이클 없는 flow 도 테이블에 노출). 키는 flowName.
    /// 실패/테이블 부재 시 빈(또는 부분) 맵 반환 — 호출측은 결측을 "데이터 없음"으로 취급한다.
    /// </summary>
    public Task<Dictionary<string, OeeCtStat>> ComputeAsync(int sampleLimit, double percentile)
        => GetOrComputeCachedAsync($"compute|{sampleLimit}|{percentile}",
            () => ComputeCoreAsync(sampleLimit, percentile));

    private async Task<Dictionary<string, OeeCtStat>> ComputeCoreAsync(int sampleLimit, double percentile)
    {
        var result = new Dictionary<string, OeeCtStat>(StringComparer.OrdinalIgnoreCase);
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return result;
        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();

            // 현재 AASX 에 없는 flow 는 숨긴다 — dspFlow/dspFlowHistory 는 UPSERT 누적이라 예전 모델의
            // 설비가 남아 있고(부팅 경로에 prune 없음), 그게 표준CT 테이블/OEE 설비축에 유령 행으로 뜬다.
            // null = 모델 미로드 → 필터 비활성(전량 숨김 방지). 삭제는 사용자의 '오래된 데이터 삭제'만.
            var modelFlows = _project.GetModelFlowNames();

            // 전체 flow 목록 — 사이클이 없어도 행을 노출(미설정 표준CT 식별).
            var dspFlowExists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlow'");
            if (dspFlowExists > 0)
            {
                var names = await conn.QueryAsync<string>(
                    "SELECT flowName FROM dspFlow WHERE flowName IS NOT NULL AND flowName <> ''");
                foreach (var n in names)
                {
                    if (modelFlows is not null && !modelFlows.Contains(n)) continue;
                    result[n] = new OeeCtStat(0, 0, 0, 0, 0);
                }
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
                if (modelFlows is not null && !modelFlows.Contains(r.FlowName)) continue;
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
    /// <paramref name="excludeUntilUtc"/>가 지정되면 기준 윈도우 상한을 해당 UTC 시각으로 제한해
    /// 당일 사이클이 자기 기준에 포함되는 순환을 줄인다(오늘 제외 = DateTime.Today.ToUniversalTime() 전달).
    /// <paramref name="decayHalfLifeDays"/>가 지정되면 오래된 사이클일수록 가중치를 높이는 감쇠 가중 평균을 적용한다.
    /// weight(age) = exp(age × ln2 / halfLife) — age가 클수록(오래될수록) 가중치 증가 → 최근 자기참조순환 영향 감소.
    /// 가중 p10 도 같은 가중치를 적용한 누적분위로 산출한다.
    /// </summary>
    /// <summary>
    /// flow별 클린 gap 중앙값 gap'(ms) — doc/23 §4. gap = WT = ct − mt(완료→다음 가동 간격, CT=MT+WT 왕복 보존).
    /// 최근 <paramref name="windowDays"/>일(기본 14) 클린사이클(IsIdle=0, ct&gt;0, mt 있음)만 — IsIdle 이 CT(주기)
    /// 이상치를 이미 제외하므로 정지를 머금은 사이클이 gap' 을 끌어올리는 오염(threshold creep)이 없다.
    /// <b>가중 없음</b>(CT 임계의 반대가중은 성능 P 자기참조 방지용 — 비가동 분류엔 불필요, 정지 gap 은 정상 gap 과
    /// 자릿수가 달라 신호가 압도적)·평균 대신 <b>중앙값</b>(미필터 긴 gap 하나에 안 끌려감).
    /// <paramref name="excludeUntilUtc"/>로 오늘 제외(자기참조 방지) — CT 임계와 동일 컨벤션.
    /// 표본 0 인 flow 는 맵에서 제외(호출측이 폴백 체인 ②③으로 처리).
    /// </summary>
    /// <summary>
    /// flow별 <b>동작 비중</b> = Σmt ÷ Σct (클린 사이클, 14일). 성능 P 의 손실을 동작(MT)/대기(WT)로 가르는 데 쓴다.
    /// <para>표준MT·표준WT 를 각각 평균으로 따로 뽑지 않고 <b>비중</b>으로 두는 이유: 표준CT 는 감쇠 가중 평균이라
    /// 가중 없는 MT/WT 평균과 더하면 표준CT 와 어긋난다. 비중을 쓰면 표준MT = 표준CT × 비중,
    /// 표준WT = 표준CT × (1−비중) 이 되어 <b>표준MT + 표준WT = 표준CT 가 항등적으로 성립</b>한다.</para>
    /// </summary>
    public Task<Dictionary<string, double>> ComputeMtRatioAsync(
        int windowDays = 14, DateTime? excludeUntilUtc = null)
        => GetOrComputeCachedAsync($"mtratio|{windowDays}|{excludeUntilUtc?.ToUniversalTime().Ticks}",
            () => ComputeMtRatioCoreAsync(windowDays, excludeUntilUtc));

    private async Task<Dictionary<string, double>> ComputeMtRatioCoreAsync(int windowDays, DateTime? excludeUntilUtc)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return result;
        try
        {
            var fromUtc = DateTime.UtcNow.AddDays(-windowDays);
            var p = new DynamicParameters();
            p.Add("From", fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            var until = "";
            if (excludeUntilUtc is DateTime ex)
            {
                until = " AND recordedAt < @Until ";
                p.Add("Until", ex.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            }
            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return result;
            var rows = await conn.QueryAsync<(string? FlowName, double SumMt, double SumCt)>($@"
                SELECT flowName AS FlowName, COALESCE(SUM(mt),0) AS SumMt, COALESCE(SUM(ct),0) AS SumCt
                FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0 AND ct IS NOT NULL AND ct > 0 AND mt IS NOT NULL AND mt >= 0
                  AND recordedAt >= @From {until}
                GROUP BY flowName", p);
            // 유령 flow 제외 — 호출측이 flow 미지정 시 ratios.Values.Average() 로 라인 평균을 내므로
            // 예전 모델 설비가 섞이면 표준MT/WT 분해가 조용히 틀어진다.
            var modelFlows = _project.GetModelFlowNames();
            foreach (var (flow, sumMt, sumCt) in rows)
                if (!string.IsNullOrWhiteSpace(flow) && sumCt > 0
                    && (modelFlows is null || modelFlows.Contains(flow)))
                    result[flow] = Math.Clamp(sumMt / sumCt, 0, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] 동작 비중(Σmt/Σct) 산출 실패");
        }
        return result;
    }

    public Task<Dictionary<string, (double MedianMs, int Sample)>> ComputeGapMedianAsync(
        int windowDays = 14, DateTime? excludeUntilUtc = null)
        => GetOrComputeCachedAsync($"gap|{windowDays}|{excludeUntilUtc?.ToUniversalTime().Ticks}",
            () => ComputeGapMedianCoreAsync(windowDays, excludeUntilUtc));

    private async Task<Dictionary<string, (double MedianMs, int Sample)>> ComputeGapMedianCoreAsync(
        int windowDays, DateTime? excludeUntilUtc)
    {
        var result = new Dictionary<string, (double MedianMs, int Sample)>(StringComparer.OrdinalIgnoreCase);
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return result;
        try
        {
            // 14일 창 스캔 — 미러 범위 안이면 인메모리 미러에서(같은 SQL, 밖/미준비면 파일 폴백).
            var conn = await _mirror.TryOpenPlcReadAsync(DateTime.UtcNow.AddDays(-Math.Max(1, windowDays)), layerB: true);
            if (conn is null)
            {
                conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
                await conn.OpenAsync();
            }
            await using var _ = conn;

            var histExists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (histExists == 0) return result;

            var since = DateTime.UtcNow.AddDays(-Math.Max(1, windowDays))
                .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            string? until = excludeUntilUtc.HasValue
                ? excludeUntilUtc.Value.ToUniversalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : null;

            // gap = ct − mt. mt 없는(미완료) 사이클은 gap 정의 불가 → 제외. ct ≥ mt 가드(비정상 행 방어).
            const string sql = @"
                SELECT flowName AS FlowName, (ct - mt) AS Ct, 0.0 AS AgeDays
                FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0 AND ct IS NOT NULL AND ct > 0
                  AND mt IS NOT NULL AND ct >= mt
                  AND recordedAt >= @Since
                  AND (@Until IS NULL OR recordedAt < @Until)";
            var raw = await conn.QueryAsync<CtRowRaw>(sql, new { Since = since, Until = until });

            var modelFlows = _project.GetModelFlowNames();   // 유령 flow 제외(null=필터 비활성)
            var grouped = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in raw)
            {
                if (string.IsNullOrEmpty(r.FlowName)) continue;
                if (modelFlows is not null && !modelFlows.Contains(r.FlowName)) continue;
                if (!grouped.TryGetValue(r.FlowName, out var list)) { list = new(); grouped[r.FlowName] = list; }
                list.Add((int)r.Ct);
            }

            foreach (var (flow, list) in grouped)
            {
                if (list.Count == 0) continue;
                list.Sort();
                result[flow] = (list[list.Count / 2], list.Count);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] gap median (14d clean WT) compute failed");
            return result;
        }
    }

    /// <summary>
    /// flow별 CT 로버스트 통계 — <b>중앙값</b>과 <b>p99</b>(ms). 자동 '가동중' 박제 해제 경계
    /// (<see cref="OeeMath.ResolveAutoAbandonBoundaryMs"/>) 전용 소스다.
    /// <para>gap' 산출(<see cref="ComputeGapMedianAsync"/>)과 같은 14일 클린 창·같은 인덱스 경로를 쓰되
    /// gap(ct−mt) 이 아니라 CT(주기) 자체를 본다 — 워치독이 재는 것이 "래치가 열린 채 흐른 시간"이라
    /// CT 분포와 같은 축이기 때문. 평균(AvgMs)은 정지를 머금은 사이클에 끌려가므로 여기서는 쓰지 않는다.</para>
    /// </summary>
    public Task<Dictionary<string, (double MedianMs, double P99Ms, int Sample)>> ComputeCtRobustAsync(
        int windowDays = 14)
        => GetOrComputeCachedAsync($"ctrobust|{windowDays}",
            () => ComputeCtRobustCoreAsync(windowDays));

    private async Task<Dictionary<string, (double MedianMs, double P99Ms, int Sample)>> ComputeCtRobustCoreAsync(
        int windowDays)
    {
        var result = new Dictionary<string, (double MedianMs, double P99Ms, int Sample)>(StringComparer.OrdinalIgnoreCase);
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return result;
        try
        {
            var conn = await _mirror.TryOpenPlcReadAsync(DateTime.UtcNow.AddDays(-Math.Max(1, windowDays)), layerB: true);
            if (conn is null)
            {
                conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
                await conn.OpenAsync();
            }
            await using var _ = conn;

            var histExists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (histExists == 0) return result;

            var since = DateTime.UtcNow.AddDays(-Math.Max(1, windowDays))
                .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            const string sql = @"
                SELECT flowName AS FlowName, ct AS Ct, 0.0 AS AgeDays
                FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0 AND ct IS NOT NULL AND ct > 0
                  AND recordedAt >= @Since";
            var raw = await conn.QueryAsync<CtRowRaw>(sql, new { Since = since });

            var modelFlows = _project.GetModelFlowNames();   // 유령 flow 제외(null=필터 비활성)
            var grouped = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in raw)
            {
                if (string.IsNullOrEmpty(r.FlowName)) continue;
                if (modelFlows is not null && !modelFlows.Contains(r.FlowName)) continue;
                if (!grouped.TryGetValue(r.FlowName, out var list)) { list = new(); grouped[r.FlowName] = list; }
                list.Add((int)r.Ct);
            }

            foreach (var (flow, list) in grouped)
            {
                if (list.Count == 0) continue;
                list.Sort();
                var median = list[list.Count / 2];
                // p99 = ComputeCoreAsync 의 분위 공식과 동일(floor 인덱스, 경계 clamp).
                var idx = Math.Clamp((int)Math.Floor(0.99 * (list.Count - 1)), 0, list.Count - 1);
                result[flow] = (median, list[idx], list.Count);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] CT robust stats (14d median/p99) compute failed");
            return result;
        }
    }

    /// <summary>
    /// flow별 14일 <b>평균(중앙값) MT</b> — 고장 유발자 판별(<see cref="Models.OeeManualSettings.FaultMtMultiplier"/>)의
    /// 기준값. CT 임계와 같은 창·같은 클린사이클(IsIdle=0) 모집단을 쓰되 열만 mt 다.
    ///
    /// <para>평균이 아니라 <b>중앙값</b>을 쓴다 — MT 는 정지를 머금은 사이클 하나에 수십 배로 끌려가서
    /// (실측: 조립 평상시 4.7초 vs 정지 사이클 216초) 평균을 쓰면 기준 자체가 오염돼 다음 고장을 못 잡는다.
    /// CT 임계가 평균을 쓰는 것과 다른 선택이며, 이유는 MT 의 이상치 진폭이 CT 보다 훨씬 크기 때문이다.</para>
    ///
    /// <para>표본이 없거나 0 인 flow 는 맵에서 빠진다 — 호출측은 "기준 없음 = 유발자 판별 불가"로 다뤄야 한다
    /// (기준 미보유 flow 를 0 으로 두면 모든 정지가 과주행으로 잡힌다).</para>
    /// </summary>
    public Task<Dictionary<string, double>> ComputeMtThresholdAsync(int windowDays = 14)
        => GetOrComputeCachedAsync($"mtthr|{windowDays}", () => ComputeMtThresholdCoreAsync(windowDays));

    private async Task<Dictionary<string, double>> ComputeMtThresholdCoreAsync(int windowDays)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return result;
        try
        {
            var conn = await _mirror.TryOpenPlcReadAsync(DateTime.UtcNow.AddDays(-Math.Max(1, windowDays)), layerB: true);
            if (conn is null)
            {
                conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
                await conn.OpenAsync();
            }
            await using var _ = conn;

            var histExists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (histExists == 0) return result;

            var since = DateTime.UtcNow.AddDays(-Math.Max(1, windowDays))
                .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            var raw = await conn.QueryAsync<(string? FlowName, long Mt)>(@"
                SELECT flowName AS FlowName, mt AS Mt
                FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0 AND mt IS NOT NULL AND mt > 0
                  AND recordedAt >= @Since", new { Since = since });

            var modelFlows = _project.GetModelFlowNames();
            var grouped = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in raw)
            {
                if (string.IsNullOrEmpty(r.FlowName)) continue;
                if (modelFlows is not null && !modelFlows.Contains(r.FlowName)) continue;
                if (!grouped.TryGetValue(r.FlowName, out var l)) grouped[r.FlowName] = l = new List<long>();
                l.Add(r.Mt);
            }
            foreach (var (flow, list) in grouped)
            {
                if (list.Count == 0) continue;
                list.Sort();
                var med = list.Count % 2 == 1
                    ? (double)list[list.Count / 2]
                    : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2.0;
                if (med > 0) result[flow] = med;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OEE] 평균 MT 산출 실패 — 유발자 판별 비활성");
        }
        return result;
    }

    /// <param name="branchView">
    /// true = 분기 뷰(설비효율/OEE 열거용): 분기 활성 flow 는 부모 항목 대신 "부모_분기" 가상 항목으로
    /// 분해(통계도 그 분기 라벨 행만으로 산출, 미분류(NULL)는 제외). false = 부모 뷰(TEEP/기존): 종전과
    /// 완전 동일 — 분기 라벨과 무관하게 flowName 단위로 집계(ct 축이 부모 의미라 수치도 불변).
    /// </param>
    public Task<Dictionary<string, (double AvgMs, double P10Ms, int Sample)>> ComputeCtThresholdAsync(
        int windowDays = 14, int minCleanCycles = 1, DateTime? excludeUntilUtc = null, double? decayHalfLifeDays = null,
        bool branchView = false)
        => GetOrComputeCachedAsync(
            $"thr|{windowDays}|{minCleanCycles}|{excludeUntilUtc?.ToUniversalTime().Ticks}|{decayHalfLifeDays}|{(branchView ? "b" : "p")}",
            () => ComputeCtThresholdCoreAsync(windowDays, minCleanCycles, excludeUntilUtc, decayHalfLifeDays, branchView));

    private async Task<Dictionary<string, (double AvgMs, double P10Ms, int Sample)>> ComputeCtThresholdCoreAsync(
        int windowDays, int minCleanCycles, DateTime? excludeUntilUtc, double? decayHalfLifeDays, bool branchView)
    {
        const double p10Percentile = 10.0; // best-demonstrated 분위수 (ComputeAsync 기본값과 동일)
        var result = new Dictionary<string, (double AvgMs, double P10Ms, int Sample)>(StringComparer.OrdinalIgnoreCase);
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!File.Exists(dbPath)) return result;
        try
        {
            // 14일 창 스캔 — 미러 범위 안이면 인메모리 미러에서(같은 SQL, 밖/미준비면 파일 폴백).
            var conn = await _mirror.TryOpenPlcReadAsync(DateTime.UtcNow.AddDays(-Math.Max(1, windowDays)), layerB: true);
            if (conn is null)
            {
                conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
                await conn.OpenAsync();
            }
            await using var _ = conn;

            var histExists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (histExists == 0) return result;

            // recordedAt 은 UTC(Z 없는 DATETIME) 문자열 — 동일 포맷 since/until 문자열로 비교.
            var since = DateTime.UtcNow.AddDays(-Math.Max(1, windowDays))
                .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            string? until = excludeUntilUtc.HasValue
                ? excludeUntilUtc.Value.ToUniversalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : null;

            // AgeDays: julianday 차이로 연령(일수) 계산 — 가중 감쇠 시 사용, 미사용 시에도 비용 미미.
            // branchName 은 옛 DB/미러에 컬럼이 없을 수 있어 폴백 쿼리를 준비한다(EnsureColumn 이전 파일).
            const string sql = @"
                SELECT flowName AS FlowName, branchName AS BranchName, ct AS Ct,
                       (julianday('now') - julianday(recordedAt)) AS AgeDays
                FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0 AND ct IS NOT NULL AND ct > 0
                  AND recordedAt >= @Since
                  AND (@Until IS NULL OR recordedAt < @Until)";
            const string sqlNoBranch = @"
                SELECT flowName AS FlowName, NULL AS BranchName, ct AS Ct,
                       (julianday('now') - julianday(recordedAt)) AS AgeDays
                FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0 AND ct IS NOT NULL AND ct > 0
                  AND recordedAt >= @Since
                  AND (@Until IS NULL OR recordedAt < @Until)";
            IEnumerable<CtRowRaw> raw;
            try { raw = await conn.QueryAsync<CtRowRaw>(sql, new { Since = since, Until = until }); }
            catch (SqliteException) { raw = await conn.QueryAsync<CtRowRaw>(sqlNoBranch, new { Since = since, Until = until }); }

            var ln2 = Math.Log(2);
            // 현재 AASX 에 없는 flow 제외 — 이 맵이 OEE/TEEP 의 설비 모집단(targetFlows) SSOT 라서,
            // 여기서 걸러야 매트릭스 축·요약·랭킹에서 유령 설비가 한 번에 사라진다. null=필터 비활성.
            var modelFlows = _project.GetModelFlowNames();
            // 분기 뷰 — 분기 활성 부모는 "부모_분기" 가상 키로 분해. 미분류(라벨 NULL) 행은 통계서 제외
            // (분기 어디에도 속하지 않는 사이클이 임계를 오염시키지 않게 — 무결성 카드가 따로 계수).
            var branchedParents = branchView ? _settings.GetBranchedParentFlows() : null;
            var grouped = new Dictionary<string, List<(int Ct, double Weight)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in raw)
            {
                if (string.IsNullOrEmpty(r.FlowName)) continue;
                if (modelFlows is not null && !modelFlows.Contains(r.FlowName)) continue;
                var key = r.FlowName;
                if (branchedParents is not null && branchedParents.Contains(r.FlowName))
                {
                    if (string.IsNullOrEmpty(r.BranchName)) continue; // 미분류 — 분기 임계에 미기여
                    key = AppSettingsService.ComposeBranchFlowName(r.FlowName, r.BranchName);
                }
                // 감쇠 가중치: 오래될수록(AgeDays 클수록) weight 증가 → 최근 사이클의 기준 기여 감소.
                double weight = decayHalfLifeDays is double half && half > 0
                    ? Math.Exp(Math.Max(0, r.AgeDays) * ln2 / half)
                    : 1.0;
                if (!grouped.TryGetValue(key, out var list)) { list = new(); grouped[key] = list; }
                list.Add(((int)r.Ct, weight));
            }

            foreach (var (flow, list) in grouped)
            {
                if (list.Count < Math.Max(1, minCleanCycles)) continue; // 클린샘플 0(또는 minClean 미만) → 산출 불가(제외)

                double totalWeight = list.Sum(x => x.Weight);
                if (totalWeight <= 0) continue;

                // 가중 평균
                double avg = list.Sum(x => x.Ct * x.Weight) / totalWeight;
                if (avg <= 0) continue;

                // 가중 p10: CT 오름차순 정렬 후 누적 가중치가 10% 지점인 값
                var sorted = list.OrderBy(x => x.Ct).ToList();
                double target = totalWeight * (p10Percentile / 100.0);
                double cum = 0;
                int p10 = sorted[0].Ct;
                foreach (var (ct, w) in sorted)
                {
                    cum += w;
                    p10 = ct;
                    if (cum >= target) break;
                }

                result[flow] = (avg, p10, list.Count);
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
