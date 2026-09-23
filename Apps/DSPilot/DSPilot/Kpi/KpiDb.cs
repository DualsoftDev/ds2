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
    /// <summary>
    /// 스키마 버전 — PRAGMA user_version 에 기록된다.
    /// v3(2026-09-17): 원시 계층(system · tag · signal · alert)을 이 코어가 소유한다.
    /// 시각은 정수 epoch ms 이고, 신호 값은 타입 친화도를 선언하지 않아 비트·수치·문자열이 한 칸에 들어간다.
    /// v4(2026-09-18): MT 축 비가동(cycle.mtMedianUsedMs) · 경계 초과(cycle.overflowMs) · work 게이트(cycleWork.gated,
    /// baseline.q1Ms/q3Ms) · MT 기준선(baseline.scope='MT'). doc/30 §3 · §4.1 · §6.
    /// </summary>
    public const int SchemaVersion = 4;

    /// <summary>판정 규칙 버전 — 사이클 행에 박제해 어떤 규칙으로 만들어진 행인지 남긴다.</summary>
    public const string SpecVersion = "v68.2";

    /// <summary>DB 파일 이름. 구 이름(plc.db)과 겹치지 않아야 한다.</summary>
    public const string FileName = "dspilot.db";

    private readonly ILogger<KpiDb> _logger;

    public string Path { get; }
    public string ConnectionString { get; }

    /// <summary>
    /// DI 생성자. 경로는 <see cref="Services.IDatabasePathResolver"/>(= Database:ConnectionString 의 폴더 + 정본 파일명)
    /// 에서 받고, 설정 키 <c>Kpi:DbPath</c> 로 덮어쓸 수 있다(비표준 배치·진단용).
    /// 생성자는 이 하나만 public 이어야 한다 — 둘이면 DI 가 모호하다고 거부한다.
    /// <para>★경로를 여기서 따로 계산하면 안 된다. 종전엔 이 클래스만 <c>SharedPaths.SharedDirectory</c> 를 썼는데,
    /// 나머지 전부는 연결 문자열의 폴더를 쓴다. 구버전에서 올라온 현장은 연결 문자열이 옛 폴더를 가리켜
    /// <c>system·tag·signal</c> 은 A 파일에, <c>dspFlow·dspCall</c> 은 B 파일에 생기는 분열이 났다. 그러면
    /// <see cref="Adapters.DspRepositoryAdapter.CreateSchemaAsync"/> 의 <c>INSERT INTO system</c> 이 B 파일에서
    /// 터지고 그 뒤 ALTER 마이그레이션이 통째로 건너뛰어져, dspFlow.flowId 누락으로 모델 적재가 영구 실패했다
    /// (2026-09-22 현장: 엔진 미초기화 → Hub 태그 전량 폐기 → 화면 전체 공백).</para>
    /// </summary>
    public KpiDb(
        ILogger<KpiDb> logger,
        Microsoft.Extensions.Configuration.IConfiguration config,
        Services.IDatabasePathResolver pathResolver)
        : this(logger, ResolveDbPath(config, pathResolver)) { }

    /// <summary>Kpi:DbPath 명시 > 공용 리졸버. 둘 다 없을 때만 공유 폴더 기본값으로 떨어진다.</summary>
    private static string ResolveDbPath(
        Microsoft.Extensions.Configuration.IConfiguration config,
        Services.IDatabasePathResolver pathResolver)
    {
        var overridePath = config["Kpi:DbPath"];
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;

        try
        {
            var shared = pathResolver.GetSharedDbPath();
            if (!string.IsNullOrWhiteSpace(shared)) return shared;
        }
        catch { /* 설정 미구성 — 아래 기본값 */ }

        return System.IO.Path.Combine(SharedPaths.SharedDirectory, FileName);
    }

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
                    foreach (var sql in Schema)
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
                // 이 표들은 전부 <b>파생</b>이다 — 원시 신호에서 다시 만들 수 있다. 그래서 구조가 바뀌면
                // ALTER 체인을 쌓는 대신 버리고 다시 만든다(doc/30 §9 의 "ALTER 체인 금지"의 실제 운용).
                _logger.LogWarning(
                    "[KpiDb] schema v{Old} → v{New} — 파생 표를 버리고 다시 만든다(원시 신호에서 재적재됨).",
                    version, SchemaVersion);
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    foreach (var name in DerivedTables)
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.Transaction = (SqliteTransaction)tx;
                        cmd.CommandText = $"DROP TABLE IF EXISTS {name}";
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    foreach (var sql in Schema)
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.Transaction = (SqliteTransaction)tx;
                        cmd.CommandText = sql;
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                }
                await ExecAsync(conn, $"PRAGMA user_version={SchemaVersion};", ct);
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

    /// <summary>
    /// 이 코어가 소유하는 표 — 버전이 바뀌면 통째로 버리고 다시 만드는 대상.
    /// 같은 파일의 다른 표(원시 신호·모델·알람·구 OEE)는 여기서 건드리지 않는다.
    /// </summary>
    private static readonly string[] DerivedTables =
    [
        "cycleWork", "cycle", "baseline", "linkEvent",
        // v1 이 만들었다 폐기한 이름 — 남아 있으면 지운다.
        "signalLog", "alertLog",
    ];

    /// <summary>
    /// 스키마 — doc/30 §9. 원시 계층(system · tag · signal · alert)과 판정 계층(cycle 이하)을 함께 소유한다.
    /// 원시는 절대 버리지 않고(재생성 불가), 판정 계층만 버전이 바뀌면 다시 만든다.
    /// </summary>
    private static readonly string[] Schema =
    [
        // ── 원시 계층 ──────────────────────────────────────────────────────────
        // 시스템 = PLC 연결 하나. 이름은 표시용이고 귀속 키는 guid 다(사용자가 이름을 바꿀 수 있다).
        """
        CREATE TABLE IF NOT EXISTS system (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            name     TEXT NOT NULL UNIQUE,
            guid     TEXT,
            vendor   TEXT,
            endpoint TEXT
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS uq_system_guid ON system(guid) WHERE guid IS NOT NULL",

        // 태그. dataType 은 UserTag 의 값 종류 어휘를 그대로 쓴다
        // (Bit · Byte · Word · DWord · Int16 · Int32 · Real · String). IO 맵 주소는 Bit.
        // label · unit · isUserTag 는 태그 모니터링 화면이 표시 형식을 정하는 근거다.
        """
        CREATE TABLE IF NOT EXISTS tag (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            systemId  INTEGER NOT NULL DEFAULT 1,
            name      TEXT    NOT NULL,
            address   TEXT    NOT NULL,
            dataType  TEXT    NOT NULL DEFAULT 'Bit',
            label     TEXT,
            unit      TEXT,
            isUserTag INTEGER NOT NULL DEFAULT 0,
            UNIQUE (systemId, address)
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_tag_address ON tag(address)",
        "CREATE INDEX IF NOT EXISTS idx_tag_user ON tag(isUserTag) WHERE isUserTag = 1",

        // 값 변화 하나가 한 줄. value 에 타입을 선언하지 않는 것이 요점이다 —
        // 비트는 1바이트 정수, 수치는 REAL, 문자열은 TEXT 로 같은 칸에 들어간다.
        """
        CREATE TABLE IF NOT EXISTS signal (
            id    INTEGER PRIMARY KEY AUTOINCREMENT,
            tagId INTEGER NOT NULL,
            atMs  INTEGER NOT NULL,
            value         NOT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_signal_tag_at ON signal(tagId, atMs)",
        "CREATE INDEX IF NOT EXISTS idx_signal_at ON signal(atMs)",

        // 이상·알람. 시스템 이름과 태그 주소를 함께 박제한다 — 모델이 바뀌어 태그가 사라져도
        // 과거 알람이 어디서 났는지 읽을 수 있어야 한다.
        """
        CREATE TABLE IF NOT EXISTS alert (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            occurredMs  INTEGER NOT NULL,
            clearedMs   INTEGER,
            systemGuid  TEXT,
            systemName  TEXT,
            name        TEXT    NOT NULL,
            level       TEXT    NOT NULL,
            tagId       INTEGER,
            tagAddress  TEXT,
            valueType   TEXT,
            matchOp     TEXT,
            matchValue  TEXT,
            actualValue TEXT
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_alert_occurred ON alert(occurredMs)",
        "CREATE INDEX IF NOT EXISTS idx_alert_level_time ON alert(level, occurredMs)",
        "CREATE INDEX IF NOT EXISTS idx_alert_name_time ON alert(name, occurredMs)",

        // ── 판정 계층 ──────────────────────────────────────────────────────────
        // 사이클 행. 상태 컬럼은 없다 — 상태와 비가동 축은 조회 시 현재 κ 로 도출한다(doc/30 §6).
        // rUsedMs · mtMedianUsedMs · worstRatio 가 완료 시점에 박제되므로 기준선이 움직여도 과거는 변하지 않는다.
        // mtMs = 경계→마지막 work 끝(work 사이 공백 포함), overflowMs = call 구간이 CT 끝을 넘은 최대량(허용치 비교는 조회 시).
        // 시각은 전부 정수 epoch ms — 텍스트 날짜(약 28바이트)보다 짧고 파싱 함정이 없다.
        """
        CREATE TABLE IF NOT EXISTS cycle (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            flow           TEXT    NOT NULL,
            branch         TEXT,
            startMs        INTEGER NOT NULL,
            endMs          INTEGER NOT NULL,
            ctMs           INTEGER NOT NULL,
            mtMs           INTEGER,
            wtMs           INTEGER,
            rUsedMs        REAL    NOT NULL DEFAULT 0,
            mtMedianUsedMs REAL    NOT NULL DEFAULT 0,
            worstWork      TEXT,
            worstRatio     REAL    NOT NULL DEFAULT 0,
            overflowMs     INTEGER NOT NULL DEFAULT 0,
            excludeReason  INTEGER NOT NULL DEFAULT 0,
            specVersion    TEXT    NOT NULL
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS uq_cycle_flow_start ON cycle(flow, startMs)",
        "CREATE INDEX IF NOT EXISTS idx_cycle_start ON cycle(startMs)",

        // 사이클별 work 지속시간(= call 구간 집합의 최소 시작~최대 끝). 비가동 판정의 근거이자 "초과 work" 표시 소스.
        // gated = 그 시점 게이트(Q3/Q1)에 걸려 판정에서 빠졌는지 — 화면에 이유를 보이기 위해 박제한다.
        """
        CREATE TABLE IF NOT EXISTS cycleWork (
            cycleId    INTEGER NOT NULL REFERENCES cycle(id) ON DELETE CASCADE,
            work       TEXT    NOT NULL,
            durationMs INTEGER NOT NULL,
            wUsedMs    REAL    NOT NULL DEFAULT 0,
            gated      INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (cycleId, work)
        ) WITHOUT ROWID
        """,

        // 기준선 일별 스냅샷. scope='R'·'MT' 면 work='' (flow·분기 기준), scope='W' 면 work 별.
        // q1Ms·q3Ms 는 W 의 게이트 근거 — 사용자가 게이트 값을 바꾸면 이 둘로 다시 가른다.
        """
        CREATE TABLE IF NOT EXISTS baseline (
            scope       TEXT    NOT NULL,
            flow        TEXT    NOT NULL,
            branch      TEXT    NOT NULL DEFAULT '',
            work        TEXT    NOT NULL DEFAULT '',
            asOfDate    TEXT    NOT NULL,
            valueMs     REAL    NOT NULL,
            sampleCount INTEGER NOT NULL,
            q1Ms        REAL,
            q3Ms        REAL,
            PRIMARY KEY (scope, flow, branch, work, asOfDate)
        ) WITHOUT ROWID
        """,

        // 시스템(PLC 연결)별 접속 전이와 심박 공백. 계산 인자가 아니라 대조용이다(doc/30 §7).
        // 1초 심박 자체는 쌓지 않는다 — 전이와 공백만 남긴다.
        """
        CREATE TABLE IF NOT EXISTS linkEvent (
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
        "CREATE INDEX IF NOT EXISTS idx_linkEvent_system_at ON linkEvent(system, atMs)",
    ];
}
