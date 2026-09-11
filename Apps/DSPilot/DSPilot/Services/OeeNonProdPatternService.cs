// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;
using DSPilot.Models;
using DSPilot.Models.Oee;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>
/// 비생산 시간대 학습기 — 일별 샘플 투표제 (doc/22 §3.5, Phase 1 참고 표시 전용 — KPI 판정 미적용).
///
/// 목표 모델(오너 사양): "어제~14일 전, 하루마다 그날의 24시간 비생산 영역 샘플 1장 → 이동평균으로
/// 오늘의 예상 비생산 시간대를 연산". 슬롯별 값 = 비생산이었던 활동일의 비율, promoteRatio 이상만 창.
///
/// 구모델과의 차이(오염 원인 제거):
///   ① 일별 샘플 + 투표 — 하루가 아무리 엉망이어도 1표(구: 14일 union, 1건이면 창).
///   ② 스팬 페인팅 — 정지가 덮는 전 구간을 그날 샘플에 칠함(구: 시작 hour 1칸만).
///   ③ 슬롯 30분(구: 60분) + 슬롯 절반 이상 커버만 투표(경계 조각 배제).
///   ④ 미계측(수신 공백, §3.4) 구간은 그날 샘플에서 제외 — 블랙아웃이 패턴을 학습시키지 않는다.
///   ⑤ 학습 입력 = 완료 사이클 행(dspFlowHistory 유휴 사이클)만 — 장기 정지도 다음 head 가 오면 한 행으로 완결되므로
///      별도 이벤트 소스가 필요 없다(2026-09-08 doc/26, 구 무가동 이벤트 합류 폐기).
///   ⑥ 활동일(사이클 ≥1) 기준 분모, 표본 minActiveDays 미만이면 창 미성립(가짜 창 금지).
///
/// 파라미터(appsettings 오버라이드): Oee:NonProdPattern:{SlotMinutes=30, PromoteRatio=0.6,
/// MinActiveDays=3, LookbackDays=14}. 라인 레벨 결과는 AutoPatternCache 에 24h 캐시(기존 재사용).
/// </summary>
public sealed class OeeNonProdPatternService
{
    private readonly IDatabasePathResolver _pathResolver;
    private readonly OeeCommHealthService _commHealth;
    private readonly AppSettingsService _settings;
    private readonly IConfiguration _configuration;
    private readonly OeeCtStatsService _ctStats;
    private readonly ILogger<OeeNonProdPatternService> _logger;

    public OeeNonProdPatternService(
        IDatabasePathResolver pathResolver,
        OeeCommHealthService commHealth,
        AppSettingsService settings,
        IConfiguration configuration,
        HistoryMirrorService mirror,
        OeeCtStatsService ctStats,
        ILogger<OeeNonProdPatternService> logger)
    {
        _pathResolver = pathResolver;
        _commHealth = commHealth;
        _settings = settings;
        _configuration = configuration;
        _mirror = mirror;
        _ctStats = ctStats;
        _logger = logger;
    }

    private readonly HistoryMirrorService _mirror;

    private int SlotMinutes => Clamp(_configuration.GetValue<int?>("Oee:NonProdPattern:SlotMinutes") ?? 30, 5, 120);
    private double PromoteRatio => Math.Clamp(_configuration.GetValue<double?>("Oee:NonProdPattern:PromoteRatio") ?? 0.6, 0.05, 1.0);
    private int MinActiveDays => Clamp(_configuration.GetValue<int?>("Oee:NonProdPattern:MinActiveDays") ?? 3, 1, 30);
    private int LookbackDays => Clamp(_configuration.GetValue<int?>("Oee:NonProdPattern:LookbackDays") ?? 14, 2, 60);
    private static int Clamp(int v, int lo, int hi) => Math.Min(hi, Math.Max(lo, v));

    private static readonly DateTime EpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static double ToMs(DateTime dt)
        => ((dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt) - EpochUtc).TotalMilliseconds;

