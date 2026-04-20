# 데이터베이스 마이그레이션 가이드

## 🎯 목적

DROP TABLE을 사용하지 않고 ALTER TABLE 기반 마이그레이션으로 누적 통계를 보존합니다.

---

## 🚫 DROP TABLE 금지 이유

### 문제점

```sql
-- ❌ 이렇게 하면 안 됩니다!
DROP TABLE IF EXISTS dspFlow;
CREATE TABLE dspFlow (...);
```

**손실되는 데이터**:
- `AverageCT`, `StdDevCT` (누적 통계)
- `CompletedCycleCount` (완료된 사이클 수)
- `GoingCount` (Call 실행 횟수)
- `AverageGoingTime`, `StdDevGoingTime` (Call 통계)

**결과**: 통계가 0으로 리셋되어 SlowFlag 판정 불가능

---

## ✅ Migration 기반 접근

### 원칙

1. **테이블은 절대 DROP하지 않음**
2. **ALTER TABLE로 컬럼 추가**
3. **마이그레이션 스크립트 버전 관리**
4. **데이터 백업 필수**

---

## 📋 마이그레이션 스크립트

### Migration 001: 초기 Projection 필드 추가

```sql
-- Migration: 001_add_projection_fields.sql
-- Date: 2025-03-22
-- Description: dspFlow 및 dspCall에 Projection 필드 추가

-- ========================================
-- dspFlow 확장
-- ========================================

-- Static Metadata
ALTER TABLE dspFlow ADD COLUMN SystemName TEXT;
ALTER TABLE dspFlow ADD COLUMN WorkName TEXT;
ALTER TABLE dspFlow ADD COLUMN SequenceNo INTEGER;
ALTER TABLE dspFlow ADD COLUMN IsHead INTEGER DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN IsTail INTEGER DEFAULT 0;

-- Real-time State
ALTER TABLE dspFlow ADD COLUMN ActiveCallCount INTEGER DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN ErrorCallCount INTEGER DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN LastCycleStartAt TEXT;
ALTER TABLE dspFlow ADD COLUMN LastCycleEndAt TEXT;
ALTER TABLE dspFlow ADD COLUMN LastCycleNo INTEGER DEFAULT 0;

-- Cumulative Statistics
ALTER TABLE dspFlow ADD COLUMN LastCycleDurationMs REAL;
ALTER TABLE dspFlow ADD COLUMN AverageCT REAL;
ALTER TABLE dspFlow ADD COLUMN StdDevCT REAL;
ALTER TABLE dspFlow ADD COLUMN MinCT REAL;
ALTER TABLE dspFlow ADD COLUMN MaxCT REAL;
ALTER TABLE dspFlow ADD COLUMN CompletedCycleCount INTEGER DEFAULT 0;

-- Derived Warnings
ALTER TABLE dspFlow ADD COLUMN SlowCycleFlag INTEGER DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN UnmappedCallCount INTEGER DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN FocusScore INTEGER DEFAULT 0;

-- ========================================
-- dspCall 확장
-- ========================================

-- Static Metadata
ALTER TABLE dspCall ADD COLUMN SystemName TEXT;
ALTER TABLE dspCall ADD COLUMN IsHead INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN IsTail INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN SequenceNo INTEGER;
ALTER TABLE dspCall ADD COLUMN InTag TEXT;
ALTER TABLE dspCall ADD COLUMN OutTag TEXT;

-- Real-time State
ALTER TABLE dspCall ADD COLUMN LastStartAt TEXT;
ALTER TABLE dspCall ADD COLUMN LastFinishAt TEXT;
ALTER TABLE dspCall ADD COLUMN LastDurationMs REAL;
ALTER TABLE dspCall ADD COLUMN CurrentCycleNo INTEGER DEFAULT 0;

-- Cumulative Statistics
ALTER TABLE dspCall ADD COLUMN MinGoingTime REAL;
ALTER TABLE dspCall ADD COLUMN MaxGoingTime REAL;
ALTER TABLE dspCall ADD COLUMN ErrorCount INTEGER DEFAULT 0;

-- Derived Warnings
ALTER TABLE dspCall ADD COLUMN SlowFlag INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN UnmappedFlag INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN FocusScore INTEGER DEFAULT 0;

-- ========================================
-- Index 추가
-- ========================================

-- dspFlow indexes
CREATE INDEX IF NOT EXISTS idx_dspFlow_State ON dspFlow(State);
CREATE INDEX IF NOT EXISTS idx_dspFlow_FocusScore ON dspFlow(FocusScore DESC);
CREATE INDEX IF NOT EXISTS idx_dspFlow_SystemName ON dspFlow(SystemName);
CREATE INDEX IF NOT EXISTS idx_dspFlow_WorkName ON dspFlow(WorkName);

-- dspCall indexes
CREATE INDEX IF NOT EXISTS idx_dspCall_State ON dspCall(State);
CREATE INDEX IF NOT EXISTS idx_dspCall_FocusScore ON dspCall(FocusScore DESC);
CREATE INDEX IF NOT EXISTS idx_dspCall_WorkName ON dspCall(WorkName);
CREATE INDEX IF NOT EXISTS idx_dspCall_InTag ON dspCall(InTag);
CREATE INDEX IF NOT EXISTS idx_dspCall_OutTag ON dspCall(OutTag);
CREATE INDEX IF NOT EXISTS idx_dspCall_IsHead ON dspCall(IsHead);
CREATE INDEX IF NOT EXISTS idx_dspCall_IsTail ON dspCall(IsTail);
```

