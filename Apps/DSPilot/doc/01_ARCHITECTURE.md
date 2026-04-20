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

### Layer 2: DSPilot Services (C#) + Engine (F#)

#### 2.1 프로젝트 관리 (C#)
- **DsProjectService**: AASX 로드, System/Flow/Work/Call 구조 관리
- **BlueprintService**: Layout 및 렌더링 데이터
- **DspDbService**: DSP 데이터베이스 스냅샷 관리

#### 2.2 PLC 이벤트 처리 (C# + F#)
- **Ev2PlcEventSource** (C#): 실제 PLC 연결 및 이벤트 스트림
- **PlcEventProcessorService** (C#): Channel 기반 이벤트 처리
  - TagStateTracker (F#): Edge 감지
  - PlcToCallMapperService (C#): Tag → Call 매핑
  - StateTransition (F#): 상태 전이 로직
- **PlcDatabaseMonitorService** (C#): DB 변경사항 폴링 및 브로드캐스트

#### 2.3 분석 및 통계 (C#)
- **CycleAnalysisService**: 사이클 경계 탐지, Gap/병목 분석
- **CallStatisticsService**: Call 통계 계산
- **FlowMetricsService**: Flow 레벨 메트릭
- **HeatmapService**: 편차 분석

#### 2.4 실시간 모니터링 (C#)
- **MonitoringBroadcastService**: SignalR를 통한 실시간 상태 브로드캐스트
- **CallStateNotificationService**: Call 상태 변경 알림
- **InMemoryCallStateStore**: 메모리 내 Call 상태 캐시

#### 2.5 Repository (F#)
- **DspRepository**: Dapper 기반 SQLite I/O
  - bulkInsertFlowsAsync, bulkInsertCallsAsync
  - getFlowByNameAsync, getCallsByFlowAsync
  - updateCallStateAsync
- **DatabasePathResolverAdapter** (C#): Unified/Split 모드 경로 해석

### Layer 3: Database
- **dspFlow**: Flow Projection
- **dspCall**: Call Projection
- **plcTagLog**: Raw PLC Log (디버그/재검증용)

### Layer 4: UI Layer (C#, Blazor)
- **규칙**: dspFlow, dspCall만 읽기
- **금지**: Mapper 호출, plcTagLog 스캔, 통계 재계산

---

## 📦 모듈 책임

### C# Services Layer (DSPilot/Services)

#### Ev2BootstrapServiceAdapter (F# Wrapper)
```
책임:
- EV2 데이터베이스 스키마 초기화
- DSP 테이블 생성 위임 (dspFlow, dspCall)
- 통합 데이터베이스 경로 관리

트리거:
- 애플리케이션 시작 시 (HostedService)
```

#### DsProjectService
```
책임:
- AASX 프로젝트 로드 및 파싱
- System, Flow, Work, Call 구조 관리
- DsStore 상태 관리

API:
- LoadProject, GetActiveSystems
- GetFlows, GetWorks, GetCalls
- GetStore (F# DsStore 접근)

트리거:
- 사용자 프로젝트 로드 요청
```

#### PlcEventProcessorService
```
책임:
- PLC 이벤트 스트림 구독 (Channel 기반)
- Edge 감지 (TagStateTracker 활용)
- Tag → Call 매핑 (PlcToCallMapperService)
- F# StateTransition 호출

데이터 흐름:
IPlcEventSource → Channel → EdgeDetection → Mapping → StateTransition

트리거:
- PLC 연결 활성화 시 (PlcConnection:Enabled=true)
```

#### PlcToCallMapperService
```
책임:
- AASX Call 메타데이터에서 Tag 매핑 추출
- Tag Address → Call 조회
- InTag/OutTag 식별

API:
- Initialize (프로젝트 로드 시 매핑 빌드)
- FindCallByTag (Tag Address → CallMappingInfo)
- GetCallTagsByCallId (Call ID → (InTag, OutTag))

트리거:
- 프로젝트 로드 시 (Initialize)
- PLC 이벤트 처리 시 (FindCallByTag)
```

#### CycleAnalysisService
```
책임:
- 사이클 경계 자동 탐지 (Head Call InTag 기반)
- Call 실행 시퀀스 분석
- Gap 분석 및 병목 탐지
- Gantt 차트 데이터 생성

API:
- DetectRecentCyclesAsync (최근 N개 사이클 탐지)
- AnalyzeCycleAsync (사이클 상세 분석)
- GetIOEventsInTimeRangeAsync (Gantt 차트용 IO 이벤트)

트리거:
- 사용자 사이클 분석 요청 (CycleAnalysis.razor)
```

### F# Engine Layer (DSPilot.Engine)

#### Database/Repository
```
책임:
- SQLite I/O (Dapper 기반)
- Projection CRUD (dspFlow, dspCall)
- Unified 모드 지원 (공유 데이터베이스)

API:
- createSchemaAsync (스키마 생성)
- bulkInsertFlowsAsync, bulkInsertCallsAsync
- getFlowByNameAsync, getCallsByFlowAsync
- updateCallStateAsync (상태 업데이트)

트리거:
- C# Services의 데이터베이스 작업 요청
```

#### Tracking/StateTransition
```
책임:
- PLC Edge 이벤트 → Call 상태 전이
- Call State: Ready → Going (InTag) / Going → Done (OutTag)
- Timestamp 기록 (LastStartAt, LastFinishAt)
- Duration 계산

API:
- processEdgeEvent (F# Async 함수)
  - dbPath: string
  - tagAddress: string
  - isInTag: bool
  - edgeType: EdgeType
  - timestamp: DateTime
  - callName: string

트리거:
- PlcEventProcessorService의 Edge 이벤트
```

#### Tracking/TagStateTracker
```
책임:
- PLC Tag 이전 상태 추적
- Rising/Falling Edge 감지

구현:
- updateTagValue (Tag Address, Value) → EdgeState
- EdgeType: NoChange | RisingEdge | FallingEdge

트리거:
- PLC 이벤트 처리 시 (PlcEventProcessorService)
```

#### Statistics/RuntimeStatsCollector (미래 구현)
```
책임:
- Incremental 통계 계산 (Welford's Algorithm)
- SlowFlag 판정
- Focus Score 계산

산출물:
- AverageGoingTime, StdDevGoingTime
- MinGoingTime, MaxGoingTime
- SlowFlag

트리거:
- Call 완료 시 (OutTag Rising Edge)
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
