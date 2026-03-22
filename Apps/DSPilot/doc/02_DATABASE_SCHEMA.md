# DSPilot 데이터베이스 스키마

## 🎯 목적

dspFlow와 dspCall 테이블은 **Projection 테이블**로, UI가 즉시 사용할 수 있도록 사전 계산된 데이터를 저장합니다.

---

## 📊 테이블 구조

### dspFlow (Flow Projection)

Flow 단위의 집계 및 상태를 저장하는 Projection 테이블입니다.

```sql
CREATE TABLE IF NOT EXISTS dspFlow (
    -- Primary Key
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    FlowName TEXT NOT NULL UNIQUE,

    -- 4.1 Static Metadata (Bootstrap 시 설정)
    SystemName TEXT,
    WorkName TEXT,  -- ⚠️ work.Name 사용 (flow.Name 아님!)
    MovingStartName TEXT,
    MovingEndName TEXT,
    SequenceNo INTEGER,
    IsHead INTEGER DEFAULT 0,  -- Boolean
    IsTail INTEGER DEFAULT 0,  -- Boolean

    -- 4.2 Real-time State (PLC Event 수신 시 업데이트)
    State TEXT DEFAULT 'Ready',  -- 'Ready', 'Going', 'Error'
    ActiveCallCount INTEGER DEFAULT 0,
    ErrorCallCount INTEGER DEFAULT 0,
    LastCycleStartAt TEXT,  -- ISO 8601 timestamp
    LastCycleEndAt TEXT,
    LastCycleNo INTEGER DEFAULT 0,

    -- 4.3 Cumulative Statistics (Tail Call 완료 시 업데이트)
    MT REAL,  -- Moving Time (ms)
    WT REAL,  -- Waiting Time (ms)
    CT REAL,  -- Cycle Time (ms)
    LastCycleDurationMs REAL,
    AverageCT REAL,
    StdDevCT REAL,
    MinCT REAL,
    MaxCT REAL,
    CompletedCycleCount INTEGER DEFAULT 0,

    -- 4.4 Derived Warnings (계산 시 자동 설정)
    SlowCycleFlag INTEGER DEFAULT 0,  -- Boolean
    UnmappedCallCount INTEGER DEFAULT 0,
    FocusScore INTEGER DEFAULT 0,

    -- Metadata
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_dspFlow_FlowName ON dspFlow(FlowName);
CREATE INDEX IF NOT EXISTS idx_dspFlow_State ON dspFlow(State);
CREATE INDEX IF NOT EXISTS idx_dspFlow_FocusScore ON dspFlow(FocusScore DESC);
```

---

### dspCall (Call Projection)

Call 단위의 상태 및 통계를 저장하는 Projection 테이블입니다.

```sql
CREATE TABLE IF NOT EXISTS dspCall (
    -- Primary Key
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CallId TEXT NOT NULL UNIQUE,  -- GUID

    -- 4.1 Static Metadata (Bootstrap 시 설정)
    CallName TEXT NOT NULL,
    ApiCall TEXT,
    WorkName TEXT NOT NULL,  -- ⚠️ work.Name 사용!
    FlowName TEXT NOT NULL,
    SystemName TEXT,
    Next TEXT,  -- Next Call Name
    Prev TEXT,  -- Previous Call Name
    IsHead INTEGER DEFAULT 0,  -- Boolean
    IsTail INTEGER DEFAULT 0,  -- Boolean
    SequenceNo INTEGER,
    Device TEXT,
    InTag TEXT,  -- PLC Tag for input signal
    OutTag TEXT,  -- PLC Tag for output signal

    -- 4.2 Real-time State (PLC Event 수신 시 업데이트)
    State TEXT DEFAULT 'Ready',  -- 'Ready', 'Going', 'Done', 'Error'
    ProgressRate REAL DEFAULT 0.0,  -- 0.0 ~ 1.0
    LastStartAt TEXT,  -- ISO 8601 timestamp
    LastFinishAt TEXT,
    LastDurationMs REAL,
    CurrentCycleNo INTEGER DEFAULT 0,

    -- 4.3 Cumulative Statistics (Call 완료 시 업데이트)
    AverageGoingTime REAL,
    StdDevGoingTime REAL,
    MinGoingTime REAL,
    MaxGoingTime REAL,
    GoingCount INTEGER DEFAULT 0,
    ErrorCount INTEGER DEFAULT 0,
    ErrorText TEXT,

    -- 4.4 Derived Warnings (계산 시 자동 설정)
    SlowFlag INTEGER DEFAULT 0,  -- Boolean: Duration > Avg + 2*StdDev
    UnmappedFlag INTEGER DEFAULT 0,  -- Boolean: InTag 또는 OutTag 누락
    FocusScore INTEGER DEFAULT 0,  -- Priority score

    -- Metadata
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_dspCall_CallId ON dspCall(CallId);
CREATE INDEX IF NOT EXISTS idx_dspCall_FlowName ON dspCall(FlowName);
CREATE INDEX IF NOT EXISTS idx_dspCall_State ON dspCall(State);
CREATE INDEX IF NOT EXISTS idx_dspCall_FocusScore ON dspCall(FocusScore DESC);
CREATE INDEX IF NOT EXISTS idx_dspCall_WorkName ON dspCall(WorkName);
```

