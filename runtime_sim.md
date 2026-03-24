# Ev2.Runtime.Sim 분석 문서

> ds2 포팅용. "자동 원위치(시뮬 정지 후 origin 복원)" 제거 대상은 ⛔ 표시.
> 시뮬 중 자동 리셋(PredecessorResetGuids 기반 F→R)은 정상 동작이므로 유지.

---

## 1. 프로젝트 구조

```
Ev2.Runtime.Sim/
├── Model/
│   ├── SimScenario.fs         시나리오 정의 (SimWork, SimCall, SimApiDef, SimApiCall, SimConditionSpec)
│   ├── SimState.fs            런타임 상태 (NodeState R/G/F/H, SimState, 이벤트 인자) — H는 미구현 예약 상태
│   ├── SimulationError.fs     에러 DU
│   ├── SimulationNodeInfo.fs  UI용 노드 정보 + StateCache (스레드 안전)
│   ├── NodeMatching.fs        상태 파싱 유틸
│   ├── Ids.fs                 강타입 ID (WorkId, CallId 등)
│   └── Validation/
│       └── ScenarioValidator.fs  시나리오 검증
│
├── Graph/
│   └── SimGraph.fs            DAG (순환 감지, 위상 정렬)
│
├── Engine/
│   ├── ISimulationEngine.fs   공개 인터페이스
│   ├── EventDrivenEngine.fs   메인 엔진 (659줄) ★ 핵심
│   ├── Core/
│   │   ├── WorkConditionChecker.fs  조건 평가 ★ 핵심
│   │   ├── StateManager.fs          상태 관리 ★ 핵심
│   │   ├── EventPublisher.fs        IO 값 발행
│   │   ├── TransitionLogic.fs       순수 상태 전이 계산
│   │   ├── MetricsCalculator.fs     MT/WT/진행률
│   │   ├── ValueSpecEvaluator.fs    ValueSpec JSON 평가
│   │   └── ScenarioIndexer.fs       역인덱스
│   └── Scheduler/
│       ├── EventScheduler.fs        PriorityQueue 스케줄러
│       └── ScheduledEvent.fs        이벤트 타입 DU
│
├── Events/
│   ├── SimulationEvents.fs          이벤트 DU
│   ├── SimulationEventBus.fs        동기 이벤트 버스
│   ├── AsyncSimulationEventBus.fs   비동기 배치 (DB/OpcUa용)
│   └── SignalIOCalculator.fs        Signal 매핑 구축
│
└── Aasx/
    ├── AasxLoader.fs                AASX → SimScenario
    ├── AasxGenerator.fs             Project → SimScenario (오케스트레이터)
    └── Converters/
        ├── WorkConverter.fs         Work/Call 변환
        ├── ApiConverter.fs          ApiDef/ApiCall 변환 + Composite GUID
        ├── ArrowProcessor.fs        Arrow → Predecessor 수집
        ├── ConditionConverter.fs    Call 조건 추출
        ├── AliasResolver.fs         Alias Work 해소
        └── ScenarioAssembler.fs     최종 조립
```

---

## 2. 상태 모델 (NodeState)

```
┌─────┐      Start      ┌─────┐     Duration Done   ┌──────┐
│  R  │ ──────────────→ │  G  │ ──────────────────→ │  F   │
│Ready│                 │Going│   Or all of Call F  │Finish│
└─────┘                 └─────┘                     └──────┘
   ↑                                                   │
   │              자동 리셋 (Reset)                     │
   └───────────────────────────────────────────────────┘
                  predecessor G 상태 → 리셋 (정상 시뮬 동작, 유지)

┌─────┐
│  H  │  Homing (미구현 — 기획상 F→H→R 2단계 복귀 중간 상태이나, 현재 ev2는 F→R 직행)
└─────┘
  기획: Work1 --|StartReset|--> Work2 일 때
    Work1 F → Work2 G → Work1 F→H(복귀중) → Work2 F → Work1 H→R
  실제: F→R 직행 (H 안 거침). ds2에서 구현 여부 판단 필요
```

