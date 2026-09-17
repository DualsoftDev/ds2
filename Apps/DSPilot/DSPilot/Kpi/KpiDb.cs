// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DSPilot.Kpi;

/// <summary>
/// 시간 기반 코어 v68 의 단일 DB. doc/30 §9.
/// <para>
/// 종전 plc.db · oee.db 2파일 체제를 대체하는 <b>한 파일</b>이다. 설치본은 처음부터 수집하므로
/// 마이그레이션은 없다 — 구 DB 는 <see cref="LegacyDbPurge"/> 가 기동 시 삭제한다.
/// </para>
/// <para>
/// 시각은 전부 <b>Unix epoch ms(INTEGER, UTC)</b> 로 저장한다. 구 스키마의 텍스트 DATETIME 은
/// 파싱 시 Kind=Local 로 되살아나 9시간 오차를 만드는 함정이 있었다(reference: FromSqliteUtcString).
/// 정수 epoch 는 그 함정 자체가 없고 정렬·범위질의도 빠르다.
/// </para>
/// <para>스키마 변경은 <see cref="SchemaVersion"/> 을 올리고 명시적 마이그레이션을 추가한다. ALTER 체인 금지.</para>
/// </summary>
public sealed class KpiDb
{
    /// <summary>스키마 버전 — PRAGMA user_version 에 기록된다.</summary>
    public const int SchemaVersion = 1;

    /// <summary>판정 규칙 버전 — 사이클 행에 박제해 어떤 규칙으로 만들어진 행인지 남긴다.</summary>
    public const string SpecVersion = "v68.1";

    /// <summary>DB 파일 이름. 구 이름(plc.db)과 겹치지 않아야 한다.</summary>
    public const string FileName = "dspilot.db";

    private readonly ILogger<KpiDb> _logger;

    public string Path { get; }
    public string ConnectionString { get; }

    /// <summary>
    /// DI 생성자. 경로는 공유 폴더의 <see cref="FileName"/> 이며 설정 키 <c>Kpi:DbPath</c> 로 덮어쓸 수 있다
    /// (비표준 배치·진단용). 생성자는 이 하나만 public 이어야 한다 — 둘이면 DI 가 모호하다고 거부한다.
    /// </summary>
    public KpiDb(ILogger<KpiDb> logger, Microsoft.Extensions.Configuration.IConfiguration config)
        : this(logger, config["Kpi:DbPath"]) { }

    private KpiDb(ILogger<KpiDb> logger, string? pathOverride)
    {
        _logger = logger;
        Path = string.IsNullOrWhiteSpace(pathOverride)
            ? System.IO.Path.Combine(SharedPaths.SharedDirectory, FileName)
            : pathOverride;
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        ConnectionString = $"Data Source={Path};Mode=ReadWriteCreate;Default Timeout=20";
    }

    /// <summary>임의 경로로 만든다 — 테스트·진단 전용. 운영 경로는 DI 생성자가 정한다.</summary>
    public static KpiDb ForPath(ILogger<KpiDb> logger, string path) => new(logger, path);

    /// <summary>쓰기 가능한 새 연결. 호출자가 using 으로 닫는다.</summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    /// <summary>읽기 전용 연결 — 조회 경로가 실수로 쓰지 못하게 한다.</summary>
    public SqliteConnection OpenRead()
    {
        var conn = new SqliteConnection($"Data Source={Path};Mode=ReadOnly;Default Timeout=20");
        conn.Open();
        return conn;
    }