    /// <summary>
    /// 라인 레벨(flow=null)은 24h 캐시 사용(forceRefresh 로 즉시 재계산). thresholds 는 호출측
    /// (OeeController.ResolveCtThresholdsAsync — 14일 평균 + 수동 표준CT 오버라이드)이 공급 — 라이브 10× 규칙과 동일 임계.
    /// </summary>
    public async Task<PlannedAutoPatternDto> GetOrComputeAsync(
        string? flowName,
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample)> thresholds,
        bool forceRefresh, CancellationToken ct)
    {
        if (flowName is null && !forceRefresh)
        {
            var cache = _settings.LoadSettings().OeeManual.AutoPatternCache;
            if (cache != null && (DateTime.UtcNow - cache.ComputedAt).TotalHours < 24)
            {
                var w = cache.Windows.Select(x => new PlannedStopWindowDto(x.StartMinutes, x.EndMinutes, x.Label)).ToList();
                return new PlannedAutoPatternDto(w, cache.DataFrom, cache.DataTo, LookbackDays,
                    ActiveDays: cache.ActiveDays, PromoteRatio: PromoteRatio);
            }
        }
        return await ComputeAsync(flowName, thresholds, ct);
    }

    private async Task<PlannedAutoPatternDto> ComputeAsync(
        string? flowName,
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample)> thresholds,
        CancellationToken ct)
    {
        // 어제 자정(로컬)까지 LookbackDays — 오늘 진행중 데이터 제외(당일 제외, 오너 사양).
        var todayLocal = DateTime.Now.Date;
        var toUtc = DateTime.SpecifyKind(todayLocal, DateTimeKind.Local).ToUniversalTime();
        var fromUtc = toUtc.AddDays(-LookbackDays);

        List<string> targetFlows;
        if (!string.IsNullOrWhiteSpace(flowName))
            targetFlows = thresholds.ContainsKey(flowName) ? new List<string> { flowName } : new List<string>();
        else
            targetFlows = thresholds.Keys.Where(k => thresholds[k].AvgMs > 0).ToList();

        var stops = new List<(double S, double E)>();   // 장시간(≥10×) 정지 구간 — UTC ms, 기간 클립
        var activeDays = new SortedSet<DateTime>();     // 사이클 ≥1 인 로컬 날짜(자정)

        if (targetFlows.Count > 0)
        {
            try
            {
                await CollectIdleCycleStopsAsync(targetFlows, thresholds, fromUtc, toUtc, stops, activeDays, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OEE] non-prod pattern compute failed");
                stops.Clear();
                activeDays.Clear();
            }
        }

        // 미계측(§3.4) 차집합 — 수신 공백이 "그날 비생산이었다"로 학습되지 않게. 조회 실패(비신뢰)면 카빙 생략(보수).
        var (unmeasured, _) = await _commHealth.TryGetUnmeasuredIntervalsAsync(fromUtc, toUtc, ct);
        var merged = UnionIntervals(stops);
        if (unmeasured.Count > 0)
            merged = SubtractIntervals(merged, unmeasured);

        // 일별 샘플(활동일만) → 슬롯 투표 → 승격.
        var slotMinutes = SlotMinutes;
        var votes = new List<bool[]>();
        foreach (var day in activeDays)
        {
            var dayStartUtc = DateTime.SpecifyKind(day, DateTimeKind.Local).ToUniversalTime();
            var dayS = ToMs(dayStartUtc);
            var dayE = ToMs(dayStartUtc.AddDays(1));
            var dayWins = OeeMath.FoldIntervalsToMinuteOfDay(merged, dayS, dayE, OeeMath.LocalMinuteOfDay);
            votes.Add(OeeMath.SlotVotesFromMinuteWindows(
                dayWins.Select(w => (w.StartMinutes, w.EndMinutes)), slotMinutes));
        }
        var promoted = OeeMath.BuildNonProdPatternWindows(votes, slotMinutes, PromoteRatio, MinActiveDays);
        var windows = promoted.Select(w => new PlannedStopWindowDto(w.StartMin, w.EndMin, null)).ToList();

        if (flowName is null)
        {
            _settings.SavePlannedAutoPatternCache(new PlannedAutoPatternCache
            {
                Windows = windows.Select(w => new PlannedStopWindow
                    { StartMinutes = w.StartMinutes, EndMinutes = w.EndMinutes, Label = w.Label }).ToList(),
                ComputedAt = DateTime.UtcNow,
                DataFrom = fromUtc.ToLocalTime(),
                DataTo = toUtc.ToLocalTime(),
                ActiveDays = votes.Count,
            });
        }

        return new PlannedAutoPatternDto(windows, fromUtc.ToLocalTime(), toUtc.ToLocalTime(), LookbackDays,
            ActiveDays: votes.Count, PromoteRatio: PromoteRatio);
    }

    /// <summary>
    /// ① dspFlowHistory 미완료 유휴 사이클(mt IS NULL, ct ≥ 10×flow평균CT) → [rec−ct, rec] 구간 +
    /// ② 활동일(ct>0 사이클이 있는 로컬 날짜) 수집 — 라이브 10× 규칙(idle-cycle 분기)과 동일 판정 기준.
    /// </summary>
    private async Task CollectIdleCycleStopsAsync(
        List<string> targetFlows,
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample)> thresholds,
        DateTime fromUtc, DateTime toUtc,
        List<(double S, double E)> stops, SortedSet<DateTime> activeDays, CancellationToken ct)
    {
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return;

        var fromStr = fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var toStr = toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        // 학습창(14일)이 미러 범위 안이면 인메모리 미러에서 읽는다(같은 SQL, 밖이면 파일 폴백).
        var conn = await _mirror.TryOpenPlcReadAsync(fromUtc, layerB: true);
        if (conn is null)
        {
            conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Default Timeout=20");
            await conn.OpenAsync(ct);
        }
        await using var _ = conn;
        var exists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
        if (exists == 0) return;

        // 활동일: 대상 flow 에 사이클이 1건이라도 있는 로컬 날짜 — 투표 분모(주말/무가동 날은 표본에서 제외).
        var days = await conn.QueryAsync<string>(@"
            SELECT DISTINCT date(substr(recordedAt,1,19), 'localtime') AS d
            FROM dspFlowHistory
            WHERE recordedAt >= @From AND recordedAt < @To AND ct > 0 AND flowName IN @Flows",
            new { From = fromStr, To = toStr, Flows = targetFlows });
        foreach (var d in days)
            if (DateTime.TryParse(d, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day))
                activeDays.Add(day.Date);

        // 사용자 설정 비생산 배수(WT 축, 2026-09-09) — 라이브 판정과 동일 문턱(설정 변경 시 학습 재료도 함께 이동).
        //   경계 = OeeMath.ResolveWtNonProdBoundaryMs(중앙 WT·중앙 CT, 하한 포함), WT 기준선 미보유 flow 는 평균 CT 폴백.
        //   비교값도 판정과 같은 COALESCE(wt, ct) — 종전 "mt IS NULL 행만" 제약은 CT 축 시절 잔재(정지 후 재개 행이
        //   wt 에 정지를 담는 지금은 mt 정상·wt 폭주 행이 장기 정지의 정본)라 함께 걷어냈다. 참고 표시 전용(KPI 미적용).
        var (idleMult, nonProdMult) = _settings.LoadSettings().OeeManual.ResolveWtMultipliers();
        var wtBase = await _ctStats.ComputeWtBaselineAsync();
        foreach (var f in targetFlows)
        {
            var thr = thresholds[f].AvgMs;
            if (thr <= 0) continue;
            double longStopMs;
            if (wtBase.TryGetValue(f, out var wb) && wb.MedianCtMs > 0)
            {
                var stopB = OeeMath.ResolveWtStopBoundaryMs(wb.MedianWtMs, wb.MedianCtMs, idleMult);
                longStopMs = OeeMath.ResolveWtNonProdBoundaryMs(wb.MedianWtMs, wb.MedianCtMs, nonProdMult, stopB);
            }
            else longStopMs = thr * nonProdMult;
            if (longStopMs <= 0) continue;

            var rows = await conn.QueryAsync<(string RecordedAt, long Ct)>(@"
                SELECT recordedAt AS RecordedAt, ct AS Ct
                FROM dspFlowHistory
                WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow
                  AND ct > 0 AND COALESCE(wt, ct) >= @LongStop",
                new { From = fromStr, To = toStr, Flow = f, LongStop = longStopMs });
            foreach (var r in rows)
            {
                if (!DateTime.TryParse(r.RecordedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var rec)) continue;
                var recMs = ToMs(rec);
                stops.Add((Math.Max(ToMs(fromUtc), recMs - r.Ct), Math.Min(ToMs(toUtc), recMs)));
            }
        }
    }

    // ── 소형 구간 연산 (반열린 [S,E), ms) — OeeController.Intervals 는 private 라 최소 복제 ──

    private static List<(double S, double E)> UnionIntervals(IEnumerable<(double S, double E)> segs)
    {
        var xs = segs.Where(x => x.E > x.S).OrderBy(x => x.S).ToList();
        var res = new List<(double S, double E)>();
        foreach (var s in xs)
        {
            if (res.Count > 0 && s.S <= res[^1].E)
                res[^1] = (res[^1].S, Math.Max(res[^1].E, s.E));
            else
                res.Add(s);
        }
        return res;
    }

    private static List<(double S, double E)> SubtractIntervals(
        IEnumerable<(double S, double E)> a, IEnumerable<(double S, double E)> b)
    {
        var aa = UnionIntervals(a);
        var bb = UnionIntervals(b);
        var res = new List<(double S, double E)>();
        foreach (var seg in aa)
        {
            var cur = seg.S;
            foreach (var x in bb)
            {
                if (x.E <= cur || x.S >= seg.E) continue;
                if (x.S > cur) res.Add((cur, Math.Min(x.S, seg.E)));
                cur = Math.Max(cur, x.E);
                if (cur >= seg.E) break;
            }
            if (cur < seg.E) res.Add((cur, seg.E));
        }
        return res;
    }
}