---

## 🔧 마이그레이션 실행 방법

### 수동 실행

```bash
# SQLite3 CLI 사용
sqlite3 /path/to/plc.db < 001_add_projection_fields.sql
```

### F# 코드로 실행

```fsharp
// DSPilot.Engine/Database/Initialization.fs

module DatabaseMigration =

    type MigrationVersion =
        { Version: int
          AppliedAt: DateTime }

    let private createMigrationTable (conn: SqliteConnection) =
        async {
            let sql = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    Version INTEGER PRIMARY KEY,
                    AppliedAt TEXT NOT NULL
                )
            """
            let! _ = conn.ExecuteAsync(sql) |> Async.AwaitTask
            return ()
        }

    let private getAppliedMigrations (conn: SqliteConnection) : Async<int list> =
        async {
            let sql = "SELECT Version FROM schema_migrations ORDER BY Version"
            let! results = conn.QueryAsync<int>(sql) |> Async.AwaitTask
            return results |> Seq.toList
        }

    let private recordMigration (conn: SqliteConnection) (version: int) =
        async {
            let sql = "INSERT INTO schema_migrations (Version, AppliedAt) VALUES (@version, @appliedAt)"
            let! _ = conn.ExecuteAsync(sql, {| version = version; appliedAt = DateTime.Now.ToString("o") |})
                    |> Async.AwaitTask
            return ()
        }

    let private migration001 (conn: SqliteConnection) =
        async {
            // dspFlow 확장
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN SystemName TEXT") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN WorkName TEXT") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN SequenceNo INTEGER") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN IsHead INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN IsTail INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN ActiveCallCount INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN ErrorCallCount INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN LastCycleStartAt TEXT") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN LastCycleEndAt TEXT") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN LastCycleNo INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN LastCycleDurationMs REAL") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN AverageCT REAL") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN StdDevCT REAL") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN MinCT REAL") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN MaxCT REAL") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN CompletedCycleCount INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN SlowCycleFlag INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN UnmappedCallCount INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN FocusScore INTEGER DEFAULT 0") |> Async.AwaitTask

            // dspCall 확장
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN SystemName TEXT") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN IsHead INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN IsTail INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN SequenceNo INTEGER") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN InTag TEXT") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN OutTag TEXT") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN LastStartAt TEXT") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN LastFinishAt TEXT") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN LastDurationMs REAL") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN CurrentCycleNo INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN MinGoingTime REAL") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN MaxGoingTime REAL") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN ErrorCount INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN SlowFlag INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN UnmappedFlag INTEGER DEFAULT 0") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN FocusScore INTEGER DEFAULT 0") |> Async.AwaitTask

            // Index 추가
            let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_dspFlow_State ON dspFlow(State)") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_dspFlow_FocusScore ON dspFlow(FocusScore DESC)") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_dspCall_State ON dspCall(State)") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_dspCall_FocusScore ON dspCall(FocusScore DESC)") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_dspCall_InTag ON dspCall(InTag)") |> Async.AwaitTask
            let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_dspCall_OutTag ON dspCall(OutTag)") |> Async.AwaitTask

            return ()
        }

    let runMigrations (dbPath: string) : Async<unit> =
        async {
            use conn = new SqliteConnection(Configuration.getConnectionString dbPath)
            do! conn.OpenAsync() |> Async.AwaitTask

            // 마이그레이션 테이블 생성
            do! createMigrationTable conn

            // 적용된 마이그레이션 확인
            let! appliedVersions = getAppliedMigrations conn

            // Migration 001
            if not (List.contains 1 appliedVersions) then
                printfn "Applying migration 001..."
                do! migration001 conn
                do! recordMigration conn 1
                printfn "Migration 001 applied successfully."

            // Migration 002 (미래)
            // if not (List.contains 2 appliedVersions) then
            //     do! migration002 conn
            //     do! recordMigration conn 2

            printfn "All migrations applied."
        }
```