**Call 특수 경로 — Skip:**
```
┌─────┐  ActiveTrigger 불만족   ┌─────┐
│R    │ ─────────────────────→ │F    │   (G를 거치지 않고 바로 F, 취소선 표시)
└─────┘                        └─────┘
```

---

## 3. 데이터 모델

### SimWork
```
Guid                    고유 ID
Name                    이름
Duration                실행 시간 (ms). G→F 전이 소요 시간
PredecessorStartGuids   시작 화살표 대상 (이들이 F여야 Start 가능)
PredecessorResetGuids   리셋 화살표 대상 (이들이 G면 F→R 리셋) — 시뮬 중 정상 동작
CallGuids               포함된 Call 리스트
SystemName              소속 System
FlowName                소속 Flow
IsAlias                 Alias Work 여부
```

### SimCall
```
Guid                    고유 ID
WorkGuid                부모 Work
Name                    이름
PredecessorStartGuids   선행 Call (이들이 F여야 Start 가능)
ApiCallGuids            연결된 ApiCall 리스트
AutoConditionSpecs      자동 실행 조건 (불만족 → R 유지)
CommonConditionSpecs    안전 조건 (불만족 → R 유지)
ActiveTriggerSpecs      트리거 조건 (불만족 → Skip R→F)
```

### SimConditionSpec
```
RxWorkGuid              확인할 RxWork (필수)
ApiCallGuid             값 비교용 ApiCall (선택)
ValueSpec               JSON (IValueSpec) 또는 "true"/"false" (선택)
ValueType               Boolean/Int32/Double 등 (선택)
```

### SimApiDef / SimApiCall
```
SimApiDef:
  Guid, Name, TxWorkGuid?, RxWorkGuid?, SystemName
  (두 System/Work 간 통신 계약)

SimApiCall:
  Guid, Name, ApiDefGuid
  InValueSpec, InTagName, InTagType, InTagAddress   (RxWork I값)
  OutValueSpec, OutTagName, OutTagType, OutTagAddress (TxWork O값)
```

### SimState (런타임)
```
WorkStates      Map<Guid, NodeState>   Work별 R/G/F/H
CallStates      Map<Guid, NodeState>   Call별 R/G/F
WorkProgress    Map<Guid, float>       0.0~1.0
IOValues        Map<Guid, string>      ApiCallGuid → "true"/"false"/커스텀
SkippedCalls    Set<Guid>              Skip된 Call
Clock           TimeSpan               논리시계
CompletedNodes  Set<Guid>              F 도달 이력
```

### SimScenario
```
Works           Map<Guid, SimWork>
Calls           Map<Guid, SimCall>
ApiDefs         Map<Guid, SimApiDef>
ApiCalls        Map<Guid, SimApiCall>
TickMs          틱 간격 (ms)
SpeedMultiplier 배속 (0.1~1000)
TimeIgnore      true면 Duration 무시
ActiveSystemNames   Active System 집합
PassiveSystemNames  Passive System 집합
```

---

## 4. 엔진 전체 흐름 (플로우차트)

### 4.1 메인 루프 (simulationLoop)

```
Start() 호출
  │
  ▼
┌────────────────────────────────────────────────────┐
│ 백그라운드 스레드 시작                                │
│                                                    │
│  while not cancelled:                              │
│    │                                               │
│    ├─ Paused? → Sleep(10ms), 시간기준 갱신, continue │
│    │                                               │
│    ├─ Running:                                     │
│    │   1. 실시간 델타 계산 (Stopwatch)               │
│    │   2. simDelta = realDelta × speedMultiplier   │
│    │   3. targetTime = currentTime + simDelta      │
│    │   4. events = scheduler.AdvanceTo(targetTime) │
│    │   5. for each event → processEvent(event)     │
│    │   6. 100ms마다 진행률/tc 발행                   │
│    │   7. Sleep(1ms)                               │
│    │                                               │
│    └─ 반복                                         │
└────────────────────────────────────────────────────┘
```

