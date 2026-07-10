// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Dapper;
using DSPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

/// <summary>appsettings "HistoryMirror" 섹션. Enabled=false 가 킬스위치(전 경로 파일 폴백).</summary>
public sealed class HistoryMirrorOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>미러 보관 창(일). UI 60일 프리셋 + 커스텀 상한 62일 + 여유.</summary>
    public int WindowDays { get; set; } = 63;
    /// <summary>레포 층(choke-point) 읽기 라우팅 on/off — 단계 배포용.</summary>
    public bool RouteLayerA { get; set; } = true;
    /// <summary>서비스/컨트롤러 인라인 읽기 라우팅 on/off — 단계 배포용.</summary>
    public bool RouteLayerB { get; set; } = true;
    /// <summary>소형 테이블(dspFlow/dspCall 등) 전체 재복사 주기.</summary>
    public int SnapshotIntervalSeconds { get; set; } = 3;
    /// <summary>파일↔미러 정합성 프로브 주기(분). 불일치 시 자가치유 재로드.</summary>
    public int VerifyIntervalMinutes { get; set; } = 5;
    public int TrimIntervalMinutes { get; set; } = 60;
    /// <summary>미러 메모리 소프트캡(MB). 초과 시 미러를 영구 강등(파일 폴백)하고 에러 로그.</summary>
    public int SoftCapMb { get; set; } = 400;
}

/// <summary>
/// 63일 인메모리 SQLite 미러 — 기간별 조회(OEE/알람/추이)의 쿼리 단가를 "총 누적 행수 비례"에서
/// "창 고정"으로 바꾼다. 파일 DB(plc.db/oee.db)가 SSOT 이고 미러는 파생 사본:
///   - 읽기: 요청 창이 미러 창 안이면 미러 커넥션, 밖이면 null(호출측이 기존 파일 경로 사용).
///     기존 SQL 문자열은 무변경 — 시맨틱 드리프트 없음.
///   - 동기화: ①write-through 대상(dspFlowHistory/userTagAlertLog/oeeDowntimeEvent/oeeCommHealthLog)은
///     파일 커밋 직후 영향 행을 파일에서 read-back 복제(<see cref="ReplicatePlcAsync"/> 계열 —
///     동일문 재실행이 아니라 SSOT 재조회라 id 발산·비결정 함수 문제가 원천 차단되고 멱등),
///     ②소형/저빈도 테이블 6종은 3초 주기 전체 스냅샷(쓰기 접점 폭증 방지, ≤3초 staleness 허용),
///     ③대량 치환(재계산 replace/일별집계 rebuild)은 구간 재복제.
///   - 자가치유: 5분 프로브(행수/MAX id 파일 대조) 불일치·복제 예외 → 전체 재로드. 재로드 중에는
///     IsReady=false 로 독자를 파일로 우회시켜 락 경합·부분 상태 노출이 없다.
/// 동시성: 인메모리는 WAL 불가 → shared-cache 테이블 락 모델. 미러 쓰기는 _writeLock 으로 단일화된
/// µs~ms 트랜잭션이고, 독자 충돌(SQLITE_LOCKED/BUSY)은 Microsoft.Data.Sqlite 가 CommandTimeout 동안
/// 재시도한다. read_uncommitted 는 쓰지 않는다 — 스냅샷(delete+reinsert) 중 "빈 테이블" 더티리드를
/// 독자에게 노출하기 때문.
/// 수명: shared-cache 인메모리 DB 는 마지막 커넥션이 닫히면 증발 — keeper 커넥션을 서비스 수명 동안
/// 명시 보유한다(풀링의 idle 커넥션에 의존 금지). keeper 부재 시 누군가 열면 "빈 DB"가 조용히 생기고
/// 기존 sqlite_master 가드가 빈 결과를 정상 처리하므로, IsReady 게이트가 유일한 방어선이다.
/// </summary>
public sealed class HistoryMirrorService : IHostedService, IDisposable
{
    private const string MirrorDataSource = "dsphist";

