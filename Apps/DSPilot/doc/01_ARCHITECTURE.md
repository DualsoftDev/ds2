# DSPilot.Engine 아키텍처

## 🎯 목적

DSPilot.Engine은 AASX 구조와 PLC 이벤트를 실시간으로 처리하여
**화면에 즉시 사용 가능한 Projection 테이블**을 유지합니다.

---

## 📐 전체 아키텍처

```
┌─────────────────────────────────────────────────────────────┐
│                        DSPilot 시스템                        │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  ┌──────────────┐       ┌──────────────┐                    │
│  │  AASX File   │       │  PLC Events  │                    │
│  │  (구조 정의)  │       │  (실시간)     │                    │
│  └──────┬───────┘       └──────┬───────┘                    │
│         │                      │                             │
│         ├──────────────────────┤                             │
│         │                      │                             │
│         ▼                      ▼                             │
│  ┌─────────────────────────────────────┐                    │
│  │     DSPilot.Engine (F#)             │                    │
│  ├─────────────────────────────────────┤                    │
│  │ 1. AASX Bootstrap                   │                    │
│  │    └─→ Static Metadata Projection   │                    │
│  │                                      │                    │
│  │ 2. PLC Event Processor               │                    │
│  │    └─→ Runtime State Update          │                    │
│  │                                      │                    │
│  │ 3. Statistics Calculator             │                    │
│  │    └─→ Average, StdDev, Min, Max     │                    │
│  │                                      │                    │
│  │ 4. Aggregator                        │                    │
│  │    └─→ Flow-level Metrics            │                    │
│  │                                      │                    │
│  │ 5. Projection Writer                 │                    │
│  │    └─→ dspFlow, dspCall UPDATE       │                    │
│  └──────────────┬──────────────────────┘                    │
│                 │                                             │
│                 ▼                                             │
│  ┌─────────────────────────────────────┐                    │
│  │     SQLite Database                  │                    │
│  ├─────────────────────────────────────┤                    │
│  │ • dspFlow   (Flow projection)       │                    │
│  │ • dspCall   (Call projection)       │                    │
│  │ • plcTagLog (Raw PLC log)           │                    │
│  └──────────────┬──────────────────────┘                    │
│                 │                                             │
│                 ▼                                             │
│  ┌─────────────────────────────────────┐                    │
│  │     DSPilot UI (Blazor, C#)         │                    │
│  ├─────────────────────────────────────┤                    │
│  │ • Dashboard                          │                    │
│  │ • Flow Detail (상세보기)             │                    │
│  │                                      │                    │
│  │ 규칙: Projection만 읽기 (계산 금지)  │                    │
│  └─────────────────────────────────────┘                    │
│                                                               │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔄 데이터 흐름

### 1. Static Bootstrap (프로젝트 로드 시)

```
AASX File
  ├─ System, Flow, Work, Call 구조 읽기
  │
  ├─ Topology 계산 (Prev, Next, IsHead, IsTail, SequenceNo)
  │
  ├─ Tag Mapping 조회 (InTag, OutTag)
  │
  ├─ Device 매핑
  │
  └─→ dspFlow, dspCall UPSERT
       (기존 통계는 유지 - COALESCE 사용)
```

### 2. Runtime Event (PLC 이벤트 수신 시)

```
PLC Tag Value Change
  │
  ├─ Rising Edge 감지
  │   (PreviousValue: false → Value: true)
  │
  ├─ Tag → Call 매핑
  │   (PlcToCallMapperService)
  │
  ├─ InTag Rising Edge?
  │   ├─ YES → State: Ready → Going
  │   │         LastStartAt 기록
  │   │         ActiveCallCount += 1
  │   │         (Head Call이면 LastCycleStartAt 기록)
  │   │
  │   └─ OutTag Rising Edge?
  │       └─ YES → State: Going → Ready
  │                 LastFinishAt 기록
  │                 Duration 계산
  │                 Statistics 업데이트
  │                 ActiveCallCount -= 1
  │                 (Tail Call이면 Cycle End 처리)
  │
  └─→ dspFlow, dspCall UPDATE
```

### 3. Aggregation (집계 계산)

```
Call 완료 이벤트
  │
  ├─ Call-level Statistics
  │   ├─ Average = (old_avg * (n-1) + new_value) / n
  │   ├─ StdDev = √(variance)
  │   ├─ Min = MIN(existing, new_value)
  │   ├─ Max = MAX(existing, new_value)
  │   └─ SlowFlag = (LastDuration > Avg + 2*StdDev)
  │
  ├─ Call-level Focus Score
  │   ├─ Error: +100
  │   ├─ Unmapped: +70
  │   ├─ Slow: +50
  │   └─ High StdDev: +30
  │
  ├─ Flow-level Aggregation
  │   ├─ ActiveCallCount = COUNT(State='Going')
  │   ├─ ErrorCallCount = COUNT(State='Error')
  │   ├─ State = Error 있으면 'Error'
  │   │         Going 있으면 'Going'
  │   │         아니면 'Ready'
  │   │
  │   └─ (Tail Call 완료 시)
  │       ├─ MT = SUM(LastDurationMs)
  │       ├─ CT = LastCycleEndAt - LastCycleStartAt
  │       └─ WT = CT - MT
  │
  └─→ dspFlow, dspCall UPDATE