### 4.2 processEvent

```
processEvent(event)
  │
  ├─ WorkTransition(workGuid, targetState)
  │   → ClearPending(Work, workGuid)
  │   → applyTransition(Work, workGuid, targetState)
  │
  ├─ CallTransition(callGuid, targetState)
  │   → ClearPending(Call, callGuid)
  │   → applyTransition(Call, callGuid, targetState)
  │
  ├─ DurationComplete(workGuid)
  │   → Work가 G면 → applyTransition(Work, workGuid, F)
  │
  └─ EvaluateConditions
      → evaluateConditions()
```

### 4.3 applyTransition (★ 핵심 — 후속 이벤트 결정)

```
applyTransition(nodeType, nodeGuid, newState)
  │
  ▼
StateManager.ApplyTransition()
  │ (Call R→G 시 shouldSkipCall → F로 리다이렉트)
  │
  ├─ 상태 안 바뀜 → return
  │
  ▼ 상태 바뀜
MetricsCalculator.OnTransition() → MT/WT 계산
EventPublisher → StateChanged 이벤트 발행
  │
  ├──────────────────────────────────────────────────────┐
  │ nodeType에 따른 후속 처리                              │
  │                                                      │
  │ ■ Work R→G:                                          │
  │   ├─ timeIgnore? → 즉시 DurationComplete 스케줄        │
  │   └─ else → Duration(ms)/speedMultiplier 후 스케줄    │
  │   └─ EvaluateConditions 스케줄                        │
  │                                                      │
  │ ■ Work G→F:                                          │
  │   └─ EvaluateConditions 스케줄                        │
  │                                                      │
  │ ■ Work ?→R (리셋 후속):                               │
  │   ├─ 내부 Call 전부 R로 리셋 스케줄                     │
  │   ├─ RxWork I값 "false" 발행 (PublishRxWorkInputReset)│
  │   └─ EvaluateConditions 스케줄                        │
  │                                                      │
  │ ■ Call R→G (또는 R→F Skip):                           │
  │   ├─ Skip이면 → TxWork O값 발행 안 함                  │
  │   └─ 정상이면:                                        │
  │       ├─ TxWork O값 "true" 발행                       │
  │       └─ TxWork G 전이 스케줄                          │
  │                                                      │
  │ ■ Call G→F:                                          │
  │   ├─ RxWork I값 "true" 발행 + IOValues 저장            │
  │   ├─ RxWork F 전이 스케줄                              │
  │   ├─ TxWork O값 "false" 리셋                          │
  │   └─ EvaluateConditions 스케줄                        │
  └──────────────────────────────────────────────────────┘
```

### 4.4 evaluateConditions (★ 핵심 — 전이 트리거 결정)

```
evaluateConditions()
  │
  │ ── 1. Work 시작 가능? ──────────────────────────────
  │   for each Work (state = R, not pending):
  │     canStartWork? → WorkTransition(workGuid, G) 스케줄
  │
  │ ── 2. Work 리셋 가능? ──────────────────────────────
  │   for each Work (state = F, not pending):
  │     canResetWork? && !IsResetTriggered?
  │       → WorkTransition(workGuid, R) 스케줄
  │       → AddResetTrigger (라이징 에지 1회만)
  │
  │ ── 3. Call 시작 가능? ──────────────────────────────
  │   for each Call (state = R, parentWork = G, not pending):
  │     canStartCall?
  │       → shouldSkipCall?
  │         → Yes: CallTransition(callGuid, F) 스케줄 (Skip)
  │         → No:  CallTransition(callGuid, G) 스케줄
  │
  │ ── 4. Call 완료 가능? ──────────────────────────────
  │   for each Call (state = G, not pending):
  │     canCompleteCall? (모든 ApiCall의 RxWork = F)
  │       → CallTransition(callGuid, F) 스케줄
  │
  │ ── 5. Work 완료 가능? ──────────────────────────────
  │   for each Work (state = G, has calls, not pending):
  │     모든 Call = F?
  │       → WorkTransition(workGuid, F) 스케줄
  │
  └─ 끝
```