    /// <summary>
    /// 스키마 v1 생성 + PRAGMA 설정. 멱등이며 기동 시 1회 호출한다.
    /// auto_vacuum 은 파일이 비어 있을 때만 바뀌므로 테이블 생성 <b>전에</b> 설정해야 한다.
    /// </summary>
    public async Task<bool> EnsureSchemaAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct);

            // 빈 파일일 때만 유효 — 이후 재설정은 무시되므로 테이블 생성보다 먼저.
            await ExecAsync(conn, "PRAGMA auto_vacuum=INCREMENTAL;", ct);
            await ExecAsync(conn, "PRAGMA journal_mode=WAL;", ct);
            await ExecAsync(conn, "PRAGMA synchronous=NORMAL;", ct);
            await ExecAsync(conn, "PRAGMA foreign_keys=ON;", ct);

            var version = await ScalarIntAsync(conn, "PRAGMA user_version;", ct);
            if (version == 0)
            {
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    foreach (var sql in SchemaV1)
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.Transaction = (SqliteTransaction)tx;
                        cmd.CommandText = sql;
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                }
                await ExecAsync(conn, $"PRAGMA user_version={SchemaVersion};", ct);
                _logger.LogInformation("[KpiDb] schema v{Ver} created — {Path}", SchemaVersion, Path);
            }
            else if (version != SchemaVersion)
            {
                _logger.LogError(
                    "[KpiDb] schema version mismatch: file={File}, expected={Expected}. 마이그레이션이 필요하다.",
                    version, SchemaVersion);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[KpiDb] EnsureSchemaAsync failed — {Path}", Path);
            return false;
        }
    }

    /// <summary>WAL 체크포인트 + 증분 진공. 보존 삭제 뒤에 호출해 파일이 계속 커지는 것을 막는다.</summary>
    public async Task CompactAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct);
            await ExecAsync(conn, "PRAGMA wal_checkpoint(TRUNCATE);", ct);
            await ExecAsync(conn, "PRAGMA incremental_vacuum;", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KpiDb] compact failed");
        }
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null || v is DBNull ? 0 : Convert.ToInt32(v);
    }

    /// <summary>스키마 v1 — doc/30 §9 표와 1:1.</summary>
    private static readonly string[] SchemaV1 =
    [
        // ── 판정 결과 ──────────────────────────────────────────────────────────
        // 사이클 행. 상태 컬럼은 없다 — 상태는 조회 시 현재 κ 로 도출한다(doc/30 §5).
        // rUsedMs · worstRatio 가 완료 시점에 박제되므로 기준선이 움직여도 과거는 변하지 않는다.
        """
        CREATE TABLE cycle (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            flow          TEXT    NOT NULL,
            branch        TEXT,
            startMs       INTEGER NOT NULL,
            endMs         INTEGER NOT NULL,
            ctMs          INTEGER NOT NULL,
            mtMs          INTEGER,
            wtMs          INTEGER,
            rUsedMs       REAL    NOT NULL DEFAULT 0,
            worstWork     TEXT,
            worstRatio    REAL    NOT NULL DEFAULT 0,
            excludeReason INTEGER NOT NULL DEFAULT 0,
            specVersion   TEXT    NOT NULL
        )
        """,
        "CREATE UNIQUE INDEX uq_cycle_flow_start ON cycle(flow, startMs)",
        "CREATE INDEX idx_cycle_start ON cycle(startMs)",
        "CREATE INDEX idx_cycle_flow_start_end ON cycle(flow, startMs, endMs)",

        // 사이클별 work 지속시간. 비가동 판정의 근거이자 화면의 "초과 work" 표시 소스.
        """
        CREATE TABLE cycleWork (
            cycleId    INTEGER NOT NULL REFERENCES cycle(id) ON DELETE CASCADE,
            work       TEXT    NOT NULL,
            durationMs INTEGER NOT NULL,
            wUsedMs    REAL    NOT NULL DEFAULT 0,
            PRIMARY KEY (cycleId, work)
        ) WITHOUT ROWID
        """,

        // 기준선 일별 스냅샷. scope='R' 이면 work='' (flow·분기 기준), scope='W' 면 work 별.
        """
        CREATE TABLE baseline (
            scope       TEXT    NOT NULL,
            flow        TEXT    NOT NULL,
            branch      TEXT    NOT NULL DEFAULT '',
            work        TEXT    NOT NULL DEFAULT '',
            asOfDate    TEXT    NOT NULL,
            valueMs     REAL    NOT NULL,
            sampleCount INTEGER NOT NULL,
            PRIMARY KEY (scope, flow, branch, work, asOfDate)
        ) WITHOUT ROWID
        """,

        // ── 보조 관측 ──────────────────────────────────────────────────────────
        // 시스템(PLC 연결)별 접속 전이와 심박 공백. 계산 인자가 아니라 대조용이다(doc/30 §7).
        // 1초 심박 자체는 쌓지 않는다 — 전이와 공백만 남긴다.
        """
        CREATE TABLE linkEvent (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            system      TEXT    NOT NULL,
            atMs        INTEGER NOT NULL,
            endMs       INTEGER,
            isConnected INTEGER NOT NULL,
            kind        TEXT    NOT NULL,
            detail      TEXT,
            source      TEXT
        )
        """,
        "CREATE INDEX idx_linkEvent_system_at ON linkEvent(system, atMs)",
        "CREATE INDEX idx_linkEvent_at ON linkEvent(atMs)",

        // ── 원시 신호(롤링 보존) ───────────────────────────────────────────────
        """
        CREATE TABLE signalLog (
            id    INTEGER PRIMARY KEY AUTOINCREMENT,
            tagId INTEGER NOT NULL,
            atMs  INTEGER NOT NULL,
            value TEXT    NOT NULL
        )
        """,
        "CREATE INDEX idx_signalLog_tag_at ON signalLog(tagId, atMs)",
        "CREATE INDEX idx_signalLog_at ON signalLog(atMs)",

        // ── 모델 현재값(AASX 로드 시 갱신) ─────────────────────────────────────
        """
        CREATE TABLE plc (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            name     TEXT NOT NULL UNIQUE,
            systemId TEXT,
            vendor   TEXT,
            endpoint TEXT
        )
        """,
        "CREATE UNIQUE INDEX uq_plc_systemId ON plc(systemId) WHERE systemId IS NOT NULL",
        """
        CREATE TABLE tag (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            plcId    INTEGER NOT NULL DEFAULT 1,
            name     TEXT NOT NULL,
            address  TEXT NOT NULL,
            dataType TEXT NOT NULL DEFAULT 'BOOL',
            UNIQUE (plcId, address)
        )
        """,
        "CREATE INDEX idx_tag_address ON tag(address)",
        """
        CREATE TABLE flow (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            name       TEXT NOT NULL UNIQUE,
            flowGuid   TEXT,
            systemName TEXT,
            headCall   TEXT,
            tailCall   TEXT
        )
        """,
        """
        CREATE TABLE call (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            callGuid TEXT,
            name     TEXT NOT NULL,
            apiCall  TEXT,
            workName TEXT,
            flowName TEXT NOT NULL,
            device   TEXT,
            UNIQUE (name, flowName, workName)
        )
        """,
        "CREATE INDEX idx_call_flow_work ON call(flowName, workName)",

        // ── 이상·알람(롤링 보존) ───────────────────────────────────────────────
        """
        CREATE TABLE alertLog (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            occurredMs  INTEGER NOT NULL,
            clearedMs   INTEGER,
            systemId    TEXT,
            systemName  TEXT,
            name        TEXT NOT NULL,
            logLevel    TEXT NOT NULL,
            tagAddress  TEXT,
            valueType   TEXT,
            matchOp     TEXT,
            matchValue  TEXT,
            actualValue TEXT
        )
        """,
        "CREATE INDEX idx_alertLog_occurred ON alertLog(occurredMs)",
        "CREATE INDEX idx_alertLog_level_time ON alertLog(logLevel, occurredMs)",
    ];
}