    /// <summary>미러 대상 테이블 스펙. WindowWhere 의 @Floor(plc=DateTime, oee=ToSqliteUtcString 문자열)로 창 클립.</summary>
    private sealed record TableSpec(string Table, bool FromOee, string? WindowWhere, string? TrimWhere, bool Snapshot, string? IdColumn);

    // TrimWhere: 창 밖으로 밀려난 행 삭제 조건(WindowWhere 의 부정 — 열린 정지 등 겹침 행은 보존).
    private static readonly TableSpec[] Tables =
    {
        new("dspFlowHistory", false, "RecordedAt >= @Floor", "RecordedAt < @Floor", Snapshot: false, IdColumn: "Id"),
        new("userTagAlertLog", false, "occurredAt >= @Floor", "occurredAt < @Floor", Snapshot: false, IdColumn: "id"),
        new("dspFlow", false, null, null, Snapshot: true, IdColumn: null),
        new("dspCall", false, null, null, Snapshot: true, IdColumn: null),
        new("userTagAlertDaily", false, null, null, Snapshot: true, IdColumn: null),
        // 열린 정지(endAt IS NULL)와 창에 걸치는 정지는 시작이 창 밖이어도 보존해야 구간 겹침 집계가 맞다.
        new("oeeDowntimeEvent", true, "startAt >= @Floor OR endAt IS NULL OR endAt >= @Floor",
            "startAt < @Floor AND endAt IS NOT NULL AND endAt < @Floor", Snapshot: false, IdColumn: "id"),
        new("oeeCommHealthLog", true, "sampledAt >= @Floor", "sampledAt < @Floor", Snapshot: false, IdColumn: "id"),
        new("oeeNonProdDetectionLog", true, "onsetAt >= @Floor OR clearAt IS NULL OR clearAt >= @Floor",
            "onsetAt < @Floor AND clearAt IS NOT NULL AND clearAt < @Floor", Snapshot: true, IdColumn: "id"),
        new("oeeProductionCount", true, null, null, Snapshot: true, IdColumn: null),
        new("oeeShiftException", true, null, null, Snapshot: true, IdColumn: null),
    };

    private readonly HistoryMirrorOptions _opt;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly ILogger<HistoryMirrorService> _logger;

    private readonly string _mirrorConnStr;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SqliteConnection? _keeper;
    private volatile bool _ready;
    private volatile bool _degraded;          // 소프트캡 초과 — 재시작 전까지 영구 파일 폴백
    private volatile bool _reloadRequested;
    private CancellationTokenSource? _cts;
    private Task? _runner;

    /// <summary>미러가 보증하는 가장 오래된 시각(UTC). 이 이후 창 요청만 미러로 라우팅.</summary>
    public DateTime WindowFloorUtc { get; private set; } = DateTime.MaxValue;

    public bool IsReady => _ready && !_degraded && _opt.Enabled;

    public HistoryMirrorService(HistoryMirrorOptions opt, IDatabasePathResolver pathResolver, ILogger<HistoryMirrorService> logger)
    {
        _opt = opt;
        _pathResolver = pathResolver;
        _logger = logger;
        // ★URI 형이어야 한다(Mode=Memory 금지) — SqliteOpenMode.Memory 는 SQLITE_OPEN_MEMORY 를 커넥션
        // 전역 플래그로 걸어 이후 ATTACH '파일경로' 까지 인메모리로 바꿔버린다(파일이 안 열림, 실측 확인).
        // URI 형은 메모리-ness 가 main 에만 적용되고 커넥션에 URI 플래그가 켜져, 같은 커넥션에서 파일
        // ATTACH(read-back 복제의 전제)가 정상 동작한다. 같은 URI 를 여는 모든 커넥션이 공유 DB 에 합류.
        _mirrorConnStr = $"Data Source=file:{MirrorDataSource}?mode=memory&cache=shared;Default Timeout=20";
    }

    private string PlcDbPath => _pathResolver.GetSharedDbPath();
    /// <summary>oee.db 경로 — OeeRepositoryAdapter 등과 동일 규칙(공유 DB 디렉토리 + oee.db).</summary>
    public string OeeDbPath => Path.Combine(Path.GetDirectoryName(PlcDbPath) ?? ".", "oee.db");

    // ── 읽기 라우팅 ─────────────────────────────────────────────────────────

