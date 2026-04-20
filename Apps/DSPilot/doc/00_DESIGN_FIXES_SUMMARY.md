# 설계 결함 수정 요약

## 📌 수정 개요

두 가지 치명적 설계 결함을 발견하고 수정했습니다:

1. **dspFlow에 Call 레벨 컬럼 혼입** → Projection 경계 복원
2. **Gantt/Cycle 분석에서 이력 손실** → 이력 테이블 추가

---

## 🚨 결함 1: Projection 경계 붕괴

### 문제

`09_REFACTORING_PLAN.md`에서 dspFlow에 Call 레벨 정보를 추가하려 함:

```sql
-- ❌ 잘못된 설계
ALTER TABLE dspFlow ADD COLUMN WorkName TEXT;
ALTER TABLE dspFlow ADD COLUMN SequenceNo INTEGER;
ALTER TABLE dspFlow ADD COLUMN IsHead INTEGER;
ALTER TABLE dspFlow ADD COLUMN IsTail INTEGER;
```

### 왜 문제인가?

- Flow 1건 : Call N건 (1:N 관계)
- WorkName, SequenceNo, IsHead, IsTail은 **각 Call마다 다른 값**
- Flow 테이블에 저장하면 **어느 Call의 값인지 불분명**

### 수정

```sql
-- ✅ dspFlow: Flow 레벨 정보만
ALTER TABLE dspFlow ADD COLUMN SystemName TEXT;          -- Flow가 속한 System
ALTER TABLE dspFlow ADD COLUMN ActiveCallCount INTEGER;  -- Flow 레벨 집계
ALTER TABLE dspFlow ADD COLUMN MT REAL;                  -- Flow 레벨 통계
ALTER TABLE dspFlow ADD COLUMN WT REAL;
ALTER TABLE dspFlow ADD COLUMN CT REAL;

-- ✅ dspCall: Call 레벨 정보 (Topology)
ALTER TABLE dspCall ADD COLUMN WorkName TEXT;   -- Call이 속한 Work
ALTER TABLE dspCall ADD COLUMN SequenceNo INTEGER;
ALTER TABLE dspCall ADD COLUMN IsHead INTEGER;
ALTER TABLE dspCall ADD COLUMN IsTail INTEGER;
ALTER TABLE dspCall ADD COLUMN Prev TEXT;
ALTER TABLE dspCall ADD COLUMN Next TEXT;
```

### 원칙

| 테이블 | 역할 | 카디널리티 |
|-------|------|-----------|
| **dspFlow** | Flow 레벨 요약 Projection | 1:1 (Flow 1건) |
| **dspCall** | Call 레벨 상세 Projection | 1:N (Call N건) |

---

## 🚨 결함 2: Gantt Chart 이력 손실

### 문제

`05_FEATURE_IMPLEMENTATION.md`에서 dspCall의 "마지막 실행값"만으로 Gantt Chart 복원 시도:

```fsharp
// ❌ 잘못된 설계
let calculateGanttLayout (flowName: string) (cycleNo: int) =
    let! calls = DspRepository.getCallsByFlow flowName
    let cycleCalls = calls |> List.filter (fun c -> c.CurrentCycleNo = cycleNo)

    // ⚠️ LastStartAt, LastFinishAt 사용
    cycleCalls
    |> List.map (fun call ->
        { StartTime = call.LastStartAt    // 마지막 값만!
          EndTime = call.LastFinishAt      // 이전 사이클 손실!
          Duration = call.LastDurationMs })
```

### 왜 문제인가?

#### 예시 1: 이전 사이클 덮어쓰기

```
Cycle 1: Work1 starts at 10:00:00, finishes at 10:00:01
dspCall.LastStartAt = 10:00:00
dspCall.LastFinishAt = 10:00:01

Cycle 2: Work1 starts at 10:05:00, finishes at 10:05:01
dspCall.LastStartAt = 10:05:00  ← 덮어쓰기!
dspCall.LastFinishAt = 10:05:01  ← 덮어쓰기!

→ Cycle 1의 Gantt Chart 복원 불가능!
```

#### 예시 2: 병렬/반복 호출 손실

