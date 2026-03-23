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

**현재 구현 상태 (Migration 001)**: 최소 필드만 포함된 초기 스키마
```sql
CREATE TABLE IF NOT EXISTS dspCall (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CallName TEXT NOT NULL UNIQUE,
    FlowName TEXT NOT NULL,
    State TEXT DEFAULT 'Ready',
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (FlowName) REFERENCES dspFlow(FlowName)
);

CREATE INDEX IF NOT EXISTS idx_call_flow ON dspCall(FlowName);
```

**계획된 전체 스키마 (향후 Migration)**: 아래 필드들은 향후 Migration으로 추가 예정
```sql
-- 추가 예정 컬럼들:
ALTER TABLE dspCall ADD COLUMN CallId TEXT;  -- GUID (UNIQUE 제약 조건 필요)
ALTER TABLE dspCall ADD COLUMN ApiCall TEXT;
ALTER TABLE dspCall ADD COLUMN WorkName TEXT;
ALTER TABLE dspCall ADD COLUMN SystemName TEXT;
ALTER TABLE dspCall ADD COLUMN Next TEXT;
ALTER TABLE dspCall ADD COLUMN Prev TEXT;
ALTER TABLE dspCall ADD COLUMN IsHead INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN IsTail INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN SequenceNo INTEGER;
ALTER TABLE dspCall ADD COLUMN Device TEXT;
ALTER TABLE dspCall ADD COLUMN InTag TEXT;  -- PLC Tag for input signal
ALTER TABLE dspCall ADD COLUMN OutTag TEXT;  -- PLC Tag for output signal
ALTER TABLE dspCall ADD COLUMN ProgressRate REAL DEFAULT 0.0;
ALTER TABLE dspCall ADD COLUMN LastStartAt TEXT;
ALTER TABLE dspCall ADD COLUMN LastFinishAt TEXT;
ALTER TABLE dspCall ADD COLUMN LastDurationMs REAL;
ALTER TABLE dspCall ADD COLUMN CurrentCycleNo INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN AverageGoingTime REAL;
ALTER TABLE dspCall ADD COLUMN StdDevGoingTime REAL;
ALTER TABLE dspCall ADD COLUMN MinGoingTime REAL;
ALTER TABLE dspCall ADD COLUMN MaxGoingTime REAL;
ALTER TABLE dspCall ADD COLUMN GoingCount INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN ErrorCount INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN ErrorText TEXT;
ALTER TABLE dspCall ADD COLUMN SlowFlag INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN UnmappedFlag INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN FocusScore INTEGER DEFAULT 0;

-- 인덱스 추가
CREATE INDEX IF NOT EXISTS idx_dspCall_CallId ON dspCall(CallId);
CREATE INDEX IF NOT EXISTS idx_dspCall_State ON dspCall(State);
CREATE INDEX IF NOT EXISTS idx_dspCall_FocusScore ON dspCall(FocusScore DESC);
CREATE INDEX IF NOT EXISTS idx_dspCall_WorkName ON dspCall(WorkName);
```

**참고**:
- 현재는 Migration 001에 정의된 최소 스키마만 사용 중
- F# Entities.fs의 DspCallEntity는 전체 스키마를 정의하지만, 실제 DB에는 일부만 존재
- 누락된 필드 접근 시 NULL 처리 또는 기본값 사용

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

### F# Entity 정의 (현재 구현)

```fsharp
// DSPilot.Engine/Database/Entities.fs

/// Flow entity - dsp.db's dspFlow table (간소화된 버전)
[<CLIMutable>]
type DspFlowEntity =
    { Id: int
      FlowName: string
      MT: int option
      WT: int option
      CT: int option
      State: string option
      MovingStartName: string option
      MovingEndName: string option
      CreatedAt: DateTime
      UpdatedAt: DateTime }

    static member Create(flowName: string) =
        { Id = 0
          FlowName = flowName
          MT = None
          WT = None
          CT = None
          State = Some "Ready"
          MovingStartName = None
          MovingEndName = None
          CreatedAt = DateTime.UtcNow
          UpdatedAt = DateTime.UtcNow }

/// Call entity - dsp.db's dspCall table (간소화된 버전)
[<CLIMutable>]
type DspCallEntity =
    { Id: int
      CallId: Guid  // GUID (DB에는 아직 미구현)
      CallName: string
      ApiCall: string
      WorkName: string
      FlowName: string
      Next: string option
      Prev: string option
      AutoPre: string option
      CommonPre: string option
      State: string
      ProgressRate: float
      PreviousGoingTime: int option
      AverageGoingTime: float option
      StdDevGoingTime: float option
      GoingCount: int
      Device: string option
      ErrorText: string option
      CreatedAt: DateTime
      UpdatedAt: DateTime }

    static member Create(callId: Guid, callName: string, apiCall: string, workName: string, flowName: string) =
        { Id = 0
          CallId = callId
          CallName = callName
          ApiCall = apiCall
          WorkName = workName
          FlowName = flowName
          Next = None
          Prev = None
          AutoPre = None
          CommonPre = None
          State = "Ready"
          ProgressRate = 0.0
          PreviousGoingTime = None
          AverageGoingTime = None
          StdDevGoingTime = None
          GoingCount = 0
          Device = None
          ErrorText = None
          CreatedAt = DateTime.UtcNow
          UpdatedAt = DateTime.UtcNow }

/// Flow state (for UI display)
[<CLIMutable>]
type FlowState =
    { Id: int
      FlowName: string
      MT: int option
      WT: int option
      State: string
      MovingStartName: string option
      MovingEndName: string option }

/// Call state DTO (for UI display)
[<CLIMutable>]
type CallStateDto =
    { Id: int
      CallName: string
      FlowName: string
      WorkName: string
      State: string
      ProgressRate: float
      GoingCount: int
      AverageGoingTime: float option
      Device: string option
      ErrorText: string option }
```

**중요**:
- 현재 Entity 정의는 실제 DB 스키마보다 더 많은 필드를 포함
- Repository 계층에서 존재하지 않는 컬럼 접근 시 NULL 처리
- 향후 Migration으로 DB 스키마를 Entity 정의에 맞춤

---

## 📚 관련 문서

- [01_ARCHITECTURE.md](./01_ARCHITECTURE.md) - 전체 아키텍처
- [03_PROJECTION_PATTERN.md](./03_PROJECTION_PATTERN.md) - Projection 패턴
- [05_UPDATE_RULES.md](./05_UPDATE_RULES.md) - 업데이트 규칙
- [10_MIGRATION_GUIDE.md](./10_MIGRATION_GUIDE.md) - 마이그레이션 가이드