---

## 5. 조건 평가 상세 (WorkConditionChecker)

### 5.1 canStartWork — Work 시작 조건

```
canStartWork(scenario, state, workGuid):
  │
  ├─ PredecessorStartGuids 비어있음 → false (수동 시작만 가능)
  │
  ├─ predecessor Work들의 (SystemName, Name) 수집 (중복 제거)
  │
  └─ 평가:
       같은 (SystemName, Name) 내 → OR (하나라도 F면 OK)
       다른 (SystemName, Name) 간 → AND (모두 만족해야 함)

예시: W1(S1.A), W2(S1.A), W3(S2.B) → W4
  W4 시작 조건:
    (S1.A 중 하나라도 F) AND (S2.B 중 하나라도 F)
```

### 5.2 canResetWork — Work 리셋 조건 (시뮬 중 자동 리셋, 유지)

```
canResetWork(scenario, state, workGuid):
  │
  ├─ 같은 SystemName+Name의 모든 인스턴스의 PredecessorResetGuids 병합
  │
  ├─ 비어있음 → false
  │
  └─ 평가: OR (하나라도 G면 리셋)
       + 라이징 에지: 같은 (predKey, targetKey) 조합은 1회만 트리거
```

### 5.3 checkConditionSpec — 조건 스펙 평가

```
checkConditionSpec(scenario, state, spec):
  │
  ├─ ValueSpec = "false"
  │   → RxWork R 상태인지 확인 (리셋 확인)
  │
  └─ ValueSpec = "true" 또는 없음
      ├─ RxWork F 상태인지 확인
      └─ ApiCallGuid 있으면:
          └─ IOValues에서 현재값 조회 → ValueSpecEvaluator로 비교
```

### 5.4 canStartCall — Call 시작 조건

```
canStartCall(scenario, state, callGuid):
  │
  ├─ 기본 조건:
  │   Parent Work = G
  │   AND 모든 Predecessor Call = F
  │
  ├─ shouldSkipCall?
  │   → Yes: 조건 무시, 시작 허용 (R→F 리다이렉트됨)
  │
  └─ No:
      AutoConditionSpecs 모두 만족 (AND)
      AND CommonConditionSpecs 모두 만족 (AND)
```

### 5.5 shouldSkipCall — Call Skip 판정

```
shouldSkipCall(scenario, state, callGuid):
  │
  ├─ ActiveTriggerSpecs 비어있음 → false (스킵 안 함)
  │
  └─ 조건이 있으면서 하나라도 불만족 → true (스킵)
     (ALL must pass; any fail → skip)
```

### 5.6 canCompleteCall — Call 완료 조건

```
canCompleteCall(scenario, state, callGuid):
  │
  ├─ ApiCallGuids 비어있음 → true (즉시 완료)
  │
  └─ 모든 ApiCall의 ApiDef.RxWorkGuid가 F → true
```

---

## 6. IO 값 발행 흐름 (EventPublisher)

### Signal 이름 규칙
```
Signal = "{SystemName}_{ApiDefName}"
Composite Key = (CallGuid, Signal)  ← Call별로 다른 ValueSpec 가능
```

### 6.1 정상 흐름

```
Call R→G (정상 시작):
  │
  ├─ TxWork O값 발행
  │   signal="S1_ApiDef1", type="O", value=OutValueSpec 또는 "true"
  │
  └─ TxWork G 전이 스케줄
       │
       ▼
  TxWork G→F (Duration 완료):
       │
       ▼
  Call G→F:
  │
  ├─ RxWork I값 발행
  │   signal=..., type="I", value=InValueSpec 또는 "true"
  │   + StateManager.IOValues에 저장 (조건 평가용)
  │
  ├─ TxWork O값 리셋
  │   bool이면 "false", 아니면 유지
  │
  └─ RxWork F 전이 스케줄
```