```
MainFlow Cycle 1:
  - Work1 (1차): 10:00:00 - 10:00:01
  - Work2:       10:00:01 - 10:00:02
  - Work1 (2차): 10:00:02 - 10:00:03  ← Work1 반복

dspCall (Work1):
  LastStartAt = 10:00:02    ← 2차만 남음
  LastFinishAt = 10:00:03

→ Work1 1차 실행 이력 손실!
→ Gantt Chart에서 1개 바만 표시됨 (실제는 2개)
```

### 수정

#### 이력 테이블 추가

```sql
-- ✅ Call 실행 이력 (Append-Only)
CREATE TABLE dspCallHistory (
    Id TEXT PRIMARY KEY,
    CallId TEXT NOT NULL,
    CallName TEXT NOT NULL,
    FlowName TEXT NOT NULL,
    CycleNo INTEGER NOT NULL,     -- 사이클 번호
    StartAt TEXT NOT NULL,         -- 시작 시각
    FinishAt TEXT,                 -- 종료 시각
    DurationMs REAL,               -- 소요 시간
    State TEXT NOT NULL,
    TriggeredBy TEXT,
    BatchTimestamp TEXT,
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY(CallId) REFERENCES dspCall(Id)
);

-- ✅ Flow 사이클 이력
CREATE TABLE dspFlowCycleHistory (
    Id TEXT PRIMARY KEY,
    FlowName TEXT NOT NULL,
    CycleNo INTEGER NOT NULL,
    StartAt TEXT NOT NULL,
    EndAt TEXT,
    DurationMs REAL,
    MT REAL, WT REAL, CT REAL,
    State TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY(FlowName) REFERENCES dspFlow(FlowName)
);
```

#### 수정된 Gantt Chart 로직

```fsharp
// ✅ 올바른 설계 (이력 기반)
let calculateGanttLayout (flowName: string) (cycleNo: int) =
    use conn = new SqliteConnection(getConnectionString())

    let sql = """
        SELECT h.CallId, h.CallName, c.WorkName,
               h.StartAt, h.FinishAt, h.DurationMs,
               c.SequenceNo, h.State
        FROM dspCallHistory h
        JOIN dspCall c ON h.CallId = c.Id
        WHERE h.FlowName = @FlowName
          AND h.CycleNo = @CycleNo
          AND h.FinishAt IS NOT NULL
        ORDER BY h.StartAt
    """

    conn.QueryAsync<GanttRow>(sql, {| FlowName = flowName; CycleNo = cycleNo |})
```

### 데이터 흐름

```
PLC Event (InTag Rising)
    ↓
┌────────────────────────────┐
│ 1. dspCall 업데이트        │
│    State = "Going"         │
│    LastStartAt = timestamp │
│    CurrentCycleNo++        │
└────────────────────────────┘
    ↓
┌────────────────────────────┐
│ 2. dspCallHistory INSERT   │
│    CycleNo, StartAt 기록   │
│    (Append-Only!)          │
└────────────────────────────┘

PLC Event (OutTag Rising)
    ↓
┌────────────────────────────┐
│ 1. dspCall 업데이트        │
│    State = "Done"          │
│    LastFinishAt = timestamp│
└────────────────────────────┘
    ↓
┌────────────────────────────┐
│ 2. dspCallHistory UPDATE   │
│    FinishAt, DurationMs    │
│    (이력 완료)             │
└────────────────────────────┘
```

---

## 📊 수정 후 효과

### Cycle 1~5 모두 복원 가능

```sql
-- Cycle 3의 Gantt Chart
SELECT * FROM dspCallHistory
WHERE FlowName = 'MainFlow' AND CycleNo = 3;

-- Cycle 1의 Gantt Chart
SELECT * FROM dspCallHistory
WHERE FlowName = 'MainFlow' AND CycleNo = 1;

-- ✅ 모든 과거 사이클 완벽 복원!
```

### 병렬/반복 호출 추적

```sql
-- Work1의 모든 실행 이력 (Cycle 1에서 2번 실행됨)
SELECT * FROM dspCallHistory
WHERE CallName = 'Work1' AND CycleNo = 1
ORDER BY StartAt;

-- Result:
-- StartAt              FinishAt             DurationMs
-- 2025-03-22T10:00:00  2025-03-22T10:00:01  1000.0
-- 2025-03-22T10:00:02  2025-03-22T10:00:03  1000.0

-- ✅ 2개 바 모두 표시 가능!
```