```

---

## 🧱 계층 구조

### Layer 1: Data Source
- **AASX File**: 정적 구조 (System, Flow, Work, Call)
- **PLC Events**: 동적 상태 (Tag Value Changes)

### Layer 2: DSPilot.Engine (F#)

#### 2.1 Bootstrap Module
- AASX 파싱
- Topology 계산
- Tag Mapping
- 초기 Projection 생성

#### 2.2 Runtime Module
- PLC Event 수신
- Rising Edge 감지
- State Transition
- Timestamp 기록

#### 2.3 Statistics Module
- Incremental Statistics (Average, StdDev)
- Min/Max Tracking
- SlowFlag 판정

#### 2.4 Aggregation Module
- Flow State 집계
- ActiveCallCount, ErrorCallCount
- Cycle Metrics (MT, WT, CT)

#### 2.5 Repository Module
- Projection UPSERT
- Projection UPDATE (Patch 방식)
- Query (GetFlowByName, GetCallsByFlow)

### Layer 3: Database
- **dspFlow**: Flow Projection
- **dspCall**: Call Projection
- **plcTagLog**: Raw PLC Log (디버그/재검증용)

### Layer 4: UI Layer (C#, Blazor)
- **규칙**: dspFlow, dspCall만 읽기
- **금지**: Mapper 호출, plcTagLog 스캔, 통계 재계산

---

## 📦 모듈 책임

### DSPilot.Engine/Bootstrap
```
책임:
- AASX 로드 및 파싱
- Flow/Call 구조 분석
- Topology 계산
- Tag Mapping 초기화

산출물:
- dspFlow (정적 메타)
- dspCall (정적 메타)

트리거:
- 프로젝트 로드 시
- 프로젝트 변경 시
```

### DSPilot.Engine/Runtime
```
책임:
- PLC 이벤트 수신
- Rising Edge 감지
- State 전이
- Timestamp 기록

산출물:
- dspCall.State
- dspCall.LastStartAt
- dspCall.LastFinishAt
- dspFlow.ActiveCallCount

트리거:
- PLC Tag 값 변경 시
```

### DSPilot.Engine/Statistics
```
책임:
- Incremental 통계 계산
- SlowFlag 판정
- Focus Score 계산

산출물:
- dspCall.AverageGoingTime
- dspCall.StdDevGoingTime
- dspCall.MinGoingTime
- dspCall.MaxGoingTime
- dspCall.SlowFlag
- dspCall.FocusScore

트리거:
- Call 완료 시
```

### DSPilot.Engine/Aggregation
```
책임:
- Flow 레벨 집계
- Cycle Metrics 계산

산출물:
- dspFlow.State
- dspFlow.ActiveCallCount
- dspFlow.ErrorCallCount
- dspFlow.MT, WT, CT
- dspFlow.LastCycleDurationMs

트리거:
- Call 상태 변경 시
- Tail Call 완료 시
```

### DSPilot.Engine/Repository
```
책임:
- Database I/O
- Projection CRUD
- Query 최적화

API:
- upsertFlow, upsertCall
- updateFlowProjection (Patch)
- updateCallProjection (Patch)
- getFlowByName
- getCallsByFlow
- countCallsByState

트리거:
- 모든 모듈의 요청
```

---

## 🔒 불변 규칙

### 1. UI는 계산 금지
```csharp
// ❌ 금지
var average = calls.Average(c => c.Duration);

// ✅ 허용
var average = call.AverageGoingTime;
```

### 2. Projection은 완전해야 함
```
모든 화면 필요 값은 Projection에 사전 계산되어 있어야 함.
UI는 단순 표시만 담당.
```

### 3. DROP TABLE 금지
```sql
-- ❌ 금지
DROP TABLE IF EXISTS dspFlow;
CREATE TABLE dspFlow (...);

-- ✅ 허용
ALTER TABLE dspFlow ADD COLUMN NewField TEXT;
```

### 4. WorkName 정확도
```fsharp
// ❌ 잘못된 코드
{ WorkName = flow.Name }

// ✅ 올바른 코드
{ WorkName = work.Name }
```

---

## 🎯 성능 목표

### Latency
- PLC Event → DB Update: < 50ms
- DB Update → UI 반영: < 100ms (polling 기준)

### Throughput
- PLC Event 처리: > 100 events/sec
- Projection Update: > 50 updates/sec

### Scalability
- Flow: 최대 100개
- Call per Flow: 최대 200개
- Total Calls: 최대 10,000개

---

## 🔄 확장성

### 향후 추가 가능 기능

1. **실시간 알림**
   - SlowFlag 발생 시 알림
   - Error 발생 시 알림

2. **이력 추적**
   - dspCallHistory 테이블
   - 시간별 통계 추이

3. **예측 분석**
   - 다음 사이클 시간 예측
   - 이상 패턴 감지

4. **다중 Flow 비교**
   - Flow 간 성능 비교
   - Benchmark 기능

---

## 📚 참조 문서

- [02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md) - 테이블 스키마
- [04_EVENT_PIPELINE.md](./04_EVENT_PIPELINE.md) - 이벤트 처리
- [11_FSHARP_MODULES.md](./11_FSHARP_MODULES.md) - F# 모듈 구조