### 6.2 리셋 흐름 (시뮬 중 자동 리셋, 유지)

```
Work F→R (predecessor G 기반 자동 리셋):
  │
  ├─ RxWork I값 리셋
  │   bool이면 "false", 비bool이면 유지
  │   + IOValues 삭제
  │
  └─ 내부 Call 전부 R로 리셋
```

### 6.3 Skip 흐름

```
Call R→F (Skip):
  │
  ├─ TxWork O값 발행 안 함
  ├─ RxWork I값 발행 안 함
  └─ SkippedCalls에 추가 (취소선 표시)
```

---

## 7. 스케줄러 (EventScheduler)

### 이벤트 타입
```fsharp
type ScheduledEventType =
    | WorkTransition of workGuid: Guid * targetState: NodeState
    | CallTransition of callGuid: Guid * targetState: NodeState
    | DurationComplete of workGuid: Guid
    | EvaluateConditions
```

### 우선순위 (같은 시간일 때)
```
PriorityStateChange    = 0   ← 상태 전이 먼저
PriorityDurationCheck  = 10  ← Duration 완료
PriorityConditionEval  = 20  ← 조건 평가 마지막
```

### 동작
```
ScheduleAfter(eventType, delayMs, priority)
  → PriorityQueue에 (currentTime + delay, priority) 기준으로 삽입

AdvanceTo(targetTimeMs)
  → targetTime 이하의 모든 이벤트를 시간순으로 추출/반환
  → 취소된 이벤트는 무시 (pendingEvents HashSet)
```

### 중복 방지
```
StateManager.MarkPending(nodeType, guid) → 스케줄 시 등록
StateManager.IsPending(nodeType, guid)   → evaluateConditions에서 확인
StateManager.ClearPending(nodeType, guid) → processEvent에서 해제
```

---

## 8. StateManager 상세

### 상태 변수
```
state                    SimState (WorkStates, CallStates, IOValues, Clock, ...)
pendingCallTransitions   Set<Guid>  스케줄 중인 Call (중복 방지)
pendingWorkTransitions   Set<Guid>  스케줄 중인 Work (중복 방지)
workGTriggeredResets    Set<(predKey, targetKey)>  라이징 에지 추적 (시뮬 중 자동 리셋용, 유지)
skippedNodes             Set<Guid>  Hot Reload용 Skip 상태
```

### ApplyTransition 흐름
```
ApplyTransition(nodeType, guid, newState, shouldSkipCall):
  │
  ├─ Call R→G && shouldSkipCall → actualState = F, isSkipped = true
  │
  ├─ 상태 안 바뀜 → HasChanged = false
  │
  └─ 상태 바뀜:
      ├─ state ← setState(...)
      ├─ Work가 G→다른상태: 라이징 에지 기록 삭제 (predKey 기반)
      └─ TransitionResult 반환
```

---

## 9. Signal 매핑 구축 (SignalIOCalculator)

### buildSignalMappings 반환값 (5-tuple)
```
1. SignalInfo list              모든 Signal 정보
2. Map<Guid, string list>       TxWorkGuid → [Signal]
3. Map<Guid, string list>       RxWorkGuid → [Signal]
4. Map<(Guid*string), ValueSpec> (CallGuid, Signal) → {OutValueSpec, InValueSpec}
5. Map<string, Guid list>       Signal → [CallGuid] (역매핑)
```

### 매핑 과정
```
for each Call:
  for each ApiCallGuid:
    ApiCall → ApiDef 조회
    Signal = "{ApiDef.SystemName}_{ApiDef.Name}"

    TxWorkGuid → TxWorkToSignals에 추가
    RxWorkGuid → RxWorkToSignals에 추가
    (CallGuid, Signal) → ValueSpec 저장
    Signal → CallGuid 역매핑
```

### Composite GUID
```
ApiConverter에서 Call별 ApiCall 복제 시:
  compositeGuid = MD5(callGuid + apiCallGuid)

같은 ApiCall을 여러 Call에서 참조할 때 각각 고유 ID 부여
```

