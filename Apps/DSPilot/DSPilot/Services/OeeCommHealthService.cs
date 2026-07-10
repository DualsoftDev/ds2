// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;
using DSPilot.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>
/// 통신 헬스 심박 + '미계측(수신 공백)' 구간 산출 (doc/22 §3.4).
///
/// 문제: plcTagLog 는 태그 <b>변화</b> 시에만 기록되는 change-log 라, "기계가 멈춤"과 "수신이 끊김"이
/// 둘 다 무행(無行)으로 보여 사후에 구분할 수 없다. 수신 공백을 정지/비생산으로 해석하면 OEE 가 거짓말을 한다
/// (17시간 VPN 단절이 '비생산 → 가용성 100%'로 미화된 2026-07-04 사례).
///
/// 해법: DSPilot 이 살아있는 동안 60초마다 PLC 어댑터 연결 상태(에이전트 보고 ▸ 직접 TCP 핑 폴백)를
/// oee.db(oeeCommHealthLog)에 심박으로 영속한다. '미계측' = 심박이 없거나(앱 다운) plcOk=0(PLC 미연결)인 구간.
/// OEE 집계는 이 구간을 가동/비가동/비생산 어디에도 넣지 않고 별도 표기한다 — 모르는 시간을 아는 척하지 않는다.
///
/// 소급 불가(의도): 심박이 시작된 시점(로그 최초 행) 이전 기간에는 미계측을 주장하지 않는다 — 과거는
/// 판정 근거가 없으므로 기존 동작 그대로. 판정 방향은 전부 보수적(불확실 = 미계측 아님):
///   · 핑 대상 미설정(시뮬레이션 등) = 정상 취급, · 3분 미만 공백 = 보고 안 함(재시작/일시 지터 허용).
/// oee.db 에 두는 이유 = plc.db 전체초기화(rebuild)에도 계측 이력이 보존되어야 하기 때문.
/// </summary>
public sealed class OeeCommHealthService : BackgroundService
{
    /// <summary>심박 주기(ms).</summary>
    public const double SampleIntervalMs = 60_000;
    /// <summary>plcOk 심박 1개가 보증하는 계측 창(ms) — 2.5×주기, 샘플 1회 유실은 공백으로 안 봄.</summary>
    public const double CoverWindowMs = 150_000;
    /// <summary>이 길이(ms) 미만의 공백은 미계측으로 보고하지 않음 — 앱 재시작·일시 지터 허용(보수).</summary>
    public const double MinReportGapMs = 180_000;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MemoTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EpochTtl = TimeSpan.FromMinutes(5);
    private static readonly DateTime EpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IDatabasePathResolver _pathResolver;
    private readonly PlcConnectionStatusTracker _tracker;
    private readonly PlcPingService _ping;
    private readonly HubSubscriberService _hub;
    private readonly HistoryMirrorService _mirror;
    private readonly ILogger<OeeCommHealthService> _logger;

    // 조회 memo — uptime 페이지가 10초 폴링으로 같은 범위를 반복 조회하므로 짧은 TTL 로 흡수.
    private readonly object _memoLock = new();
    private readonly Dictionary<(long FromMs, long ToMs), (DateTime AtUtc, List<(double S, double E)> Result)> _memo = new();
    // 심박 최초 시각(ms) — 미계측 판정의 하한(그 전 기간은 판정 근거 없음). 캐시 + TTL 재조회.
    private double? _epochMs;
    private DateTime _epochCheckedUtc = DateTime.MinValue;
    private bool _tableEnsured;

    public OeeCommHealthService(
        IDatabasePathResolver pathResolver,
        PlcConnectionStatusTracker tracker,
        PlcPingService ping,
        HubSubscriberService hub,
        HistoryMirrorService mirror,
        ILogger<OeeCommHealthService> logger)
    {
        _pathResolver = pathResolver;
        _tracker = tracker;
        _ping = ping;
        _hub = hub;
        _mirror = mirror;
        _logger = logger;
    }

    private string OeeDbPath()
    {
        var shared = _pathResolver.GetSharedDbPath();
        var dir = System.IO.Path.GetDirectoryName(shared);
        return string.IsNullOrEmpty(dir) ? "oee.db" : System.IO.Path.Combine(dir, "oee.db");
    }

    private static string Iso(DateTime utc) => SqliteDateTimeHelpers.ToSqliteUtcString(utc);
    private static string IsoMs(double ms) => Iso(EpochUtc.AddMilliseconds(ms));
    private static double ToMs(DateTime utc) => (utc - EpochUtc).TotalMilliseconds;

