# 치명적 설계 결함 수정

## 🚨 발견된 치명적 결함

### 결함 1: dspFlow에 Call 레벨 컬럼 혼입

**문제**: `09_REFACTORING_PLAN.md` (line 25-30)에서 dspFlow에 WorkName, SequenceNo, IsHead, IsTail 추가

```sql
-- ❌ 잘못된 설계
ALTER TABLE dspFlow ADD COLUMN WorkName TEXT;
ALTER TABLE dspFlow ADD COLUMN SequenceNo INTEGER;
ALTER TABLE dspFlow ADD COLUMN IsHead INTEGER DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN IsTail INTEGER DEFAULT 0;
```

**문제점**:
1. **WorkName, SequenceNo, IsHead, IsTail는 Call별 1:N 정보**
   - 하나의 Flow는 여러 Call을 가짐
   - 각 Call마다 다른 WorkName, SequenceNo 존재
   - Flow 테이블에 저장하면 어느 Call의 값인지 불분명

2. **진실의 원천(Source of Truth) 분산**
   - dspFlow.WorkName과 dspCall.WorkName 중 어느 것이 진실인가?
   - 동기화 문제 발생 (불일치 가능성)

3. **Projection 경계 붕괴**
   - dspFlow: Flow 레벨 요약 Projection
   - dspCall: Call 레벨 상세 Projection
   - 혼합 시 Projection 목적 상실

**영향 범위**:
- Flow Detail View에서 잘못된 Work 그룹핑
- Topology 정보 불일치
- 유지보수 복잡도 증가

---

### 결함 2: Gantt/Cycle 분석에서 이력 손실

**문제**: `05_FEATURE_IMPLEMENTATION.md` (line 374-402)에서 dspCall의 "마지막 실행값"만으로 Gantt Chart 복원 시도

```fsharp
// ❌ 잘못된 설계
let calculateGanttLayout (flowName: string) (cycleNo: int) : Async<GanttBar list> =
    async {
        let! calls = DspRepository.getCallsByFlow flowName

        // CurrentCycleNo로 필터링
        let cycleCalls = calls |> List.filter (fun c -> c.CurrentCycleNo = cycleNo)

        // LastStartAt, LastFinishAt 사용
        let bars =
            cycleCalls
            |> List.map (fun call ->
                { StartTime = call.LastStartAt  // ⚠️ 마지막 실행만!
                  EndTime = call.LastFinishAt
                  Duration = call.LastDurationMs })
    }
```

**문제점**:
1. **이전 사이클 이력 덮어쓰기**
   ```
   Cycle 1: Work1 starts at 10:00:00
   Cycle 2: Work1 starts at 10:05:00  ← LastStartAt 덮어쓰기

   → Cycle 1의 Gantt Chart 복원 불가능!
   ```

2. **병렬/반복 호출 추적 불가**
   ```
   Flow: MainFlow
     - Work1 (반복 3회)
       - 1차: 10:00:00 - 10:00:01
       - 2차: 10:00:02 - 10:00:03
       - 3차: 10:00:04 - 10:00:05

   dspCall.LastStartAt = 10:00:04  ← 3차만 남음
   → 1차, 2차 이력 손실!
   ```

3. **Cycle 경계 불분명**
   ```
   CurrentCycleNo = 5

   → Cycle 1~4의 데이터는?
   → 각 Cycle의 시작/종료 시점은?
   → Gap, Wait Time 계산 불가!
   ```

**영향 범위**:
- Gantt Chart 부정확 (과거 사이클 복원 불가)
- Cycle Time 분석 불가 (MT, WT, CT 계산 오류)
- Bottleneck 분석 불가 (Gap 계산 오류)
- Replay 기능 불가 (이력 손실)

---

## ✅ 수정 방안

### 수정 1: dspFlow 스키마 정리 - Call 레벨 컬럼 제거

**원칙**:
- **dspFlow**: Flow 레벨 요약 정보만 저장 (1:1 매핑)
- **dspCall**: Call 레벨 상세 정보 저장 (1:N 매핑)

**수정된 스키마**:

```sql
-- ✅ 올바른 dspFlow 스키마
CREATE TABLE dspFlow (
    Id TEXT PRIMARY KEY,
    FlowName TEXT NOT NULL UNIQUE,
    SystemName TEXT,                      -- Flow가 속한 System (1:1)
    MovingStartName TEXT,                 -- Flow 시작 신호 (1:1)
    MovingEndName TEXT,                   -- Flow 종료 신호 (1:1)

    -- Flow 레벨 상태
    State TEXT NOT NULL DEFAULT 'Ready',  -- Ready, Going, Done, Error
    ActiveCallCount INTEGER DEFAULT 0,    -- 현재 실행 중인 Call 수
    ErrorCallCount INTEGER DEFAULT 0,     -- 오류 발생한 Call 수
    UnmappedCallCount INTEGER DEFAULT 0,  -- 매핑되지 않은 Call 수

    -- Flow 레벨 사이클 정보
    LastCycleStartAt TEXT,                -- 마지막 사이클 시작 시각
    LastCycleEndAt TEXT,                  -- 마지막 사이클 종료 시각
    LastCycleNo INTEGER DEFAULT 0,        -- 마지막 사이클 번호
    LastCycleDurationMs REAL,             -- 마지막 사이클 소요 시간

    -- Flow 레벨 통계
    MT REAL,                              -- Moving Time (총 작업 시간)
    WT REAL,                              -- Waiting Time (총 대기 시간)
    CT REAL,                              -- Cycle Time (총 사이클 시간)
    AverageCT REAL,                       -- 평균 사이클 시간
    StdDevCT REAL,                        -- 사이클 시간 표준편차
    MinCT REAL,                           -- 최소 사이클 시간
    MaxCT REAL,                           -- 최대 사이클 시간
    CompletedCycleCount INTEGER DEFAULT 0,-- 완료된 사이클 수

    -- Flow 레벨 플래그
    SlowCycleFlag INTEGER DEFAULT 0,      -- 느린 사이클 플래그
    FocusScore INTEGER DEFAULT 0,         -- 주목도 점수

    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

-- ✅ 올바른 dspCall 스키마
CREATE TABLE dspCall (
    Id TEXT PRIMARY KEY,
    CallName TEXT NOT NULL,
    ApiCall TEXT,

    -- Call이 속한 계층 정보 (1:1, 정적)
    WorkName TEXT NOT NULL,               -- ⚠️ Call이 속한 Work (정적)
    FlowName TEXT NOT NULL,               -- ⚠️ Call이 속한 Flow (정적)
    SystemName TEXT,                      -- ⚠️ Call이 속한 System (정적)

    -- Call Topology (1:1, 정적)
    Prev TEXT,                            -- 이전 Call 이름
    Next TEXT,                            -- 다음 Call 이름
    IsHead INTEGER DEFAULT 0,             -- Flow 시작 Call 여부
    IsTail INTEGER DEFAULT 0,             -- Flow 종료 Call 여부
    SequenceNo INTEGER,                   -- Flow 내 순서

    -- Call PLC 매핑 (1:1, 정적)
    Device TEXT,
    InTag TEXT,                           -- 센서 입력 태그
    OutTag TEXT,                          -- 출력 신호 태그

    -- Call 실행 상태 (동적, 최신값)
    State TEXT NOT NULL DEFAULT 'Ready',  -- Ready, Going, Done, Error
    ProgressRate REAL DEFAULT 0.0,        -- 진행률 (0.0 ~ 1.0)
    LastStartAt TEXT,                     -- ⚠️ 마지막 시작 시각 (덮어쓰기)
    LastFinishAt TEXT,                    -- ⚠️ 마지막 종료 시각 (덮어쓰기)
    LastDurationMs REAL,                  -- ⚠️ 마지막 소요 시간 (덮어쓰기)
    CurrentCycleNo INTEGER DEFAULT 0,     -- 현재 사이클 번호

    -- Call 통계 (Incremental)
    AverageGoingTime REAL,                -- 평균 실행 시간
    StdDevGoingTime REAL,                 -- 실행 시간 표준편차
    MinGoingTime REAL,                    -- 최소 실행 시간
    MaxGoingTime REAL,                    -- 최대 실행 시간
    GoingCount INTEGER DEFAULT 0,         -- 실행 횟수

    -- Call 오류/플래그
    ErrorCount INTEGER DEFAULT 0,
    ErrorText TEXT,
    SlowFlag INTEGER DEFAULT 0,           -- 느린 실행 플래그
    UnmappedFlag INTEGER DEFAULT 0,       -- 매핑 안됨 플래그
    FocusScore INTEGER DEFAULT 0,         -- 주목도 점수

    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),

    FOREIGN KEY(FlowName) REFERENCES dspFlow(FlowName)
);

CREATE INDEX idx_dspCall_FlowName ON dspCall(FlowName);
CREATE INDEX idx_dspCall_WorkName ON dspCall(WorkName);
CREATE INDEX idx_dspCall_State ON dspCall(State);
CREATE INDEX idx_dspCall_CurrentCycleNo ON dspCall(CurrentCycleNo);
```

