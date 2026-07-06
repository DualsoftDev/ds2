// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models;
using DSPilot.Models.Oee;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace DSPilot.Controllers;

/// <summary>
/// OEE 컨트롤러 공통 base — 4개 도메인 컨트롤러(Metrics/Downtime/Production/PlannedStops)가 상속.
/// 공유 DI 의존성 + 모든 계산 헬퍼가 여기에 집중됨.
/// </summary>
public abstract class OeeControllerBase : ControllerBase
{
    protected readonly IOeeRepository _repo;
    protected readonly AppSettingsService _settings;
    protected readonly DsProjectService _project;
    protected readonly IDatabasePathResolver _pathResolver;
    protected readonly OeeCtStatsService _ctStats;
    protected readonly OeeAutoShiftInferenceService _shiftInfer;
    protected readonly OeeCommHealthService _commHealth;
    protected readonly OeeNonProdPatternService _nonProdPattern;
    protected readonly ILogger _logger;

    protected OeeControllerBase(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        OeeCtStatsService ctStats,
        OeeAutoShiftInferenceService shiftInfer,
        OeeCommHealthService commHealth,
        OeeNonProdPatternService nonProdPattern,
        ILogger logger)
    {
        _repo = repo;
        _settings = settings;
        _project = project;
        _pathResolver = pathResolver;
        _ctStats = ctStats;
        _shiftInfer = shiftInfer;
        _commHealth = commHealth;
        _nonProdPattern = nonProdPattern;
        _logger = logger;
    }

    // ── 파일명 정제 ──────────────────────────────────────────────────────────

    protected static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ── 정지 단서 조인 ────────────────────────────────────────────────────────