    private bool Covers(DateTime fromUtc) =>
        IsReady && (fromUtc.Kind == DateTimeKind.Local ? fromUtc.ToUniversalTime() : fromUtc) >= WindowFloorUtc;

    /// <summary>
    /// plc.db 계열(dspFlowHistory/dspFlow/dspCall/userTagAlert*) 읽기용 미러 커넥션.
    /// 요청 창이 미러 창 밖이거나 미러 미준비면 null — 호출측은 기존 파일 커넥션 경로를 그대로 사용(폴백).
    /// 무창(기간 조건 없는) 쿼리는 호출하지 말 것 — 파일 고정이 정확성 규약.
    /// </summary>
    public async Task<SqliteConnection?> TryOpenPlcReadAsync(DateTime fromUtc, bool layerB = false)
    {
        if (layerB ? !_opt.RouteLayerB : !_opt.RouteLayerA) return null;
        if (!Covers(fromUtc)) return null;
        return await OpenMirrorAsync();
    }

    /// <summary>
    /// oee.db 계열 읽기용 미러 커넥션. fromUtc=null 은 전체 복사 소형 테이블(production/shift) 전용.
    /// </summary>
    public async Task<SqliteConnection?> TryOpenOeeReadAsync(DateTime? fromUtc, bool layerB = false)
    {
        if (layerB ? !_opt.RouteLayerB : !_opt.RouteLayerA) return null;
        if (fromUtc is DateTime f) { if (!Covers(f)) return null; }
        else if (!IsReady) return null;
        return await OpenMirrorAsync();
    }

    private async Task<SqliteConnection> OpenMirrorAsync()
    {
        var conn = new SqliteConnection(_mirrorConnStr);
        await conn.OpenAsync();
        return conn;
    }

    // ── write-through: 파일 SSOT read-back 복제 ────────────────────────────
    //
    // 원리: 파일 커밋이 끝난 뒤 "영향 행 식별 조건(where)"으로 미러에서 DELETE 후 파일에서 INSERT SELECT.
    // 순수 삭제(파일에 남은 행 없음)면 자연히 삭제만 전파된다. 항상 파일 현재값을 다시 읽으므로 멱등이며
    // 재로드 캐치업과 중복 적용돼도 무해하다. 실패는 호출측(파일 쓰기)에 전파하지 않고 MarkDirty 로
    // 자가치유에 위임한다.

    public Task ReplicatePlcAsync(string table, string where, object? args = null)
        => ReplicateAsync(PlcDbPath, table, where, args);

    public Task ReplicateOeeAsync(string table, string where, object? args = null)
        => ReplicateAsync(OeeDbPath, table, where, args);