**변경 요약**:
- ❌ **제거**: dspFlow.WorkName, dspFlow.SequenceNo, dspFlow.IsHead, dspFlow.IsTail
- ✅ **유지**: dspCall.WorkName, dspCall.SequenceNo, dspCall.IsHead, dspCall.IsTail
- ✅ **추가**: dspFlow.SystemName (Flow 레벨 정보)

---

### 수정 2: 이력 테이블 추가 - Cycle별 완전한 기록

**원칙**:
- **dspCall**: 현재 상태 (마지막 실행값, 통계)
- **dspCallHistory**: 모든 실행 이력 (Immutable)

**새 테이블 추가**:

```sql
-- ✅ Call 실행 이력 테이블 (Append-Only)
CREATE TABLE dspCallHistory (
    Id TEXT PRIMARY KEY,                  -- UUID
    CallId TEXT NOT NULL,                 -- dspCall.Id 참조
    CallName TEXT NOT NULL,
    FlowName TEXT NOT NULL,
    CycleNo INTEGER NOT NULL,             -- 사이클 번호

    -- 실행 시간
    StartAt TEXT NOT NULL,                -- 시작 시각 (ISO 8601)
    FinishAt TEXT,                        -- 종료 시각 (NULL = 진행중)
    DurationMs REAL,                      -- 소요 시간

    -- 실행 결과
    State TEXT NOT NULL,                  -- Going, Done, Error
    ErrorText TEXT,

    -- 컨텍스트
    TriggeredBy TEXT,                     -- 트리거 소스 (InTag, OutTag, Manual)
    BatchTimestamp TEXT,                  -- PLC 배치 타임스탬프

    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),

    FOREIGN KEY(CallId) REFERENCES dspCall(Id)
);

CREATE INDEX idx_dspCallHistory_CallId ON dspCallHistory(CallId);
CREATE INDEX idx_dspCallHistory_FlowName_CycleNo ON dspCallHistory(FlowName, CycleNo);
CREATE INDEX idx_dspCallHistory_StartAt ON dspCallHistory(StartAt);

-- ✅ Flow 사이클 이력 테이블 (Append-Only)
CREATE TABLE dspFlowCycleHistory (
    Id TEXT PRIMARY KEY,                  -- UUID
    FlowName TEXT NOT NULL,
    CycleNo INTEGER NOT NULL,             -- 사이클 번호

    -- 사이클 시간
    StartAt TEXT NOT NULL,                -- Head Call 시작 시각
    EndAt TEXT,                           -- Tail Call 종료 시각
    DurationMs REAL,                      -- 총 소요 시간

    -- 사이클 통계
    MT REAL,                              -- Moving Time (작업 시간)
    WT REAL,                              -- Waiting Time (대기 시간)
    CT REAL,                              -- Cycle Time (총 시간)

    -- 사이클 상태
    State TEXT NOT NULL,                  -- InProgress, Completed, Error
    ErrorCallCount INTEGER DEFAULT 0,
    SlowCycleFlag INTEGER DEFAULT 0,

    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),

    FOREIGN KEY(FlowName) REFERENCES dspFlow(FlowName)
);

CREATE INDEX idx_dspFlowCycleHistory_FlowName_CycleNo ON dspFlowCycleHistory(FlowName, CycleNo);
CREATE INDEX idx_dspFlowCycleHistory_StartAt ON dspFlowCycleHistory(StartAt);
```

**데이터 흐름**:

```
PLC Event (InTag Rising)
    ↓
StateTransition.handleInTagRisingEdge()
    ↓
┌─────────────────────────────────┐
│ 1. dspCall 업데이트 (현재 상태)│
│    - State = "Going"            │
│    - LastStartAt = timestamp    │
│    - CurrentCycleNo++           │
└─────────────────────────────────┘
    ↓
┌─────────────────────────────────┐
│ 2. dspCallHistory INSERT        │
│    - CallId, CycleNo            │
│    - StartAt = timestamp        │
│    - State = "Going"            │
└─────────────────────────────────┘
    ↓
PLC Event (OutTag Rising)
    ↓
StateTransition.handleOutTagRisingEdge()
    ↓
┌─────────────────────────────────┐
│ 1. dspCall 업데이트 (현재 상태)│
│    - State = "Done"             │
│    - LastFinishAt = timestamp   │
│    - LastDurationMs = duration  │
└─────────────────────────────────┘
    ↓
┌─────────────────────────────────┐
│ 2. dspCallHistory UPDATE        │
│    - FinishAt = timestamp       │
│    - DurationMs = duration      │
│    - State = "Done"             │
└─────────────────────────────────┘
    ↓
┌─────────────────────────────────┐
│ 3. dspFlowCycleHistory 체크     │
│    - Tail Call 완료시           │
│    - Flow Cycle 완료 기록       │
└─────────────────────────────────┘
```

---

## 📊 수정된 Gantt Chart 구현

### F# 분석 모듈

```fsharp
// DSPilot.Engine/Analysis/GanttLayout.fs

type GanttBar =
    { CallId: Guid
      CallName: string
      WorkName: string
      StartTime: DateTime
      EndTime: DateTime
      Duration: float
      YPosition: int
      State: string }

/// Gantt Chart 레이아웃 계산 (이력 기반)
let calculateGanttLayout (flowName: string) (cycleNo: int) : Async<GanttBar list> =
    async {
        // ✅ 이력 테이블에서 조회 (덮어쓰기 없음!)
        use conn = new SqliteConnection(getConnectionString())

        let sql = """
            SELECT
                h.CallId,
                h.CallName,
                c.WorkName,
                h.StartAt,
                h.FinishAt,
                h.DurationMs,
                c.SequenceNo,
                h.State
            FROM dspCallHistory h
            JOIN dspCall c ON h.CallId = c.Id
            WHERE h.FlowName = @FlowName
              AND h.CycleNo = @CycleNo
              AND h.FinishAt IS NOT NULL
            ORDER BY h.StartAt
        """

        let! rows =
            conn.QueryAsync<GanttRow>(sql, {| FlowName = flowName; CycleNo = cycleNo |})
            |> Async.AwaitTask

        let bars =
            rows
            |> Seq.mapi (fun i row ->
                { CallId = row.CallId
                  CallName = row.CallName
                  WorkName = row.WorkName
                  StartTime = DateTime.Parse(row.StartAt)
                  EndTime = DateTime.Parse(row.FinishAt)
                  Duration = row.DurationMs
                  YPosition = i
                  State = row.State })
            |> Seq.toList

        return bars
    }

/// Gap 분석 (연속된 Call 간 대기시간)
let calculateGaps (flowName: string) (cycleNo: int) : Async<GapInfo list> =
    async {
        use conn = new SqliteConnection(getConnectionString())

        let sql = """
            WITH OrderedCalls AS (
                SELECT
                    h.CallName,
                    h.StartAt,
                    h.FinishAt,
                    c.SequenceNo,
                    LAG(h.FinishAt) OVER (ORDER BY c.SequenceNo) AS PrevFinishAt
                FROM dspCallHistory h
                JOIN dspCall c ON h.CallId = c.Id
                WHERE h.FlowName = @FlowName
                  AND h.CycleNo = @CycleNo
                  AND h.FinishAt IS NOT NULL
            )
            SELECT
                CallName,
                PrevFinishAt,
                StartAt,
                (julianday(StartAt) - julianday(PrevFinishAt)) * 86400000.0 AS GapMs
            FROM OrderedCalls
            WHERE PrevFinishAt IS NOT NULL
        """

        let! gaps =
            conn.QueryAsync<GapInfo>(sql, {| FlowName = flowName; CycleNo = cycleNo |})
            |> Async.AwaitTask

        return gaps |> Seq.toList
    }

/// Cycle 목록 조회
let getCycleList (flowName: string) : Async<CycleInfo list> =
    async {
        use conn = new SqliteConnection(getConnectionString())

        let sql = """
            SELECT
                CycleNo,
                StartAt,
                EndAt,
                DurationMs,
                MT,
                WT,
                CT,
                State
            FROM dspFlowCycleHistory
            WHERE FlowName = @FlowName
            ORDER BY CycleNo DESC
            LIMIT 100
        """

        let! cycles =
            conn.QueryAsync<CycleInfo>(sql, {| FlowName = flowName |})
            |> Async.AwaitTask

        return cycles |> Seq.toList
    }
```