    /// <summary>
    /// 정지 행 [startAt, endAt|now] 에 시간이 겹치는 abnormal/usertag 점 이벤트를 단서로 붙인다(표시 전용).
    /// abnormal = valueType='Abnormal' AND matchOp='AbnormalDetect'(matchValue=Kind), usertag = logLevel='Error' 일반 행.
    /// userTagAlertLog 는 flowName 컬럼이 없어 abnormal 은 tagAddress 첫 경로 세그먼트(FLOW), 그 외는 systemName 으로 스코프 매칭.
    /// ★건수·길이·MTBF 에는 절대 반영하지 않는다 — Downtime/Summary 의 집계는 oeeDowntimeEvent 만 본다(doc/21 §4 정직성).
    /// </summary>
    protected async Task<List<OeeDowntimeDto>> AttachCluesAsync(
        IReadOnlyList<OeeDowntimeDto> rows, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var list = rows.ToList();
        if (list.Count == 0) return list;
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return list;

        var clues = new List<(string? Flow, string? System, DateTime At, string Label, string Src)>();
        try
        {
            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='userTagAlertLog'");
            if (exists == 0) return list;

            var endBound = toUtc > DateTime.UtcNow ? toUtc : DateTime.UtcNow;
            const string sql = @"
                SELECT occurredAt AS OccurredAt, systemName AS SystemName, name AS Name,
                       tagAddress AS TagAddress, valueType AS ValueType, matchOp AS MatchOp, matchValue AS MatchValue
                FROM userTagAlertLog
                WHERE occurredAt >= @From AND occurredAt <= @To
                  AND ((matchOp = 'AbnormalDetect' AND valueType = 'Abnormal') OR logLevel = 'Error')";
            var alerts = await conn.QueryAsync<AlertRow>(sql, new
            {
                From = SqliteDateTimeHelpers.ToSqliteUtcString(fromUtc),
                To = SqliteDateTimeHelpers.ToSqliteUtcString(endBound),
            });
            foreach (var a in alerts)
            {
                var at = SqliteDateTimeHelpers.FromSqliteUtcString(a.OccurredAt);
                if (at is null) continue;
                var isAbn = string.Equals(a.MatchOp, "AbnormalDetect", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(a.ValueType, "Abnormal", StringComparison.OrdinalIgnoreCase);
                string? cFlow = null;
                if (isAbn && !string.IsNullOrEmpty(a.TagAddress))
                {
                    var ix = a.TagAddress.IndexOf(" / ", StringComparison.Ordinal);
                    cFlow = ix > 0 ? a.TagAddress[..ix].Trim() : null;
                }
                var label = isAbn
                    ? AbnormalKindLabel(a.MatchValue)
                    : (string.IsNullOrWhiteSpace(a.Name) ? "이상 신호" : a.Name!.Trim());
                clues.Add((cFlow, a.SystemName, at.Value, label, isAbn ? "abnormal" : "usertag"));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[OEE] downtime clue join failed"); return list; }

        if (clues.Count == 0) return list;
        var nowLocal = DateTime.Now;
        for (var idx = 0; idx < list.Count; idx++)
        {
            var d = list[idx];
            var spanEnd = d.EndAt ?? nowLocal;
            (string Label, string Src)? best = null;
            var bestAt = DateTime.MinValue;
            foreach (var c in clues)
            {
                if (c.At < d.StartAt || c.At > spanEnd) continue;
                var scope = c.Flow is not null
                    ? string.Equals(c.Flow, d.FlowName, StringComparison.OrdinalIgnoreCase)
                    : (string.Equals(c.System, d.SystemName, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(c.System, d.FlowName, StringComparison.OrdinalIgnoreCase));
                if (!scope) continue;
                if (c.At >= bestAt) { bestAt = c.At; best = (c.Label, c.Src); }
            }
            if (best is not null)
                list[idx] = d with { Clue = new OeeDowntimeClue(best.Value.Label, best.Value.Src) };
        }
        return list;
    }

    private static string AbnormalKindLabel(string? kind) => kind switch
    {
        "ActionOver" => "동작지연",
        "ActionUnder" => "동작빠름",
        "SensorShort" => "조기완료",
        "SensorOpen" => "센서끊김",
        _ => string.IsNullOrWhiteSpace(kind) ? "이상감지" : kind!,
    };

    private sealed class AlertRow
    {
        public string? OccurredAt { get; set; }
        public string? SystemName { get; set; }
        public string? Name { get; set; }
        public string? TagAddress { get; set; }
        public string? ValueType { get; set; }
        public string? MatchOp { get; set; }
        public string? MatchValue { get; set; }
    }

    // ── 시프트 기반 OEE 산출 ─────────────────────────────────────────────────

    protected async Task<OeeShiftSummaryDto> BuildShiftSummaryAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var periodMs = Math.Max(0, (toUtc - fromUtc).TotalMilliseconds);
        var shift = _settings.LoadSettings().Shift;
        var scheduled = BuildScheduledIntervals(shift, fromUtc, toUtc);

        var av = await ComputeShiftAvailabilityAsync(flowName, fromUtc, toUtc, scheduled, ct);
        var (failureDurationMs, failureCount) = await _repo.GetFailureAggregateAsync(fromUtc, toUtc, flowName, ct);
        int? totalCount = await CountFlowHistoryAsync(flowName, fromUtc, toUtc);
        var (_, _, prodReject, hasReject) =
            await _repo.QueryProductionAsync(fromUtc.ToLocalTime(), toUtc.ToLocalTime(), flowName, ct);

        double? availability = null;
        string? availNote;
        if (av.ScheduledMs <= 0)
            availNote = "조회기간이 시프트 창과 겹치지 않음 — 시프트(Start/End) 설정을 확인하세요.";
        else if (av.PlannedProductionMs <= 0)
            availNote = "계획생산시간(PPT) 0 — 계획정지(시프트 예외)가 시프트 전체를 덮고 있습니다.";
        else
        {
            availability = Math.Clamp(av.RunTimeMs / av.PlannedProductionMs, 0.0, 1.0);
            availNote = "가동시간 ÷ 계획생산시간(PPT). PPT = 시프트 ∩ 기간 − 계획정지.";
        }

        var (idealCT, idealCtSource) = ResolveIdealCycle(flowName);
        double? performance = null;
        string? perfNote;
        if (string.IsNullOrWhiteSpace(flowName))
        {
            (performance, perfNote) = await ComputeShiftLinePerformanceAsync(fromUtc, toUtc, scheduled, ct);
        }
        else if (idealCT is null || idealCT <= 0)
            perfNote = "표준 사이클(idealCT) 미설정 — 성능 산출 불가. 클린사이클이 모이면 자동 기입됩니다(또는 표준CT 직접 입력).";
        else if (totalCount is null || totalCount <= 0)
            perfNote = "기간 내 생산 사이클 0 — 성능 산출 불가.";
        else if (av.RunTimeMs <= 0)
            perfNote = "시프트 가동시간 0 — 성능 산출 불가.";
        else
        {
            performance = Math.Min(1.0, (idealCT.Value * (double)totalCount.Value) / av.RunTimeMs);
            perfNote = null;
        }

        var manualQualityPct = _settings.LoadSettings().OeeManual.QualityPercent;
        var (quality, qualNote, qualitySource, rejectOut, goodOut) =
            OeeMath.ResolveQuality(manualQualityPct, totalCount, prodReject, hasReject);

        double? oee = null;
        string? oeeNote = null;
        if (availability is double a && performance is double p && quality is double q)
        {
            oee = a * p * q;
            if (qualitySource == "assumed")
                oeeNote = "품질 100% 가정 포함(불량 미입력).";
        }
        else
        {
            var missing = new List<string>();
            if (availability is null) missing.Add("가용성");
            if (performance is null) missing.Add("성능");
            if (quality is null) missing.Add("품질");
            oeeNote = $"구성요소 미산출({string.Join(", ", missing)}) — OEE 산출 불가.";
        }

        double? mtbf = null;
        string? mtbfNote;
        double? mttr = null;
        string? mttrNote;
        if (failureCount <= 0)
        {
            mtbfNote = "고장(분류 unplanned) 건수 0 — MTBF 산출 불가.";
            mttrNote = "고장(분류 unplanned, 마감됨) 건수 0 — MTTR 산출 불가.";
        }
        else
        {
            mtbf = av.RunTimeMs / failureCount;
            mtbfNote = "시프트 가동시간 / 고장건수.";
            mttr = (double)failureDurationMs / failureCount;
            mttrNote = "Σ고장 지속시간(마감 이벤트만) / 고장건수.";
        }

        var shiftLabel = $"{shift.Start}–{shift.End}";
        return new OeeShiftSummaryDto(
            FlowName: flowName,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            PeriodMs: periodMs,
            ScheduledMs: av.ScheduledMs,
            PlannedStopMs: av.PlannedStopMs,
            PlannedProductionMs: av.PlannedProductionMs,
            DowntimeMs: av.DowntimeMs,
            DowntimeCount: av.DowntimeCount,
            RunTimeMs: av.RunTimeMs,
            TotalCount: totalCount,
            RejectCount: rejectOut,
            GoodCount: goodOut,
            IdealCycleTimeMs: idealCT,
            IdealCycleTimeSource: idealCtSource,
            Availability: availability,
            AvailabilityNote: availNote,
            Performance: performance,
            PerformanceNote: perfNote,
            Quality: quality,
            QualityNote: qualNote,
            QualitySource: qualitySource,
            Oee: oee,
            OeeNote: oeeNote,
            ShiftStart: shift.Start,
            ShiftEnd: shift.End,
            ShiftType: shift.ShiftType,
            ShiftLabel: shiftLabel,
            FailureCount: failureCount,
            Mtbf: mtbf,
            MtbfNote: mtbfNote,
            Mttr: mttr,
            MttrNote: mttrNote);
    }

    private readonly record struct ShiftAvail(
        double ScheduledMs, double PlannedStopMs, double PlannedProductionMs,
        double DowntimeMs, double RunTimeMs, int DowntimeCount);

    private async Task<ShiftAvail> ComputeShiftAvailabilityAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc, List<(double S, double E)> scheduled, CancellationToken ct)
    {
        var scheduledMs = Intervals.Total(scheduled);

        var exc = await _repo.QueryShiftExceptionsAsync(fromUtc, toUtc, flowName, ct);
        var excSegs = exc.Select(x => (ToMs(x.StartAt), ToMs(x.EndAt)));
        var plannedStop = Intervals.Intersect(scheduled, excSegs);
        var ppt = Intervals.Subtract(scheduled, excSegs);
        var pptMs = Intervals.Total(ppt);

        var dt = await _repo.QueryDowntimeAsync(fromUtc, toUtc, null, null, flowName, ct);
        var nowMs = ToMs(DateTime.UtcNow);
        var dtSegs = dt
            .Where(e => !string.Equals(e.Category, "planned", StringComparison.OrdinalIgnoreCase))
            .Select(e => (ToMs(e.StartAt), e.EndAt.HasValue ? ToMs(e.EndAt.Value) : nowMs));
        var dtInPpt = Intervals.Intersect(ppt, dtSegs);
        var downtimeMs = Intervals.Total(dtInPpt);
        var runTimeMs = Math.Max(0, pptMs - downtimeMs);

        return new ShiftAvail(scheduledMs, Intervals.Total(plannedStop), pptMs, downtimeMs, runTimeMs, dt.Count);
    }

    private async Task<(double? Perf, string? Note)> ComputeShiftLinePerformanceAsync(
        DateTime fromUtc, DateTime toUtc, List<(double S, double E)> scheduled, CancellationToken ct)
    {
        var flows = _settings.GetFlowsWithIdealCycleTime();
        if (flows.Count == 0)
            return (null, "표준 사이클(idealCT) 설정된 Flow 없음 — 성능 산출 불가. 표준CT 입력 필요.");

        double weightedPerf = 0;
        long weight = 0;
        int usedFlows = 0;
        foreach (var (flow, ideal) in flows)
        {
            var count = await CountFlowHistoryAsync(flow, fromUtc, toUtc);
            if (count <= 0) continue;
            var av = await ComputeShiftAvailabilityAsync(flow, fromUtc, toUtc, scheduled, ct);
            if (av.RunTimeMs <= 0) continue;
            var perf = Math.Min(1.0, (ideal * (double)count) / av.RunTimeMs);
            weightedPerf += perf * count;
            weight += count;
            usedFlows++;
        }
        if (weight <= 0)
            return (null, "표준CT 설정된 Flow 의 시프트 가동시간 내 생산 사이클 0 — 성능 산출 불가.");
        return (weightedPerf / weight, $"Flow {usedFlows}개 성능의 생산수 가중평균 (시프트 가동시간 기준).");
    }

    private static List<(double S, double E)> BuildScheduledIntervals(ShiftSettings shift, DateTime fromUtc, DateTime toUtc)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!TimeSpan.TryParseExact(shift.Start, "hh\\:mm", inv, out var startT)) startT = new TimeSpan(8, 0, 0);
        if (!TimeSpan.TryParseExact(shift.End, "hh\\:mm", inv, out var endT)) endT = new TimeSpan(17, 0, 0);
        bool crosses = endT <= startT;

        var fromMs = ToMs(fromUtc);
        var toMs = ToMs(toUtc);
        var fromLocalDate = fromUtc.ToLocalTime().Date;
        var toLocalDate = toUtc.ToLocalTime().Date;

        var segs = new List<(double S, double E)>();
        for (var d = fromLocalDate.AddDays(-1); d <= toLocalDate.AddDays(1); d = d.AddDays(1))
        {
            var sLocal = d + startT;
            var eLocal = crosses ? d.AddDays(1) + endT : d + endT;
            var sUtc = DateTime.SpecifyKind(sLocal, DateTimeKind.Local).ToUniversalTime();
            var eUtc = DateTime.SpecifyKind(eLocal, DateTimeKind.Local).ToUniversalTime();
            var s = Math.Max(ToMs(sUtc), fromMs);
            var e = Math.Min(ToMs(eUtc), toMs);
            if (e > s) segs.Add((s, e));
        }
        return Intervals.Union(segs);
    }

