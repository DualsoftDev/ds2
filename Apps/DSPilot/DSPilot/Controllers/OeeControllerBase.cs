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
    protected readonly HistoryMirrorService _mirror;
    protected readonly ILogger _logger;

    /// <summary>
    /// 정상이 아닌 사이클 행(고장 또는 비생산 후보) 선택 SQL — doc/28 §1 두 규칙, <b>사이클 행 단위</b>. <see cref="OeeMath.ClassifyCycle"/> 와
    /// 같은 규칙(SSOT 쌍) — 한쪽만 바꾸지 말 것. 정상 행 = <c>NOT (dtCond)</c> 로 상보 분할된다(둘 다에 들거나 어디에도 안 드는
    /// 행이 없어야 패스 2 의 '미귀속' 이 0 이다).
    /// <list type="bullet">
    ///   <item>완료 행(mt 있음): <c>mt &gt; @MtThr</c>(고장 경계 MT = 중앙 MT × 고장배수) 또는 <c>COALESCE(wt, ct−mt) ≥ @NpThr</c>(비생산 경계 WT)</item>
    ///   <item>불인정 행(mt NULL, ct = 시작~다음 시작): <c>ct &gt; @CtFaultThr</c>(중앙 CT × 고장배수) 또는 <c>ct ≥ @CtNpThr</c>(중앙 CT × 비생산배수)</item>
    /// </list>
    /// 비활성 절은 <see cref="ThrOff"/> 로 바인딩한다(0 금지 규약 — 0 이면 전 행이 후보). MT 기준선 미보유 flow 는 @MtThr/@CtFaultThr
    /// 비활성(고장 판별 불가), 완료 행이 없는 flow 는 @NpThr 비활성, 표본 게이트(완료 사이클 &lt; MinBaselineSamples) flow 는 네 절 전부
    /// 비활성(전부 정상). 종전 CT 축·WT/MT 2축+정지 밴드·신호 판별(doc/25·27)은 2026-09-11 폐기.
    /// <para>SSOT — 집계(<c>ComputeCycleAggregateCoreAsync</c>)와 계측 품질(<c>/api/oee/measurement-quality</c>)이 같은 문자열을 공유한다.</para>
    /// </summary>
    protected const string DtCondSql =
        "ct > 0 AND ((mt IS NOT NULL AND mt > @MtThr) OR (mt IS NULL AND ct > @CtFaultThr)"
        + " OR (mt IS NOT NULL AND COALESCE(wt, ct - mt) >= @NpThr) OR (mt IS NULL AND ct >= @CtNpThr))";

    /// <summary>dtCond 절 비활성 바인딩 값 — 어떤 실측 ms 보다 큰 값(0 을 넣으면 전 행이 후보가 되는 함정 방지).</summary>
    protected const double ThrOff = 9e15;

    protected OeeControllerBase(
        IOeeRepository repo,
        AppSettingsService settings,
        DsProjectService project,
        IDatabasePathResolver pathResolver,
        OeeCtStatsService ctStats,
        OeeAutoShiftInferenceService shiftInfer,
        OeeCommHealthService commHealth,
        OeeNonProdPatternService nonProdPattern,
        HistoryMirrorService mirror,
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
        _mirror = mirror;
        _logger = logger;
    }

    // ── 미러 라우팅 헬퍼 — 창이 미러 범위 안이면 인메모리 미러, 밖/미준비면 파일(기존 경로) ──

    private async Task<SqliteConnection> OpenSharedReadAsync(DateTime fromUtc)
    {
        var m = await _mirror.TryOpenPlcReadAsync(fromUtc, layerB: true);
        if (m is not null) return m;
        var conn = new SqliteConnection(
            $"Data Source={_pathResolver.GetSharedDbPath()};Mode=ReadWriteCreate;Default Timeout=20");
        await conn.OpenAsync();
        return conn;
    }

    private async Task<SqliteConnection> OpenOeeReadAsync(DateTime fromUtc, string oeeDbPath)
    {
        var m = await _mirror.TryOpenOeeReadAsync(fromUtc, layerB: true);
        if (m is not null) return m;
        var conn = new SqliteConnection($"Data Source={oeeDbPath};Mode=ReadOnly;Default Timeout=20");
        await conn.OpenAsync();
        return conn;
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
            await using var conn = await OpenSharedReadAsync(fromUtc);
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

    /// <summary>
    /// 고장·비생산으로 판정된 사이클 행(=failureCount 의 성분)을 정지 이벤트 로그 행으로 합성한다 (doc/28 사이클 단위).
    /// 이 소스는 oeeDowntimeEvent 테이블에 적히지 않아(계산만) '정지 이벤트 로그' 내역이 비었던 원인 —
    /// failureCount 와 동일한 ComputeCycleAggregate 경로(collectDowntimeCycles)로 수집해 건수를 정합시킨다.
    /// 합성 행은 DB 행이 아니므로 Id 를 음수(-1,-2,…)로 부여(고유 key). 사용자 전환(고장으로/비생산으로)은
    /// reclassify/set-fault API 가 실행 시점에 실제 이벤트 행으로 materialize 한다(행 단위 수동 라벨).
    /// 고장 행의 표시 구간은 <b>행 전체</b>[시작, 끝] — 초과분만 잡던 종전 구간·onset 어긋남은 사라진다(§2.1).
    /// NonProdScoped 는 DB 이벤트 행(수동 확정분 등)의 구분 표시(flow 스코프 비생산 겹침 판정)에 쓰라고 함께 반환한다.
    /// </summary>
    protected async Task<(List<OeeDowntimeDto> Rows, List<(string? Flow, double S, double E)> NonProdScoped)>
        GetOverThresholdCycleDowntimeAsync(
            string? flowName, DateTime fromUtc, DateTime toUtc, CancellationToken ct,
            IReadOnlySet<string>? flowFilter = null)
    {
        (flowName, flowFilter) = NormalizeOeeScope(flowName, flowFilter);
        var thresholds = await ResolveCtThresholdsAsync(branchView: true);
        var (plannedWindows, _, applyLongStop) = await ResolvePlannedWindowsAsync(thresholds, ct);
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct,
            collectDowntimeCycles: true, flowFilter: flowFilter);
        var nonProdScoped = agg.NonProdScoped ?? new List<(string? Flow, double S, double E)>();
        var cycles = agg.DowntimeCycles ?? new List<DowntimeCycleRow>();

        var sysMap = BuildFlowSystemMap();       // flowName → systemName (AASX 미로드/미매칭이면 빈 문자열)
        // 판정 근거 문구 — 집계와 같은 경계 함수(BuildFlowBounds → OeeMath.Resolve*BoundaryMs)로 환산한다.
        //   기준선은 부모(물리 설비) 키 — 분기는 부모 기준선을 공유(집계 MapF 규약과 동일).
        var nonProdMult = ResolveNonProdWtMultiplier();
        var faultMult = _settings.LoadSettings().OeeManual.ResolveFaultMtMultiplier();
        var bounds = BuildFlowBounds(thresholds, await _ctStats.ComputeMtThresholdAsync(), await _ctStats.ComputeWtBaselineAsync(),
            nonProdMult, faultMult);
        var branchMap = _settings.GetBranchVirtualMap();
        string Phys(string f) => branchMap.TryGetValue(f, out var bm) ? bm.Parent : f;

        var list = new List<OeeDowntimeDto>(cycles.Count + 8);
        var synthId = 0L;
        foreach (var c in cycles)
        {
            bounds.TryGetValue(c.Flow ?? "", out var b);
            var note = BuildCycleNote(c, b, nonProdMult, faultMult);
            var manual = string.Equals(c.Source, "manual", StringComparison.Ordinal);
            list.Add(new OeeDowntimeDto(
                Id: --synthId,                   // -1,-2,… : 합성(DB 없음) 표식 + x-for 고유 key
                SystemName: c.Flow is not null && sysMap.TryGetValue(c.Flow, out var s) ? s : "",
                FlowName: c.Flow,
                DeviceName: null,
                StartAt: DateTimeOffset.FromUnixTimeMilliseconds((long)c.StartMs).LocalDateTime,
                EndAt: DateTimeOffset.FromUnixTimeMilliseconds((long)c.EndMs).LocalDateTime,
                DurationMs: (long)(c.EndMs - c.StartMs),
                ReasonCode: c.NonProd ? OeeMath.NonProductionReasonCode : "cycle_overtime",
                Category: c.NonProd ? "nonproduction" : "unplanned",
                IsFailure: !c.NonProd,
                DetectSource: "over-cycle",
                SourceLogId: null,
                Note: note,
                Status: "recovered",
                ClassifySource: manual ? "manual"
                    : string.Equals(c.Source, "planned", StringComparison.Ordinal) ? "auto-planned"
                    : c.NonProd ? "auto-longstop" : "auto-cycle",
                IsNonProd: c.NonProd,
                NeedsReview: c.NeedsReview,
                Axis: c.Axis));
        }

        // ── 진행 중(열린 사이클) 행 (doc/26 · doc/28 §2.4) — 물리 설비당 1행. ──
        //   기준 = 열린 사이클의 head↑ 이후 경과 > 고장 경계(MT). 종전 "마지막 완료 후 경과 > 중앙 MT + 정지 경계"는 tail 후
        //   대기 중인 상태까지 포함했다 — 지금은 dspFlow.state 가 'Going'(head↑ 후 tail 미도달)인 flow 만 후보다(열린 대기는 표시 없음).
        //   구분(고장/비생산)·건수·MTBF 어디에도 안 들어가고(분모 밖), 다음 head 가 오면 완료 행으로 바뀌어 그때 분류된다.
        //   분기 소속은 완료 후에야 정해지므로 부모(물리 설비) 이름으로 한 줄만 낸다.
        if (agg.InProgressScoped is { Count: > 0 } ipRows)
        {
            var goingFlows = await LoadGoingFlowsAsync();   // null = 상태 조회 실패 → 경과만으로 판정(종전 폴백)
            var seenPhysical = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (ipFlow, ipS, ipE) in ipRows)
            {
                if (ipFlow is null) continue;
                if (!bounds.TryGetValue(ipFlow, out var b) || b.Gated || b.MtFault <= 0) continue;   // MT 기준 없음 = 판별 불가
                var physical = Phys(ipFlow);
                if (goingFlows is not null && !goingFlows.Contains(physical)) continue;           // 열린 대기(tail 완료) — 표시 없음
                var elapsed = ipE - ipS;
                if (elapsed <= b.MtFault) continue;
                if (!seenPhysical.Add(physical)) continue;
                var review = OeeMath.IsReviewPending(elapsed, b.CtNonProd);
                list.Add(new OeeDowntimeDto(
                    Id: --synthId,
                    SystemName: sysMap.TryGetValue(ipFlow, out var s2) ? s2 : "",
                    FlowName: physical,
                    DeviceName: null,
                    StartAt: DateTimeOffset.FromUnixTimeMilliseconds((long)ipS).LocalDateTime,
                    EndAt: null,
                    DurationMs: (long)elapsed,
                    ReasonCode: null,
                    Category: null,
                    IsFailure: false,
                    DetectSource: "in-progress",
                    SourceLogId: null,
                    Note: $"진행 중 — 사이클 시작(head↑) 후 {Dur(elapsed)} 경과 (고장 기준 {Dur(b.MtFault)} = 평소 동작 {Dur(b.MedianMt)} × {faultMult:0.#}배 초과). "
                          + "다음 사이클이 시작되면 완료 행으로 확정·분류됩니다. 집계 미반영(분모 밖)."
                          + (review ? " · 확인 필요(길이가 비생산 기준 이상)" : "")
                          + (physical != ipFlow ? " 분기 소속은 완료 후 확정." : ""),
                    Status: "open",
                    ClassifySource: "pending",
                    NeedsReview: review,
                    Axis: "mt"));
            }
        }
        return (list, nonProdScoped);
    }

    /// <summary>
    /// 지금 열린 사이클이 <b>Going</b>(head↑ 후 tail 미도달)인 flow 의 부모 이름 집합 — dspFlow.state 라이브 값. 조회 실패면 null
    /// (호출측은 경과만으로 판정하는 종전 폴백). tail 을 찍고 다음 head 를 기다리는 열린 대기는 '진행 중' 고장 후보가 아니다(doc/28 §2.4).
    /// </summary>
    private async Task<HashSet<string>?> LoadGoingFlowsAsync()
    {
        try
        {
            var dbPath = _pathResolver.GetSharedDbPath();
            if (!System.IO.File.Exists(dbPath)) return null;
            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Default Timeout=5");
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlow'");
            if (exists == 0) return null;
            var rows = await conn.QueryAsync<string>(
                "SELECT flowName FROM dspFlow WHERE state = @Going AND flowName IS NOT NULL", new { Going = FlowLatchBadge.Going });
            return new HashSet<string>(rows, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OEE] dspFlow 라이브 상태 조회 실패 — '진행 중' 은 경과만으로 판정");
            return null;
        }
    }

    /// <summary>지속시간 문구 — 화면(dspFmt.dur)·Excel(FormatMs)과 같은 규약: 상위 2단위, 1분 미만은 초(소수 1자리).</summary>
    protected static string Dur(double ms)
    {
        if (ms < 0) ms = 0;
        if (ms < 60_000) return (ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "초";
        if (ms < 3_600_000) return $"{(int)(ms / 60_000)}분 {(int)(ms % 60_000 / 1000)}초";
        if (ms < 86_400_000) return $"{(int)(ms / 3_600_000)}시간 {(int)(ms % 3_600_000 / 60_000)}분";
        return $"{(int)(ms / 86_400_000)}일 {(int)(ms % 86_400_000 / 3_600_000)}시간";
    }

    /// <summary>
    /// 정지 로그 행 노트(doc/28 §2.1 — 3종: 고장 MT 초과 / 고장 미완료 / 비생산 대기 초과, + 불인정 행 비생산). 사이클 전체 길이를
    /// 앞에 두고 평소 대비 초과량을 병기한다 — 고장 시간 표시가 행 전체라 "얼마나 늘어졌나"는 노트가 알려준다.
    /// </summary>
    private static string BuildCycleNote(DowntimeCycleRow c, FlowBounds b, double nonProdMult, double faultMult)
    {
        var prefix = c.Source switch
        {
            "manual" => "사용자 확정 · ",
            "planned" => "비생산 지정 시각대 시작 · ",
            _ => "",
        };
        string body;
        if (c.NonProd)
        {
            body = c.Axis == "wt" && c.WtMs is long wt
                ? $"대기 {Dur(wt)} (평소 {Dur(b.MedianWt)}) ≥ 비생산 기준 {Dur(b.WtNonProd)} = 평소 대기 × {nonProdMult:0.#}배"
                  + (b.WtNonProd > Math.Max(0, b.MedianWt) * nonProdMult + 0.5 ? $" (하한 {OeeMath.WtNonProdFloorCtMultiples:0}사이클 적용)" : "")
                : c.Axis == "ct"
                    ? $"완료 신호 없는 사이클 {Dur(c.CtMs)} ≥ 비생산 기준 {Dur(b.CtNonProd)} = 평소 사이클 {Dur(b.MedianCt)} × {nonProdMult:0.#}배"
                    : $"사이클 {Dur(c.CtMs)} — 비생산(분모 밖)";
        }
        else if (c.Axis == "mt" && c.MtMs is long mt)
        {
            body = $"사이클 {Dur(c.CtMs)} · 동작 {Dur(mt)} (평소 {Dur(b.MedianMt)}, +{Dur(Math.Max(0, mt - b.MedianMt))})"
                   + $" — 동작 초과 = 고장, 기준 {Dur(b.MtFault)} = 평소 동작 × {faultMult:0.#}배";
        }
        else if (c.Axis == "ct")
        {
            body = $"사이클 {Dur(c.CtMs)} · 완료 신호 없음 (평소 사이클 {Dur(b.MedianCt)}, 기준 {Dur(b.CtFault)} = 평소 사이클 × {faultMult:0.#}배)";
        }
        else
        {
            body = $"사이클 {Dur(c.CtMs)} — 고장";
        }
        var suffix = c.NeedsReview ? " · 확인 필요(길이가 비생산 기준 이상 — 끄고 간 정지가 아닌지 확인)" : "";
        return prefix + body + suffix;
    }

    /// <summary>
    /// ?system= 스코프 → 그 시스템의 flow 이름 집합(시스템 단위 묶음 조회, 2026-08-25 좌측 나브 시스템 화면).
    /// system 미지정 = null(전체 = 종전 동작). 지정했는데 시스템이 없거나 AASX 미로드면 <b>빈 집합</b> —
    /// 전체로 폴백하면 라인 수치가 그 시스템 것처럼 보이므로(가장 위험한 오해) 정직하게 0건으로 둔다.
    /// flow(설비) 필터가 함께 오면 호출측이 이 함수를 아예 부르지 않는다(설비 지정이 시스템보다 우선).
    /// </summary>
    protected HashSet<string>? ResolveSystemFlowSet(string? system)
    {
        if (string.IsNullOrWhiteSpace(system)) return null;
        var name = system.Trim();
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var sys in _project.GetActiveSystems())
            {
                if (!string.Equals(sys.Name, name, StringComparison.Ordinal)) continue;
                foreach (var f in _project.GetFlows(sys.Id)) set.Add(f.Name);
                break;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[OEE] system→flow 집합 해석 실패: {System}", name); }
        return set;
    }

    // flowName → systemName. AASX 미로드/예외 시 빈 맵(합성 행 systemName 은 빈 문자열로 폴백).
    private Dictionary<string, string> BuildFlowSystemMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var sys in _project.GetActiveSystems())
                foreach (var f in _project.GetFlows(sys.Id))
                    map[f.Name] = sys.Name;
            // 사이클 분기 — 가상 이름("부모_분기")도 부모의 시스템으로 매핑(정지 로그 합성 행 표기용).
            foreach (var kv in _settings.GetBranchVirtualMap())
                if (map.TryGetValue(kv.Value.Parent, out var sysName))
                    map[kv.Key] = sysName;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[OEE] flow→system map build failed (AASX 미로드?)"); }
        return map;
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
        public string? ClearedAt { get; set; }   // usertag 해소 시각(2026-08-21). NULL = 미해소.
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
            mtbfNote = "고장(분류 unplanned) 건수 0 — 평균 고장 간격 산출 불가.";
            mttrNote = "고장(분류 unplanned, 마감됨) 건수 0 — 평균 복구 시간 산출 불가.";
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
    /// <summary>dspFlow.movingStartName 은 "flow.Call" 형태 — Call 이름만 남긴다(dspCall.callName 과 맞추기 위해).</summary>
    protected static string? StripFlowPrefix(string? qualified, string flowName)
    {
        if (string.IsNullOrWhiteSpace(qualified)) return null;
        var p = flowName + ".";
        return qualified!.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? qualified[p.Length..] : qualified;
    }

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
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample, double MedianMs)>? ctThresholds = null,
        IReadOnlySet<string>? flowFilter = null)
    {
        var periodMs = (toUtc - fromUtc).TotalMilliseconds;
        if (periodMs < 0) periodMs = 0;

        // 사이클 분기 — 이벤트/생산수/카운트는 부모 이름 축(oee.db·plcTagLog 세계)이라 부모로 번역해 조회하고,
        // 사이클 집계(agg)만 가상 이름 그대로(코어가 분기 스코프 SQL 로 태운다).
        var dbFlowName = TranslateToDbFlow(flowName);
        var dbFilter = TranslateToDbFlows(flowFilter);

        // 시스템 스코프(flowFilter) — 라인 경로와 같은 구간 union 집계(flow별 합산은 동시 정지를 이중 계상, 실측 6배).
        var (downtimeMs, downtimeCount) = flowFilter is null
            ? await _repo.GetDowntimeAggregateAsync(fromUtc, toUtc, dbFlowName, ct)
            : await _repo.GetDowntimeAggregateForFlowsAsync(fromUtc, toUtc, dbFilter!, ct);
        int? totalCount = await CountFlowHistoryAsync(flowName, fromUtc, toUtc,
            dbFilter is null ? null : new HashSet<string>(dbFilter, StringComparer.Ordinal));
        int prodReject; bool hasReject;
        if (flowFilter is null)
            (_, _, prodReject, hasReject) =
                await _repo.QueryProductionAsync(fromUtc.ToLocalTime(), toUtc.ToLocalTime(), dbFlowName, ct);
        else
        {
            prodReject = 0; hasReject = false;
            foreach (var f in dbFilter!)
            {
                var (_, _, rj, hr) = await _repo.QueryProductionAsync(fromUtc.ToLocalTime(), toUtc.ToLocalTime(), f, ct);
                prodReject += rj; hasReject |= hr;
            }
        }

        var thresholds = ctThresholds ?? await ResolveCtThresholdsAsync(branchView: true);
        var (plannedWindows, plannedSource, applyLongStop) = await ResolvePlannedWindowsAsync(thresholds, ct);
        var evIntervals = await _repo.GetDowntimeIntervalsAsync(fromUtc, toUtc, dbFlowName, ct);
        if (flowFilter is not null)
        {
            var dbSet = new HashSet<string>(dbFilter!, StringComparer.OrdinalIgnoreCase);
            evIntervals = evIntervals
                .Where(x => x.FlowName is null || dbSet.Contains(x.FlowName))   // flow 미상(라인 귀속)은 보존
                .ToList();
        }
        var maintIv = evIntervals
            .Where(x => x.Kind is 0 or 2 && x.EndMs > x.StartMs)
            .Select(x => ((double)x.StartMs, (double)x.EndMs, x.FlowName))
            .ToList();
        var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct,
            maintIntervals: maintIv, flowFilter: flowFilter);

        // 가용성 A = 벽시계 단일모델(doc/28 §2.7): 가동 ÷ 생산가능(캘린더 − 비생산 − 미계측 − 진행 중), Σ_flow 양변 같은 축.
        //   비가동 = 생산가능 − 가동 = 유지보수 + 고장(+ 미귀속 0). 고장 행은 행 전체가 손실이라 초과분 라벨과 어긋날 일이 없다.
        //   수집된 정상 행만 분자 — 달력근사 폴백은 없다(사이클 0건이면 분모 0 → 산출 불가로 정직 표기).
        var (cycleA, cycleANote) = OeeMath.ComputeWallClockAvailability(agg.RunWallMs, agg.AvailableWallMs);
        double? availability = cycleA;
        string? availNote = cycleANote;
        string? availabilitySource = "cycle";

        // 동작 비중(Σmt/Σct) — 표준MT = 표준CT × 비중, 표준WT = 표준CT × (1−비중) 으로 쓰면
        // 표준MT + 표준WT = 표준CT 가 항등 성립한다(감쇠 가중과 무관).
        double? mtRatio = null;
        {
            var ratios = await _ctStats.ComputeMtRatioAsync(excludeUntilUtc: DateTime.Today.ToUniversalTime());
            foreach (var (k, v) in await _ctStats.ComputeMtRatioAsync()) ratios.TryAdd(k, v);
            if (flowName is not null) { if (ratios.TryGetValue(flowName, out var r1)) mtRatio = r1; }
            else
            {
                var vals = flowFilter is null
                    ? ratios.Values.ToList()
                    : ratios.Where(kv => flowFilter.Contains(kv.Key)).Select(kv => kv.Value).ToList();
                if (vals.Count > 0) mtRatio = vals.Average();
            }
        }

        // 성능 P — 표준치 = 14일 중앙 CT(agg.CtThresholdMs, doc/28 §2.2).
        var (performance, perfNote) = OeeMath.ComputeCyclePerformance(
            agg.NormalCount, agg.CtThresholdMs, agg.NormalCtMs);

        var manualQualityPct = _settings.LoadSettings().OeeManual.QualityPercent;
        var (quality, qualNote, qualitySource, rejectOut, goodOut) =
            OeeMath.ResolveQuality(manualQualityPct, totalCount, prodReject, hasReject);

        var (oee, oeeNote) = OeeMath.ComputeOee(availability, performance, quality, qualitySource);

        // onset = 고장 행 시작, 고장 사이클 평균 시간 = 고장 행 ct 평균 (doc/28 §2.1). 유지보수 과반 겹침 행은 둘 다에서 제외.
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
            NormalMtMs: agg.NormalMtMs,
            NormalWtMs: agg.NormalWtMs,
            MtRatio: mtRatio,
            IdleCtMs: agg.IdleCtMs,
            NormalCycleCount: agg.HasThreshold ? agg.NormalCount : (int?)null,
            CtThresholdMs: agg.CtThresholdMs,
            // 표시용 비생산 시간은 <b>구간 union</b>(NonProdWallMs) — CT 합산(PlannedCtMs)은 오염 시 사이클끼리
            // 겹쳐 창을 넘는다(실측 2026-08-21: 15.4시간 창에 70.7시간). A·TEEP 와 같은 축으로 맞춘다.
            //   PlannedCtMs 는 TEEP 분자 카빙 등 내부 계산에만 남긴다.
            PlannedDownMs: agg.NonProdWallMs,
            PlannedStopSource: agg.HasThreshold ? plannedSource : null,
            CtSampleCount: agg.HasThreshold ? agg.CtSampleMin : (int?)null,
            CtSampleLow: agg.HasThreshold && agg.CtSampleMin < OeeCtStatsService.ConfidentMinCleanCycles,
            IdleMaintCtMs: agg.IdleMaintCtMs,
            IdleCalendarMs: agg.IdleCalendarMs,
            CycleFlowCount: agg.FlowCount,
            UnmeasuredMs: agg.UnmeasuredMs,
            RunWallMs: agg.RunWallMs,
            AvailableWallMs: agg.AvailableWallMs,
            DownMaintWallMs: agg.DownMaintWallMs,
            NonProdWallMs: agg.NonProdWallMs,
            DownFaultWallMs: agg.DownFaultWallMs,
            InProgressWallMs: agg.InProgressWallMs,
            ReviewPendingCount: agg.ReviewPendingCount,
            ReviewPendingMs: agg.ReviewPendingMs,
            UnattributedWallMs: agg.UnattributedWallMs);
    }

    // ── 비생산 판정 모드 ─────────────────────────────────────────────────────

    // 비생산 시간대 해석 (2026-07-08 당일 판정 + 수동 지정 병행 모델 — 14일 학습창 KPI 적용·자동/수동 배타 토글 폐기):
    //   ① 당일 자동 판정 — 항상 켜짐(applyLongStop=true): 실측 10×CT 장시간 정지를 그때그때 비생산으로 분류.
    //      학습창이 못 덮는 불규칙 패턴을 당일 판정이 흡수하고, TEEP(캘린더)와 같은 실측 파티션 공유.
    //   ② 수동 지정 시간대(PlannedStops) — 있으면 추가로 "무조건 비생산"으로 자르는 보조 규칙(창 안=가동/정지 불문 지표 밖).
    //      자동이 못 잡는 임계 미만 반복 휴게·느린 CT 설비의 점심 등을 확정 지정.
    // Source: "auto"=자동만 / "both"=자동+지정. 자동 판정이 어긋나면 정지 이벤트 로그의 '비생산↔비가동
    // 보내기'(classifySource='manual')로 행 단위 확정 — ComputeCycleAggregateAsync 가 양방향 우선 적용.
    // (14일 학습 패턴 OeeNonProdPatternService 는 auto-pattern 참고 표시 전용으로 존치.)
    protected Task<(List<(int StartMin, int EndMin)> Windows, string Source, bool ApplyLongStop)>
        ResolvePlannedWindowsAsync(
            IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample, double MedianMs)> thresholds, CancellationToken ct)
    {
        var manual = _settings.LoadSettings().OeeManual.PlannedStops;
        return manual is { Count: > 0 }
            ? Task.FromResult((manual.Select(w => (w.StartMinutes, w.EndMinutes)).ToList(), "both", true))
            : Task.FromResult((new List<(int, int)>(), "auto", true));
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

    // (구 BuildExcludedWeekdayIntervalsMs[휴무 요일] 은 2026-07-08 당일 비생산 판정 모델로 제거 — 쉬는 날은
    //  사이클이 없어 10×CT 장시간 정지 규칙이 자동으로 비생산 처리한다.)

    // ── 사이클 분기(branch) 스코프 헬퍼 ─────────────────────────────────────
    //   가상 이름("부모_분기") 세계(OEE 열거·사이클 집계)와 부모 이름 세계(oee.db 이벤트·생산수·nav 모델)
    //   사이의 번역기. 분기 미사용이면 전부 항등 — 종전 경로와 완전 동일.

    /// <summary>가상 flow 이름 → 부모 flow 이름(이벤트/카운트 조회용). 분기 아님이면 그대로.</summary>
    protected string? TranslateToDbFlow(string? flowName)
        => flowName is not null && _settings.GetBranchVirtualMap().TryGetValue(flowName, out var m)
            ? m.Parent : flowName;

    /// <summary>flow 집합의 가상 이름들을 부모 이름으로 번역(중복 제거, 순서 무관). null → null.</summary>
    protected List<string>? TranslateToDbFlows(IReadOnlySet<string>? flowSet)
    {
        if (flowSet is null) return null;
        var map = _settings.GetBranchVirtualMap();
        if (map.Count == 0) return flowSet.ToList();
        return flowSet.Select(f => map.TryGetValue(f, out var m) ? m.Parent : f)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 시스템 스코프(부모 이름 집합)를 OEE 분기 뷰용으로 확장 — 분기 활성 부모는 그 가상(분기) 이름들로
    /// 치환한다(분기 뷰 thresholds 키와 교집합이 성립하도록). 분기 미사용이면 원본 그대로.
    /// </summary>
    protected IReadOnlySet<string>? ExpandBranchFlowSet(IReadOnlySet<string>? flowSet)
    {
        if (flowSet is null) return null;
        var map = _settings.GetBranchVirtualMap();
        if (map.Count == 0) return flowSet;
        var branched = _settings.GetBranchedParentFlows();
        var res = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in flowSet)
        {
            if (branched.Contains(f))
            {
                foreach (var kv in map)
                    if (string.Equals(kv.Value.Parent, f, StringComparison.OrdinalIgnoreCase))
                        res.Add(kv.Key);
            }
            else res.Add(f);
        }
        return res;
    }

    /// <summary>
    /// OEE 스코프 정규화 — flow 가 분기 활성 <b>부모</b>면(예: TEEP 드릴 링크) 분기 뷰에 그 항목이 없으므로
    /// "그 부모의 분기 집합 필터"로 폴백한다. flow 가 가상/일반이면 그대로.
    /// </summary>
    protected (string? FlowName, IReadOnlySet<string>? FlowSet) NormalizeOeeScope(
        string? flowName, IReadOnlySet<string>? flowSet)
    {
        flowSet = ExpandBranchFlowSet(flowSet);
        if (flowName is null || !_settings.GetBranchedParentFlows().Contains(flowName))
            return (flowName, flowSet);
        var virts = _settings.GetBranchVirtualMap()
            .Where(kv => string.Equals(kv.Value.Parent, flowName, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key);
        return (null, new HashSet<string>(virts, StringComparer.Ordinal));
    }

    // ── CT 이상치(임계) 산출 ─────────────────────────────────────────────────

    /// <param name="branchView">
    /// true = 설비효율(OEE) 열거 뷰: 분기 활성 flow 를 "부모_분기" 가상 항목으로 치환(부모 제외).
    /// false = 부모 뷰(TEEP/공용): 종전과 완전 동일 — 분기 도입 전후 수치 불변이 규약.
    /// </param>
    protected async Task<Dictionary<string, (double AvgMs, double P10Ms, int Sample, double MedianMs)>> ResolveCtThresholdsAsync(
        bool branchView = false)
    {
        var thr = await _ctStats.ComputeCtThresholdAsync(
            excludeUntilUtc: DateTime.Today.ToUniversalTime(),
            decayHalfLifeDays: 7.0, branchView: branchView);
        var thrToday = await _ctStats.ComputeCtThresholdAsync(branchView: branchView);
        foreach (var (flow, val) in thrToday)
            thr.TryAdd(flow, val);
        var settings = _settings.LoadSettings();
        // 수동 표준CT 는 부모 flow 이름으로 저장돼 있다 — 분기 뷰에선 부모 키를 되살리면 이중 계상이므로
        // 그 부모의 모든 가상(분기) 항목에 같은 값을 적용한다(분기별 수동 표준CT 는 아직 미지원).
        var branchSets = branchView
            ? settings.FlowCycle.BranchSets.Where(s => s.Branches.Count > 0)
                .ToDictionary(s => s.FlowName, StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var ov in settings.FlowCycle.Overrides)
        {
            if (string.IsNullOrWhiteSpace(ov.FlowName)) continue;
            var src = ov.IdealCycleTimeSource;
            var isManual = ov.IdealCycleTimeMs is > 0 && src != "auto" && src != "auto-median";
            if (!isManual) continue;
            var v = (ov.IdealCycleTimeMs!.Value, (double)ov.IdealCycleTimeMs!.Value, int.MaxValue);
            if (branchSets is not null && branchSets.TryGetValue(ov.FlowName, out var bs))
            {
                foreach (var b in bs.Branches)
                    thr[AppSettingsService.ComposeBranchFlowName(bs.FlowName, b.Name)] =
                        (v.Item1, v.Item2, v.Item3, (double)v.Item1);
            }
            else
            {
                thr[ov.FlowName] = (v.Item1, v.Item2, v.Item3, (double)v.Item1);
            }
        }
        return thr;
    }

    // ── 사이클기반 집계 ──────────────────────────────────────────────────────

    /// <summary>
    /// 정상이 아닌 사이클 행 1건(정지 로그 합성·미리보기·건수 정합용). 판정·표시 단위 = 행 전체(doc/28).
    /// Source: "auto"(규칙 판정) / "manual"(사용자 라벨 — 고장으로/비생산으로) / "planned"(비생산 지정 시각대 시작 행).
    /// Axis: "mt"(완료 행 동작 초과) / "wt"(완료 행 대기 초과) / "ct"(불인정 행 길이). NeedsReview = 고장 행 ∧ 길이 ≥ 비생산 경계(CT) ∧ 자동.
    /// Maintenance = 유지보수 이벤트 과반 겹침(고장 통계 제외, A 손실 유지) — 정지 로그엔 DB 이벤트 행이 있으므로 합성 목록엔 넣지 않는다.
    /// </summary>
    protected sealed record DowntimeCycleRow(
        string? Flow, double StartMs, double EndMs, double CtMs, bool NonProd,
        long? MtMs, long? WtMs, string Source, string Axis, bool NeedsReview);

    /// <summary>
    /// flow(thresholds 키 = 가상 이름 포함) 하나의 판정 경계·기준선 묶음 — dtCond 바인딩·행 판정·정지 로그 문구·칩 환산이
    /// 전부 이 하나를 본다(경계 SSOT, doc/28 §1). 기준선 키는 부모(물리 설비) flow 라 분기는 부모 기준선을 공유한다.
    /// <list type="bullet">
    ///   <item>MedianCt = 완료 행 기준선(중앙 CT, 부모 키) 우선, 없으면(tail 미정의) thresholds 의 전 행 중앙 CT.</item>
    ///   <item>Sample = 완료 사이클 표본(부모 키) 우선, 없으면 전 행 표본 — <see cref="OeeMath.MinBaselineSamples"/> 미만이면 Gated(전부 정상).</item>
    ///   <item>HasMt=false(중앙 MT 없음 = tail 미정의) → MtFault=CtFault=0: 고장 판별 불가, 비생산(CT 축)만.</item>
    /// </list>
    /// </summary>
    protected readonly record struct FlowBounds(
        string DbFlow, double MedianMt, double MedianWt, double MedianCt, double AvgCt, int Sample,
        bool HasMt, bool HasWt, bool Gated,
        double MtFault, double CtFault, double WtNonProd, double CtNonProd)
    {
        /// <summary>dtCond @MtThr — 비활성이면 <see cref="ThrOff"/>.</summary>
        public double MtThrBind => !Gated && MtFault > 0 ? MtFault : ThrOff;
        public double CtFaultThrBind => !Gated && CtFault > 0 ? CtFault : ThrOff;
        public double NpThrBind => !Gated && HasWt && WtNonProd > 0 ? WtNonProd : ThrOff;
        public double CtNpThrBind => !Gated && CtNonProd > 0 ? CtNonProd : ThrOff;
    }

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
        // ── 벽시계 단일모델(2026-07-06, doc/25 §3.1 flow별 분모 전환 → doc/28 §2.7) — 세 뷰(추이·정산·도넛) 공통 SSOT. ──
        double RunWallMs = 0,                                       // Σ가동(벽시계) = 정상 행 ∩ 그 flow 생산가능
        double AvailableWallMs = 0,                                 // Σ_flow 생산가능 = Σ(기간 − 미계측 − 비생산_flow − 진행 중 − 형제가동)
        double DownMaintWallMs = 0,                                 // Σ유지보수(비가동 ∩ 유지보수 이벤트)
        double NonProdWallMs = 0,                                   // Σ_flow 비생산(벽시계, 기간 클립·미계측 차감) — 도넛 세그먼트
        List<(double S, double E)>? RunWallIntervals = null,        // flow별 가동 구간 연결(concat, 합산 슬롯용 — union 아님)
        List<(double S, double E)>? DownMaintWallIntervals = null,  // flow별 유지보수 구간 연결(concat)
        // 고장 = 비가동 ∩ 고장 행 전체 구간(유지보수 차감 후). 비가동 − 유지보수 − 고장 = 미귀속(0 기대, 진단 지표).
        double DownFaultWallMs = 0,                                 // Σ고장 벽시계
        List<(double S, double E)>? DownFaultWallIntervals = null,  // flow별 고장 귀속 구간 연결(concat, daily 슬롯용)
        // 정상 아닌 행(고장·비생산) 개별 목록 — 정지 로그(oeeDowntimeEvent)에는 안 적히는 소스라, '정지 이벤트 로그' 내역이
        // failureCount 와 정합되도록 여기서 수집해 노출한다(collectDowntimeCycles).
        List<DowntimeCycleRow>? DowntimeCycles = null,
        List<(string? Flow, double S, double E)>? NonProdScoped = null,  // flow 귀속 비생산(지정창+판정+라벨) — 로그 구분/스코프 판정용
        // ── 성능 P 손실 분해(2026-08-21) — 정상 사이클의 실측 MT/WT 합(클립 없음, ct=mt+wt 항등 유지) ──
        double NormalMtMs = 0,
        double NormalWtMs = 0,
        // ── 진행 중(열린 사이클, 2026-09-08 doc/26) — flow 마지막 완료 사이클 이후 다음 head 가 없는 구간. ──
        //   가동·비가동·비생산 어느 쪽도 주장하지 않는 네 번째 상태(미계측과 같은 자리 — 분모 밖). 과거 창엔 0.
        double InProgressWallMs = 0,                                     // Σ_flow 진행 중 벽시계(기간·미계측·비생산 클립)
        List<(double S, double E)>? InProgressIntervals = null,          // flow별 진행 중 구간 연결(concat, daily 슬롯용)
        List<(string? Flow, double S, double E)>? InProgressScoped = null, // 로그용 — (flow, 열린 사이클 head↑, 지금) 클립 전
        // ── doc/28 (2026-09-11) ──
        double UnattributedWallMs = 0,                                   // Σ_flow 미귀속 = 비가동 − 유지보수 − 고장 (0 기대)
        Dictionary<string, double>? UnattributedByFlow = null,           // flow별 미귀속 — 계측 품질 진단 행
        int ReviewPendingCount = 0,                                      // '확인 필요' 고장 행 수(자동 판정 ∧ 길이 ≥ 비생산 경계 CT)
        double ReviewPendingMs = 0,                                      // 그 행들의 계측 길이 합
        int NonProdCount = 0,                                            // 비생산 행 수(자동+라벨+시각대)
        int FaultMtCount = 0, int FaultCtCount = 0,                      // 고장 축별 건수(미리보기 표기)
        int NonProdWtCount = 0, int NonProdCtCount = 0);                 // 비생산 축별 건수

    private sealed class CycleAggRow { public long NormalCt { get; set; } public long NormalCount { get; set; } public long NonProdNormalCt { get; set; } }
    private sealed class DtCycleRaw { public string? RecordedAt { get; set; } public long? Ct { get; set; } public long? Mt { get; set; } public long? Wt { get; set; } }

    // ── 사이클 집계 TTL 캐시 + single-flight ─────────────────────────────────
    // 폴링 엔드포인트 4종(summary/daily/actual/teep)과 ranking 의 flow별 루프가 같은 (기간,flow) 집계를
    // 요청마다 전량 재계산한다 — 동접 탭 수만큼 정비례 증폭되는 최대 항목. 결과는 10초 TTL 로 공유한다
    // (폴링 주기와 동일 = 체감 무손실). toUtc 는 보통 '지금'이라 키만 10초 격자로 양자화 — 재사용 시
    // 벽시계 합산이 최대 10초 어긋나지만 시간 단위 창의 % 지표에서 무시 가능. static 인 이유: 컨트롤러는
    // 요청마다 새로 만들어지므로 인스턴스 캐시는 무의미. 캐시 원본은 불변 취급 — 반환은 반드시 CloneAgg.
    private static readonly TimeSpan AggCacheTtl = TimeSpan.FromSeconds(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ExpiresUtc, CycleAgg Value)> s_aggCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<CycleAgg>>> s_aggInflight = new();

    private static string BuildAggKey(
        string? flowName, DateTime fromUtc, DateTime toUtc,
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample, double MedianMs)> thresholds,
        IReadOnlyList<(int StartMin, int EndMin)> plannedWindows, bool applyLongStop,
        bool collectRunIntervals, IReadOnlyList<(double S, double E, string? Flow)>? maintIntervals,
        bool collectNormalCycles, bool collectDowntimeCycles,
        double nonProdMult, double faultMult,
        IReadOnlyDictionary<string, double> mtThresholds,
        IReadOnlyDictionary<string, (double MedianWtMs, double MedianCtMs, int Sample)> wtBaselines,
        IReadOnlySet<string>? flowFilter)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append("v33|");   // 분모/분류 모델 버전(v33 = 두 규칙·사이클 단위 고장·불인정 행 CT 축·공백 삭제 2026-09-11, doc/28) — 모델 변경 배포 직후 L1 캐시 혼재 방지
                             // v32(2026-09-09): 정지·비생산 판정 CT축→WT축 전환(doc/27)
                             // v31(2026-09-08): 분기 최소 위반 판별
                             // v29(2026-08-27): 사이클 분기(branch) — 가상 flow("부모_분기") 스코프 집계 + 형제가동 카빙
                             // v28(2026-08-24): 고장 유발자 판별을 CT축 → MT축(평균MT×고장배수)으로 전환
        sb.Append(flowName ?? "*").Append('|').Append(fromUtc.Ticks).Append('|')
          .Append(toUtc.Ticks / (TimeSpan.TicksPerSecond * 10)).Append('|')  // 10초 격자
          .Append(applyLongStop ? '1' : '0').Append(collectRunIntervals ? '1' : '0')
          .Append(collectNormalCycles ? '1' : '0').Append(collectDowntimeCycles ? '1' : '0').Append('|')
          .Append(nonProdMult).Append('x').Append(faultMult).Append('|');   // 판정 배수 — 설정/미리보기 변경 즉시 반영
        foreach (var k in thresholds.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var v = thresholds[k];
            sb.Append(k).Append(':').Append(v.AvgMs).Append(':').Append(v.P10Ms).Append(':').Append(v.Sample).Append(':').Append(v.MedianMs).Append(';');
        }
        sb.Append('|');
        foreach (var k in mtThresholds.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            sb.Append(k).Append(':').Append(mtThresholds[k]).Append(';');   // MT 기준 — CT 임계와 같은 이유로 서명
        sb.Append('|');
        foreach (var k in wtBaselines.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var w = wtBaselines[k];
            sb.Append(k).Append(':').Append(w.MedianWtMs).Append(':').Append(w.MedianCtMs).Append(':').Append(w.Sample).Append(';');   // WT/CT 기준선·표본(게이트) — 같은 이유로 서명
        }
        sb.Append('|');
        foreach (var w in plannedWindows) sb.Append(w.StartMin).Append('-').Append(w.EndMin).Append(';');
        sb.Append('|');
        if (maintIntervals is not null)
            foreach (var m in maintIntervals) sb.Append(m.S).Append(':').Append(m.E).Append(':').Append(m.Flow).Append(';');
        sb.Append('|');
        if (flowFilter is not null)   // 시스템 스코프(모집단 축소) — 라인 집계와 키가 섞이면 스코프 수치가 라인에 오염
        {
            sb.Append("ff:");
            foreach (var k in flowFilter.OrderBy(x => x, StringComparer.Ordinal)) sb.Append(k).Append(';');
        }
        return sb.ToString();
    }

    private static CycleAgg CloneAgg(in CycleAgg v) => v with
    {
        OnsetsMs = new List<double>(v.OnsetsMs),
        RepairMsList = new List<double>(v.RepairMsList),
        NonProdIntervals = v.NonProdIntervals is null ? null : new List<(double S, double E)>(v.NonProdIntervals),
        RunIntervals = v.RunIntervals is null ? null : new List<(double S, double E)>(v.RunIntervals),
        IdleIntervals = v.IdleIntervals is null ? null : new List<(double S, double E)>(v.IdleIntervals),
        NormalCycles = v.NormalCycles is null ? null : new List<(double StartMs, double CtMs)>(v.NormalCycles),
        UnmeasuredIntervals = v.UnmeasuredIntervals is null ? null : new List<(double S, double E)>(v.UnmeasuredIntervals),
        RunWallIntervals = v.RunWallIntervals is null ? null : new List<(double S, double E)>(v.RunWallIntervals),
        DownMaintWallIntervals = v.DownMaintWallIntervals is null ? null : new List<(double S, double E)>(v.DownMaintWallIntervals),
        DownFaultWallIntervals = v.DownFaultWallIntervals is null ? null : new List<(double S, double E)>(v.DownFaultWallIntervals),
        DowntimeCycles = v.DowntimeCycles is null ? null : new List<DowntimeCycleRow>(v.DowntimeCycles),
        NonProdScoped = v.NonProdScoped is null ? null : new List<(string? Flow, double S, double E)>(v.NonProdScoped),
        InProgressIntervals = v.InProgressIntervals is null ? null : new List<(double S, double E)>(v.InProgressIntervals),
        InProgressScoped = v.InProgressScoped is null ? null : new List<(string? Flow, double S, double E)>(v.InProgressScoped),
        UnattributedByFlow = v.UnattributedByFlow is null ? null : new Dictionary<string, double>(v.UnattributedByFlow, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>비생산 판정 배수 — 사용자 설정(설비효율 현황) 정규화 값. 집계·문구·DTO 공용.</summary>
    protected double ResolveNonProdWtMultiplier()
        => _settings.LoadSettings().OeeManual.ResolveNonProdWtMultiplier();

    /// <summary>
    /// thresholds 키(가상 flow 포함)별 판정 경계·기준선(<see cref="FlowBounds"/>) — 경계 SSOT(doc/28 §1). 기준선은 부모(물리 설비)
    /// 키로 찾는다(분기는 부모 기준선 공유). 완료 행 기준선(중앙 WT·CT·표본)이 있으면 그것을, 없으면(tail 미정의) thresholds 의
    /// 전 행 중앙 CT·표본을 쓴다. MT 기준선이 없으면 고장 판별 불가(MtFault=CtFault=0).
    /// </summary>
    protected Dictionary<string, FlowBounds> BuildFlowBounds(
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample, double MedianMs)> thresholds,
        IReadOnlyDictionary<string, double> mtThresholds,
        IReadOnlyDictionary<string, (double MedianWtMs, double MedianCtMs, int Sample)> wtBaselines,
        double nonProdMult, double faultMult)
    {
        var branchMap = _settings.GetBranchVirtualMap();
        var res = new Dictionary<string, FlowBounds>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, th) in thresholds)
        {
            if (th.AvgMs <= 0) continue;
            var db = branchMap.TryGetValue(f, out var bm) ? bm.Parent : f;
            var hasWt = wtBaselines.TryGetValue(db, out var wb) && wb.MedianCtMs > 0;
            var hasMt = mtThresholds.TryGetValue(db, out var medMt) && medMt > 0;
            var medCt = hasWt ? wb.MedianCtMs : (th.MedianMs > 0 ? th.MedianMs : th.AvgMs);
            var sample = hasWt ? wb.Sample : th.Sample;
            var gated = sample < OeeMath.MinBaselineSamples;
            res[f] = new FlowBounds(
                DbFlow: db,
                MedianMt: hasMt ? medMt : 0,
                MedianWt: hasWt ? wb.MedianWtMs : 0,
                MedianCt: medCt,
                AvgCt: th.AvgMs,
                Sample: sample,
                HasMt: hasMt, HasWt: hasWt, Gated: gated,
                MtFault: hasMt ? OeeMath.ResolveMtFaultBoundaryMs(medMt, faultMult) : 0,
                CtFault: hasMt ? OeeMath.ResolveCtFaultBoundaryMs(medCt, faultMult) : 0,
                WtNonProd: hasWt ? OeeMath.ResolveWtNonProdBoundaryMs(wb.MedianWtMs, wb.MedianCtMs, nonProdMult) : 0,
                CtNonProd: OeeMath.ResolveCtNonProdBoundaryMs(medCt, nonProdMult));
        }
        return res;
    }

    protected async Task<CycleAgg> ComputeCycleAggregateAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc,
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample, double MedianMs)> thresholds,
        IReadOnlyList<(int StartMin, int EndMin)> plannedWindows, bool applyLongStop, CancellationToken ct,
        bool collectRunIntervals = false,
        IReadOnlyList<(double S, double E, string? Flow)>? maintIntervals = null,
        bool collectNormalCycles = false,
        bool collectDowntimeCycles = false,
        double? nonProdMultOverride = null,     // 판정 기준 미리보기 전용 — 저장 없이 배수를 바꿔 계산(ct-multipliers/preview)
        double? faultMultOverride = null,       // 고장 배수 미리보기 오버라이드 — 위와 동일 규약
        // 시스템 스코프(?system=) — 집계 <b>모집단</b>(targetFlows)만 이 집합으로 좁힌다.
        IReadOnlySet<string>? flowFilter = null)
    {
        var nonProdMult = ResolveNonProdWtMultiplier();
        // 오버라이드(미리보기)는 저장 전 what-if 계산 — 결과는 정상 계산과 동일 경로지만, 감지로그 materialize 는
        // 막는다(제안 배수의 판정이 oeeNonProdDetectionLog 에 영구 기록되면 TEEP/actual 이 오염).
        // 시스템 스코프 패스도 읽기 전용 — 라인 패스(flowName=null·필터 없음)의 자가치유(라인 스코프 invalidate)와
        // 섞이면 스코프 밖 flow 의 감지 행이 오폭되므로 기록 자체를 막는다(라인/설비 패스가 로그 신선도를 유지).
        var suppressDetectionLog = nonProdMultOverride is not null || faultMultOverride is not null || flowFilter is not null;
        if (nonProdMultOverride is double no && double.IsFinite(no))
            nonProdMult = Math.Clamp(no, OeeManualSettings.NonProdMultMin, OeeManualSettings.NonProdMultMax);

        // 고장 배수 — dtCond @MtThr/@CtFaultThr 에 쓰이므로 키에도 반드시 들어간다(빠지면 배수 변경 후 TTL 동안 구 기준 집계가
        // 계속 나온다). MT 기준 맵·WT/CT 기준선 맵도 CT 임계와 같은 이유로 키에 서명.
        var faultMult = faultMultOverride is double fo && double.IsFinite(fo)
            ? Math.Clamp(fo, OeeManualSettings.FaultMultMin, OeeManualSettings.FaultMultMax)
            : _settings.LoadSettings().OeeManual.ResolveFaultMtMultiplier();
        var mtThresholds = await _ctStats.ComputeMtThresholdAsync();
        var wtBaselines = await _ctStats.ComputeWtBaselineAsync();

        var key = BuildAggKey(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop,
            collectRunIntervals, maintIntervals, collectNormalCycles, collectDowntimeCycles, nonProdMult,
            faultMult, mtThresholds, wtBaselines, flowFilter);

        if (s_aggCache.TryGetValue(key, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
            return CloneAgg(hit.Value);

        Task<CycleAgg> ComputeSelfAsync() => ComputeCycleAggregateCoreAsync(
            flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct,
            collectRunIntervals, maintIntervals, collectNormalCycles, collectDowntimeCycles,
            nonProdMult, suppressDetectionLog, faultMult, mtThresholds, flowFilter, wtBaselines);

        var lazy = new Lazy<Task<CycleAgg>>(ComputeSelfAsync);
        var winner = s_aggInflight.GetOrAdd(key, lazy);
        if (ReferenceEquals(winner, lazy))
        {
            try
            {
                var v = await lazy.Value;
                s_aggCache[key] = (DateTime.UtcNow.Add(AggCacheTtl), v);
                if (s_aggCache.Count > 256)
                    foreach (var stale in s_aggCache.Where(e => e.Value.ExpiresUtc <= DateTime.UtcNow).ToList())
                        s_aggCache.TryRemove(stale.Key, out _);
                return CloneAgg(v);
            }
            finally
            {
                s_aggInflight.TryRemove(key, out _);
            }
        }

        try
        {
            var shared = await winner.Value;
            return CloneAgg(shared);
        }
        catch
        {
            // 계산 주체 요청이 취소(_repo 는 Scoped — 주체의 스코프/토큰에 묶임)되거나 실패한 경우
            // 예외를 공유하지 않고 이 요청의 스코프로 직접 재계산한다.
            var v = await ComputeSelfAsync();
            s_aggCache[key] = (DateTime.UtcNow.Add(AggCacheTtl), v);
            return CloneAgg(v);
        }
    }

    private async Task<CycleAgg> ComputeCycleAggregateCoreAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc,
        IReadOnlyDictionary<string, (double AvgMs, double P10Ms, int Sample, double MedianMs)> thresholds,
        IReadOnlyList<(int StartMin, int EndMin)> plannedWindows, bool applyLongStop, CancellationToken ct,
        bool collectRunIntervals = false,
        IReadOnlyList<(double S, double E, string? Flow)>? maintIntervals = null,
        bool collectNormalCycles = false,
        bool collectDowntimeCycles = false,
        // 판정 배수(호출측 ComputeCycleAggregateAsync 가 정규화해 전달) — doc/28 두 규칙: 비생산 = 중앙 WT × nonProdMult(완료 행) /
        // 중앙 CT × nonProdMult(불인정 행), 고장 = 중앙 MT × faultMult(완료 행) / 중앙 CT × faultMult(불인정 행). 성능 P 표준치 = 중앙 CT.
        double nonProdMult = OeeMath.NonProductionWtMultiplier,
        // 미리보기(배수 오버라이드) 계산 — 감지로그 materialize 금지(제안 배수 판정의 영구 기록 방지).
        bool suppressDetectionLog = false,
        double faultMult = OeeMath.FaultMtMultiplierDefault,
        IReadOnlyDictionary<string, double>? mtThresholds = null,
        // 시스템 스코프 — 모집단(targetFlows)만 축소.
        IReadOnlySet<string>? flowFilter = null,
        // flow(부모 키)별 14일 중앙 WT·중앙 CT·완료 표본 — 완료 행 경계·표본 게이트 소스. wrapper 가 키에 서명한 뒤 전달.
        IReadOnlyDictionary<string, (double MedianWtMs, double MedianCtMs, int Sample)>? wtBaselines = null)
    {
        mtThresholds ??= new Dictionary<string, double>();
        wtBaselines ??= new Dictionary<string, (double MedianWtMs, double MedianCtMs, int Sample)>();
        // ── 사이클 분기(2026-08-27) — 가상 이름("부모_분기") 해석. targetFlows/내부 귀속 키는 가상 이름을
        //    그대로 쓰고, "부모 이름 세계"와 만나는 지점(DB flowName 바인딩·유지보수·수동 라벨·MT/WT 기준선)만 dbFlow 로 번역한다.
        var branchMap = _settings.GetBranchVirtualMap();
        (string DbFlow, string? Branch) MapF(string f)
            => branchMap.TryGetValue(f, out var m) ? (m.Parent, m.Branch) : (f, null);
        // ── 판정 경계(doc/28 §1) — thresholds 키별 FlowBounds 하나. dtCond 바인딩·행 판정·감지 로그 스냅샷이 전부 이 맵을 쓴다. ──
        var bounds = BuildFlowBounds(thresholds, mtThresholds, wtBaselines, nonProdMult, faultMult);
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
            targetFlows = thresholds.Keys.Where(k => flowFilter is null || flowFilter.Contains(k)).ToList();
        if (targetFlows.Count == 0) return empty;

        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return empty;

        var fromStr = fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var toStr = toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        double normalCtMs = 0, idleCtMs = 0, plannedCtMs = 0, perfNumerator = 0;
        // 성능 P 손실을 동작(MT)/대기(WT)로 가르기 위한 실측 합 — 클립하지 않는다.
        //   ct = mt + wt 가 행마다 성립해야 손실 분해가 정확히 덧셈으로 갈리기 때문(L = L_MT + L_WT).
        double normalMtMs = 0, normalWtMs = 0;
        int normalCount = 0, dtEventCount = 0, nonProdCount = 0;
        int faultMtCount = 0, faultCtCount = 0, nonProdWtCount = 0, nonProdCtCount = 0;
        int reviewPendingCount = 0; double reviewPendingMs = 0;
        int ctSampleMin = int.MaxValue;
        bool hasThreshold = false;
        double thrSum = 0; int thrCount = 0;
        // 비생산 — flow 귀속(doc/25 §3.1). 수동 지정 시각대 창은 라인 공통 그대로 두고, 패스 2에서 각 flow 창에 합집합해 flow별
        // 생산가능 창을 만든다. 비생산으로 넘긴 행은 반드시 행 전체를 여기 등록한다(종전 시각대 시작 행이 등록 없이 빠져 '미귀속' 이 됐다).
        var plannedIvCommon = ExpandPlannedIntervalsMs(plannedWindows, fromUtc, toUtc);
        var nonProdByFlow = new Dictionary<string, List<(double S, double E)>>(StringComparer.Ordinal);
        void AddNonProdFor(string f, (double S, double E) seg)
        {
            if (seg.E <= seg.S) return;
            if (!nonProdByFlow.TryGetValue(f, out var l)) nonProdByFlow[f] = l = new List<(double S, double E)>();
            l.Add(seg);
        }
        var nonProdDetections = new List<OeeNonProdDetectionLog>();
        var runIntervals = collectRunIntervals ? new List<(double S, double E)>() : null;
        // 정상 사이클 (시작, CT) 목록 — 매트릭스(teep/matrix)가 시간버킷에 귀속시키는 원본. NormalCt(SQL) 분류와
        // 동일하게 비생산 시간대 시작분은 제외해, 버킷 합계 ≈ KPI 가동(NormalCtMs)이 되게 한다.
        var normalCycles = collectNormalCycles ? new List<(double StartMs, double CtMs)>() : null;
        // 정상 아닌 행 목록 — 정지 로그 내역 정합용(dtEventCount·nonProdCount 와 1:1, 유지보수 과반 행 제외).
        var downtimeCycles = collectDowntimeCycles ? new List<DowntimeCycleRow>() : null;

        // ── 사용자 라벨(doc/28 §2.6) — 정지 로그의 '고장으로/비생산으로' 전환(classifySource='manual', 행 단위 저장). ──
        //   toNonProdIv : 비생산 라벨(reasonCode='non_production'). toDownIv : 고장/유지보수 확정 라벨.
        //   재도출로 행 경계가 조금 움직여도 같은 행에 다시 붙도록 행과 <b>과반</b> 겹치는 라벨만 채택한다(내부 조인 허용치 —
        //   사용자 규칙이 아니다). 종전 교집합 카빙(forcedNp)은 행을 쪼개 잔여를 만들어 폐기. 우선순위: 고장 확정 > 비생산 라벨 > 시각대 > 자동.
        var toNonProdIv = new List<(string? Flow, double S, double E)>();
        var toDownIv = new List<(string? Flow, double S, double E)>();
        try
        {
            foreach (var (rf, rs, re, toNp) in await _repo.GetManualReclassIntervalsAsync(fromUtc, toUtc, ct))
                (toNp ? toNonProdIv : toDownIv).Add((rf, rs, re));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[OEE] 수동 라벨 조회 실패 — 자동 판정만 적용"); }
        static double LabelOverlapMs(List<(string? Flow, double S, double E)> src, string flow, double s, double e)
        {
            double sum = 0;
            foreach (var x in src)
            {
                if (x.Flow is not null && !string.Equals(x.Flow, flow, StringComparison.Ordinal)) continue;
                var o = Math.Min(x.E, e) - Math.Max(x.S, s);
                if (o > 0) sum += o;
            }
            return sum;
        }
        // maintIntervals = GetDowntimeIntervalsAsync 의 Kind 0|2 (= category 'planned' 이거나 isFailure=0)
        //   → 이름은 'maint' 지만 실제 의미는 "분류된 비-고장 정지" 집합. ① 정산 바 유지보수 분할 ② 고장 통계 제외(IsMaintenanceCovered).
        Dictionary<string, List<(double S, double E)>>? maintByFlow = null;
        if (maintIntervals is { Count: > 0 })
        {
            maintByFlow = maintIntervals.Where(x => !string.IsNullOrEmpty(x.Flow) && x.E > x.S)
                .GroupBy(x => x.Flow!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Intervals.Union(g.Select(x => (x.S, x.E)).ToList()), StringComparer.Ordinal);
        }
        double idleMaintCtMs = 0;
        var idleCalIntervals = new List<(double S, double E)>();
        // 고장 행의 flow별 귀속 — 패스 2에서 비가동을 [유지보수 / 고장(고장 행 덮임)] 2분할하기 위한 소스(행 전체 구간).
        var idleByFlow = new Dictionary<string, List<(double S, double E)>>(StringComparer.Ordinal);
        void AddIdleFor(string f, List<(double S, double E)> segs)
        {
            if (!idleByFlow.TryGetValue(f, out var list)) idleByFlow[f] = list = new List<(double S, double E)>();
            list.AddRange(segs);
        }
        static double OverlapMs(List<(double S, double E)>? iv, double s, double e)
        {
            if (iv is null) return 0;
            double sum = 0;
            foreach (var (a, b) in iv) { var o = Math.Min(b, e) - Math.Max(a, s); if (o > 0) sum += o; }
            return sum;
        }

        // ── 벽시계 단일모델(2026-07-06 도입, 2026-07-08 2-패스 전환): 생산가능 = 기간 − 비생산 − 미계측. ──
        //   비생산이 행 판정·라벨로 루프 중에 확정되므로, 생산가능 창과 flow별 가동/비가동 벽시계는 분류가 끝난 뒤(패스 2)에 잰다.
        double periodStartMs = ToMs(fromUtc), periodEndMs = ToMs(toUtc);
        var nowMs = ToMs(DateTime.UtcNow);
        var flowRunByFlow = new Dictionary<string, List<(double S, double E)>>(StringComparer.Ordinal);
        // 진행 중(열린 사이클, doc/26) — flow별 [마지막 완료 사이클 끝 = 열린 사이클 head↑, 지금) ∩ 기간. 패스 2에서 분모 밖으로 카빙.
        var inProgressByFlow = new Dictionary<string, (double S, double E)>(StringComparer.Ordinal);
        var inProgressScoped = new List<(string? Flow, double S, double E)>();
        // 형제가동(분기 전용) — 같은 부모의 다른 분기/미분류 정상 사이클 스팬. 패스 2에서 그 분기의
        // 생산가능(availF)에서 통째로 카빙된다(카빙 사슬: 미계측 ▸ 비생산 ▸ 형제가동 ▸ 진행 중 ▸ 비가동).
        var siblingRunByFlow = new Dictionary<string, List<(double S, double E)>>(StringComparer.Ordinal);

        const string dtCond = DtCondSql;
        var nonProdStartSql = BuildNonProductionStartSql(plannedWindows);

        try
        {
            await using var conn = await OpenSharedReadAsync(fromUtc);
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return empty;

            foreach (var f in targetFlows)
            {
                var th = thresholds[f];
                if (th.AvgMs <= 0 || !bounds.TryGetValue(f, out var b)) continue;
                hasThreshold = true;
                // 성능 P 표준치 = 14일 중앙 CT(doc/28 §2.2). 중앙값 미산출(옛 캐시 등)이면 평균 폴백.
                var thr = th.MedianMs > 0 ? th.MedianMs : th.AvgMs;
                thrSum += thr; thrCount++;
                ctSampleMin = Math.Min(ctSampleMin, b.Sample);

                // 분기 스코프 — DB 는 부모 flowName + branchName 라벨로 저장돼 있다. 분기 미사용이면
                // branchCond="" = 종전 SQL 그대로(옛 DB/미러에 branchName 컬럼이 없어도 안전).
                var (dbFlow, branch) = MapF(f);
                var branchCond = branch is null ? "" : " AND branchName = @Branch ";

                var p = new DynamicParameters();
                // dtCond 네 경계(doc/28 §1) — 비활성 절은 ThrOff. 표본 게이트 flow 는 네 절 전부 비활성(전부 정상).
                p.Add("From", fromStr); p.Add("To", toStr); p.Add("Flow", dbFlow);
                p.Add("MtThr", b.MtThrBind); p.Add("CtFaultThr", b.CtFaultThrBind);
                p.Add("NpThr", b.NpThrBind); p.Add("CtNpThr", b.CtNpThrBind);
                if (branch is not null) p.Add("Branch", branch);

                var aggRow = await conn.QueryFirstOrDefaultAsync<CycleAggRow>($@"
                    SELECT
                      COALESCE(SUM(CASE WHEN ct>0 AND NOT ({dtCond}) AND NOT ({nonProdStartSql}) THEN ct ELSE 0 END),0)  AS NormalCt,
                      COALESCE(SUM(CASE WHEN ct>0 AND NOT ({dtCond}) AND NOT ({nonProdStartSql}) THEN 1  ELSE 0 END),0)  AS NormalCount,
                      COALESCE(SUM(CASE WHEN ct>0 AND NOT ({dtCond}) AND ({nonProdStartSql}) THEN ct ELSE 0 END),0)  AS NonProdNormalCt
                    FROM dspFlowHistory
                    WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow{branchCond}", p);
                if (aggRow is not null)
                {
                    // 건수만 SQL 집계에서 취한다. CT 합은 아래 루프에서 기간 클립 후 누적 —
                    // SQL 의 SUM(ct) 는 기간 시작 이전으로 뻗은 사이클을 통째로 더해 짧은 창에서 초과를 만든다.
                    normalCount += (int)aggRow.NormalCount;
                    perfNumerator += aggRow.NormalCount * thr;
                }

                // 벽시계 가동 산출 위해 정상 사이클 구간은 항상 조회(collectRunIntervals/normalCycles 는 부가 수집).
                var flowRun = new List<(double S, double E)>();
                {
                    var runRows = await conn.QueryAsync<DtCycleRaw>($@"
                        SELECT recordedAt AS RecordedAt, ct AS Ct, mt AS Mt
                        FROM dspFlowHistory
                        WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow{branchCond}
                          AND ct > 0 AND NOT ({dtCond})", p);
                    foreach (var r in runRows)
                        if (r.Ct is long rc && rc > 0 && ParseUtcMs(r.RecordedAt) is double rrec)
                        {
                            flowRun.Add((rrec - rc, rrec));
                            runIntervals?.Add((rrec - rc, rrec));
                            // NormalCt(SQL)와 동일 기준 — 비생산 시간대 시작 사이클은 KPI 가동에서 빠지므로 여기서도 제외.
                            if (normalCycles is not null && !IsPlannedTimeOfDay(rrec - rc, plannedWindows))
                                normalCycles.Add((rrec - rc, rc));
                            // 기간 클립분만 CT 축에 적립. 시작 시각 판정(비생산 시간대)은 원래 시작으로 하고, 더하는 길이만 창 교집합으로 자른다.
                            var clipped = Math.Min(rrec, periodEndMs) - Math.Max(rrec - rc, periodStartMs);
                            if (clipped <= 0) continue;
                            if (IsPlannedTimeOfDay(rrec - rc, plannedWindows))
                            {
                                // 비생산 시각대에 시작한 정상 행 — 행 전체를 비생산 구간으로 등록(패스 2 생산가능에서 빠진다).
                                plannedCtMs += clipped;
                                AddNonProdFor(f, (Math.Max(rrec - rc, periodStartMs), Math.Min(rrec, periodEndMs)));
                            }
                            else
                            {
                                normalCtMs += clipped;
                                if (r.Mt is long mtv && mtv >= 0 && mtv <= rc)
                                {
                                    normalMtMs += mtv;
                                    normalWtMs += rc - mtv;   // wt = ct − mt (행 단위 항등)
                                }
                            }
                        }
                }
                // 벽시계 가동/비가동은 비생산 확정 후 패스 2(루프 아래)에서 — 여기선 flow별 정상 사이클 구간만 보관.
                flowRunByFlow[f] = flowRun;

                // ── 진행 중(열린 사이클) — 마지막 완료 사이클 이후 다음 head 가 아직 없는 구간(2026-09-08, doc/26). ──
                //   창 끝(To) 이후 기록된 행이 하나라도 있으면 창 끝 시점의 사이클은 이미 완료된 것(경계 사이클) → 진행 중 아님.
                //   없으면 [max(마지막 recordedAt, 창 시작), min(지금, 창 끝)) — 가동·비가동·비생산 어느 쪽도 주장하지 않는
                //   네 번째 상태(분모 밖, 미계측과 같은 자리). 부모(물리 설비) 축으로 잰다 — 열린 사이클의 분기 소속은 완료 후 정해진다.
                try
                {
                    var nextRec = await conn.ExecuteScalarAsync<string?>(
                        "SELECT MIN(recordedAt) FROM dspFlowHistory WHERE flowName = @Flow AND recordedAt >= @To", p);
                    if (string.IsNullOrEmpty(nextRec))
                    {
                        var lastRec = await conn.ExecuteScalarAsync<string?>(
                            "SELECT MAX(recordedAt) FROM dspFlowHistory WHERE flowName = @Flow AND recordedAt < @To", p);
                        if (ParseUtcMs(lastRec) is double lastMs)
                        {
                            var ipS = Math.Max(lastMs, periodStartMs);
                            var ipE = Math.Min(nowMs, periodEndMs);
                            if (ipE > ipS)
                            {
                                inProgressByFlow[f] = (ipS, ipE);
                                inProgressScoped.Add((f, lastMs, ipE));   // 로그용 — 시작은 열린 사이클 head↑(= 마지막 행 끝, 기간 클립 전)
                            }
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "[OEE] 진행 중 구간 조회 실패: {Flow}", f); }

                // 형제가동 수집(분기 전용) — 같은 부모의 다른 분기/미분류 정상 사이클 스팬. 형제 <b>정지</b>
                // 사이클(dtCond)은 카빙에서 제외한다: 물리 정지는 이 분기에도 손실로 남아야 한다(양쪽 표시).
                if (branch is not null)
                {
                    var sibRows = await conn.QueryAsync<DtCycleRaw>($@"
                        SELECT recordedAt AS RecordedAt, ct AS Ct, mt AS Mt
                        FROM dspFlowHistory
                        WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow
                          AND COALESCE(branchName,'') <> @Branch
                          AND ct > 0 AND NOT ({dtCond})", p);
                    var sib = new List<(double S, double E)>();
                    foreach (var r in sibRows)
                        if (r.Ct is long sc && sc > 0 && ParseUtcMs(r.RecordedAt) is double srec)
                            sib.Add((srec - sc, srec));
                    if (sib.Count > 0) siblingRunByFlow[f] = sib;
                }

                var rows = await conn.QueryAsync<DtCycleRaw>($@"
                    SELECT recordedAt AS RecordedAt, ct AS Ct, mt AS Mt, wt AS Wt
                    FROM dspFlowHistory
                    WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow{branchCond} AND {dtCond}
                    ORDER BY recordedAt", p);
                foreach (var r in rows)
                {
                    if (r.Ct is not long ctMsL || ctMsL <= 0) continue;
                    double cMs = ctMsL;
                    var recMs = ParseUtcMs(r.RecordedAt);
                    if (recMs is not double rec) continue;
                    double startMs = rec - cMs;

                    // ── 행 판정(doc/28 §1) — SQL dtCond 가 고른 행을 C# 규칙으로 다시 분류(SSOT 쌍). ──
                    var cls = OeeMath.ClassifyCycle(
                        (int?)r.Mt, (int?)r.Ct, (int?)r.Wt,
                        b.MtFault, b.CtFault, b.HasWt ? b.WtNonProd : 0, b.CtNonProd, b.Sample);
                    var axis = r.Mt is null ? "ct" : (cls == OeeMath.CycleClass.Fault ? "mt" : "wt");
                    // ── 사용자 라벨(행 단위, 과반 조인) > 비생산 지정 시각대(행 시작 시각) > 자동 판정(§2.6). ──
                    var source = "auto";
                    if (OeeMath.IsMajorityCovered(cMs, LabelOverlapMs(toDownIv, dbFlow, startMs, rec)))
                    { cls = OeeMath.CycleClass.Fault; source = "manual"; }
                    else if (OeeMath.IsMajorityCovered(cMs, LabelOverlapMs(toNonProdIv, dbFlow, startMs, rec)))
                    { cls = OeeMath.CycleClass.NonProduction; source = "manual"; }
                    else if (IsPlannedTimeOfDay(startMs, plannedWindows))
                    { cls = OeeMath.CycleClass.NonProduction; source = "planned"; }
                    if (cls is not (OeeMath.CycleClass.Fault or OeeMath.CycleClass.NonProduction))
                    {
                        // SQL 과 C# 규칙이 어긋난 행 — 정상으로 되돌릴 수 없다(정상 행 SQL 은 이미 지나갔다). 미귀속으로 남겨 진단에 드러낸다.
                        _logger.LogDebug("[OEE] dtCond/ClassifyCycle 불일치 flow={Flow} rec={Rec} cls={Cls}", f, r.RecordedAt, cls);
                        continue;
                    }

                    // 기간 클립 → 미계측 카빙(§3.4). 창 밖으로 뻗은 부분은 이 기간의 시간이 아니고, 수신 공백과 겹친 부분은
                    // 어떤 상태도 주장하지 않는다. startMs/rec 원값은 분류·로그 표시에 그대로 쓰고, 길이 적립만 자른다.
                    var rowSpan = new List<(double S, double E)>();
                    {
                        var cs = Math.Max(startMs, periodStartMs);
                        var ce = Math.Min(rec, periodEndMs);
                        if (ce > cs) rowSpan.Add((cs, ce));
                    }
                    if (rowSpan.Count == 0) continue;
                    var rowSegs = unmeasured.Count > 0 ? Intervals.Subtract(rowSpan, unmeasured) : rowSpan;
                    var measuredMs = Intervals.Total(rowSegs);
                    if (measuredMs <= 0) continue;                              // 전 구간 미계측 — 고장/비생산/onset 전부 미계상

                    if (cls == OeeMath.CycleClass.NonProduction)
                    {
                        // 비생산 — 행 전체가 분모 밖. 구간을 flow 비생산에 등록(패스 2 생산가능에서 빠짐). 자동 판정만 감지 로그 기록.
                        plannedCtMs += measuredMs;
                        foreach (var seg in rowSegs)
                        {
                            AddNonProdFor(f, seg);
                            if (source == "auto")
                                nonProdDetections.Add(NewNonProdDetection(
                                    dbFlow, seg.S, seg.E, b.HasWt && axis == "wt" ? b.MedianWt : b.MedianCt, "idle-cycle", nonProdMult));
                        }
                        nonProdCount++;
                        if (axis == "ct") nonProdCtCount++; else nonProdWtCount++;
                        downtimeCycles?.Add(new DowntimeCycleRow(f, startMs, rec, cMs, true, r.Mt, r.Wt, source, axis, false));
                        continue;
                    }

                    // 고장 — 행 전체가 A 손실. onset = 행 시작, 고장 사이클 시간 = 행 ct (doc/28 §2.1).
                    idleCtMs += measuredMs;
                    idleCalIntervals.AddRange(rowSegs);
                    AddIdleFor(f, rowSegs);
                    // 유지보수 이벤트(같은 부모 flow)와 겹친 만큼 유지보수로 귀속 — 과반이면 고장 통계 제외(A 손실은 유지).
                    var maintOverlapMs = Math.Min(measuredMs,
                        rowSegs.Sum(seg => OverlapMs(maintByFlow?.GetValueOrDefault(dbFlow), seg.S, seg.E)));
                    idleMaintCtMs += maintOverlapMs;
                    if (OeeMath.IsMaintenanceCovered(measuredMs, maintOverlapMs))
                        continue;                                   // 유지보수 확정 — 정지 로그엔 DB 이벤트 행이 있으므로 합성 생략
                    var needsReview = source == "auto" && OeeMath.IsReviewPending(cMs, b.CtNonProd);
                    if (needsReview) { reviewPendingCount++; reviewPendingMs += measuredMs; }
                    onsets.Add(startMs);
                    repairs.Add(cMs);
                    dtEventCount++;
                    if (axis == "ct") faultCtCount++; else faultMtCount++;
                    downtimeCycles?.Add(new DowntimeCycleRow(f, startMs, rec, cMs, false, r.Mt, r.Wt, source, axis, needsReview));
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
            var thrVals = targetFlows.Where(bounds.ContainsKey)
                .Select(f => thresholds[f].MedianMs > 0 ? thresholds[f].MedianMs : thresholds[f].AvgMs).Where(v => v > 0).ToList();
            displayThr = thrVals.Count > 0 ? thrVals.Average() : (double?)null;
        }

        // 미계측 조회가 실패(비신뢰)한 요청에선 스킵 — 카빙 안 된 블랙아웃 스팬이 비생산으로 영구 기록되는 오염 방지.
        // 감지 0건이어도 배치를 보낸다 — 자가치유(doc/25 §4.1)가 창 안의 stale 감지를 invalidate 마킹해야 하므로.
        if (applyLongStop && unmeasuredTrusted && !suppressDetectionLog)
            NonProdWriteQueueService.Enqueue(new NonProdWriteQueueService.Batch(
                nonProdDetections, fromUtc, toUtc,
                flowRunByFlow.Keys.Select(k => MapF(k).DbFlow)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                IncludeLineScope: flowName is null));   // 라인 패스만 라인 스코프('') 구 행 정리

        // ── 패스 2 — 벽시계 산출(doc/28 §2.7): 비생산(지정 창 + 판정 + 라벨)이 전부 확정된 뒤, flow별 생산가능 창을 각자 만들어
        //    가동/비가동/유지보수/고장을 잰다. 카빙 사슬: 미계측 ▸ 비생산 ▸ 형제가동 ▸ 진행 중 ▸ 비가동(= 유지보수 + 고장 + 미귀속).
        var periodIv = new List<(double S, double E)> { (periodStartMs, periodEndMs) };
        double availableWallMs = 0, nonProdWallMs = 0;
        double runWallMs = 0, downMaintWallMs = 0, downFaultWallMs = 0, unattributedWallMs = 0;
        double inProgressWallMs = 0;
        var inProgressIntervals = new List<(double S, double E)>();
        var runWallIntervals = new List<(double S, double E)>();
        var downMaintWallIntervals = new List<(double S, double E)>();
        var downFaultWallIntervals = new List<(double S, double E)>();
        var nonProdFlat = new List<(double S, double E)>();
        var nonProdScoped = new List<(string? Flow, double S, double E)>();
        var unattributedByFlow = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, flowRun) in flowRunByFlow)
        {
            // flow별 비생산 창 = 지정 시각대(라인 공통, 구간) ∪ 이 flow 의 비생산 행 구간.
            var npF = Intervals.Union(plannedIvCommon.Concat(nonProdByFlow.GetValueOrDefault(f) ?? new List<(double S, double E)>()).ToList());
            var availF = Intervals.Subtract(periodIv, npF.Concat(unmeasured).ToList());
            // 형제가동 카빙(사이클 분기, 2026-08-27) — 형제 분기가 돌던 시간은 이 분기의 생산가능이 아니다.
            if (siblingRunByFlow.TryGetValue(f, out var sib) && sib.Count > 0)
                availF = Intervals.Subtract(availF, Intervals.Union(sib));
            // 진행 중 카빙(doc/26) — 열린 사이클의 시간은 아직 아무 상태도 아니다.
            if (inProgressByFlow.TryGetValue(f, out var ip))
            {
                var ipIv = Intervals.Subtract(new List<(double S, double E)> { ip }, npF.Concat(unmeasured).ToList());
                if (ipIv.Count > 0)
                {
                    availF = Intervals.Subtract(availF, ipIv);
                    inProgressWallMs += Intervals.Total(ipIv);
                    inProgressIntervals.AddRange(ipIv);
                }
            }
            availableWallMs += Intervals.Total(availF);
            // 비생산 벽시계(도넛/표시) = 기간 클립 후 미계측 차감(미계측 우선 — daily 표시와 동일 규칙).
            var npClipped = Intervals.Subtract(Intervals.Intersect(npF, periodIv), unmeasured);
            nonProdWallMs += Intervals.Total(npClipped);
            nonProdFlat.AddRange(npClipped);
            foreach (var seg in npClipped) nonProdScoped.Add((f, seg.S, seg.E));
            // 가동 = 이 flow 정상 행 ∩ 생산가능. 비가동 = 생산가능 − 가동. 유지보수 = 비가동 ∩ 유지보수 이벤트(부모 키).
            // 고장 = (비가동 − 유지보수) ∩ 고장 행 전체 구간. 미귀속 = 나머지(0 기대 — 데이터 결함 진단).
            var flowRunAvail = Intervals.Intersect(Intervals.Union(flowRun), availF);
            runWallMs += Intervals.Total(flowRunAvail);
            runWallIntervals.AddRange(flowRunAvail);
            var flowDownIv = Intervals.Subtract(availF, flowRun);
            var flowMaintIv = Intervals.Intersect(flowDownIv,
                maintByFlow?.GetValueOrDefault(MapF(f).DbFlow) ?? new List<(double S, double E)>());
            downMaintWallMs += Intervals.Total(flowMaintIv);
            downMaintWallIntervals.AddRange(flowMaintIv);
            var flowIdleEv = idleByFlow.TryGetValue(f, out var idleF) ? Intervals.Union(idleF) : new List<(double S, double E)>();
            var flowNotMaint = Intervals.Subtract(flowDownIv, flowMaintIv);
            var flowFaultIv = Intervals.Intersect(flowNotMaint, flowIdleEv);
            downFaultWallMs += Intervals.Total(flowFaultIv);
            downFaultWallIntervals.AddRange(flowFaultIv);
            var ua = Intervals.Total(Intervals.Subtract(flowNotMaint, flowFaultIv));
            unattributedWallMs += ua;
            unattributedByFlow[f] = ua;
        }

        // 비가동 union(TEEP 정지 항) — 비생산·미계측·가동과 겹치면 안 된다(그 시간은 이미 다른 몫). 고장 행 전체 구간 기준(doc/28).
        var idleCalUnion = Intervals.Subtract(
            Intervals.Union(idleCalIntervals),
            Intervals.Union(nonProdFlat
                .Concat(unmeasured)
                .Concat(flowRunByFlow.Values.SelectMany(v => v))
                .ToList()));
        return new CycleAgg(normalCtMs, idleCtMs, normalCount, dtEventCount, displayThr, onsets, repairs, true, plannedCtMs,
            ctSampleMin == int.MaxValue ? 0 : ctSampleMin, nonProdFlat,
            runIntervals is not null ? Intervals.Union(runIntervals) : null,
            Math.Min(idleMaintCtMs, idleCtMs),
            Intervals.Total(idleCalUnion), thrCount,
            IdleIntervals: idleCalUnion, NormalCycles: normalCycles,
            UnmeasuredMs: unmeasuredMs, UnmeasuredIntervals: unmeasured,
            RunWallMs: runWallMs, AvailableWallMs: availableWallMs,
            DownMaintWallMs: downMaintWallMs,
            NonProdWallMs: nonProdWallMs,
            RunWallIntervals: runWallIntervals, DownMaintWallIntervals: downMaintWallIntervals,
            DownFaultWallMs: downFaultWallMs, DownFaultWallIntervals: downFaultWallIntervals,
            DowntimeCycles: downtimeCycles,
            NonProdScoped: nonProdScoped,
            NormalMtMs: normalMtMs, NormalWtMs: normalWtMs,
            InProgressWallMs: inProgressWallMs, InProgressIntervals: inProgressIntervals,
            InProgressScoped: inProgressScoped,
            UnattributedWallMs: unattributedWallMs, UnattributedByFlow: unattributedByFlow,
            ReviewPendingCount: reviewPendingCount, ReviewPendingMs: reviewPendingMs,
            NonProdCount: nonProdCount,
            FaultMtCount: faultMtCount, FaultCtCount: faultCtCount,
            NonProdWtCount: nonProdWtCount, NonProdCtCount: nonProdCtCount);
    }

    private static OeeNonProdDetectionLog NewNonProdDetection(
        string? flow, double onsetMs, double clearMs, double thrMs, string reason, double nonProdMult)
        => new()
        {
            FlowName = flow,
            OnsetAt = DateTimeOffset.FromUnixTimeMilliseconds((long)onsetMs).UtcDateTime,
            ClearAt = DateTimeOffset.FromUnixTimeMilliseconds((long)clearMs).UtcDateTime,
            DurationMs = (long)(clearMs - onsetMs),
            DetectionSource = "auto-10xct",   // 감지 규칙 식별자(dedup 키 일부) — 배수가 바뀌어도 규칙명은 유지, 적용 배수는 CtMultiplier 스냅샷
            DetectionReason = reason,
            CtThresholdMs = thrMs,            // 2026-09-09 부터 기준선 = 중앙 WT(미보유 flow 는 폴백 평균 CT) — 컬럼명은 호환 유지
            CtMultiplier = nonProdMult,
        };


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

    protected async Task<int> CountFlowHistoryAsync(string? flowName, DateTime fromUtc, DateTime toUtc,
        IReadOnlyCollection<string>? flowFilter = null)
    {
        if (flowName is null && flowFilter is { Count: 0 }) return 0;   // 시스템 미매칭 — 정직하게 0(전체 폴백 금지)
        var dbPath = _pathResolver.GetSharedDbPath();
        if (!System.IO.File.Exists(dbPath)) return 0;
        try
        {
            await using var conn = await OpenSharedReadAsync(fromUtc);

            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
            if (exists == 0) return 0;

            var p = new DynamicParameters();
            p.Add("From", fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            p.Add("To", toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            var flowClause = "";
            if (!string.IsNullOrWhiteSpace(flowName))
            {
                // 분기 가상 이름이면 부모 + branchName 라벨로 좁힌다(가동 수 = 그 분기의 사이클만).
                var name = flowName.Trim();
                if (_settings.GetBranchVirtualMap().TryGetValue(name, out var bm))
                {
                    flowClause = " AND flowName = @Flow AND branchName = @Branch ";
                    p.Add("Flow", bm.Parent);
                    p.Add("Branch", bm.Branch);
                }
                else
                {
                    flowClause = " AND flowName = @Flow ";
                    p.Add("Flow", name);
                }
            }
            else if (flowFilter is not null)
            {
                flowClause = " AND flowName IN @Flows ";   // 시스템 스코프 — Dapper 리스트 확장(부모 이름 축)
                p.Add("Flows", TranslateToDbFlows(new HashSet<string>(flowFilter, StringComparer.Ordinal)));
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
            await using var conn = await OpenSharedReadAsync(fromUtc);

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
            await using var conn = await OpenSharedReadAsync(fromUtc);
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

    /// <summary>커스텀 기간 스팬 상한(일) — UI 클램프(shell.js DSP_MAX_RANGE_DAYS)와 동일 값. 인메모리 미러 창(63일)보다 작게 유지.</summary>
    protected const int MaxRangeDays = 62;

    protected static (DateTime FromUtc, DateTime ToUtc) ResolveRange(DateTime? from, DateTime? to)
    {
        var toUtc = to.HasValue ? ToUtc(to.Value) : DateTime.UtcNow;
        var fromUtc = from.HasValue ? ToUtc(from.Value) : toUtc.AddHours(-24);
        if (fromUtc > toUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
        // UI 는 자체 클램프하지만 외부 API 소비자가 수년 창을 요청하는 것을 서버에서도 방어(끝 기준으로 시작을 당김).
        if ((toUtc - fromUtc).TotalDays > MaxRangeDays)
            fromUtc = toUtc.AddDays(-MaxRangeDays);
        return (fromUtc, toUtc);
    }

    protected static DateTime ToUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime(),
    };

    // ── 계측 품질(사이클 제외·누락률) — OEE 지표와 별개 축 ─────────────────────

    private sealed class MeasureQualityRow
    {
        public long Total { get; set; }
        public long Excluded { get; set; }
        public long Incomplete { get; set; }
        public long Idle { get; set; }
        public long Unclassified { get; set; }   // 분기 미분류(branchName NULL) — 분기 활성 flow 만 계수
    }

    /// <summary>
    /// 설비(Flow)별 사이클 제외 현황. 판정은 <see cref="DtCondSql"/> SSOT 를 그대로 재사용해 "가동에서 빠진
    /// 사이클 수"와 화면 표시가 어긋나지 않게 한다(경계 = <see cref="FlowBounds"/>, doc/28).
    /// 임계 보유(14일 표준 CT &gt; 0) flow 만 모집단 — 임계 없는 flow 는 판정 자체가 불가라 제외율이 무의미하다.
    /// <para>Total=0 인 flow 는 비율을 null 로 둔다 — 사이클이 없는 기간에 "제외 0%"(=완벽)로 보이면
    /// 수집 정지와 정상 가동이 같은 화면이 된다(doc/21 §10 정직성).</para>
    /// <para>미귀속 시간(doc/28 §2.7) — 같은 스코프의 집계 1회에서 flow별 '비가동 − 유지보수 − 고장' 잔여를 받아 붙인다.
    /// 행이 연속이라 0 이어야 정상이고, 0 이 아니면 행 겹침·누락·심박이 못 덮은 재시작 조각 등 데이터 결함 위치다.</para>
    /// </summary>
    protected async Task<OeeMeasureQualityDto> ComputeMeasureQualityAsync(
        string? flowName, DateTime fromUtc, DateTime toUtc,
        Dictionary<string, (double AvgMs, double P10Ms, int Sample, double MedianMs)> thresholds,
        CancellationToken ct,
        IReadOnlySet<string>? flowFilter = null)
    {
        var nonProdMult = ResolveNonProdWtMultiplier();
        var mqFaultMult = _settings.LoadSettings().OeeManual.ResolveFaultMtMultiplier();
        var bounds = BuildFlowBounds(thresholds, await _ctStats.ComputeMtThresholdAsync(), await _ctStats.ComputeWtBaselineAsync(),
            nonProdMult, mqFaultMult);
        var branchedParentsMq = _settings.GetBranchedParentFlows();   // 분기 활성 flow — 미분류 계수 대상
        var rows = new List<OeeMeasureQualityRowDto>();

        // 미귀속 시간 — 스코프 집계 1회(TTL 캐시 공유). 실패해도 계측 품질 본체는 진행(진단 항목만 0).
        Dictionary<string, double>? unattributedByFlow = null;
        try
        {
            var (plannedWindows, _, applyLongStop) = await ResolvePlannedWindowsAsync(thresholds, ct);
            var agg = await ComputeCycleAggregateAsync(flowName, fromUtc, toUtc, thresholds, plannedWindows, applyLongStop, ct,
                flowFilter: flowFilter);
            unattributedByFlow = agg.UnattributedByFlow;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "[OEE] 계측 품질 — 미귀속 시간 집계 실패(진단 항목 생략)"); }

        var dbPath = _pathResolver.GetSharedDbPath();
        if (System.IO.File.Exists(dbPath))
        {
            try
            {
                await using var conn = await OpenSharedReadAsync(fromUtc);
                var exists = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlowHistory'");
                if (exists > 0)
                {
                    // 경계(head/tail) + Call 발화 횟수 — 경계 진단용. 같은 공유 DB 라 추가 연결이 없다.
                    var bnd = new Dictionary<string, (string? Head, string? Tail)>(StringComparer.OrdinalIgnoreCase);
                    var goingByFlowCall = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var b in await conn.QueryAsync<(string? FlowName, string? Head, string? Tail)>(
                            "SELECT flowName AS FlowName, movingStartName AS Head, movingEndName AS Tail FROM dspFlow"))
                            if (!string.IsNullOrWhiteSpace(b.FlowName))
                                bnd[b.FlowName!] = (StripFlowPrefix(b.Head, b.FlowName!), StripFlowPrefix(b.Tail, b.FlowName!));
                        foreach (var g in await conn.QueryAsync<(string? FlowName, string? CallName, long GoingCount)>(
                            "SELECT flowName AS FlowName, callName AS CallName, COALESCE(goingCount,0) AS GoingCount FROM dspCall"))
                        {
                            if (string.IsNullOrWhiteSpace(g.FlowName) || string.IsNullOrWhiteSpace(g.CallName)) continue;
                            if (!goingByFlowCall.TryGetValue(g.FlowName!, out var m))
                                goingByFlowCall[g.FlowName!] = m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            m[g.CallName!] = (int)Math.Min(g.GoingCount, int.MaxValue);
                        }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "[OEE] 경계 진단 소스 조회 실패"); }

                    // 모집단 = 구성된 flow 전체(dspFlow). 임계 보유분만 세면 "측정이 아예 안 되는 설비"가
                    // 목록에서 사라져 화면이 '전부 양호'로 읽힌다 — 그게 가장 숨겨선 안 되는 상태다.
                    var configured = new List<string>();
                    var flowTableExists = await conn.ExecuteScalarAsync<long>(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dspFlow'");
                    if (flowTableExists > 0)
                        configured = [.. (await conn.QueryAsync<string>(
                            "SELECT flowName FROM dspFlow WHERE flowName IS NOT NULL AND flowName <> ''"))
                            .Where(_project.IsModelFlow)];
                    var targets = configured
                        .Concat(thresholds.Where(kv => kv.Value.AvgMs > 0).Select(kv => kv.Key))
                        .Where(f => flowName is null || string.Equals(f, flowName, StringComparison.OrdinalIgnoreCase))
                        .Where(f => flowFilter is null || flowFilter.Contains(f))   // 시스템 스코프
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToList();

                    // 경계 진단 — 관측 가능한 증상만으로 판정한다(래치 자격 같은 내부 상태에 의존하지 않는다).
                    //   no-signal  : 경계 Call 자체가 안 돈다 → 경계가 실제 동작과 무관
                    //   no-cycle   : 경계 Call 은 도는데 사이클이 0건 → 후보 모호(래치 비활성) 또는 순서 반대
                    //   skip-cycle : head:tail 발화 비율이 1:1 에서 20% 이상 벗어남 → 격사이클 tail(CT 2배 위험)
                    (string? Issue, string? H, string? T, int HG, int TG) Diagnose(string f, int cycles)
                    {
                        var (h, t) = bnd.TryGetValue(f, out var b) ? b : (null, null);
                        goingByFlowCall.TryGetValue(f, out var gm);
                        int hg = h is not null && gm is not null && gm.TryGetValue(h, out var a) ? a : 0;
                        int tg = t is not null && gm is not null && gm.TryGetValue(t, out var c2) ? c2 : 0;
                        if (h is null || t is null) return ("no-signal", h, t, hg, tg);
                        if (hg == 0 || tg == 0) return ("no-signal", h, t, hg, tg);
                        if (cycles == 0) return ("no-cycle", h, t, hg, tg);
                        var ratio = tg > 0 ? hg / (double)tg : 0;
                        if (ratio > 1.2 || ratio < 0.8) return ("skip-cycle", h, t, hg, tg);
                        return (null, h, t, hg, tg);
                    }
                    var fromStr = fromUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                    var toStr = toUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                    foreach (var f in targets)
                    {
                        ct.ThrowIfCancellationRequested();
                        // 임계 미보유(클린샘플 0) = 측정 불가 — 판정 경계가 없어 제외율을 계산할 수 없다.
                        // 0% 로 채우지 않고 Measurable=false 행으로 남긴다(정상과 구별).
                        if (!bounds.TryGetValue(f, out var fb))
                        {
                            var dg0 = Diagnose(f, 0);
                            rows.Add(new OeeMeasureQualityRowDto(
                                FlowName: f, TotalCycles: 0, NormalCycles: 0, ExcludedCycles: 0,
                                IncompleteCycles: 0, IdleCycles: 0, ExclusionRate: null, IncompleteRate: null,
                                ThresholdMs: 0, Measurable: false,
                                BoundaryIssue: dg0.Issue, HeadCall: dg0.H, TailCall: dg0.T,
                                HeadGoingCount: dg0.HG, TailGoingCount: dg0.TG));
                            continue;
                        }
                        // 분기 활성 flow — 미분류(branchName NULL) 사이클을 함께 계수한다. 분기 미사용이면
                        // 컬럼을 참조하지 않아(상수 0) branchName 없는 옛 DB/미러에서도 안전.
                        var isBranched = branchedParentsMq.Contains(f);
                        var unclassifiedSel = isBranched
                            ? "COALESCE(SUM(CASE WHEN branchName IS NULL THEN 1 ELSE 0 END),0)"
                            : "0";
                        var p = new DynamicParameters();
                        p.Add("From", fromStr); p.Add("To", toStr); p.Add("Flow", f);
                        p.Add("MtThr", fb.MtThrBind); p.Add("CtFaultThr", fb.CtFaultThrBind);
                        p.Add("NpThr", fb.NpThrBind); p.Add("CtNpThr", fb.CtNpThrBind);
                        var q = await conn.QueryFirstOrDefaultAsync<MeasureQualityRow>($@"
                            SELECT
                              COUNT(*)                                                          AS Total,
                              COALESCE(SUM(CASE WHEN {DtCondSql} THEN 1 ELSE 0 END),0)          AS Excluded,
                              COALESCE(SUM(CASE WHEN mt IS NULL THEN 1 ELSE 0 END),0)           AS Incomplete,
                              COALESCE(SUM(CASE WHEN COALESCE(IsIdle,0)=1 THEN 1 ELSE 0 END),0) AS Idle,
                              {unclassifiedSel}                                                 AS Unclassified
                            FROM dspFlowHistory
                            WHERE recordedAt >= @From AND recordedAt < @To AND flowName = @Flow AND ct > 0", p);
                        var total = (int)(q?.Total ?? 0);
                        var dg = Diagnose(f, total);
                        var excluded = (int)(q?.Excluded ?? 0);
                        var incomplete = (int)(q?.Incomplete ?? 0);
                        var unclassified = (int)(q?.Unclassified ?? 0);
                        rows.Add(new OeeMeasureQualityRowDto(
                            FlowName: f,
                            TotalCycles: total,
                            NormalCycles: Math.Max(0, total - excluded),
                            ExcludedCycles: excluded,
                            IncompleteCycles: incomplete,
                            IdleCycles: (int)(q?.Idle ?? 0),
                            ExclusionRate: total > 0 ? excluded / (double)total : null,
                            IncompleteRate: total > 0 ? incomplete / (double)total : null,
                            ThresholdMs: fb.MtFault,          // 고장 경계(MT). 0 = MT 기준선 없음(고장 판별 불가)
                            Measurable: true,
                            BoundaryIssue: dg.Issue, HeadCall: dg.H, TailCall: dg.T,
                            HeadGoingCount: dg.HG, TailGoingCount: dg.TG,
                            Branched: isBranched,
                            UnclassifiedCycles: unclassified,
                            UnclassifiedRate: total > 0 ? unclassified / (double)total : null,
                            UnattributedWallMs: unattributedByFlow is not null && unattributedByFlow.TryGetValue(f, out var ua) ? ua : 0,
                            BaselineSampleCount: fb.Sample,
                            BaselineGated: fb.Gated));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OEE] 계측 품질 집계 실패");
            }
        }

        var sumTotal = rows.Sum(r => r.TotalCycles);
        var sumExcluded = rows.Sum(r => r.ExcludedCycles);
        var sumIncomplete = rows.Sum(r => r.IncompleteCycles);
        return new OeeMeasureQualityDto(
            TotalCycles: sumTotal,
            NormalCycles: rows.Sum(r => r.NormalCycles),
            ExcludedCycles: sumExcluded,
            IncompleteCycles: sumIncomplete,
            ExclusionRate: sumTotal > 0 ? sumExcluded / (double)sumTotal : null,
            IncompleteRate: sumTotal > 0 ? sumIncomplete / (double)sumTotal : null,
            UnmeasurableFlowCount: rows.Count(r => !r.Measurable),
            Flows: rows);
    }
}

// ── 요청 DTO ─────────────────────────────────────────────────────────────────

public record ClassifyRequest(string? ReasonCode, string? Category);
public record CloseRequest(DateTime? EndAt);
public record BulkClassifyRequest(List<long> Ids, string? ReasonCode, string? Category);
public record BulkCloseRequest(List<long> Ids, DateTime? EndAt);
// Flow/StartAt/EndAt: 합성 행(이상치 초과 사이클, id 없음) 지원 — reclassify 와 동일하게 실제 이벤트 행을
// materialize 한 뒤 분류한다(2026-07-16, doc/25 — 의도된 정지가 이상치로 잡혔을 때 유지보수 해제 가능해야 함).
public record SetFaultRequest(bool IsFault, string? Flow = null, DateTime? StartAt = null, DateTime? EndAt = null);
public record BulkSetFaultRequest(List<long> Ids, bool IsFault);
public record ProductionRequest(DateTime? Date, string Flow, string? Shift, int Reject);
public record ManualQualityRequest(double? QualityPercent);
public record PlannedStopsRequest(List<PlannedStopWindowDto>? Windows);
// (구 PlannedStopsAutoRequest[자동/수동 배타 토글] 은 2026-07-08 병행 모델로 폐기 — 자동 판정 상시 + 지정 시간대 추가 적용.)
// 정지 이벤트 재분류(비생산↔비가동 보내기). Id>0 = 기존 이벤트, Id 없음/음수 = 합성 행(over-cycle) — Flow/StartAt/EndAt 로
// 실제 이벤트 행을 materialize 한 뒤 분류한다. ToNonProd=true → 비생산(A 분모 밖), false → 비가동(고장 기본).
public record ReclassifyDowntimeRequest(long? Id, string? Flow, DateTime? StartAt, DateTime? EndAt, bool ToNonProd);
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