---

## 🔄 컬럼 책임 분류

### 4.1 Static Metadata (정적 메타데이터)

**설정 시점**: AASX Bootstrap 시 (프로젝트 로드)
**업데이트 빈도**: 프로젝트 변경 시만

**dspFlow**:
- SystemName, WorkName, MovingStartName, MovingEndName
- SequenceNo, IsHead, IsTail

**dspCall**:
- CallName, ApiCall, WorkName, FlowName, SystemName
- Next, Prev, IsHead, IsTail, SequenceNo
- Device, InTag, OutTag

**업데이트 규칙**:
```fsharp
// UPSERT 시 기존 통계는 보존
INSERT INTO dspFlow (FlowName, SystemName, WorkName, ...)
VALUES (?, ?, ?, ...)
ON CONFLICT(FlowName) DO UPDATE SET
    SystemName = excluded.SystemName,
    WorkName = excluded.WorkName,
    -- 통계 필드는 COALESCE로 기존 값 유지
    AverageCT = COALESCE(dspFlow.AverageCT, excluded.AverageCT),
    UpdatedAt = datetime('now')
```

---

### 4.2 Real-time State (실시간 상태)

**설정 시점**: PLC Event 수신 시
**업데이트 빈도**: 수십 ms ~ 수초

**dspFlow**:
- State, ActiveCallCount, ErrorCallCount
- LastCycleStartAt, LastCycleEndAt, LastCycleNo

**dspCall**:
- State, ProgressRate
- LastStartAt, LastFinishAt, LastDurationMs
- CurrentCycleNo, ErrorText

**업데이트 트리거**:
- InTag Rising Edge → State: Ready → Going
- OutTag Rising Edge → State: Going → Done
- Error 발생 → State: Error

---

### 4.3 Cumulative Statistics (누적 통계)

**설정 시점**: Call/Flow 완료 시
**업데이트 빈도**: 수초 ~ 수십 초

**dspFlow**:
- MT, WT, CT, LastCycleDurationMs
- AverageCT, StdDevCT, MinCT, MaxCT
- CompletedCycleCount

**dspCall**:
- AverageGoingTime, StdDevGoingTime, MinGoingTime, MaxGoingTime
- GoingCount, ErrorCount

**계산 알고리즘**:
```fsharp
// Incremental Average
let newAvg = (oldAvg * float(n - 1) + newValue) / float(n)

// Incremental Variance (Welford's method)
let delta = newValue - oldAvg
let newAvg = oldAvg + delta / float(n)
let delta2 = newValue - newAvg
let newM2 = oldM2 + delta * delta2
let newVariance = newM2 / float(n)
let newStdDev = sqrt(newVariance)

// Min/Max
let newMin = min(oldMin, newValue)
let newMax = max(oldMax, newValue)
```

---

### 4.4 Derived Warnings (파생 경고)

**설정 시점**: 통계 계산 시 자동 판정
**업데이트 빈도**: Call/Flow 완료 시

**dspFlow**:
- SlowCycleFlag: CT > AverageCT + 2*StdDevCT
- UnmappedCallCount: COUNT(UnmappedFlag=1)
- FocusScore: 집계된 우선순위 점수