    private async Task ReplicateAsync(string sourceDbPath, string table, string where, object? args)
    {
        if (!_opt.Enabled || _degraded) return;

        // 재로드(수 초, 락 보유)가 진행 중이면 스킵 — 파일 커밋은 이미 끝났으므로 재로드의
        // 벌크+캐치업이 이 변경을 포함한다. 평상시 복제는 µs~ms 라 2초 대기면 충분.
        if (!await _writeLock.WaitAsync(TimeSpan.FromSeconds(2)))
            return;
        try
        {
            if (!_ready) return;
            await using var conn = await OpenMirrorAsync();
            await AttachAsync(conn, sourceDbPath);
            try
            {
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
                await conn.ExecuteAsync($"DELETE FROM main.[{table}] WHERE {where}", args, tx);
                await conn.ExecuteAsync($"INSERT INTO main.[{table}] SELECT * FROM src.[{table}] WHERE {where}", args, tx);
                await tx.CommitAsync();
            }
            finally
            {
                await DetachAsync(conn);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mirror] replicate 실패 table={Table} where={Where} — 재로드 예약", table, where);
            MarkDirty($"replicate:{table}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task AttachAsync(SqliteConnection conn, string sourceDbPath)
        => await conn.ExecuteAsync("ATTACH DATABASE @p AS src", new { p = sourceDbPath });

    private static async Task DetachAsync(SqliteConnection conn)
    {
        try { await conn.ExecuteAsync("DETACH DATABASE src"); }
        catch { /* 커넥션 폐기로도 해제됨 */ }
    }

    // ── 무효화 훅 ───────────────────────────────────────────────────────────

    /// <summary>정합성이 의심되는 사건 발생 — 백그라운드 전체 재로드 예약(그동안 독자는 파일 폴백).</summary>
    public void MarkDirty(string reason)
    {
        if (!_opt.Enabled || _degraded) return;
        _logger.LogInformation("[Mirror] dirty({Reason}) — 재로드 예약", reason);
        _reloadRequested = true;
    }

    /// <summary>
    /// 파괴적 파일 작업(plc.db 삭제/재구축) 직전 호출 — 즉시 독자를 파일로 우회시키고 진행 중
    /// 복제가 끝나기를 기다린다(파일 핸들 잔류로 삭제가 막히지 않게). 완료 후 MarkDirty 로 재적재.
    /// </summary>
    public async Task SuspendAsync()
    {
        _ready = false;
        await _writeLock.WaitAsync();
        _writeLock.Release();
    }

    // ── 수명 주기 ───────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_opt.Enabled)
        {
            _logger.LogInformation("[Mirror] Enabled=false — 전 경로 파일 폴백으로 동작");
            return;
        }

        _keeper = await OpenMirrorAsync();
        _cts = new CancellationTokenSource();
        _runner = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ready = false;
        _cts?.Cancel();
        if (_runner is not null)
        {
            try { await _runner.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch { /* shutdown 경로 — 강제 진행 */ }
        }
        _keeper?.Dispose();
        _keeper = null;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _keeper?.Dispose();
        _writeLock.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // 부팅 웜업 — 실패하면 1분 간격 재시도(그동안 전 경로 파일 폴백이라 무해).
        while (!ct.IsCancellationRequested && !_ready && !_degraded)
        {
            try
            {
                await ReloadAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Mirror] 웜업 실패 — 60초 후 재시도");
                try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return; }
            }
        }

        var snapshotEvery = TimeSpan.FromSeconds(Math.Max(1, _opt.SnapshotIntervalSeconds));
        var verifyEvery = TimeSpan.FromMinutes(Math.Max(1, _opt.VerifyIntervalMinutes));
        var trimEvery = TimeSpan.FromMinutes(Math.Max(5, _opt.TrimIntervalMinutes));
        var lastVerify = DateTime.UtcNow;
        var lastTrim = DateTime.UtcNow;

        using var timer = new PeriodicTimer(snapshotEvery);
        while (!ct.IsCancellationRequested && !_degraded)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct);

                if (_reloadRequested)
                {
                    _reloadRequested = false;
                    await ReloadAsync(ct);
                    continue;
                }
                if (!_ready) continue;

                await SnapshotSmallTablesAsync(ct);

                if (DateTime.UtcNow - lastTrim >= trimEvery)
                {
                    lastTrim = DateTime.UtcNow;
                    await TrimAsync(ct);
                }
                if (DateTime.UtcNow - lastVerify >= verifyEvery)
                {
                    lastVerify = DateTime.UtcNow;
                    await VerifyAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Mirror] 유지 루프 오류 — 재로드 예약");
                _reloadRequested = true;
            }
        }
    }

    // ── 적재/재적재 ─────────────────────────────────────────────────────────

    private async Task ReloadAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ready = false;

        await _writeLock.WaitAsync(ct);
        try
        {
            var floor = DateTime.UtcNow.AddDays(-_opt.WindowDays);

            await using var conn = await OpenMirrorAsync();

            // 1) 스키마 재구축 — 파일의 현재 DDL(테이블+인덱스)을 그대로 복사(컬럼 순서/제약 동일 보장).
            foreach (var group in Tables.GroupBy(t => t.FromOee))
            {
                var srcPath = group.Key ? OeeDbPath : PlcDbPath;
                if (!File.Exists(srcPath)) continue;
                await AttachAsync(conn, srcPath);
                try
                {
                    foreach (var t in group)
                    {
                        await conn.ExecuteAsync($"DROP TABLE IF EXISTS main.[{t.Table}]");
                        var ddls = (await conn.QueryAsync<string>(
                            @"SELECT sql FROM src.sqlite_master
                              WHERE tbl_name = @t AND sql IS NOT NULL AND type IN ('table','index')
                              ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END",
                            new { t = t.Table })).ToList();
                        foreach (var ddl in ddls)
                            await conn.ExecuteAsync(ddl);

                        // 2) 창 적재 (id 포함 SELECT * — 파일과 동일 행)
                        if (ddls.Count > 0)
                        {
                            var where = BuildWindowWhere(t, floor, out var args);
                            await conn.ExecuteAsync(
                                $"INSERT INTO main.[{t.Table}] SELECT * FROM src.[{t.Table}] WHERE {where}", args);
                        }
                    }
                }
                finally
                {
                    await DetachAsync(conn);
                }
            }

            // 3) 캐치업 — 벌크 적재 동안 파일에 커밋된 append 분(id 증가 테이블)을 한 번 더 끌어온다.
            //    (write-through 는 !_ready 동안 스킵되므로 이 델타가 공백을 메운다. 갱신형 소형
            //     테이블은 3초 스냅샷이 즉시 따라잡는다.)
            foreach (var t in Tables.Where(x => x.IdColumn is not null && !x.Snapshot))
            {
                var srcPath = t.FromOee ? OeeDbPath : PlcDbPath;
                if (!File.Exists(srcPath)) continue;
                var maxId = await conn.ExecuteScalarAsync<long?>($"SELECT MAX([{t.IdColumn}]) FROM main.[{t.Table}]") ?? 0L;
                await AttachAsync(conn, srcPath);
                try
                {
                    await conn.ExecuteAsync(
                        $"INSERT OR REPLACE INTO main.[{t.Table}] SELECT * FROM src.[{t.Table}] WHERE [{t.IdColumn}] > @maxId",
                        new { maxId });
                }
                finally
                {
                    await DetachAsync(conn);
                }
            }

            WindowFloorUtc = floor;
            _ready = true;
            _logger.LogInformation("[Mirror] 적재 완료 — 창 {Days}일(floor={Floor:u}), {Ms}ms",
                _opt.WindowDays, floor, sw.ElapsedMilliseconds);
        }
        finally
        {
            _writeLock.Release();
        }

        await CheckMemoryAsync();
    }

    private static string BuildWindowWhere(TableSpec t, DateTime floorUtc, out object args)
    {
        if (t.WindowWhere is null)
        {
            args = new { };
            return "1=1";
        }
        // plc.db 계열은 Dapper DateTime 직렬화("yyyy-MM-dd HH:mm:ss.fffffff"), oee.db 계열은
        // ToSqliteUtcString("...Z") — 각 DB 의 저장 포맷과 같은 형으로 비교해야 문자열 비교가 성립.
        args = t.FromOee
            ? new { Floor = SqliteDateTimeHelpers.ToSqliteUtcString(floorUtc) }
            : (object)new { Floor = floorUtc };
        return t.WindowWhere;
    }

    private async Task SnapshotSmallTablesAsync(CancellationToken ct)
    {
        foreach (var group in Tables.Where(t => t.Snapshot).GroupBy(t => t.FromOee))
        {
            var srcPath = group.Key ? OeeDbPath : PlcDbPath;
            if (!File.Exists(srcPath)) continue;

            if (!await _writeLock.WaitAsync(TimeSpan.FromSeconds(2), ct)) return;
            try
            {
                if (!_ready) return;
                await using var conn = await OpenMirrorAsync();
                await AttachAsync(conn, srcPath);
                try
                {
                    foreach (var t in group)
                    {
                        var where = BuildWindowWhere(t, WindowFloorUtc, out var args);
                        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
                        await conn.ExecuteAsync($"DELETE FROM main.[{t.Table}]", transaction: tx);
                        await conn.ExecuteAsync(
                            $"INSERT INTO main.[{t.Table}] SELECT * FROM src.[{t.Table}] WHERE {where}", args, tx);
                        await tx.CommitAsync();
                    }
                }
                finally
                {
                    await DetachAsync(conn);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Mirror] 스냅샷 복사 실패 — 재로드 예약");
                MarkDirty("snapshot");
                return;
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    private async Task TrimAsync(CancellationToken ct)
    {
        var floor = DateTime.UtcNow.AddDays(-_opt.WindowDays);
        // Floor 를 먼저 전진 — 독자가 "커버된다"고 믿는 범위가 삭제분을 절대 포함하지 않게.
        WindowFloorUtc = floor;

        if (!await _writeLock.WaitAsync(TimeSpan.FromSeconds(5), ct)) return;
        try
        {
            if (!_ready) return;
            await using var conn = await OpenMirrorAsync();
            foreach (var t in Tables.Where(x => x.TrimWhere is not null))
            {
                var args = t.FromOee
                    ? new { Floor = SqliteDateTimeHelpers.ToSqliteUtcString(floor) }
                    : (object)new { Floor = floor };
                await conn.ExecuteAsync($"DELETE FROM main.[{t.Table}] WHERE {t.TrimWhere}", args);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mirror] 트림 실패");
        }
        finally
        {
            _writeLock.Release();
        }

        await CheckMemoryAsync();
    }

    /// <summary>파일↔미러 정합성 프로브 — write-through 누락/드리프트의 최후 안전망.</summary>
    private async Task VerifyAsync(CancellationToken ct)
    {
        try
        {
            await using var mirror = await OpenMirrorAsync();
            foreach (var t in Tables.Where(x => !x.Snapshot))
            {
                var srcPath = t.FromOee ? OeeDbPath : PlcDbPath;
                if (!File.Exists(srcPath)) continue;

                var where = BuildWindowWhere(t, WindowFloorUtc, out var args);
                await using var file = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = srcPath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 20 }.ToString());
                await file.OpenAsync(ct);

                var fileCount = await file.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM [{t.Table}] WHERE {where}", args);
                var mirrorCount = await mirror.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM main.[{t.Table}] WHERE {where}", args);
                long fileMax = 0, mirrorMax = 0;
                if (t.IdColumn is not null)
                {
                    fileMax = await file.ExecuteScalarAsync<long?>($"SELECT MAX([{t.IdColumn}]) FROM [{t.Table}] WHERE {where}", args) ?? 0;
                    mirrorMax = await mirror.ExecuteScalarAsync<long?>($"SELECT MAX([{t.IdColumn}]) FROM main.[{t.Table}] WHERE {where}", args) ?? 0;
                }

                // 프로브와 라이브 쓰기 사이의 순간 차는 정상 — 한 틱 유예를 두고 두 번 연속 불일치만 dirty 처리.
                if (fileCount != mirrorCount || fileMax != mirrorMax)
                {
                    await Task.Delay(500, ct);
                    var fileCount2 = await file.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM [{t.Table}] WHERE {where}", args);
                    var mirrorCount2 = await mirror.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM main.[{t.Table}] WHERE {where}", args);
                    if (fileCount2 != mirrorCount2)
                    {
                        _logger.LogWarning("[Mirror] 프로브 불일치 {Table}: file={File}, mirror={Mirror} — 재로드",
                            t.Table, fileCount2, mirrorCount2);
                        MarkDirty($"verify:{t.Table}");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mirror] 프로브 실패");
        }
    }

    private async Task CheckMemoryAsync()
    {
        try
        {
            await using var conn = await OpenMirrorAsync();
            var pageCount = await conn.ExecuteScalarAsync<long>("PRAGMA page_count");
            var pageSize = await conn.ExecuteScalarAsync<long>("PRAGMA page_size");
            var bytes = pageCount * pageSize;
            _logger.LogInformation("[Mirror] 메모리 {Mb:F1}MB (창 {Days}일)", bytes / 1024.0 / 1024.0, _opt.WindowDays);

            if (bytes > (long)_opt.SoftCapMb * 1024 * 1024)
            {
                _degraded = true;
                _ready = false;
                _logger.LogError("[Mirror] 소프트캡 {Cap}MB 초과({Mb:F1}MB) — 미러 강등, 재시작 전까지 파일 폴백",
                    _opt.SoftCapMb, bytes / 1024.0 / 1024.0);
            }
        }
        catch { /* 진단 전용 */ }
    }
}