---

## 🔄 State Transition 로직 수정

### F# StateTransition 모듈

```fsharp
// DSPilot.Engine/Tracking/StateTransition.fs

let handleInTagRisingEdge (callId: Guid) (timestamp: DateTime) : Async<unit> =
    async {
        let! call = DspRepository.getCallById callId

        // 1. CurrentCycleNo 증가
        let newCycleNo = call.CurrentCycleNo + 1

        // 2. dspCall 현재 상태 업데이트
        let patch =
            { CallId = callId
              State = Some "Going"
              LastStartAt = Some timestamp
              CurrentCycleNo = Some newCycleNo
              ProgressRate = Some 0.5 }

        do! DspRepository.patchCall patch

        // 3. ✅ dspCallHistory INSERT (이력 기록)
        let historyId = Guid.NewGuid()
        let history =
            { Id = historyId.ToString()
              CallId = callId.ToString()
              CallName = call.CallName
              FlowName = call.FlowName
              CycleNo = newCycleNo
              StartAt = timestamp.ToString("o")
              FinishAt = null
              DurationMs = null
              State = "Going"
              TriggeredBy = "InTag"
              BatchTimestamp = timestamp.ToString("o") }

        do! DspRepository.insertCallHistory history

        // 4. Flow ActiveCallCount 증가
        do! FlowMetricsCalculator.incrementActiveCount call.FlowName

        // 5. ✅ Head Call이면 Flow Cycle 시작 기록
        if call.IsHead then
            do! DspRepository.insertOrUpdateFlowCycleHistory
                { FlowName = call.FlowName
                  CycleNo = newCycleNo
                  StartAt = timestamp.ToString("o")
                  State = "InProgress" }
    }

let handleOutTagRisingEdge (callId: Guid) (timestamp: DateTime) : Async<unit> =
    async {
        let! call = DspRepository.getCallById callId

        if call.LastStartAt.IsNone then
            // 시작 없이 종료: 오류 상태
            return ()
        else
            let duration = (timestamp - call.LastStartAt.Value).TotalMilliseconds

            // 1. dspCall 현재 상태 업데이트
            let patch =
                { CallId = callId
                  State = Some "Done"
                  LastFinishAt = Some timestamp
                  LastDurationMs = Some duration
                  ProgressRate = Some 1.0 }

            do! DspRepository.patchCall patch

            // 2. ✅ dspCallHistory UPDATE (이력 완료)
            do! DspRepository.updateCallHistory
                { CallId = callId.ToString()
                  CycleNo = call.CurrentCycleNo
                  FinishAt = timestamp.ToString("o")
                  DurationMs = duration
                  State = "Done" }

            // 3. 통계 업데이트
            do! StatisticsCalculator.updateCallStatistics callId duration

            // 4. Flow ActiveCallCount 감소
            do! FlowMetricsCalculator.decrementActiveCount call.FlowName

            // 5. ✅ Tail Call이면 Flow Cycle 완료 기록
            if call.IsTail then
                do! FlowMetricsCalculator.completeFlowCycle call.FlowName call.CurrentCycleNo timestamp
    }
```

---

## 🎯 Repository API 추가

### F# Repository 확장