    // ── UTC epoch ms 변환 ──────────────────────────────────────────────────

    private static readonly DateTime _epochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // DateTime → epoch-ms. Local→UTC환산, Unspecified→UTC간주, Utc→그대로.
    protected static double ToMs(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
        return (utc - _epochUtc).TotalMilliseconds;
    }

    // ── 구간 연산 (합집합/교집합/차집합/합계) ───────────────────────────────

    protected static class Intervals
    {
        public static List<(double S, double E)> Union(IEnumerable<(double S, double E)> segs)
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

        public static double Total(IEnumerable<(double S, double E)> segs)
            => segs.Sum(s => Math.Max(0, s.E - s.S));

        public static List<(double S, double E)> Intersect(
            IEnumerable<(double S, double E)> a, IEnumerable<(double S, double E)> b)
        {
            var aa = Union(a);
            var bb = Union(b);
            var res = new List<(double S, double E)>();
            int i = 0, j = 0;
            while (i < aa.Count && j < bb.Count)
            {
                var s = Math.Max(aa[i].S, bb[j].S);
                var e = Math.Min(aa[i].E, bb[j].E);
                if (e > s) res.Add((s, e));
                if (aa[i].E < bb[j].E) i++; else j++;
            }
            return res;
        }

        public static List<(double S, double E)> Subtract(
            IEnumerable<(double S, double E)> a, IEnumerable<(double S, double E)> b)
        {
            var aa = Union(a);
            var bb = Union(b);
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

    // ── OEE 요약 산출 코어 ──────────────────────────────────────────────────

    protected async Task<OeeSummaryDto> BuildSummaryAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc, CancellationToken ct,
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample)>? ctThresholds = null)
    {
        var periodMs = (toUtc - fromUtc).TotalMilliseconds;
        if (periodMs < 0) periodMs = 0;

        var (downtimeMs, downtimeCount) = await _repo.GetDowntimeAggregateAsync(fromUtc, toUtc, flowName, ct);
        int? totalCount = await CountFlowHistoryAsync(flowName, fromUtc, toUtc);
        var (_, _, prodReject, hasReject) =
            await _repo.QueryProductionAsync(fromUtc.ToLocalTime(), toUtc.ToLocalTime(), flowName, ct);

        var thresholds = ctThresholds ?? await ResolveCtThresholdsAsync();
        var (plannedWindows, plannedSource, applyLongStop) = await ResolvePlannedWindowsAsync(thresholds, ct);
        var evIntervals = await _repo.GetDowntimeIntervalsAsync(fromUtc, toUtc, flowName, ct);
        var maintIv = evIntervals
            .Where(x => x.Kind is 0 or 2 && x.EndMs > x.StartMs)
            .Select(x => ((double)x.StartMs, (double)x.EndMs, x.FlowName))
            .ToList();
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct,
            maintIntervals: maintIv);

        // 가용성 A = 벽시계 단일모델(2026-07-06): 가동 ÷ 생산가능시간. 추이·정산·도넛 3뷰 공통 SSOT.
        double? availability; string? availNote; string? availabilitySource; double runtimeMs;
        var (wallA, wallANote) = OeeMath.ComputeWallClockAvailability(agg.RunWallMs, agg.AvailableWallMs);
        if (agg.HasThreshold && wallA is not null)
        {
            availability = wallA;
            availNote = wallANote;
            availabilitySource = "wallclock";
            runtimeMs = agg.RunWallMs;
        }
        else
        {
            var av = await ResolveAvailabilityAsync(flowName, fromUtc, toUtc, downtimeMs, periodMs, ct);
            availability = av.Availability;
            availNote = (av.Note ?? "") + " (생산가능시간 0 또는 임계 미보유 — 시간기반 폴백).";
            availabilitySource = av.Source;
            runtimeMs = av.RuntimeMs;
        }

        var (performance, perfNote) = OeeMath.ComputeCyclePerformance(
            agg.NormalCount, agg.CtThresholdMs, agg.NormalCtMs);

        var manualQualityPct = _settings.LoadSettings().OeeManual.QualityPercent;
        var (quality, qualNote, qualitySource, rejectOut, goodOut) =
            OeeMath.ResolveQuality(manualQualityPct, totalCount, prodReject, hasReject);

        var (oee, oeeNote) = OeeMath.ComputeOee(availability, performance, quality, qualitySource);

        var sortedOnsets = agg.OnsetsMs.OrderBy(x => x).ToList();
        var (mtbf, mtbfNote, _) = OeeMath.ComputeMtbf2(sortedOnsets);
        var (mttr, mttrNote) = OeeMath.ComputeMttr(agg.RepairMsList);
        var failureCount = agg.DowntimeEventCount;

        var (idealCT, idealCtSource) = ResolveIdealCycle(flowName);