### 앱 시작 시 자동 실행

```fsharp
// DSPilot.Engine/Ev2Bootstrap.fs

let initialize (dbPath: string) =
    async {
        // 1. 마이그레이션 실행
        do! DatabaseMigration.runMigrations dbPath

        // 2. Bootstrap
        // ...
    }
```

---

## 🔍 마이그레이션 검증

### 1. 컬럼 존재 확인

```sql
PRAGMA table_info(dspFlow);
PRAGMA table_info(dspCall);
```

### 2. 기존 통계 보존 확인

```sql
-- 마이그레이션 전 통계
SELECT FlowName, AverageCT, CompletedCycleCount FROM dspFlow;

-- 마이그레이션 후 (동일해야 함)
SELECT FlowName, AverageCT, CompletedCycleCount FROM dspFlow;
```

### 3. Index 확인

```sql
SELECT name FROM sqlite_master
WHERE type = 'index' AND tbl_name IN ('dspFlow', 'dspCall');
```

---

## 🛡️ 백업 및 롤백

### 백업

```bash
# 마이그레이션 전 백업 필수
cp /path/to/plc.db /path/to/plc.db.backup.$(date +%Y%m%d_%H%M%S)
```

### 롤백 (최후의 수단)

```bash
# 백업에서 복원
cp /path/to/plc.db.backup.20250322_120000 /path/to/plc.db
```

**참고**: ALTER TABLE은 롤백이 어려우므로 백업 필수

---

## 📝 마이그레이션 이력 관리

### schema_migrations 테이블

```sql
CREATE TABLE IF NOT EXISTS schema_migrations (
    Version INTEGER PRIMARY KEY,
    AppliedAt TEXT NOT NULL
);
```

### 적용된 마이그레이션 조회

```sql
SELECT * FROM schema_migrations ORDER BY Version;
```

**출력 예시**:
```
Version | AppliedAt
--------|-------------------------
1       | 2025-03-22T12:00:00Z
2       | 2025-03-25T14:30:00Z
```

---

## 🚀 향후 마이그레이션 예시

### Migration 002: 새 필드 추가 (예시)

```sql
-- Migration: 002_add_performance_fields.sql
-- Date: 2025-04-01
-- Description: 성능 모니터링 필드 추가

ALTER TABLE dspFlow ADD COLUMN AvgProcessingTimeMs REAL;
ALTER TABLE dspFlow ADD COLUMN MaxProcessingTimeMs REAL;

ALTER TABLE dspCall ADD COLUMN LastProcessingTimeMs REAL;

CREATE INDEX IF NOT EXISTS idx_dspFlow_AvgProcessingTime ON dspFlow(AvgProcessingTimeMs);
```

```fsharp
let private migration002 (conn: SqliteConnection) =
    async {
        let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN AvgProcessingTimeMs REAL") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN MaxProcessingTimeMs REAL") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN LastProcessingTimeMs REAL") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_dspFlow_AvgProcessingTime ON dspFlow(AvgProcessingTimeMs)") |> Async.AwaitTask
        return ()
    }
```

---

## ⚠️ 주의사항

### SQLite 제약사항

1. **컬럼 삭제 불가**
   - SQLite는 `ALTER TABLE DROP COLUMN` 미지원
   - 필요 시 새 테이블 생성 후 데이터 복사

2. **컬럼 타입 변경 불가**
   - SQLite는 `ALTER TABLE ALTER COLUMN` 미지원
   - 신중하게 타입 선택

3. **NOT NULL 제약 추가 어려움**
   - 기존 데이터에 NULL이 있으면 불가능
   - DEFAULT 값 설정 후 추가

### 권장사항

1. **백업 필수**
2. **테스트 DB에서 먼저 실행**
3. **운영 시간 외 실행**
4. **마이그레이션 스크립트 버전 관리 (Git)**

---

## 📚 관련 문서

- [02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md) - 스키마 설계
- [09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md) - 리팩토링 계획