```fsharp
// DSPilot.Engine/Database/Repository.fs

/// Call 실행 이력 삽입
let insertCallHistory (history: DspCallHistoryEntity) : Async<unit> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            INSERT INTO dspCallHistory (Id, CallId, CallName, FlowName, CycleNo,
                                        StartAt, FinishAt, DurationMs, State, TriggeredBy, BatchTimestamp)
            VALUES (@Id, @CallId, @CallName, @FlowName, @CycleNo,
                    @StartAt, @FinishAt, @DurationMs, @State, @TriggeredBy, @BatchTimestamp)
        """
        let! _ = conn.ExecuteAsync(sql, history) |> Async.AwaitTask
        return ()
    }

/// Call 실행 이력 업데이트 (완료 시)
let updateCallHistory (update: CallHistoryUpdate) : Async<unit> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            UPDATE dspCallHistory
            SET FinishAt = @FinishAt,
                DurationMs = @DurationMs,
                State = @State
            WHERE CallId = @CallId
              AND CycleNo = @CycleNo
              AND FinishAt IS NULL
        """
        let! _ = conn.ExecuteAsync(sql, update) |> Async.AwaitTask
        return ()
    }

/// Flow Cycle 이력 삽입 또는 업데이트
let insertOrUpdateFlowCycleHistory (cycle: DspFlowCycleHistoryEntity) : Async<unit> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            INSERT INTO dspFlowCycleHistory (Id, FlowName, CycleNo, StartAt, State)
            VALUES (@Id, @FlowName, @CycleNo, @StartAt, @State)
            ON CONFLICT(FlowName, CycleNo) DO UPDATE SET
                EndAt = excluded.EndAt,
                DurationMs = excluded.DurationMs,
                MT = excluded.MT,
                WT = excluded.WT,
                CT = excluded.CT,
                State = excluded.State,
                UpdatedAt = datetime('now')
        """
        let! _ = conn.ExecuteAsync(sql, cycle) |> Async.AwaitTask
        return ()
    }
```

---

## 📈 Cycle Time 분석 예시

### SQL 쿼리 예시

```sql
-- ✅ Cycle 3번의 Gantt Chart 데이터
SELECT
    h.CallName,
    c.WorkName,
    h.StartAt,
    h.FinishAt,
    h.DurationMs,
    c.SequenceNo
FROM dspCallHistory h
JOIN dspCall c ON h.CallId = c.Id
WHERE h.FlowName = 'MainFlow'
  AND h.CycleNo = 3
  AND h.FinishAt IS NOT NULL
ORDER BY c.SequenceNo;

-- Result:
-- CallName | WorkName | StartAt              | FinishAt             | DurationMs | SequenceNo
-- Work1    | Moving   | 2025-03-22T10:00:00  | 2025-03-22T10:00:01  | 1050.23    | 1
-- Work2    | Moving   | 2025-03-22T10:00:02  | 2025-03-22T10:00:03  | 1023.45    | 2
-- Work3    | Process  | 2025-03-22T10:00:04  | 2025-03-22T10:00:05  | 1123.67    | 3

-- ✅ Cycle 간 Gap 분석
WITH OrderedCalls AS (
    SELECT
        h.CallName,
        h.FinishAt,
        LEAD(h.StartAt) OVER (ORDER BY c.SequenceNo) AS NextStartAt
    FROM dspCallHistory h
    JOIN dspCall c ON h.CallId = c.Id
    WHERE h.FlowName = 'MainFlow'
      AND h.CycleNo = 3
)
SELECT
    CallName,
    FinishAt,
    NextStartAt,
    (julianday(NextStartAt) - julianday(FinishAt)) * 86400000.0 AS GapMs
FROM OrderedCalls
WHERE NextStartAt IS NOT NULL;

-- Result:
-- CallName | FinishAt             | NextStartAt          | GapMs
-- Work1    | 2025-03-22T10:00:01  | 2025-03-22T10:00:02  | 1000.0  ← 1초 대기
-- Work2    | 2025-03-22T10:00:03  | 2025-03-22T10:00:04  | 1000.0  ← 1초 대기

-- ✅ 모든 Cycle의 통계
SELECT
    CycleNo,
    StartAt,
    EndAt,
    MT,
    WT,
    CT,
    State
FROM dspFlowCycleHistory
WHERE FlowName = 'MainFlow'
ORDER BY CycleNo DESC
LIMIT 10;

-- Result:
-- CycleNo | StartAt              | EndAt                | MT      | WT     | CT      | State
-- 5       | 2025-03-22T10:20:00  | 2025-03-22T10:20:05  | 3123.45 | 1876.55| 5000.0  | Completed
-- 4       | 2025-03-22T10:15:00  | 2025-03-22T10:15:05  | 3150.23 | 1849.77| 5000.0  | Completed
-- 3       | 2025-03-22T10:10:00  | 2025-03-22T10:10:05  | 3097.35 | 1902.65| 5000.0  | Completed
```