---

## 10. 시나리오 변환 파이프라인 (Aasx/)

```
Project (ds2 도메인 모델)
  │
  ▼
AasxGenerator.convertProjectToScenario:
  │
  ├─ 1. System 이름 수집 (Active/Passive)
  │
  ├─ 2. WorkConverter: Work/Call → SimWork/SimCall
  │     (Duration, SystemName, FlowName 추출)
  │
  ├─ 3. ApiConverter: ApiDef/ApiCall → SimApiDef/SimApiCall
  │     + ValueSpec 매핑 구축
  │     + Call별 ApiCall 복제 (Composite GUID)
  │
  ├─ 4. ArrowProcessor: Arrow → Predecessor 수집
  │     ├─ Start 화살표 → PredecessorStartGuids
  │     ├─ Reset 화살표 → PredecessorResetGuids
  │     ├─ StartReset → Start + Reset
  │     └─ ResetReset → 양방향 Reset
  │     └─ Group Arrow → Union-Find로 분해
  │
  ├─ 5. ScenarioAssembler: Work에 predecessor 적용
  │
  ├─ 6. ScenarioAssembler: Call에 predecessor + ApiCallGuids 적용
  │
  ├─ 7. ConditionConverter: Call에 Auto/Common/ActiveTrigger 조건 적용
  │
  ├─ 8. AliasResolver: Alias Work의 Call 복제
  │
  └─ 9. 최종 SimScenario 조립
```

---

## 11. 역인덱스 (ScenarioIndexer)

```
SuccessorIndex        workGuid → 후속 Work들 (Start 화살표 역추적)
ResetTargetIndex      workGuid → Reset 대상 Work들 (시뮬 중 자동 리셋용, 유지)
RxWorkToCallsIndex    rxWorkGuid → 참조하는 Call들
FlowFirstCallIndex    flowName → 첫 Call
FlowLastCallIndex     flowName → 마지막 Call
CallToFlowIndex       callGuid → Flow 이름
WorkFirstCallIndex    workGuid → 첫 Call (DAG Root)
WorkLastCallIndex     workGuid → 마지막 Call (DAG Leaf)
CallToWorkIndex       callGuid → Work Guid
```

---

## 12. 리포트 시스템 (Ev2.Runtime.Sim.Report)

### 모델
```
StateSegment:     State(R/G/F/H), StartTime, EndTime?, DurationSeconds
ReportEntry:      Id, Name, Type(Work/Call), SystemId, Segments[], Duration, FlowName
ReportMetadata:   StartTime, EndTime, TotalDuration, WorkCount, CallCount
SimulationReport: Metadata + Entries[]
```

### 내보내기 형식
```
CSV 상세:     세그먼트별 행 (State, Start, End, Duration)
CSV 요약:     Work/Call별 (TotalGoingTime, StateChanges)
HTML:         다크테마 인터랙티브 간트차트 + SVG 화살표 + JS 줌
Excel:        4시트 — Gantt(시간축) + CAPA(UPH/병목/Gap%) + Summary + Detail
```

### C# 연결 (SimulationExporter.cs)
```
GanttChartViewModel → ToSimulationReport() → F# SimulationReport
  → ReportServiceClass.Export(report, options) → 파일 저장
```

---

## 13. 포팅 시 제거/유지 판정

### 유지 항목 (시뮬 중 자동 리셋 — 정상 동작)