**dspCall**:
- SlowFlag: LastDurationMs > AverageGoingTime + 2*StdDevGoingTime
- UnmappedFlag: InTag IS NULL OR OutTag IS NULL
- FocusScore: Error(+100) + Unmapped(+70) + Slow(+50) + HighStdDev(+30)

---

## 🚨 중요 규칙

### 1. WorkName 정확도

```fsharp
// ❌ 잘못된 코드
{ WorkName = flow.Name }

// ✅ 올바른 코드
{ WorkName = work.Name }
```

**이유**: Flow는 System → Flow 계층이지만, Call은 Work 소속입니다.
Flow detail view에서 Work별 그룹핑이 필요하므로 정확한 WorkName이 필수입니다.

---

### 2. DROP TABLE 금지

```sql
-- ❌ 금지
DROP TABLE IF EXISTS dspFlow;
CREATE TABLE dspFlow (...);

-- ✅ 허용
ALTER TABLE dspFlow ADD COLUMN NewField TEXT;
```

**이유**: DROP하면 누적 통계(AverageCT, GoingCount 등)가 모두 손실됩니다.
Migration 기반 스키마 관리를 사용해야 합니다.

---

### 3. UPSERT 시 통계 보존

```sql
INSERT INTO dspCall (CallId, CallName, FlowName, ...)
VALUES (?, ?, ?, ...)
ON CONFLICT(CallId) DO UPDATE SET
    CallName = excluded.CallName,
    FlowName = excluded.FlowName,
    -- 통계는 기존 값 유지
    AverageGoingTime = COALESCE(dspCall.AverageGoingTime, excluded.AverageGoingTime),
    GoingCount = COALESCE(dspCall.GoingCount, excluded.GoingCount),
    UpdatedAt = datetime('now')
```

---

## 🔗 참조

### F# Entity 정의

```fsharp
// DSPilot.Engine/Database/Entities.fs

type DspFlowEntity =
    { Id: int option
      FlowName: string
      SystemName: string option
      WorkName: string option
      MovingStartName: string option
      MovingEndName: string option
      SequenceNo: int option
      IsHead: bool
      IsTail: bool
      State: string option
      ActiveCallCount: int
      ErrorCallCount: int
      LastCycleStartAt: DateTime option
      LastCycleEndAt: DateTime option
      LastCycleNo: int
      MT: float option
      WT: float option
      CT: float option
      LastCycleDurationMs: float option
      AverageCT: float option
      StdDevCT: float option
      MinCT: float option
      MaxCT: float option
      CompletedCycleCount: int
      SlowCycleFlag: bool
      UnmappedCallCount: int
      FocusScore: int
      CreatedAt: DateTime
      UpdatedAt: DateTime }

type DspCallEntity =
    { Id: int option
      CallId: Guid
      CallName: string
      ApiCall: string option
      WorkName: string
      FlowName: string
      SystemName: string option
      Next: string option
      Prev: string option
      IsHead: bool
      IsTail: bool
      SequenceNo: int option
      Device: string option
      InTag: string option
      OutTag: string option
      State: string
      ProgressRate: float
      LastStartAt: DateTime option
      LastFinishAt: DateTime option
      LastDurationMs: float option
      CurrentCycleNo: int
      AverageGoingTime: float option
      StdDevGoingTime: float option
      MinGoingTime: float option
      MaxGoingTime: float option
      GoingCount: int
      ErrorCount: int
      ErrorText: string option
      SlowFlag: bool
      UnmappedFlag: bool
      FocusScore: int
      CreatedAt: DateTime
      UpdatedAt: DateTime }
```

---

## 📚 관련 문서

- [01_ARCHITECTURE.md](./01_ARCHITECTURE.md) - 전체 아키텍처
- [03_PROJECTION_PATTERN.md](./03_PROJECTION_PATTERN.md) - Projection 패턴
- [05_UPDATE_RULES.md](./05_UPDATE_RULES.md) - 업데이트 규칙
- [10_MIGRATION_GUIDE.md](./10_MIGRATION_GUIDE.md) - 마이그레이션 가이드