---

## ✅ 검증 시나리오

### 시나리오 1: 병렬 호출 추적

```
MainFlow:
  Work1: 10:00:00 - 10:00:01 (Cycle 1)
  Work1: 10:00:02 - 10:00:03 (Cycle 1, 2차 호출)
  Work2: 10:00:01 - 10:00:02 (Cycle 1)

dspCall (현재 상태):
  Work1: LastStartAt = 10:00:02, LastDurationMs = 1000 (2차 값)
  Work2: LastStartAt = 10:00:01, LastDurationMs = 1000

dspCallHistory (완전한 이력):
  Row 1: Work1, CycleNo=1, StartAt=10:00:00, FinishAt=10:00:01, DurationMs=1000
  Row 2: Work1, CycleNo=1, StartAt=10:00:02, FinishAt=10:00:03, DurationMs=1000
  Row 3: Work2, CycleNo=1, StartAt=10:00:01, FinishAt=10:00:02, DurationMs=1000

✅ Gantt Chart: 모든 3개 바 표시 가능
✅ Gap 계산: Work1(1차) → Work2, Work2 → Work1(2차) 모두 계산 가능
```

### 시나리오 2: 과거 Cycle Replay

```
요청: Cycle 3의 Gantt Chart 표시

SELECT * FROM dspCallHistory WHERE FlowName = 'MainFlow' AND CycleNo = 3

✅ Cycle 3의 모든 Call 이력 복원 가능
✅ Cycle 1, 2, 4, 5와 독립적으로 조회 가능
```

### 시나리오 3: Cycle 경계 명확화

```
dspFlowCycleHistory:
  CycleNo=1, StartAt=10:00:00, EndAt=10:00:05
  CycleNo=2, StartAt=10:05:00, EndAt=10:05:06
  CycleNo=3, StartAt=10:10:00, EndAt=10:10:05

✅ 각 Cycle의 시작/종료 명확
✅ Cycle Time (CT) = EndAt - StartAt 정확히 계산
✅ Gap Between Cycles = Cycle2.StartAt - Cycle1.EndAt 계산 가능
```

---

## 📋 마이그레이션 체크리스트

### Phase 1: 스키마 수정
- [ ] dspFlow에서 WorkName, SequenceNo, IsHead, IsTail 컬럼 제거
- [ ] dspCallHistory 테이블 생성
- [ ] dspFlowCycleHistory 테이블 생성
- [ ] 인덱스 생성

### Phase 2: Repository 확장
- [ ] insertCallHistory 구현
- [ ] updateCallHistory 구현
- [ ] insertOrUpdateFlowCycleHistory 구현
- [ ] getCallHistoryByCycle 구현

### Phase 3: StateTransition 수정
- [ ] handleInTagRisingEdge에 history INSERT 추가
- [ ] handleOutTagRisingEdge에 history UPDATE 추가
- [ ] Head Call 감지 시 Flow Cycle 시작 기록
- [ ] Tail Call 감지 시 Flow Cycle 완료 기록

### Phase 4: GanttLayout 재구현
- [ ] calculateGanttLayout을 history 기반으로 수정
- [ ] calculateGaps 구현
- [ ] getCycleList 구현

### Phase 5: UI 수정
- [ ] CycleTimeAnalysis.razor 업데이트
- [ ] Cycle 선택 드롭다운 추가
- [ ] Gantt Chart 렌더링 수정

---

## 📚 관련 문서

- [02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md) - 전체 스키마
- [09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md) - 리팩토링 계획 (수정됨)
- [05_FEATURE_IMPLEMENTATION.md](./05_FEATURE_IMPLEMENTATION.md) - 기능 구현 (수정됨)
- [12_REPOSITORY_API.md](./12_REPOSITORY_API.md) - Repository API