| # | 파일 | 항목 | 유지 이유 |
|---|------|------|----------|
| 1 | `SimScenario.fs` | `SimWork.PredecessorResetGuids` | ResetReset 화살표 기반 시뮬 중 자동 F→R |
| 2 | `WorkConditionChecker.fs` | `canResetWork` 함수 | predecessor G면 리셋 판정 |
| 3 | `EventDrivenEngine.fs` | `evaluateConditions` 내 리셋 블록 | 시뮬 중 리셋 트리거 |
| 4 | `EventDrivenEngine.fs` | `applyTransition` 내 Work→R 후속 | Call 리셋 + I값 리셋 |
| 5 | `StateManager.fs` | `workGTriggeredResets` | 라이징 에지 1회 트리거 보장 |
| 6 | `StateManager.fs` | `ApplyTransition` 내 라이징 에지 삭제 | G→다른상태 시 기록 정리 |
| 7 | `ScenarioIndexer.fs` | `ResetTargetIndex` | 리셋 대상 역인덱스 |
| 8 | `ArrowProcessor.fs` | `Reset`, `StartReset`, `ResetReset` 분기 | 화살표 종류별 predecessor 수집 |
| 9 | `ScenarioAssembler.fs` | `resetPreds` 파라미터 | Work에 reset predecessor 적용 |
| 10 | `EventPublisher.fs` | `PublishRxWorkInputResetForWork` | Work→R 시 I값 "false" 발행 |
| 11 | `ISimulationEngine.fs` | `Reset()`, `ForceWorkState()` | 수동 리셋 (UI Work 리셋 버튼) |
| 12 | `SimState.fs` | `NodeState.H` | 미구현 — 기획상 F→H→R 2단계 복귀 중간 상태. ev2는 F→R 직행. ds2에서 구현 여부 판단 필요 |

### ⛔ 제거 항목

| # | 항목 | 제거 이유 |
|---|------|----------|
| 1 | 자동 원위치 기능 (시뮬 정지 후 전체 노드를 origin 상태로 복원) | ev2에서도 미구현 — Stop()은 상태 동결만 함, Reset()은 전체 R 초기화. H 전이 로직 없음 |

### 참고
- 시뮬 중 자동 리셋(predecessor G → F→R)과 자동 원위치(시뮬 정지 후 origin 복원)는 **별개 기능**
- 자동 리셋 = 정상 시뮬 동작, 유지
- 자동 원위치 = ev2에서도 미구현. Stop()은 상태 동결만, Reset()은 전체 R 초기화
- **NodeState.H**: 기획상 F→H→R 2단계 복귀 (StartReset 화살표에서 복귀 중 중간 상태). 현재 ev2는 F→R 직행으로 구현. 타입 정의 + UI 표시(파란색) + ForceWorkState 강제 설정만 가능. ds2에서 구현 여부 판단 필요
- **Stop()**: 백그라운드 스레드 종료 + 상태 동결 (H 전환 없음)
- **Reset()**: Stop() + 모든 Work/Call을 R로 초기화 + 스케줄러/메트릭/IOValues 클리어

---

## 14. 실행 시나리오 예시

### 순차 실행 (W1 → W2, 각 Work에 Call 1개)

```
시간  이벤트                              상태 변화
────────────────────────────────────────────────────────
 0    Start() → W1 수동 시작               W1: R→G
 0    EvaluateConditions                   C1: R→G (parentWork=G)
 0    Call G → TxWork O="true"             Signal O 발행
 0    TxWork G → Duration 스케줄            TxWork: R→G
100   DurationComplete(TxWork)             TxWork: G→F
100   EvaluateConditions                   C1 RxWork=F → canCompleteCall
100   Call G→F → RxWork I="true"           Signal I 발행 + IOValues 저장
100   DurationComplete(W1)                 W1: G→F
100   EvaluateConditions                   W2: canStartWork (W1=F) → W2: R→G
100   EvaluateConditions                   C2: R→G ...
200   ...                                  W2 완료
```

### Skip 시나리오 (ActiveTrigger 불만족)

```
시간  이벤트                              상태 변화
────────────────────────────────────────────────────────
 0    Work G                               W1: R→G
 0    EvaluateConditions
      canStartCall(C1) = true
      shouldSkipCall(C1) = true            C1: R→F (Skip, 취소선)
      (Auto/Common 조건 무시됨)
 0    EvaluateConditions                   C2: R→G (C1=F, predecessor 만족)
```