### Gap 분석 가능

```sql
WITH OrderedCalls AS (
    SELECT CallName, FinishAt,
           LEAD(StartAt) OVER (ORDER BY SequenceNo) AS NextStartAt
    FROM dspCallHistory h
    JOIN dspCall c ON h.CallId = c.Id
    WHERE FlowName = 'MainFlow' AND CycleNo = 3
)
SELECT CallName,
       (julianday(NextStartAt) - julianday(FinishAt)) * 86400000 AS GapMs
FROM OrderedCalls
WHERE NextStartAt IS NOT NULL;

-- Result:
-- CallName  GapMs
-- Work1     1000.0  ← 1초 대기
-- Work2     500.0   ← 0.5초 대기

-- ✅ Bottleneck 분석 가능!
```

---

## 📋 수정된 문서

1. **[13_CRITICAL_DESIGN_FIXES.md](./13_CRITICAL_DESIGN_FIXES.md)** (신규)
   - 두 결함의 상세 분석
   - 수정 방안 및 구현 코드
   - 검증 시나리오

2. **[09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md)** (수정)
   - Phase 1: dspFlow에서 WorkName, SequenceNo, IsHead, IsTail 제거
   - Phase 1: dspCallHistory, dspFlowCycleHistory 테이블 추가
   - Phase 1: dspCall에 WorkName, Prev, Next 추가

3. **[05_FEATURE_IMPLEMENTATION.md](./05_FEATURE_IMPLEMENTATION.md)** (수정)
   - Section 4: Gantt Chart 로직을 이력 기반으로 수정
   - calculateGanttLayout: dspCallHistory 사용
   - calculateGaps: Gap 분석 추가

---

## ✅ 검증 체크리스트

### 결함 1 수정 검증
- [ ] dspFlow 스키마에 Call 레벨 컬럼 없음
- [ ] dspCall 스키마에 WorkName, SequenceNo, IsHead, IsTail, Prev, Next 존재
- [ ] Bootstrap 시 Call.WorkName = work.Name 설정
- [ ] Flow Detail View에서 Work 그룹핑 정상 동작

### 결함 2 수정 검증
- [ ] dspCallHistory 테이블 생성
- [ ] dspFlowCycleHistory 테이블 생성
- [ ] InTag/OutTag Rising Edge 시 history INSERT/UPDATE
- [ ] Gantt Chart가 dspCallHistory에서 조회
- [ ] Cycle 1~10 모두 복원 가능
- [ ] 병렬/반복 호출 모두 추적
- [ ] Gap 계산 정확도 검증

---

## 🔄 마이그레이션 순서

```
1. Database Schema 수정
   ├─ dspFlow에서 WorkName, SequenceNo, IsHead, IsTail 제거
   ├─ dspCall에 WorkName, Prev, Next 추가
   ├─ dspCallHistory 테이블 생성
   └─ dspFlowCycleHistory 테이블 생성

2. F# Repository 확장
   ├─ insertCallHistory 구현
   ├─ updateCallHistory 구현
   └─ insertOrUpdateFlowCycleHistory 구현

3. StateTransition 수정
   ├─ handleInTagRisingEdge: history INSERT 추가
   └─ handleOutTagRisingEdge: history UPDATE 추가

4. GanttLayout 재구현
   ├─ calculateGanttLayout: dspCallHistory 기반
   ├─ calculateGaps: Gap 분석 추가
   └─ getCycleList: Cycle 목록 조회

5. UI 수정
   ├─ CycleTimeAnalysis.razor 업데이트
   └─ Cycle 선택 드롭다운 추가
```

---

## 📚 관련 문서

- **[13_CRITICAL_DESIGN_FIXES.md](./13_CRITICAL_DESIGN_FIXES.md)** - 상세 분석 및 구현
- **[09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md)** - 리팩토링 계획 (수정됨)
- **[05_FEATURE_IMPLEMENTATION.md](./05_FEATURE_IMPLEMENTATION.md)** - 기능 구현 (수정됨)
- **[02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md)** - 전체 스키마
- **[12_REPOSITORY_API.md](./12_REPOSITORY_API.md)** - Repository API