        return new OeeSummaryDto(
            FlowName: flowName,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            PeriodMs: periodMs,
            DowntimeMs: downtimeMs,
            DowntimeCount: downtimeCount,
            TotalCount: totalCount,
            RejectCount: rejectOut,
            GoodCount: goodOut,
            IdealCycleTimeMs: idealCT,
            IdealCycleTimeSource: idealCtSource,
            Availability: availability,
            AvailabilityNote: availNote,
            AvailabilitySource: availabilitySource,
            Performance: performance,
            PerformanceNote: perfNote,
            Quality: quality,
            QualityNote: qualNote,
            QualitySource: qualitySource,
            Oee: oee,
            OeeNote: oeeNote,
            FailureCount: failureCount,
            Mtbf: mtbf,
            MtbfNote: mtbfNote,
            Mttr: mttr,
            MttrNote: mttrNote,
            NormalCtMs: agg.NormalCtMs,
            IdleCtMs: agg.IdleCtMs,
            NormalCycleCount: agg.HasThreshold ? agg.NormalCount : (int?)null,
            CtThresholdMs: agg.CtThresholdMs,
            PlannedDownMs: agg.PlannedCtMs,
            PlannedStopSource: agg.HasThreshold ? plannedSource : null,
            CtSampleCount: agg.HasThreshold ? agg.CtSampleMin : (int?)null,
            CtSampleLow: agg.HasThreshold && agg.CtSampleMin < OeeCtStatsService.ConfidentMinCleanCycles,
            IdleMaintCtMs: agg.IdleMaintCtMs,
            IdleCalendarMs: agg.IdleCalendarMs,
            CycleFlowCount: agg.FlowCount,
            UnmeasuredMs: agg.UnmeasuredMs,
            RunWallMs: agg.RunWallMs,
            AvailableWallMs: agg.AvailableWallMs,
            DownMaintWallMs: agg.DownMaintWallMs);
    }

    // ── 비생산 판정 모드 ─────────────────────────────────────────────────────

    // 비생산 시간대 해석 (2026-07-06 단일 벽시계 모델). 비생산 = "지정된 것"만 — 당일 데이터를 쓰지 않는다:
    //   auto   : 14일 학습패턴 창(OeeNonProdPatternService, 어제까지 히스토리 투표제 — 당일 제외).
    //   manual : 사용자 지정 시간대 창.
    // 어느 경우도 당일 10×CT 자동판정(applyLongStop)은 적용하지 않는다(항상 false) — 평일 긴 정지는
    // '비생산'으로 숨기지 않고 비가동(고장)으로 정직하게 드러낸다. applyLongStop 인자는 실측 패턴 조회
    // (planned-stops/actual?detected)만 true 로 강제 사용.
    protected async Task<(List<(int StartMin, int EndMin)> Windows, string Source, bool ApplyLongStop)>
        ResolvePlannedWindowsAsync(
            IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample)> thresholds, CancellationToken ct)
    {
        var oee = _settings.LoadSettings().OeeManual;
        if (oee.PlannedStopsAutoEffective)
        {
            try
            {
                var pattern = await _nonProdPattern.GetOrComputeAsync(null, thresholds, forceRefresh: false, ct);
                return (pattern.Windows.Select(x => (x.StartMinutes, x.EndMinutes)).ToList(), "auto", false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OEE] 학습 비생산 패턴 조회 실패 — 비생산 창 없음 폴백");
                return (new List<(int, int)>(), "auto", false);
            }
        }

        var manual = oee.PlannedStops;
        if (manual is { Count: > 0 })
            return (manual.Select(w => (w.StartMinutes, w.EndMinutes)).ToList(), "manual", false);

        return (new List<(int, int)>(), "none", false);
    }

    private static string BuildNonProductionStartSql(IReadOnlyList<(int StartMin, int EndMin)> windows)
    {
        if (windows.Count == 0) return "0";
        const string startMin =
            "(CAST(strftime('%H', substr(recordedAt,1,19), 'localtime', (-ct/1000.0)||' seconds') AS INTEGER)*60"
            + " + CAST(strftime('%M', substr(recordedAt,1,19), 'localtime', (-ct/1000.0)||' seconds') AS INTEGER))";
        var clauses = windows.Select(w => $"({startMin} >= {w.StartMin} AND {startMin} < {w.EndMin})");
        return "(" + string.Join(" OR ", clauses) + ")";
    }

    private static bool IsPlannedTimeOfDay(double recMs, IReadOnlyList<(int StartMin, int EndMin)> windows)
    {
        if (windows.Count == 0) return false;
        var local = _epochUtc.AddMilliseconds(recMs).ToLocalTime();
        var min = local.Hour * 60 + local.Minute;
        foreach (var w in windows)
            if (min >= w.StartMin && min < w.EndMin) return true;
        return false;
    }

    protected static List<(double S, double E)> ExpandPlannedIntervalsMs(
        IReadOnlyList<(int StartMin, int EndMin)> windows, DateTime fromUtc, DateTime toUtc)
    {
        var res = new List<(double S, double E)>();
        if (windows.Count == 0) return res;
        var localFrom = fromUtc.ToLocalTime().Date.AddDays(-1);
        var localToEnd = toUtc.ToLocalTime().Date.AddDays(1);
        for (var d = localFrom; d <= localToEnd; d = d.AddDays(1))
        {
            foreach (var w in windows)
            {
                var sLocal = DateTime.SpecifyKind(d.AddMinutes(w.StartMin), DateTimeKind.Local);
                var eLocal = DateTime.SpecifyKind(d.AddMinutes(w.EndMin), DateTimeKind.Local);
                var s = ToMs(sLocal.ToUniversalTime());
                var e = ToMs(eLocal.ToUniversalTime());
                if (e > s) res.Add((s, e));
            }
        }
        return res;
    }

    /// <summary>
    /// 휴무 요일(생산하지 않는 요일)의 하루(00:00~24:00, 로컬) 구간을 [fromUtc, toUtc] 범위에서 epoch-ms 구간으로 전개한다.
    /// 값은 <see cref="DayOfWeek"/> 정수(0=일 … 6=토). 비생산·미계측과 동일하게 가용성(A) 분모에서 빼기 위한 소스.
    /// 비어 있으면 빈 목록(제외 없음).
    /// </summary>
    protected static List<(double S, double E)> BuildExcludedWeekdayIntervalsMs(
        IReadOnlyCollection<int>? excludedWeekdays, DateTime fromUtc, DateTime toUtc)
    {
        var res = new List<(double S, double E)>();
        if (excludedWeekdays is null || excludedWeekdays.Count == 0) return res;
        var localFrom = fromUtc.ToLocalTime().Date;
        var localToEnd = toUtc.ToLocalTime().Date.AddDays(1);
        for (var d = localFrom; d < localToEnd; d = d.AddDays(1))
        {
            if (!excludedWeekdays.Contains((int)d.DayOfWeek)) continue;
            var sLocal = DateTime.SpecifyKind(d, DateTimeKind.Local);
            var eLocal = DateTime.SpecifyKind(d.AddDays(1), DateTimeKind.Local);
            res.Add((ToMs(sLocal.ToUniversalTime()), ToMs(eLocal.ToUniversalTime())));
        }
        return res;
    }

    // ── CT 이상치(임계) 산출 ─────────────────────────────────────────────────

    protected async Task<Dictionary<string, (double AvgMs, double P10Ms, int Sample)>> ResolveCtThresholdsAsync()
    {
        var thr = await _ctStats.ComputeCtThresholdAsync(
            excludeUntilUtc: DateTime.Today.ToUniversalTime(),
            decayHalfLifeDays: 7.0);
        var thrToday = await _ctStats.ComputeCtThresholdAsync();
        foreach (var (flow, val) in thrToday)
            thr.TryAdd(flow, val);
        var settings = _settings.LoadSettings();
        foreach (var ov in settings.FlowCycle.Overrides)
        {
            if (string.IsNullOrWhiteSpace(ov.FlowName)) continue;
            var src = ov.IdealCycleTimeSource;
            var isManual = ov.IdealCycleTimeMs is > 0 && src != "auto" && src != "auto-median";
            if (isManual) thr[ov.FlowName] = (ov.IdealCycleTimeMs!.Value, ov.IdealCycleTimeMs!.Value, int.MaxValue);
        }
        return thr;
    }

    // ── 사이클기반 집계 ──────────────────────────────────────────────────────

    protected readonly record struct CycleAgg(
        double NormalCtMs, double IdleCtMs, int NormalCount, int DowntimeEventCount,
        double? CtThresholdMs, List<double> OnsetsMs, List<double> RepairMsList, bool HasThreshold,
        double PlannedCtMs, int CtSampleMin = 0, List<(double S, double E)>? NonProdIntervals = null,
        List<(double S, double E)>? RunIntervals = null, double IdleMaintCtMs = 0,
        double IdleCalendarMs = 0, int FlowCount = 0,
        List<(double S, double E)>? IdleIntervals = null,
        List<(double StartMs, double CtMs)>? NormalCycles = null,
        double UnmeasuredMs = 0,                                    // 미계측(수신 공백, §3.4) 달력시간 — 기간 클립·Union
        List<(double S, double E)>? UnmeasuredIntervals = null,     // 미계측 구간(daily/actual 표시·차집합 공용)
        // ── 벽시계 단일모델(2026-07-06) — 세 뷰(추이·정산·도넛) 공통 SSOT. flow별 합산(라인=Σ). ──
        double RunWallMs = 0,                                       // Σ가동(벽시계) = 정상 사이클 ∩ 생산가능
        double AvailableWallMs = 0,                                 // Σ생산가능시간 = (기간−비생산−미계측) × flow수
        double DownMaintWallMs = 0,                                 // Σ유지보수(비가동 ∩ 유지보수 이벤트) — 고장 = (Avail−Run) − 이 값
        List<(double S, double E)>? RunWallIntervals = null,        // flow별 가동 구간 연결(concat, 합산 슬롯용 — union 아님)
        List<(double S, double E)>? DownMaintWallIntervals = null); // flow별 유지보수 구간 연결(concat)

    private sealed class CycleAggRow { public long NormalCt { get; set; } public long NormalCount { get; set; } public long NonProdNormalCt { get; set; } }
    private sealed class DtCycleRaw { public string? RecordedAt { get; set; } public long? Ct { get; set; } public long? Mt { get; set; } }
    private sealed class NocycleRaw { public string? StartAt { get; set; } public string? EndAt { get; set; } }

    protected async Task<CycleAgg> ComputeCycleAggregateAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc,
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample)> thresholds,
        IReadOnlyList<(int StartMin, int EndMin)> plannedWindows, bool applyLongStop, CancellationToken ct,
        bool collectRunIntervals = false,
        IReadOnlyList<(double S, double E, string? Flow)>? maintIntervals = null,
        bool collectNormalCycles = false)
    {
        var onsets = new List<double>();
        var repairs = new List<double>();
        // 미계측(수신 공백, doc/22 §3.4) — 통신 헬스 심박이 보증하지 못한 구간. 가동/비가동/비생산 어디에도
        // 넣지 않는다(모르는 시간을 아는 척 금지). 심박 epoch 이전 기간은 빈 목록(소급 주장 없음) = 기존 동작.
        // trusted=false(조회 실패 폴백)면 카빙 없이 계산은 진행하되 감지 로그 materialize 는 스킵(영구 오염 방지).
        var (unmeasured, unmeasuredTrusted) = await _commHealth.TryGetUnmeasuredIntervalsAsync(fromUtc, toUtc, ct);
        var unmeasuredMs = unmeasured.Sum(u => u.E - u.S);
        var empty = new CycleAgg(0, 0, 0, 0, null, onsets, repairs, false, 0,
            UnmeasuredMs: unmeasuredMs, UnmeasuredIntervals: unmeasured);

        List<string> targetFlows;
        if (!string.IsNullOrWhiteSpace(flowName))
            targetFlows = thresholds.ContainsKey(flowName) ? new List<string> { flowName } : new List<string>();
        else
            targetFlows = thresholds.Keys.ToList();
        if (targetFlows.Count == 0) return empty;

        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return empty;

        var fromStr = fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var toStr = toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        double normalCtMs = 0, idleCtMs = 0, plannedCtMs = 0, perfNumerator = 0;
        int normalCount = 0, dtEventCount = 0;
        int ctSampleMin = int.MaxValue;
        bool hasThreshold = false;
        double thrSum = 0; int thrCount = 0;
        var cycleIdleIntervals = new List<(double S, double E)>();
        var nonProdIntervals = new List<(double S, double E)>(ExpandPlannedIntervalsMs(plannedWindows, fromUtc, toUtc));
        var nonProdDetections = new List<OeeNonProdDetectionLog>();
        var runIntervals = collectRunIntervals ? new List<(double S, double E)>() : null;
        // 정상 사이클 (시작, CT) 목록 — 매트릭스(teep/matrix)가 시간버킷에 귀속시키는 원본. NormalCt(SQL) 분류와
        // 동일하게 비생산 시간대 시작분은 제외해, 버킷 합계 ≈ KPI 가동(NormalCtMs)이 되게 한다.
        var normalCycles = collectNormalCycles ? new List<(double StartMs, double CtMs)>() : null;
        Dictionary<string, List<(double S, double E)>>? maintByFlow = null;
        List<(double S, double E)>? maintAll = null;
        if (maintIntervals is { Count: > 0 })
        {
            maintByFlow = maintIntervals.Where(x => !string.IsNullOrEmpty(x.Flow) && x.E > x.S)
                .GroupBy(x => x.Flow!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Intervals.Union(g.Select(x => (x.S, x.E)).ToList()), StringComparer.Ordinal);
            maintAll = Intervals.Union(maintIntervals.Where(x => x.E > x.S).Select(x => (x.S, x.E)).ToList());
        }
        double idleMaintCtMs = 0;
        var idleCalIntervals = new List<(double S, double E)>();
        static double OverlapMs(List<(double S, double E)>? iv, double s, double e)
        {
            if (iv is null) return 0;
            double sum = 0;
            foreach (var (a, b) in iv) { var o = Math.Min(b, e) - Math.Max(a, s); if (o > 0) sum += o; }
            return sum;
        }

        // ── 벽시계 단일모델(2026-07-06): 생산가능 = 기간 − 비생산(지정/학습) − 미계측. flow별 가동/비가동을 이 창에서 잰다. ──
        //   nonProdIntervals 는 이 시점 지정/학습 비생산만(당일 10× 미포함 — applyLongStop=false). 생산가능은 flow 공통.
        double periodStartMs = ToMs(fromUtc), periodEndMs = ToMs(toUtc);
        // 휴무 요일(생산 안 하는 요일)의 하루 전체 = 비생산·미계측과 동일하게 생산가능시간에서 통째 제외 — 쉬는 날의
        //   정지가 가용성을 깎지 않게. OEE 전용: 아래 wallAvailableIv 는 runWall/availableSingle(=벽시계 A)만 좌우하고,
        //   TEEP 가 읽는 NormalCt/IdleCt/PlannedCt(원 SQL 집계)에는 영향 없다 → TEEP 달력 분모는 표준 그대로 유지됨.
        //   비생산과 달리 nonProdIntervals(시각화·감지 로그)에는 넣지 않는다 — '휴무일'은 '비생산 시간대'와 별개 개념.
        var excludedDayIv = BuildExcludedWeekdayIntervalsMs(
            _settings.LoadSettings().OeeManual.ExcludedWeekdays, fromUtc, toUtc);
        var wallAvailableIv = Intervals.Subtract(
            new List<(double S, double E)> { (periodStartMs, periodEndMs) },
            Intervals.Union(nonProdIntervals).Concat(unmeasured).Concat(excludedDayIv).ToList());
        double availableSingleMs = Intervals.Total(wallAvailableIv);
        double runWallMs = 0, downMaintWallMs = 0;
        var runWallIntervals = new List<(double S, double E)>();       // flow별 가동(생산가능 클립) 연결
        var downMaintWallIntervals = new List<(double S, double E)>(); // flow별 유지보수(비가동∩유지이벤트) 연결

        const string dtCond = "ct > 0 AND ((mt IS NOT NULL AND mt > @Thr) OR (mt IS NULL AND ct > @Thr))";
        var nonProdStartSql = BuildNonProductionStartSql(plannedWindows);

        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync(ct);
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return empty;

            foreach (var f in targetFlows)
            {
                var thr = thresholds[f].AvgMs;
                if (thr <= 0) continue;
                hasThreshold = true;
                thrSum += thr; thrCount++;
                ctSampleMin = Math.Min(ctSampleMin, thresholds[f].Sample);

                var p = new DynamicParameters();
                p.Add("From", fromStr); p.Add("To", toStr); p.Add("Flow", f); p.Add("Thr", thr);

                var aggRow = await conn.QueryFirstOrDefaultAsync<CycleAggRow>($@"
                    SELECT
                      COALESCE(SUM(CASE WHEN ct>0 AND NOT ({dtCond}) AND NOT ({nonProdStartSql}) THEN ct ELSE 0 END),0)  AS NormalCt,
                      COALESCE(SUM(CASE WHEN ct>0 AND NOT ({dtCond}) AND NOT ({nonProdStartSql}) THEN 1  ELSE 0 END),0)  AS NormalCount,
                      COALESCE(SUM(CASE WHEN ct>0 AND NOT ({dtCond}) AND ({nonProdStartSql}) THEN ct ELSE 0 END),0)  AS NonProdNormalCt
                    FROM dspFlowHistory
                    WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow", p);
                if (aggRow is not null)
                {
                    normalCtMs += aggRow.NormalCt;
                    normalCount += (int)aggRow.NormalCount;
                    perfNumerator += aggRow.NormalCount * thr;
                    plannedCtMs += aggRow.NonProdNormalCt;
                }

                // 벽시계 가동 산출 위해 정상 사이클 구간은 항상 조회(collectRunIntervals/normalCycles 는 부가 수집).
                var flowRun = new List<(double S, double E)>();
                {
                    var runRows = await conn.QueryAsync<DtCycleRaw>($@"
                        SELECT recordedAt AS RecordedAt, ct AS Ct, mt AS Mt
                        FROM dspFlowHistory
                        WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow
                          AND ct > 0 AND NOT ({dtCond})", p);
                    foreach (var r in runRows)
                        if (r.Ct is long rc && rc > 0 && ParseUtcMs(r.RecordedAt) is double rrec)
                        {
                            flowRun.Add((rrec - rc, rrec));
                            runIntervals?.Add((rrec - rc, rrec));
                            // NormalCt(SQL)와 동일 기준 — 비생산 시간대 시작 사이클은 KPI 가동에서 빠지므로 여기서도 제외.
                            if (normalCycles is not null && !IsPlannedTimeOfDay(rrec - rc, plannedWindows))
                                normalCycles.Add((rrec - rc, rc));
                        }
                }
                // 벽시계 단일모델: 가동 = 이 flow 정상 사이클 ∩ 생산가능. 비가동 = 생산가능 − 가동(잔여).
                //   유지보수 = 비가동 ∩ 유지보수 이벤트(같은 flow, kind 0/2). 고장 = 비가동 − 유지보수(호출측이 차감).
                var flowRunAvail = Intervals.Intersect(Intervals.Union(flowRun), wallAvailableIv);
                runWallMs += Intervals.Total(flowRunAvail);
                runWallIntervals.AddRange(flowRunAvail);
                var flowDownIv = Intervals.Subtract(wallAvailableIv, flowRun);
                var flowMaintIv = Intervals.Intersect(flowDownIv,
                    maintByFlow?.GetValueOrDefault(f) ?? new List<(double S, double E)>());
                downMaintWallMs += Intervals.Total(flowMaintIv);
                downMaintWallIntervals.AddRange(flowMaintIv);

                var rows = await conn.QueryAsync<DtCycleRaw>($@"
                    SELECT recordedAt AS RecordedAt, ct AS Ct, mt AS Mt
                    FROM dspFlowHistory
                    WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow AND {dtCond}
                    ORDER BY recordedAt", p);
                foreach (var r in rows)
                {
                    if (r.Ct is not long ctMsL || ctMsL <= 0) continue;
                    double cMs = ctMsL;
                    var recMs = ParseUtcMs(r.RecordedAt);
                    if (recMs is not double rec) continue;
                    double startMs = rec - cMs;
                    cycleIdleIntervals.Add((startMs, rec));
                    // 미계측 겹침 카빙(§3.4) — 수신 공백과 겹친 부분은 어떤 상태도 주장하지 않는다. 비생산 10× 판정도
                    // 계측된 잔여 길이로만 한다(보수 — 모르는 시간이 임계를 채워 정지를 비생산으로 승격시키지 않게).
                    var rowSegs = unmeasured.Count > 0
                        ? Intervals.Subtract(new List<(double S, double E)> { (startMs, rec) }, unmeasured)
                        : new List<(double S, double E)> { (startMs, rec) };
                    var measuredMs = Intervals.Total(rowSegs);
                    if (measuredMs <= 0) continue;                              // 전 구간 미계측 — 비가동/비생산/onset 전부 미계상
                    if (IsPlannedTimeOfDay(startMs, plannedWindows))
                    {
                        plannedCtMs += measuredMs;                              // 비생산 시간대 — A 분모서 제외, MTBF/MTTR 미반영
                        continue;
                    }
                    if (applyLongStop && r.Mt is null && OeeMath.IsLongStopNonProduction(measuredMs, thr))
                    {
                        plannedCtMs += measuredMs;
                        foreach (var seg in rowSegs)
                        {
                            nonProdIntervals.Add(seg);              // 자동(10×) 유휴 사이클 → 비생산(제외) 시각화
                            nonProdDetections.Add(NewNonProdDetection(f, seg.S, seg.E, thr, "idle-cycle"));
                        }
                        continue;
                    }
                    idleCtMs += measuredMs;
                    idleCalIntervals.AddRange(rowSegs);
                    // 유지보수 이벤트(같은 flow)와 겹친 만큼 유지보수로 귀속(잔여 = 고장) — 가용성 바 3분할용.
                    // 계측 잔여(rowSegs) 기준으로 겹침 계산 — 미계측 안에만 있는 유지보수가 잔여 idle 로 오귀속되지 않게.
                    idleMaintCtMs += Math.Min(measuredMs,
                        rowSegs.Sum(seg => OverlapMs(maintByFlow?.GetValueOrDefault(f), seg.S, seg.E)));
                    onsets.Add(startMs + thr);
                    // going 회복: complete(MT) 또는 CT 종료. 미계측 카빙된 행은 계측 잔여 기준(공백이 MTTR 을 부풀리지 않게).
                    double repair = r.Mt is long mtL && measuredMs >= cMs ? (mtL - thr) : (measuredMs - thr);
                    if (repair >= 0) repairs.Add(repair);
                    dtEventCount++;
                }
            }

            var avgThr = thrCount > 0 ? thrSum / thrCount : 0;
            var nocycle = await GetNocycleIntervalsMsAsync(flowName, fromUtc, toUtc);
            if (nocycle.Count > 0)
            {
                var plannedIntervals = ExpandPlannedIntervalsMs(plannedWindows, fromUtc, toUtc);
                foreach (var seg in Intervals.Subtract(nocycle, cycleIdleIntervals))
                {
                    var segList = new List<(double S, double E)> { seg };
                    if (plannedIntervals.Count > 0)
                    {
                        plannedCtMs += Intervals.Total(Intervals.Intersect(segList, plannedIntervals));
                        segList = Intervals.Subtract(segList, plannedIntervals);
                    }
                    foreach (var u in segList)
                    {
                        // 미계측 카빙(§3.4) — 판정·적립은 계측 잔여(uSegs)로만 하되, 이벤트(onset/repair/건수)는
                        // 원 구간당 1건 유지: 수신 공백이 정지 하나를 N건 고장으로 쪼개 MTBF/MTTR 을 왜곡하지 않게.
                        var uSegs = unmeasured.Count > 0
                            ? Intervals.Subtract(new List<(double S, double E)> { u }, unmeasured)
                            : new List<(double S, double E)> { u };
                        var len = Intervals.Total(uSegs);
                        if (len <= 0) continue;                     // 전 구간 미계측 — 비가동/비생산/onset 전부 미계상
                        if (applyLongStop && OeeMath.IsLongStopNonProduction(len, avgThr))
                        {
                            plannedCtMs += len;
                            foreach (var us in uSegs)
                            {
                                nonProdIntervals.Add(us);           // 자동(10×) 무사이클 갭 → 비생산(제외) 시각화
                                nonProdDetections.Add(NewNonProdDetection(flowName, us.S, us.E, avgThr, "nocycle-gap"));
                            }
                            continue;
                        }
                        idleCtMs += len;
                        idleCalIntervals.AddRange(uSegs);
                        // 무사이클 잔여의 유지보수 귀속 — flow 조회는 그 flow 의 유지보수 구간, 라인은 전체 Union.
                        // 계측 잔여 세그먼트 기준(미계측 안 유지보수의 오귀속 방지).
                        idleMaintCtMs += Math.Min(len, uSegs.Sum(us => OverlapMs(
                            flowName is not null ? maintByFlow?.GetValueOrDefault(flowName) : maintAll, us.S, us.E)));
                        onsets.Add(uSegs[0].S);                     // onset = 첫 계측 세그먼트 시작(공백 안 onset 금지)
                        repairs.Add(len);
                        dtEventCount++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] cycle aggregate failed");
            return empty;
        }

        if (!hasThreshold) return empty;

        double? displayThr;
        if (normalCount > 0)
            displayThr = perfNumerator / normalCount;
        else
        {
            var thrVals = targetFlows.Select(f => thresholds[f].AvgMs).Where(v => v > 0).ToList();
            displayThr = thrVals.Count > 0 ? thrVals.Average() : (double?)null;
        }

        // 미계측 조회가 실패(비신뢰)한 요청에선 스킵 — 카빙 안 된 블랙아웃 스팬이 비생산으로 영구 기록되는 오염 방지
        // (UPSERT 키가 onset 이라 이후 정상 요청이 자가 치유하지 못함).
        if (applyLongStop && unmeasuredTrusted && nonProdDetections.Count > 0)
        {
            try { await _repo.UpsertNonProdDetectionsAsync(nonProdDetections, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[OEE] 비생산 감지 로그 materialize 실패"); }
        }

        var idleCalUnion = Intervals.Union(idleCalIntervals);
        return new CycleAgg(normalCtMs, idleCtMs, normalCount, dtEventCount, displayThr, onsets, repairs, true, plannedCtMs,
            ctSampleMin == int.MaxValue ? 0 : ctSampleMin, Intervals.Union(nonProdIntervals),
            runIntervals is not null ? Intervals.Union(runIntervals) : null,
            Math.Min(idleMaintCtMs, idleCtMs),
            Intervals.Total(idleCalUnion), thrCount,
            IdleIntervals: idleCalUnion, NormalCycles: normalCycles,
            UnmeasuredMs: unmeasuredMs, UnmeasuredIntervals: unmeasured,
            RunWallMs: runWallMs, AvailableWallMs: availableSingleMs * thrCount,
            DownMaintWallMs: downMaintWallMs,
            RunWallIntervals: runWallIntervals, DownMaintWallIntervals: downMaintWallIntervals);
    }

    private static OeeNonProdDetectionLog NewNonProdDetection(string? flow, double onsetMs, double clearMs, double thrMs, string reason)
        => new()
        {
            FlowName = flow,
            OnsetAt = DateTimeOffset.FromUnixTimeMilliseconds((long)onsetMs).UtcDateTime,
            ClearAt = DateTimeOffset.FromUnixTimeMilliseconds((long)clearMs).UtcDateTime,
            DurationMs = (long)(clearMs - onsetMs),
            DetectionSource = "auto-10xct",
            DetectionReason = reason,
            CtThresholdMs = thrMs,
            CtMultiplier = OeeMath.NonProductionCtMultiplier,
        };

    private async Task<List<(double S, double E)>> GetNocycleIntervalsMsAsync(string? flowName, DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<(double S, double E)>();
        var sharedDb = _pathResolver.GetSharedDbPath();
        var dir = System.IO.Path.GetDirectoryName(sharedDb);
        if (string.IsNullOrEmpty(dir)) return result;
        var oeeDb = System.IO.Path.Combine(dir, "oee.db");
        if (!System.IO.File.Exists(oeeDb)) return result;

        var fromMs = ToMs(fromUtc);
        var toMs = ToMs(toUtc);
        try
        {
            await using var conn = new SqliteConnection($"Data Source={oeeDb};Mode=ReadOnly;Default Timeout=20");
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='oeeDowntimeEvent'");
            if (exists == 0) return result;

            var flowClause = string.IsNullOrWhiteSpace(flowName) ? "" : " AND flowName = @Flow ";
            var rows = await conn.QueryAsync<NocycleRaw>(
                $@"SELECT startAt AS StartAt, endAt AS EndAt FROM oeeDowntimeEvent
                   WHERE detectSource = 'nocycle' {flowClause}",
                new { Flow = flowName?.Trim() });
            foreach (var r in rows)
            {
                var s = ParseUtcMs(r.StartAt);
                if (s is not double sMs) continue;
                var eMs = ParseUtcMs(r.EndAt) ?? Math.Min(toMs, ToMs(DateTime.UtcNow));
                var clipS = Math.Max(sMs, fromMs);
                var clipE = Math.Min(eMs, toMs);
                if (clipE > clipS) result.Add((clipS, clipE));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] nocycle intervals query failed");
        }
        return result;
    }

    protected static double? ParseUtcMs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            return (dt - _epochUtc).TotalMilliseconds;
        return null;
    }

    protected (int? Ms, string? Source) ResolveIdealCycle(string? flowName)
    {
        if (string.IsNullOrWhiteSpace(flowName)) return (null, null);
        var ov = _settings.GetFlowCycleOverride(flowName);
        return ov?.IdealCycleTimeMs is > 0 ? (ov.IdealCycleTimeMs, ov.IdealCycleTimeSource) : (null, null);
    }

    // ── dspFlowHistory 조회 헬퍼 ─────────────────────────────────────────────

    protected async Task<int> CountFlowHistoryAsync(string? flowName, DateTime fromUtc, DateTime toUtc)
    {
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return 0;
        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();

            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return 0;

            var p = new DynamicParameters();
            p.Add("From", fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            p.Add("To", toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            var flowClause = "";
            if (!string.IsNullOrWhiteSpace(flowName))
            {
                flowClause = " AND flowName = @Flow ";
                p.Add("Flow", flowName.Trim());
            }
            return await conn.ExecuteScalarAsync<int>($@"
                SELECT COUNT(*) FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0
                  AND recordedAt >= @From AND recordedAt < @To {flowClause}", p);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] dspFlowHistory count failed");
            return 0;
        }
    }

    protected async Task<int> CountDistinctActiveFlowsAsync(DateTime fromUtc, DateTime toUtc)
    {
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return 0;
        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();

            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return 0;

            var p = new DynamicParameters();
            p.Add("From", fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            p.Add("To", toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            return await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(DISTINCT flowName) FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0
                  AND recordedAt >= @From AND recordedAt < @To", p);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] dspFlowHistory distinct-flow count failed");
            return 0;
        }
    }

    protected async Task<List<string>> GetDistinctFlowNamesAsync()
    {
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return [];
        try
        {
            await using var conn = new SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();

            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return [];

            var rows = await conn.QueryAsync<string>(
                "SELECT DISTINCT flowName FROM dspFlowHistory WHERE flowName IS NOT NULL AND flowName <> ''");
            return [.. rows];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] dspFlowHistory distinct flowName query failed");
            return [];
        }
    }

    // ── 가용성 분모 폴백 체인 ────────────────────────────────────────────────

    protected readonly record struct AvailabilityResult(double? Availability, string? Note, string Source, double PlannedMs, double RuntimeMs);

    protected async Task<AvailabilityResult> ResolveAvailabilityAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc, long downtimeMs, double periodMs, CancellationToken ct)
    {
        var shift = _settings.LoadSettings().Shift;
        if (shift.UserSet)
        {
            var scheduled = BuildScheduledIntervals(shift, fromUtc, toUtc);
            var sav = await ComputeShiftAvailabilityAsync(flowName, fromUtc, toUtc, scheduled, ct);
            if (sav.PlannedProductionMs > 0)
                return new AvailabilityResult(
                    Math.Clamp(sav.RunTimeMs / sav.PlannedProductionMs, 0, 1),
                    "가동시간 ÷ 계획생산시간(사용자 시프트 ∩ 기간 − 계획정지).", "shift", sav.PlannedProductionMs, sav.RunTimeMs);
        }

        var win = _shiftInfer.Get(flowName);
        if (win is not null)
        {
            var (pptMs, runtimeMs, ok) = await ComputeAutoAvailabilityAsync(flowName, win, fromUtc, toUtc, ct);
            if (ok && pptMs > 0)
                return new AvailabilityResult(
                    Math.Clamp(runtimeMs / pptMs, 0, 1),
                    "가동시간 ÷ 자동추정 계획시간(14일 활동 시간창 × 활동일수 − 계획정비).", "auto", pptMs, runtimeMs);
        }

        if (periodMs > 0)
        {
            var rt = Math.Max(0, periodMs - downtimeMs);
            return new AvailabilityResult(
                Math.Clamp(rt / periodMs, 0, 1),
                "달력근사 (1 − 정지/기간). 시프트 미설정·활동 데이터 부족 시 폴백.", "calendar", periodMs, rt);
        }
        return new AvailabilityResult(null, "기간이 0 — 가용성 산출 불가.", "calendar", 0, 0);
    }

    private async Task<(double PptMs, double RuntimeMs, bool Ok)> ComputeAutoAvailabilityAsync(
        string? flowName, ShiftWindow win, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var activeDates = await GetActiveLocalDatesAsync(flowName, fromUtc, toUtc);
        if (activeDates.Count == 0) return (0, 0, false);

        var fromMs = ToMs(fromUtc);
        var toMs = ToMs(toUtc);
        var segs = new List<(double S, double E)>();
        foreach (var d in activeDates)
        {
            for (int h = 0; h < 24; h++)
            {
                if (!win.InBand[h]) continue;
                var sLocal = d.AddHours(h);
                var sUtc = DateTime.SpecifyKind(sLocal, DateTimeKind.Local).ToUniversalTime();
                var eUtc = DateTime.SpecifyKind(sLocal.AddHours(1), DateTimeKind.Local).ToUniversalTime();
                var s = Math.Max(ToMs(sUtc), fromMs);
                var e = Math.Min(ToMs(eUtc), toMs);
                if (e > s) segs.Add((s, e));
            }
        }
        var planned = Intervals.Union(segs);
        if (Intervals.Total(planned) <= 0) return (0, 0, false);

        var dt = await _repo.QueryDowntimeAsync(fromUtc, toUtc, null, null, flowName, ct);
        var nowMs = ToMs(DateTime.UtcNow);
        var plannedStop = dt
            .Where(e => string.Equals(e.Category, "planned", StringComparison.OrdinalIgnoreCase))
            .Select(e => (ToMs(e.StartAt), e.EndAt.HasValue ? ToMs(e.EndAt.Value) : nowMs));
        var ppt = Intervals.Subtract(planned, plannedStop);
        var pptMs = Intervals.Total(ppt);
        if (pptMs <= 0) return (0, 0, false);

        var nonPlanned = dt
            .Where(e => !string.Equals(e.Category, "planned", StringComparison.OrdinalIgnoreCase))
            .Select(e => (ToMs(e.StartAt), e.EndAt.HasValue ? ToMs(e.EndAt.Value) : nowMs));
        var downInPpt = Intervals.Total(Intervals.Intersect(ppt, nonPlanned));
        var runtimeMs = Math.Max(0, pptMs - downInPpt);
        return (pptMs, runtimeMs, true);
    }

    protected async Task<List<DateTime>> GetActiveLocalDatesAsync(string? flowName, DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<DateTime>();
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return result;
        try
        {
            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20");
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return result;

            var p = new DynamicParameters();
            p.Add("From", fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            p.Add("To", toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            var flowClause = "";
            if (!string.IsNullOrWhiteSpace(flowName)) { flowClause = " AND flowName = @Flow "; p.Add("Flow", flowName.Trim()); }
            var sql = $@"
                SELECT DISTINCT strftime('%Y-%m-%d', substr(recordedAt,1,19), 'localtime') AS D
                FROM dspFlowHistory
                WHERE COALESCE(IsIdle,0) = 0 AND recordedAt >= @From AND recordedAt < @To {flowClause}";
            var dates = await conn.QueryAsync<string>(sql, p);
            foreach (var s in dates)
                if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var d))
                    result.Add(DateTime.SpecifyKind(d.Date, DateTimeKind.Local));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[OEE] active local dates query failed"); }
        return result;
    }

    // ── 일자별/시간별 슬롯 헬퍼 ──────────────────────────────────────────────

    protected static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    protected static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    // (구 BuildDailySlot — 이벤트로그 kind 예산배분 — 은 벽시계 단일모델 전환[2026-07-06]으로 제거.
    //  Daily 는 이제 agg 벽시계 구간[RunWall/DownMaintWall/NonProd/Unmeasured]을 슬롯에 직접 합산한다.)

    // ── 공통 헬퍼 ────────────────────────────────────────────────────────────

    protected static (DateTime FromUtc, DateTime ToUtc) ResolveRange(DateTime? from, DateTime? to)
    {
        var toUtc = to.HasValue ? ToUtc(to.Value) : DateTime.UtcNow;
        var fromUtc = from.HasValue ? ToUtc(from.Value) : toUtc.AddHours(-24);
        if (fromUtc > toUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
        return (fromUtc, toUtc);
    }

    protected static DateTime ToUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime(),
    };
}

// ── 요청 DTO ─────────────────────────────────────────────────────────────────

public record ClassifyRequest(string? ReasonCode, string? Category);
public record CloseRequest(DateTime? EndAt);
public record BulkClassifyRequest(List<long> Ids, string? ReasonCode, string? Category);
public record BulkCloseRequest(List<long> Ids, DateTime? EndAt);
public record SetFaultRequest(bool IsFault);
public record BulkSetFaultRequest(List<long> Ids, bool IsFault);
public record ProductionRequest(DateTime? Date, string Flow, string? Shift, int Reject);
public record ManualQualityRequest(double? QualityPercent);
public record PlannedStopsRequest(List<PlannedStopWindowDto>? Windows);
public record PlannedStopsAutoRequest(bool Enabled);
public record ExcludedWeekdaysRequest(List<int>? Days);
public record ShiftExceptionRequest(string? Flow, DateTime? StartAt, DateTime? EndAt, string Kind, string? Note);
public record IdealCycleRequest(string Flow, int? IdealCycleTimeMs, string? Mode = null);
public record IdealCycleBatchRequest(List<IdealCycleRequest> Items);

public record OutputCountDto(int Count, string Mode);
public record OutputFlowStateDto(List<string> Flows, List<string> Selected);
public record OutputFlowSaveDto(List<string>? Flows);

public record IdealCycleRowDto(
    string FlowName,
    int? IdealCycleTimeMs,
    string? Source,
    int? RecommendedMs,
    int SampleCount,
    int? MinCt,
    int? MedianCt,
    int? AvgCt);

public record OeePlanTimeDto(
    string Source,
    double PlannedMs,
    double RuntimeMs,
    bool ShiftUserSet,
    string ShiftLabel,
    bool AutoAvailable,
    int? AutoStartHour,
    int? AutoEndHour,
    bool AutoCrosses,
    int AutoSampleCycles,
    int AutoSampleDays,
    int ActiveDays,
    int[] Histogram);

public sealed record OeeShiftSummaryDto(
    string? FlowName,
    DateTime FromUtc,
    DateTime ToUtc,
    double PeriodMs,
    double ScheduledMs,
    double PlannedStopMs,
    double PlannedProductionMs,
    double DowntimeMs,
    int DowntimeCount,
    double RunTimeMs,
    int? TotalCount,
    int? RejectCount,
    int? GoodCount,
    int? IdealCycleTimeMs,
    string? IdealCycleTimeSource,
    double? Availability,
    string? AvailabilityNote,
    double? Performance,
    string? PerformanceNote,
    double? Quality,
    string? QualityNote,
    string? QualitySource,
    double? Oee,
    string? OeeNote,
    string ShiftStart,
    string ShiftEnd,
    string ShiftType,
    string ShiftLabel,
    int FailureCount,
    double? Mtbf,
    string? MtbfNote,
    double? Mttr,
    string? MttrNote);