    // ── 심박 writer ────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[OEE] comm health heartbeat starting (interval={Sec}s → oeeCommHealthLog)",
            SampleIntervalMs / 1000);
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await SampleOnceAsync(stoppingToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "[OEE] comm health sample failed"); }

                try { await Task.Delay(TimeSpan.FromMilliseconds(SampleIntervalMs), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task SampleOnceAsync(CancellationToken ct)
    {
        var plcOk = await ResolvePlcOkAsync(ct);
        await using var conn = new SqliteConnection($"Data Source={OeeDbPath()};Mode=ReadWriteCreate;Default Timeout=20");
        await conn.OpenAsync(ct);
        if (!_tableEnsured)
        {
            await EnsureTableAsync(conn);
            _tableEnsured = true;
        }
        var id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO oeeCommHealthLog (sampledAt, plcOk) VALUES (@At, @Ok) RETURNING id",
            new { At = Iso(DateTime.UtcNow), Ok = plcOk ? 1 : 0 });
        // 레포 우회 writer — 미러 write-through 를 여기서 직접(파일 read-back, 멱등).
        await _mirror.ReplicateOeeAsync("oeeCommHealthLog", "id = @Id", new { Id = id });
    }

    internal static async Task EnsureTableAsync(SqliteConnection conn)
    {
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS oeeCommHealthLog (
              id        INTEGER PRIMARY KEY AUTOINCREMENT,
              sampledAt TEXT NOT NULL,
              plcOk     INTEGER NOT NULL
            )");
        await conn.ExecuteAsync(
            "CREATE INDEX IF NOT EXISTS idx_oeeCommHealth_time ON oeeCommHealthLog(sampledAt)");
    }

    /// <summary>
    /// 현재 수신 상태. 판정 순서:
    ///   ① Hub 연결 + 에이전트 보고 있음 → 보고 기준(하나라도 끊김=down, comm blackout 배너와 동일).
    ///   ② 핑 대상 미설정(PlcConnection.json 없음 = 시뮬레이션/미구성) → true(미계측 주장 안 함 — 보수).
    ///   ③ PLC 는 설정돼 있는데 Hub(Promaker.Agent) 단절 → false — 태그 수신 경로 자체가 죽어 있어 PLC 가
    ///      핑에 응답해도 수신은 0 이다(에이전트 다운이 §3.4 문제 정의의 명시 케이스). 핑만 믿으면 '계측됨' 오기록.
    ///   ④ Hub 연결 + 어댑터 보고 부재(모니터링 비활성 등) → 직접 TCP 핑 폴백.
    /// </summary>
    private async Task<bool> ResolvePlcOkAsync(CancellationToken ct)
    {
        var hubConnected = _hub.CurrentStatus == HubConnectionState.Connected;
        var reported = _tracker.CurrentStatuses;
        if (hubConnected && reported.Count > 0) return reported.All(s => s.IsConnected);

        try
        {
            var pings = await _ping.ProbeAsync(ct);
            if (pings.Count == 0) return true;   // PLC 미구성(시뮬레이션) — 정상 취급
            if (!hubConnected) return false;      // PLC 구성됨 + 수신 경로(Hub) 단절 = 미계측
            return pings.All(p => p.Connected);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OEE] comm health ping fallback failed — 상태 미상은 정상 취급(보수)");
            return true;
        }
    }

    // ── 미계측 reader ──────────────────────────────────────────────────────

    /// <summary>
    /// [fromUtc, min(toUtc, now)) 중 '미계측' 구간(UTC epoch ms, Union·정렬)을 반환.
    /// 심박 최초 시각 이전 기간은 판정하지 않는다(빈 결과 방향 — 소급 주장 금지).
    /// Trusted=false 는 조회 실패 폴백(빈 결과)이라는 뜻 — 호출측은 이때 카빙 결과에 의존하는
    /// 영속 기록(비생산 감지 로그 materialize 등)을 스킵해야 한다(오염 방지). 실패 결과는 memo 에 넣지 않는다.
    /// </summary>
    public async Task<(List<(double S, double E)> Intervals, bool Trusted)> TryGetUnmeasuredIntervalsAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var fromMs = ToMs(fromUtc.Kind == DateTimeKind.Local ? fromUtc.ToUniversalTime() : fromUtc);
        var toMs = ToMs(toUtc.Kind == DateTimeKind.Local ? toUtc.ToUniversalTime() : toUtc);
        var capMs = Math.Min(toMs, ToMs(DateTime.UtcNow));
        if (capMs <= fromMs) return (new List<(double S, double E)>(), true);

        var key = ((long)fromMs, (long)toMs);
        lock (_memoLock)
        {
            if (_memo.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.AtUtc < MemoTtl)
                return (hit.Result, true);
        }

        List<(double S, double E)> result;
        try
        {
            result = await QueryUnmeasuredAsync(fromMs, capMs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OEE] unmeasured interval query failed — 미계측 없음으로 폴백(비신뢰, memo 미기록)");
            return (new List<(double S, double E)>(), false);
        }

        lock (_memoLock)
        {
            if (_memo.Count > 64) _memo.Clear();
            _memo[key] = (DateTime.UtcNow, result);
        }
        return (result, true);
    }

    private async Task<List<(double S, double E)>> QueryUnmeasuredAsync(double fromMs, double capMs, CancellationToken ct)
    {
        var dbPath = OeeDbPath();
        if (!System.IO.File.Exists(dbPath)) return new List<(double S, double E)>();

        // 심박 epoch (최초 행) — 이전 기간은 판정 근거 없음. 테이블 미존재도 동일 취급.
        // ★epoch 프로브는 파일 고정 — 미러는 63일 창으로 트림돼 MIN(sampledAt)이 진짜 epoch 가 아니다.
        if (_epochMs is null || DateTime.UtcNow - _epochCheckedUtc > EpochTtl)
        {
            await using var fileConn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Default Timeout=20");
            await fileConn.OpenAsync(ct);
            var exists = await fileConn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='oeeCommHealthLog'");
            if (exists == 0) return new List<(double S, double E)>();
            var minAt = await fileConn.ExecuteScalarAsync<string?>("SELECT MIN(sampledAt) FROM oeeCommHealthLog");
            _epochMs = ParseMs(minAt);
            _epochCheckedUtc = DateTime.UtcNow;
        }
        if (_epochMs is not double epochMs) return new List<(double S, double E)>();

        var effFrom = Math.Max(fromMs, epochMs);
        if (capMs <= effFrom) return new List<(double S, double E)>();

        // 샘플 조회는 창이 미러 범위 안이면 인메모리 미러에서(같은 SQL, 밖이면 파일 폴백).
        var queryFromUtc = EpochUtc.AddMilliseconds(effFrom - CoverWindowMs);
        var conn = await _mirror.TryOpenOeeReadAsync(queryFromUtc, layerB: true);
        if (conn is null)
        {
            conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Default Timeout=20");
            await conn.OpenAsync(ct);
        }
        await using var _ = conn;

        // 커버 창이 범위 시작에 걸치는 직전 샘플까지 포함해 조회.
        var rows = await conn.QueryAsync<HealthRow>(@"
            SELECT sampledAt AS SampledAt, plcOk AS PlcOk
            FROM oeeCommHealthLog
            WHERE sampledAt >= @From AND sampledAt < @To
            ORDER BY sampledAt",
            new { From = IsoMs(effFrom - CoverWindowMs), To = IsoMs(capMs) });

        var samples = new List<(double SampleMs, bool PlcOk)>();
        foreach (var r in rows)
            if (ParseMs(r.SampledAt) is double t)
                samples.Add((t, r.PlcOk != 0));

        return ComputeUnmeasured(effFrom, capMs, samples, CoverWindowMs, MinReportGapMs);
    }

    private sealed class HealthRow { public string? SampledAt { get; set; } public long PlcOk { get; set; } }

    private static double? ParseMs(string? s)
    {
        var dt = SqliteDateTimeHelpers.FromSqliteUtcString(s);
        if (dt is not DateTime d) return null;
        // FromSqliteUtcString 은 Kind=Local(로컬 벽시계)로 돌려준다 — UTC 축으로 정규화(ToMs Kind 함정).
        var utc = d.Kind == DateTimeKind.Local ? d.ToUniversalTime()
            : d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return (utc - EpochUtc).TotalMilliseconds;
    }

    /// <summary>
    /// 순수 판정 함수(테스트 대상): plcOk=true 샘플 1개가 [t, t+coverWindowMs) 를 계측으로 보증하고,
    /// [rangeStart, rangeEnd) 중 보증되지 않은 잔여가 미계측. minReportGapMs 미만 조각은 버린다(보수).
    /// plcOk=false 샘플은 아무것도 보증하지 않는다(그 시각 PLC 미연결 = 미계측).
    /// </summary>
    public static List<(double S, double E)> ComputeUnmeasured(
        double rangeStartMs, double rangeEndMs,
        IReadOnlyList<(double SampleMs, bool PlcOk)> samples,
        double coverWindowMs, double minReportGapMs)
    {
        var gaps = new List<(double S, double E)>();
        if (rangeEndMs <= rangeStartMs) return gaps;

        // ok 샘플 커버 창 → 정렬 병합(Union)
        var covers = samples.Where(s => s.PlcOk)
            .Select(s => (S: s.SampleMs, E: s.SampleMs + coverWindowMs))
            .OrderBy(x => x.S).ToList();
        var merged = new List<(double S, double E)>();
        foreach (var c in covers)
        {
            if (merged.Count > 0 && c.S <= merged[^1].E)
                merged[^1] = (merged[^1].S, Math.Max(merged[^1].E, c.E));
            else
                merged.Add(c);
        }

        // 범위 내 보수(complement)
        var cur = rangeStartMs;
        foreach (var (s, e) in merged)
        {
            if (e <= cur) continue;
            if (s >= rangeEndMs) break;
            if (s > cur) gaps.Add((cur, Math.Min(s, rangeEndMs)));
            cur = Math.Max(cur, e);
            if (cur >= rangeEndMs) break;
        }
        if (cur < rangeEndMs) gaps.Add((cur, rangeEndMs));

        gaps.RemoveAll(g => g.E - g.S < minReportGapMs);
        return gaps;
    }
}
